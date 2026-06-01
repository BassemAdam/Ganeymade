using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;


public sealed class VoxelTracerSystem : MonoBehaviour
{
    //inspector options

    [Header("Compute Shaders")]
    public ComputeShader coreCS; //attach here the VoxelTracerCore.compute which builds the fill texture

    [Header("Grid")]
    public BoundsMode boundsMode = BoundsMode.AutoFitScene; //forms a bounding box around existing scene objects, ideal if sim is guaranteed to stay within the initally placed objects, smaller scenes
    [Tooltip("Only used when Bounds Mode = Manual")]
    public Vector3 gridMin = new Vector3(-10, -2, -10);
    [Tooltip("Only used when Bounds Mode = Manual")]
    public Vector3 gridMax = new Vector3(10, 10, 10);
    [Min(0.01f)] public float voxelSize = 0.25f;
    [Tooltip("Padding (in world units) added around auto-fit bounds")]
    [Min(0)] public float autoFitPadding = 1f; //allow room for movement outside range of initally placed objects

    [Header("Volume Fill")]
    [Tooltip("Fill interior volume between front and back surfaces")]
    public bool fillVolume = true;
    [Tooltip("Number of sweep rounds for flood fill (1 handles most geometry, 2 for complex concavities)")]
    [Range(1, 4)] public int fillSweepRounds = 1; //almost never needed more

    [Header("Normals")]
    [Tooltip("Compute gradient normals each frame. Only enable if a consumer reads NormalsTexture.")]
    public bool computeNormals = false;

    [Header("SDF")]
    [Tooltip("Compute a signed distance field each frame via Jump Flood Algorithm.")]
    public bool computeSDF = false; //this will result in normals being calculated from a blurr texture

    [Header("Scene Input")]
    public bool includeMeshRenderers = true;
    public bool includeSkinnedMeshRenderers = true; //if you don't have deformable/animated meshes just keep it off
    public bool includeTerrains = true; //terrains are quite expensive at scale, we recommend using this with manual mode set to a small, relevant portion of the terrain
    [Range(1, 32)] public int terrainSampleStep = 4;

    [Tooltip("Only objects on these layers are voxelized. Set to 'Everything' to include all layers (default behavior).")]
    public LayerMask voxelizeLayers = ~0; //default: Everything

    [Header("Material Properties")]
    [Tooltip("Default temperature for filled solid voxels (ambient)")]
    public float defaultSolidTemperature = 25f;
    [Tooltip("Default thermal diffusivity for filled solid voxels")]
    [Min(0)] public float defaultSolidDiffusivity = 0.1f;

    [Header("Safety")] //the default values are quite sufficient for most cases to function normally
    [Range(32, 512)] public int maxVoxelsPerAxis = 256;
    [Min(1)] public float maxVoxelCountMillions = 32f;

    //enums and structs

    public enum BoundsMode { Manual, AutoFitScene }

    [StructLayout(LayoutKind.Sequential)] //forces C# to layout the struct the same way in memory as specified in code, provides memory predictability
    struct Tri
    {
        public Vector3 a, b, c;
    }

    //Per-object dirty region in grid coordinates.
    struct DirtyRegion
    {
        public Vector3Int min, max;
    }

    //GPU-side material source stamp. Matches compute shader struct.
    [StructLayout(LayoutKind.Sequential)]
    struct MaterialSource
    {
        public Vector3 position;   //world-space center
        public Vector3 extents;    //half-size (AABB) or (radius,radius,radius) for sphere
        public float temperature;
        public float thermalDiffusivity;
        public float phase;      //0=solid, 1=fluid
        public uint shape;      //0 = AABB, 1=sphere
    }


    //Public accessors for the camera

    public RenderTexture FillTexture => _fillTex;
    public RenderTexture NormalsTexture => _normalsTex;
    public RenderTexture TemperatureTexture => _temperatureTex;
    public RenderTexture DiffusivityTexture => _diffusivityTex;
    public RenderTexture PhaseTexture => _phaseTex;
    public RenderTexture SDFTexture => _sdfTex; //signed distance field: the distance of any voxel to nearest surface vox
    public RenderTexture HeatSourceTexture => _heatSourceTex;
    public int Nx => _nx;
    public int Ny => _ny;
    public int Nz => _nz;
    public Vector3 ActiveGridMin => _activeMin;
    public float ActiveVoxelSize => voxelSize;
    public bool IsReady => _fillTex != null && _nx > 0;

    //Incremented every time VoxelizeFrame writes new data into the fill/material textures.
    //ThermalReceiver polls this to detect dynamic-object changes that require a GPU
    //mask/diffusivity/heat-source re-upload
    public int VoxelizeFrameCount { get; private set; }

    //Read-only access to registered heat sources (for external sim module).
    public static IReadOnlyCollection<VoxelHeatSource> HeatSources => _registeredHeatSources;
    //Read-only access to registered fluid sources (for external sim module).
    public static IReadOnlyCollection<VoxelFluidSource> FluidSources => _registeredFluidSources;
    //Read-only access to registered water bodies (for external sim module).
    public static IReadOnlyCollection<VoxelWaterBody> WaterBodies => _registeredWaterBodies;
    //Read-only access to registered solid material properties (for external sim module).
    public static IReadOnlyCollection<VoxelSolidMaterial> SolidMaterials => _registeredSolidMaterials;
    //Read-only access to registered fluid material properties (for external sim module).
    public static IReadOnlyCollection<VoxelFluidMaterial> FluidMaterials => _registeredFluidMaterials;


    //Private state

    //Kernel indices
    int KClear, KSurface, KSweepFill, KBuildTexture, KComputeNormals;
    int KBlurFill;
    int KCopyAndClearFlood, KCopyAndClearFloodLinear;
    int KRestoreStaticFull, KRestoreStaticFullLinear;
    int KClearVoxelBuffer, KCopyWorkingToStatic;
    int KWriteMaterialProperties;
    int KSDFSeed, KSDFJumpFlood, KSDFFinalize, KComputeSDFNormals;
    bool _kernelsCached;
    int KWriteHeatSources;

    //GPU buffers
    ComputeBuffer _voxelBuffer;      //working buffer: packed (bit 0=surface, bit 1=outside)
    ComputeBuffer _staticVoxelBuf;   //cached static packed (surface + flood)
    ComputeBuffer _staticTriBuffer;  //static triangles (uploaded once)
    ComputeBuffer _dynamicTriBuffer; //dynamic triangles (uploaded every frame)
    int _staticTriCount;
    int _dynamicTriCount;

    //Textures
    RenderTexture _fillTex;
    RenderTexture _blurredFillTex;
    RenderTexture _normalsTex;
    RenderTexture _temperatureTex;
    RenderTexture _diffusivityTex;
    RenderTexture _phaseTex;
    RenderTexture _sdfTex;
    RenderTexture _heatSourceTex;

    //SDF (Jump Flood Algorithm) buffers
    ComputeBuffer _jfaBufferA;
    ComputeBuffer _jfaBufferB;

    //Material source GPU buffer
    ComputeBuffer _materialSourceBuffer;
    ComputeBuffer _heatSourceMaterialBuffer;
    readonly List<MaterialSource> _materialSourceList = new List<MaterialSource>(64);

    //Grid state
    int _nx, _ny, _nz;
    int _totalVoxels;
    Vector3 _activeMin, _activeMax;

    //Triangle lists (CPU)
    readonly List<Tri> _staticTriList = new List<Tri>(128 * 1024);
    readonly List<Tri> _dynamicTriList = new List<Tri>(16 * 1024);
    Mesh _bakedMesh;

    //Reusable mesh data lists
    readonly List<Vector3> _tmpVerts = new List<Vector3>(4096);
    readonly List<int> _tmpIndices = new List<int>(12288);

    //Registration-based object tracking
    static readonly HashSet<VoxelDynamic> _registeredDynamics = new HashSet<VoxelDynamic>();
    static readonly HashSet<SkinnedMeshRenderer> _registeredSkins = new HashSet<SkinnedMeshRenderer>();
    static readonly HashSet<VoxelHeatSource> _registeredHeatSources = new HashSet<VoxelHeatSource>();
    static readonly HashSet<VoxelFluidSource> _registeredFluidSources = new HashSet<VoxelFluidSource>();
    static readonly HashSet<VoxelWaterBody> _registeredWaterBodies = new HashSet<VoxelWaterBody>();
    static readonly HashSet<VoxelSolidMaterial> _registeredSolidMaterials = new HashSet<VoxelSolidMaterial>();
    static readonly HashSet<VoxelFluidMaterial> _registeredFluidMaterials = new HashSet<VoxelFluidMaterial>();

    public static void RegisterDynamic(VoxelDynamic vd) { if (vd != null) _registeredDynamics.Add(vd); }
    public static void UnregisterDynamic(VoxelDynamic vd) { _registeredDynamics.Remove(vd); }
    public static IReadOnlyCollection<VoxelDynamic> RegisteredDynamics => _registeredDynamics;
    public static void RegisterSkin(SkinnedMeshRenderer smr) { if (smr != null) _registeredSkins.Add(smr); }
    public static void UnregisterSkin(SkinnedMeshRenderer smr) { _registeredSkins.Remove(smr); }
    public static void RegisterHeatSource(VoxelHeatSource hs) { if (hs != null) _registeredHeatSources.Add(hs); }
    public static void UnregisterHeatSource(VoxelHeatSource hs) { _registeredHeatSources.Remove(hs); }
    public static void RegisterFluidSource(VoxelFluidSource fs) { if (fs != null) _registeredFluidSources.Add(fs); }
    public static void UnregisterFluidSource(VoxelFluidSource fs) { _registeredFluidSources.Remove(fs); }
    public static void RegisterWaterBody(VoxelWaterBody wb) { if (wb != null) _registeredWaterBodies.Add(wb); }
    public static void UnregisterWaterBody(VoxelWaterBody wb) { _registeredWaterBodies.Remove(wb); }
    public static void RegisterSolidMaterial(VoxelSolidMaterial sm) { if (sm != null) _registeredSolidMaterials.Add(sm); }
    public static void UnregisterSolidMaterial(VoxelSolidMaterial sm) { _registeredSolidMaterials.Remove(sm); }
    public static void RegisterFluidMaterial(VoxelFluidMaterial fm) { if (fm != null) _registeredFluidMaterials.Add(fm); }
    public static void UnregisterFluidMaterial(VoxelFluidMaterial fm) { _registeredFluidMaterials.Remove(fm); }

    //Boundary collider registration
    static readonly HashSet<VoxelBoundaryCollider> _registeredBoundaryColliders = new HashSet<VoxelBoundaryCollider>();
    public static IReadOnlyCollection<VoxelBoundaryCollider> BoundaryColliders => _registeredBoundaryColliders;
    public static void RegisterBoundaryCollider(VoxelBoundaryCollider bc) { if (bc != null) _registeredBoundaryColliders.Add(bc); }
    public static void UnregisterBoundaryCollider(VoxelBoundaryCollider bc) { _registeredBoundaryColliders.Remove(bc); }

    //dirty flags
    bool _staticDirty = true;  //rebuild static tris and re-voxelize static layer
    bool _hasDynamicObjects;    //any dynamic objects exist in scene

    //per object dirty region tracking
    readonly List<DirtyRegion> _curDirtyRegions = new List<DirtyRegion>(16);
    readonly List<DirtyRegion> _prevDirtyRegions = new List<DirtyRegion>(16);
    readonly List<DirtyRegion> _mergedDirtyRegions = new List<DirtyRegion>(32);
    readonly List<DirtyRegion> _consolidatedRegions = new List<DirtyRegion>(16);


    //Lifecycle


    void OnEnable()
    {
        if (coreCS == null) return;
        CacheKernels(); //removes need for string lookup of kernel's name upon dispatching
        RebuildStatic(); //static triangle list
        VoxelizeFrame(); //first frame call
    }

    void OnDisable()
    {
        ReleaseAll(); //release all gpu resources, which are not managed by C#
    }

    void LateUpdate()
    {
        if (coreCS == null) return;

        if (_staticDirty)
            RebuildStatic();

        VoxelizeFrame();

        //pump async voxel-buffer readback so the cached snapshot used by
        //boundary-particle generation stays fresh without ever stalling.
        PollVoxelReadback();

        //Press F3 during play mode to dump temperature & diffusivity stats
        if (Input.GetKeyDown(KeyCode.F3))
            DebugPrintMaterialTextures();
    }


    //Public API

    //Call when static geometry changes (e.g. terrain edited, static objects added/removed).
    [ContextMenu("Rebuild Static")]
    public void MarkStaticDirty() => _staticDirty = true;

    //Full rebuild of everything.
    [ContextMenu("Force Voxelize")]
    public void ForceRebuild()
    {
        _staticDirty = true;
        RebuildStatic();
        VoxelizeFrame();
    }

    //Boundary Particle Surface Extraction

    //Async voxel-buffer snapshot. The CPU never blocks on the GPU; instead the
    //most recent completed snapshot is reused until a fresh one arrives. A snapshot
    //is at most one frame stale relative to a normal interval-driven boundary refresh.
    UnityEngine.Rendering.AsyncGPUReadbackRequest _voxelReadbackRequest;
    bool _voxelReadbackPending;
    uint[] _voxelSnapshot;          //last completed snapshot (sized _totalVoxels at request time)
    int _voxelSnapshotNx, _voxelSnapshotNy, _voxelSnapshotNz;
    Vector3 _voxelSnapshotMin;
    float _voxelSnapshotVoxelSize;
    bool _voxelSnapshotValid;

    //Reused output buffer (avoids per-call List allocation).
    readonly List<Vector3> _surfacePositionsCache = new List<Vector3>(4096);

    //True once at least one async voxel-buffer snapshot has completed.
    public bool HasSurfaceSnapshot => _voxelSnapshotValid;

    //
    //Fires an async readback of the current voxel buffer if none is in flight.
    //Safe to call every frame; work is automatically coalesced.
    //
    public void RequestSurfaceVoxelSnapshot()
    {
        if (_voxelBuffer == null || _totalVoxels == 0 || _nx == 0) return;
        if (_voxelReadbackPending) return;

        //Resize backing array only when grid dimensions change.
        if (_voxelSnapshot == null || _voxelSnapshot.Length != _totalVoxels)
            _voxelSnapshot = new uint[_totalVoxels];

        //Capture current grid metadata so a stale snapshot doesn't get sampled
        //against a different grid layout than the one it was taken from.
        _voxelSnapshotNx = _nx;
        _voxelSnapshotNy = _ny;
        _voxelSnapshotNz = _nz;
        _voxelSnapshotMin = _activeMin;
        _voxelSnapshotVoxelSize = voxelSize;

        _voxelReadbackRequest = UnityEngine.Rendering.AsyncGPUReadback.Request(_voxelBuffer);
        _voxelReadbackPending = true;
    }

    void PollVoxelReadback()
    {
        if (!_voxelReadbackPending) return;
        if (!_voxelReadbackRequest.done) return;

        _voxelReadbackPending = false;
        if (_voxelReadbackRequest.hasError)
            return;

        var data = _voxelReadbackRequest.GetData<uint>();
        if (data.Length != _voxelSnapshot.Length)
            _voxelSnapshot = new uint[data.Length];
        //Copy out; the NativeArray is invalidated after this callback frame.
        data.CopyTo(_voxelSnapshot);
        _voxelSnapshotValid = true;
    }

    //
    //Returns world-space positions of surface voxels, optionally filtered by bounds and normal direction.
    //Uses the most recent async voxel-buffer snapshot; never blocks the GPU pipeline.
    //On the very first call (before any snapshot has completed) this returns an empty list and
    //kicks off an async request; callers that retry next frame (e.g. boundary particle init)
    //will succeed once data arrives.
    //
    public List<Vector3> GetSurfaceVoxelPositions(float spacing, Bounds[] colliderBounds = null,
        bool useNormalFilter = false, Vector3 filterDirection = default, float filterThreshold = 0f)
    {
        //make sure we have a request in flight so the next call can return data.
        RequestSurfaceVoxelSnapshot();
        PollVoxelReadback();

        _surfacePositionsCache.Clear();
        if (!_voxelSnapshotValid) return _surfacePositionsCache;

        //use the snapshot's captured grid metadata (in case the grid resized
        //after the readback was issued).
        int snx = _voxelSnapshotNx;
        int sny = _voxelSnapshotNy;
        int snz = _voxelSnapshotNz;
        Vector3 snapMin = _voxelSnapshotMin;
        float snapVoxel = _voxelSnapshotVoxelSize;
        uint[] voxels = _voxelSnapshot;

        int step = 1;
        if (spacing > snapVoxel && snapVoxel > 0f)
            step = Mathf.Max(1, Mathf.RoundToInt(spacing / snapVoxel));

        bool filterByBounds = colliderBounds != null && colliderBounds.Length > 0;
        Vector3 filterDir = useNormalFilter ? filterDirection.normalized : Vector3.zero;
        float halfVoxel = snapVoxel * 0.5f;

        for (int z = 0; z < snz; z += step)
            for (int y = 0; y < sny; y += step)
                for (int x = 0; x < snx; x += step)
                {
                    int idx = z * (snx * sny) + y * snx + x;
                    if ((voxels[idx] & 1u) == 0) continue; //bit 0 = surface voxel

                    Vector3 worldPos = snapMin + new Vector3(
                        x * snapVoxel + halfVoxel,
                        y * snapVoxel + halfVoxel,
                        z * snapVoxel + halfVoxel);

                    if (filterByBounds)
                    {
                        bool inside = false;
                        for (int b = 0; b < colliderBounds.Length; b++)
                        {
                            if (colliderBounds[b].Contains(worldPos))
                            { inside = true; break; }
                        }
                        if (!inside) continue;
                    }

                    if (useNormalFilter)
                    {
                        Vector3 normal = EstimateSurfaceNormalSnapshot(voxels, snx, sny, snz, x, y, z);
                        if (Vector3.Dot(normal, filterDir) < filterThreshold)
                            continue;
                    }

                    _surfacePositionsCache.Add(worldPos);
                }
        return _surfacePositionsCache;
    }

    //cpu lookup methods
    Vector3 EstimateSurfaceNormalSnapshot(uint[] voxels, int snx, int sny, int snz, int x, int y, int z)
    {
        float xn = SampleOccupancySnapshot(voxels, snx, sny, snz, x - 1, y, z);
        float xp = SampleOccupancySnapshot(voxels, snx, sny, snz, x + 1, y, z);
        float yn = SampleOccupancySnapshot(voxels, snx, sny, snz, x, y - 1, z);
        float yp = SampleOccupancySnapshot(voxels, snx, sny, snz, x, y + 1, z);
        float zn = SampleOccupancySnapshot(voxels, snx, sny, snz, x, y, z - 1);
        float zp = SampleOccupancySnapshot(voxels, snx, sny, snz, x, y, z + 1);
        Vector3 n = new Vector3(xn - xp, yn - yp, zn - zp);
        float mag = n.magnitude;
        return mag > 0.001f ? n / mag : Vector3.up;
    }

    float SampleOccupancySnapshot(uint[] voxels, int snx, int sny, int snz, int x, int y, int z)
    {
        if (x < 0 || x >= snx || y < 0 || y >= sny || z < 0 || z >= snz)
            return 0f;
        int idx = z * (snx * sny) + y * snx + x;
        return (voxels[idx] & 1u) != 0 ? 1f : 0f;
    }

    Vector3 EstimateSurfaceNormal(uint[] voxels, int x, int y, int z)
    {
        float xn = SampleOccupancy(voxels, x - 1, y, z);
        float xp = SampleOccupancy(voxels, x + 1, y, z);
        float yn = SampleOccupancy(voxels, x, y - 1, z);
        float yp = SampleOccupancy(voxels, x, y + 1, z);
        float zn = SampleOccupancy(voxels, x, y, z - 1);
        float zp = SampleOccupancy(voxels, x, y, z + 1);
        //Normal points from occupied toward empty
        Vector3 n = new Vector3(xn - xp, yn - yp, zn - zp);
        float mag = n.magnitude;
        return mag > 0.001f ? n / mag : Vector3.up;
    }

    float SampleOccupancy(uint[] voxels, int x, int y, int z) //lookup which voxels in the snapshot have been occupied
    {
        if (x < 0 || x >= _nx || y < 0 || y >= _ny || z < 0 || z >= _nz)
            return 0f;
        int idx = z * (_nx * _ny) + y * _nx + x;
        return (voxels[idx] & 1u) != 0 ? 1f : 0f;
    }


    //Static rebuild (runs once, or when MarkStaticDirty called)


    void RebuildStatic()
    {
        if (!_kernelsCached) CacheKernels();
        _staticDirty = false;

        //Gather ALL triangles (static + dynamic) to compute bounds
        BuildTriangleLists();

        int totalTris = _staticTriList.Count + _dynamicTriList.Count;
        if (totalTris == 0) { _nx = _ny = _nz = 0; return; }

        //Compute bounds from all geometry (static + dynamic)
        ComputeBoundsFromBothLists(out Vector3 mn, out Vector3 mx);
        _activeMin = mn;
        _activeMax = mx;

        ComputeGridSize(mn, mx, out int gx, out int gy, out int gz);
        _nx = gx; _ny = gy; _nz = gz;
        _totalVoxels = gx * gy * gz;

        AllocateResources(gx, gy, gz);

        //Upload static triangles
        UploadStaticTriangles();

        if (_staticTriCount == 0)
        {
            //No static geometry; just clear the static voxel cache
            SetGridUniforms(gx, gy, gz);
            SetRegionMin(0, 0, 0);
            ClearStaticVoxelCache(gx, gy, gz);
            return;
        }

        //Voxelize static geometry once -> store in _staticVoxelBuf
        SetGridUniforms(gx, gy, gz);
        SetRegionMin(0, 0, 0);
        BindClearBuffers();
        Dispatch3D(KClear, gx, gy, gz);

        coreCS.SetBuffer(KSurface, "_VoxelBuffer", _voxelBuffer);
        coreCS.SetBuffer(KSurface, "_Tris", _staticTriBuffer);
        coreCS.SetInt("_TriCount", _staticTriCount);
        DispatchLinear(KSurface, _staticTriCount);

        //Run the full fill pipeline so output textures are pre-populated
        //with the static-only result. This makes per-frame work ZERO
        //when no dynamic objects exist.
        var fullMin = Vector3Int.zero;
        var fullMax = new Vector3Int(gx - 1, gy - 1, gz - 1);
        RunFillPipeline(gx, gy, gz, fullMin, fullMax);

        //Copy afterfill pipeline: static cache now includes surface bit 0 + flood bit 1.
        //This enables restoration frames to skip sweep entirely.
        CopyWorkingToStaticCache(gx, gy, gz);

        //Reset dirty-region tracking after full bake
        _prevDirtyRegions.Clear();
    }

    void ClearStaticVoxelCache(int gx, int gy, int gz)
    {
        //GPU-side clear via the ClearVoxelBuffer kernel (3D dispatch, safe for any grid size)
        coreCS.SetBuffer(KClearVoxelBuffer, "_VoxelBuffer", _staticVoxelBuf);
        Dispatch3D(KClearVoxelBuffer, gx, gy, gz);
    }

    void CopyWorkingToStaticCache(int gx, int gy, int gz)
    {
        //GPU-side copy: working -> static cache without readback
        coreCS.SetBuffer(KCopyWorkingToStatic, "_VoxelBuffer", _voxelBuffer);
        coreCS.SetBuffer(KCopyWorkingToStatic, "_DstBuffer", _staticVoxelBuf);
        Dispatch3D(KCopyWorkingToStatic, gx, gy, gz);
    }

    //Per-frame voxelization (fast path)

    void VoxelizeFrame()
    {
        if (_nx == 0 || _fillTex == null) return;

        int gx = _nx, gy = _ny, gz = _nz;

        //rebuild dynamic triangle list every frame
        BuildDynamicTriangleList();

        bool hasDynamics = _dynamicTriList.Count > 0;

        //fast path: no dynamic objects and no previous dirty regions to restore
        if (!hasDynamics && _prevDirtyRegions.Count == 0)
            return;

        //collect all raw regions (current + previous, un-padded)
        _mergedDirtyRegions.Clear();
        for (int i = 0; i < _curDirtyRegions.Count; i++)
            _mergedDirtyRegions.Add(_curDirtyRegions[i]);
        for (int i = 0; i < _prevDirtyRegions.Count; i++)
            _mergedDirtyRegions.Add(_prevDirtyRegions[i]);

        //pad all regions, then consolidate overlapping/nearby ones
        const int pad = 3;
        var gridMax = new Vector3Int(gx - 1, gy - 1, gz - 1);
        for (int i = 0; i < _mergedDirtyRegions.Count; i++)
        {
            var r = _mergedDirtyRegions[i];
            r.min = Vector3Int.Max(r.min - new Vector3Int(pad, pad, pad), Vector3Int.zero);
            r.max = Vector3Int.Min(r.max + new Vector3Int(pad, pad, pad), gridMax);
            _mergedDirtyRegions[i] = r;
        }
        ConsolidateRegions(_mergedDirtyRegions, _consolidatedRegions);

        //update tracking: current becomes previous for next frame
        _prevDirtyRegions.Clear();
        for (int i = 0; i < _curDirtyRegions.Count; i++)
            _prevDirtyRegions.Add(_curDirtyRegions[i]); //store UN-padded

        SetGridUniforms(gx, gy, gz);

        //decide if full-grid fast path OR per-region path.
        bool useFullGrid = false;
        if (_consolidatedRegions.Count == 1)
        {
            var r = _consolidatedRegions[0];
            long dirtyVol = (long)(r.max.x - r.min.x + 1)
                          * (r.max.y - r.min.y + 1)
                          * (r.max.z - r.min.z + 1);
            if (dirtyVol * 2 >= _totalVoxels)
                useFullGrid = true;
        }

        //non-dynamic restoration: static cache includes flood, skip sweep entirely.
        if (!hasDynamics)
        {
            VoxelizeFrameRestore(gx, gy, gz, useFullGrid);
            return;
        }

        UploadDynamicTriangles();

        if (useFullGrid)
        {
            VoxelizeFrameFullGrid(gx, gy, gz,
                _consolidatedRegions[0]);
        }
        else
        {
            VoxelizeFrameRegions(gx, gy, gz);
        }
    }

    //restoration path: dynamics have disappeared, restore static state.
    //static cache includes pre-computed flood marks, so sweep is skipped entirely.
    //saves 3 sweep dispatches + surface dispatch on transition frames.
    void VoxelizeFrameRestore(int gx, int gy, int gz, bool useFullGrid)
    {
        if (useFullGrid)
        {
            //Restore full static surface + flood with linear coalescing
            coreCS.SetInt("_TotalVoxels", _totalVoxels);
            coreCS.SetBuffer(KRestoreStaticFullLinear, "_VoxelBuffer", _voxelBuffer);
            coreCS.SetBuffer(KRestoreStaticFullLinear, "_StaticVoxelBuffer", _staticVoxelBuf);
            DispatchLinear(KRestoreStaticFullLinear, _totalVoxels);

            //BuildTexture scoped to dirty region (skip sweep; flood is correct from cache)
            var r = _consolidatedRegions[0];
            RunBuildOnly(gx, gy, gz, r.min, r.max);
        }
        else
        {
            //Per-region restore: copy full static (surface + flood) into dirty regions
            coreCS.SetBuffer(KRestoreStaticFull, "_VoxelBuffer", _voxelBuffer);
            coreCS.SetBuffer(KRestoreStaticFull, "_StaticVoxelBuffer", _staticVoxelBuf);
            for (int i = 0; i < _consolidatedRegions.Count; i++)
            {
                var r = _consolidatedRegions[i];
                Vector3Int sz = r.max - r.min + Vector3Int.one;
                SetRegionMin(r.min.x, r.min.y, r.min.z);
                Dispatch3D(KRestoreStaticFull, sz.x, sz.y, sz.z);
            }

            //BuildTexture per region (no sweep needed)
            for (int i = 0; i < _consolidatedRegions.Count; i++)
            {
                var r = _consolidatedRegions[i];
                RunBuildOnly(gx, gy, gz, r.min, r.max);
            }
        }
    }

    //Full-grid path: linear kernel for buffer copy (perfect coalescing),
    //fill pipeline scoped to dirty region. Used when dirty volume is large.
    void VoxelizeFrameFullGrid(int gx, int gy, int gz, DirtyRegion dirtyRegion)
    {
        //1) Copy static surface, clear flood; single linear dispatch
        coreCS.SetInt("_TotalVoxels", _totalVoxels);
        coreCS.SetBuffer(KCopyAndClearFloodLinear, "_VoxelBuffer", _voxelBuffer);
        coreCS.SetBuffer(KCopyAndClearFloodLinear, "_StaticVoxelBuffer", _staticVoxelBuf);
        DispatchLinear(KCopyAndClearFloodLinear, _totalVoxels);

        //2) Surface voxelization; dynamic triangles
        coreCS.SetBuffer(KSurface, "_VoxelBuffer", _voxelBuffer);
        coreCS.SetBuffer(KSurface, "_Tris", _dynamicTriBuffer);
        coreCS.SetInt("_TriCount", _dynamicTriCount);
        DispatchLinear(KSurface, _dynamicTriCount);

        //3) Fill pipeline; scoped to dirty region
        RunFillPipeline(gx, gy, gz, dirtyRegion.min, dirtyRegion.max);
    }

    //Per-region path: only processes dirty sub-volumes.
    //Used when dirty volume is small relative to total grid.
    void VoxelizeFrameRegions(int gx, int gy, int gz)
    {
        //1) Restore static surface + clear flood per dirty region
        coreCS.SetBuffer(KCopyAndClearFlood, "_VoxelBuffer", _voxelBuffer);
        coreCS.SetBuffer(KCopyAndClearFlood, "_StaticVoxelBuffer", _staticVoxelBuf);
        for (int i = 0; i < _consolidatedRegions.Count; i++)
        {
            var r = _consolidatedRegions[i];
            Vector3Int sz = r.max - r.min + Vector3Int.one;
            SetRegionMin(r.min.x, r.min.y, r.min.z);
            Dispatch3D(KCopyAndClearFlood, sz.x, sz.y, sz.z);
        }

        //2) Surface voxelization; all dynamic triangles at once
        coreCS.SetBuffer(KSurface, "_VoxelBuffer", _voxelBuffer);
        coreCS.SetBuffer(KSurface, "_Tris", _dynamicTriBuffer);
        coreCS.SetInt("_TriCount", _dynamicTriCount);
        DispatchLinear(KSurface, _dynamicTriCount);

        //3) Fill pipeline per consolidated region
        for (int i = 0; i < _consolidatedRegions.Count; i++)
        {
            var r = _consolidatedRegions[i];
            RunFillPipeline(gx, gy, gz, r.min, r.max);
        }
    }

    //Merge overlapping or nearby regions to minimize dispatch count.
    //Uses greedy iterative merging: any two regions whose AABBs overlap are
    //unioned into one. Repeats until stable. O(N^2) but N is tiny (< 20).
    static void ConsolidateRegions(List<DirtyRegion> input, List<DirtyRegion> output)
    {
        output.Clear();
        for (int i = 0; i < input.Count; i++)
            output.Add(input[i]);

        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < output.Count; i++)
            {
                for (int j = i + 1; j < output.Count; j++)
                {
                    var a = output[i];
                    var b = output[j];

                    //Check AABB overlap (regions already padded, so touching = overlapping)
                    if (a.min.x <= b.max.x && a.max.x >= b.min.x &&
                        a.min.y <= b.max.y && a.max.y >= b.min.y &&
                        a.min.z <= b.max.z && a.max.z >= b.min.z)
                    {
                        //Union them
                        output[i] = new DirtyRegion
                        {
                            min = Vector3Int.Min(a.min, b.min),
                            max = Vector3Int.Max(a.max, b.max)
                        };
                        output.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
                if (merged) break;
            }
        }
    }

    //Shared fill pipeline: sweep fill -> build texture -> blur -> normals.
    //Region parameters control which voxels are processed.
    void RunFillPipeline(int gx, int gy, int gz, Vector3Int regMin, Vector3Int regMax)
    {
        Vector3Int regSize = regMax - regMin + Vector3Int.one;

        //Sweep flood fill; only lines that cross the dirty region,
        //but each line sweeps full axis length for correctness
        if (fillVolume)
        {
            coreCS.SetBuffer(KSweepFill, "_VoxelBuffer", _voxelBuffer);
            for (int round = 0; round < fillSweepRounds; round++)
            {
                DispatchSweepRegion(0, regMin, regMax);
                DispatchSweepRegion(1, regMin, regMax);
                DispatchSweepRegion(2, regMin, regMax);
            }
        }

        RunBuildOnly(gx, gy, gz, regMin, regMax);
    }

    //Build fill texture (+ optional blur/normals) without sweep.
    //Used both after sweep and for restoration frames where flood is pre-computed.
    void RunBuildOnly(int gx, int gy, int gz, Vector3Int regMin, Vector3Int regMax)
    {
        Vector3Int regSize = regMax - regMin + Vector3Int.one;

        //Build fill texture (dirty region only)
        SetRegionMin(regMin.x, regMin.y, regMin.z);
        coreCS.SetInt("_FillVolume", fillVolume ? 1 : 0);
        coreCS.SetBuffer(KBuildTexture, "_VoxelBuffer", _voxelBuffer);
        coreCS.SetTexture(KBuildTexture, "_FillTex", _fillTex);
        Dispatch3D(KBuildTexture, regSize.x, regSize.y, regSize.z);

        //Blur fill + compute normals (legacy path; only when SDF is off) but it ususally will be on.
        if (!computeSDF && computeNormals && _blurredFillTex != null && _normalsTex != null)
        {
            Vector3Int blurMin = Vector3Int.Max(regMin - Vector3Int.one, Vector3Int.zero);
            Vector3Int blurMax = Vector3Int.Min(regMax + Vector3Int.one,
                new Vector3Int(gx - 1, gy - 1, gz - 1));
            Vector3Int blurSize = blurMax - blurMin + Vector3Int.one;

            SetRegionMin(blurMin.x, blurMin.y, blurMin.z);
            coreCS.SetTexture(KBlurFill, "_FillTex", _fillTex);
            coreCS.SetTexture(KBlurFill, "_BlurredFillTex", _blurredFillTex);
            Dispatch3D(KBlurFill, blurSize.x, blurSize.y, blurSize.z);

            coreCS.SetTexture(KComputeNormals, "_FillTex", _fillTex);
            coreCS.SetTexture(KComputeNormals, "_BlurredFillTex", _blurredFillTex);
            coreCS.SetTexture(KComputeNormals, "_NormalTex", _normalsTex);
            Dispatch3D(KComputeNormals, blurSize.x, blurSize.y, blurSize.z);
        }

        //write material properties (temperature, phase) after fill is known
        StampMaterialProperties(gx, gy, gz, regMin, regMax);

        //compute SDF + SDF-based normals (replaces blur normals when SDF is on)
        if (computeSDF && _sdfTex != null)
            ComputeSDFJumpFlood(gx, gy, gz);
        //stamp heat-source voxels into the dedicated HeatSourceTexture.
        //must run after StampMaterialProperties so fill is guaranteed written.
        StampHeatSources(gx, gy, gz, regMin, regMax);

        //signal to external systems (ex: ThermalReceiver) that texture data changed.
        VoxelizeFrameCount++;
    }

    void SetGridUniforms(int gx, int gy, int gz)
    {
        coreCS.SetInt("_Width", gx);
        coreCS.SetInt("_Height", gy);
        coreCS.SetInt("_Depth", gz);
        coreCS.SetVector("_Start", _activeMin);
        coreCS.SetFloat("_Unit", voxelSize);
        coreCS.SetFloat("_HalfUnit", voxelSize * 0.5f);
    }

    void SetRegionMin(int x, int y, int z)
    {
        coreCS.SetInt("_RegionMinX", x);
        coreCS.SetInt("_RegionMinY", y);
        coreCS.SetInt("_RegionMinZ", z);
    }

    void BindClearBuffers()
    {
        coreCS.SetBuffer(KClear, "_VoxelBuffer", _voxelBuffer);
        coreCS.SetTexture(KClear, "_FillTex", _fillTex);
    }


    //Triangle extraction (CPU)


    void BuildTriangleLists()
    {
        _staticTriList.Clear();
        _dynamicTriList.Clear();
        _hasDynamicObjects = false;

        if (includeMeshRenderers)
        {
            var filters = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
            foreach (var mf in filters)
            {
                if (mf == null || !mf.gameObject.activeInHierarchy) continue;
                if ((voxelizeLayers.value & (1 << mf.gameObject.layer)) == 0) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled) continue;
                if (mf.sharedMesh == null) continue;

                bool isDynamic = mf.GetComponent<VoxelDynamic>() != null;
                if (isDynamic)
                {
                    AppendMesh(mf.sharedMesh, mf.transform.localToWorldMatrix, _dynamicTriList);
                    _hasDynamicObjects = true;
                }
                else
                {
                    AppendMesh(mf.sharedMesh, mf.transform.localToWorldMatrix, _staticTriList);
                }
            }
        }

        if (includeSkinnedMeshRenderers)
        {
            if (_bakedMesh == null) _bakedMesh = new Mesh();
            var skins = FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None);
            foreach (var smr in skins)
            {
                if (smr == null || !smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                if ((voxelizeLayers.value & (1 << smr.gameObject.layer)) == 0) continue;
                _bakedMesh.Clear();
                try { smr.BakeMesh(_bakedMesh); } catch { continue; }
                AppendMesh(_bakedMesh, smr.transform.localToWorldMatrix, _dynamicTriList);
                _hasDynamicObjects = true;
            }
        }

        if (includeTerrains)
        {
            var terrains = FindObjectsByType<Terrain>(FindObjectsSortMode.None);
            foreach (var t in terrains)
            {
                if (t == null || !t.isActiveAndEnabled) continue;
                if ((voxelizeLayers.value & (1 << t.gameObject.layer)) == 0) continue;
                AppendTerrain(t, terrainSampleStep, _staticTriList);
            }
        }
    }

    //Lightweight per-frame rebuild of dynamic triangles only.
    //Uses registration-based tracking (O(1) list access) instead of FindObjectsByType (O(N) scene scan).
    //Also computes per-object grid AABBs for dirty-region tracking.
    void BuildDynamicTriangleList()
    {
        _dynamicTriList.Clear();
        _curDirtyRegions.Clear();

        float inv = 1f / voxelSize;
        var gridClampMax = new Vector3Int(_nx - 1, _ny - 1, _nz - 1);

        if (includeMeshRenderers)
        {
            foreach (var vd in _registeredDynamics)
            {
                if (vd == null || !vd.gameObject.activeInHierarchy) continue;
                if ((voxelizeLayers.value & (1 << vd.gameObject.layer)) == 0) continue;
                var mf = vd.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mr = vd.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled) continue;

                bool hasMoved = vd.HasMoved();
                AppendMesh(mf.sharedMesh, mf.transform.localToWorldMatrix, _dynamicTriList);
                AddDirtyRegionFromBounds(mr.bounds, inv, gridClampMax);
            }
        }

        if (includeSkinnedMeshRenderers)
        {
            if (_bakedMesh == null) _bakedMesh = new Mesh();
            foreach (var smr in _registeredSkins)
            {
                if (smr == null || !smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                if ((voxelizeLayers.value & (1 << smr.gameObject.layer)) == 0) continue;
                _bakedMesh.Clear();
                try { smr.BakeMesh(_bakedMesh); } catch { continue; }

                AppendMesh(_bakedMesh, smr.transform.localToWorldMatrix, _dynamicTriList);
                AddDirtyRegionFromBounds(smr.bounds, inv, gridClampMax);
            }
        }
    }

    void AddDirtyRegionFromBounds(Bounds bounds, float inv, Vector3Int gridClampMax)
    {
        Vector3 mn = bounds.min;
        Vector3 mx = bounds.max;

        var gMin = new Vector3Int(
            Mathf.FloorToInt((mn.x - _activeMin.x) * inv),
            Mathf.FloorToInt((mn.y - _activeMin.y) * inv),
            Mathf.FloorToInt((mn.z - _activeMin.z) * inv)
        );
        var gMax = new Vector3Int(
            Mathf.CeilToInt((mx.x - _activeMin.x) * inv),
            Mathf.CeilToInt((mx.y - _activeMin.y) * inv),
            Mathf.CeilToInt((mx.z - _activeMin.z) * inv)
        );

        gMin = Vector3Int.Max(gMin, Vector3Int.zero);
        gMax = Vector3Int.Min(gMax, gridClampMax);

        _curDirtyRegions.Add(new DirtyRegion { min = gMin, max = gMax });
    }


    //Iterates all submeshes
    void AppendMesh(Mesh mesh, Matrix4x4 l2w, List<Tri> target)
    {
        mesh.GetVertices(_tmpVerts); //zero garbage collection since no new list is being created
        int vertCount = _tmpVerts.Count;
        if (vertCount == 0) return;

        int subMeshCount = mesh.subMeshCount;
        for (int sub = 0; sub < subMeshCount; sub++)
        {
            if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;

            mesh.GetIndices(_tmpIndices, sub);
            int idxCount = _tmpIndices.Count;
            if (idxCount < 3) continue;

            for (int i = 0; i < idxCount; i += 3)
            {
                int i0 = _tmpIndices[i], i1 = _tmpIndices[i + 1], i2 = _tmpIndices[i + 2];
                if ((uint)i0 >= (uint)vertCount ||
                    (uint)i1 >= (uint)vertCount ||
                    (uint)i2 >= (uint)vertCount) continue;

                Vector3 a = l2w.MultiplyPoint3x4(_tmpVerts[i0]);
                Vector3 b = l2w.MultiplyPoint3x4(_tmpVerts[i1]);
                Vector3 c = l2w.MultiplyPoint3x4(_tmpVerts[i2]);

                //degenerate triangle check (no sqrt needed; just check cross product magnitude)
                Vector3 cross = Vector3.Cross(b - a, c - a);
                if (cross.sqrMagnitude < 1e-20f) continue;

                target.Add(new Tri { a = a, b = b, c = c });
            }
        }
    }

    void AppendTerrain(Terrain terrain, int step, List<Tri> target)
    {
        var td = terrain.terrainData;
        if (td == null) return;

        Vector3 tPos = terrain.transform.position;
        Vector3 tSize = td.size;
        int hRes = td.heightmapResolution;
        step = Mathf.Max(1, step);

        float[,] heights = td.GetHeights(0, 0, hRes, hRes);

        int xSteps = (hRes - 1) / step;
        int zSteps = (hRes - 1) / step;

        float bottomY = tPos.y; //bottom of terrain volume

        for (int iz = 0; iz < zSteps; iz++)
            for (int ix = 0; ix < xSteps; ix++)
            {
                int x0 = ix * step, x1 = Mathf.Min(x0 + step, hRes - 1);
                int z0 = iz * step, z1 = Mathf.Min(z0 + step, hRes - 1);

                Vector3 v00 = HToW(x0, z0, heights[z0, x0], hRes, tPos, tSize);
                Vector3 v10 = HToW(x1, z0, heights[z0, x1], hRes, tPos, tSize);
                Vector3 v01 = HToW(x0, z1, heights[z1, x0], hRes, tPos, tSize);
                Vector3 v11 = HToW(x1, z1, heights[z1, x1], hRes, tPos, tSize);

                //top surface (heightmap)
                AddTri(v00, v11, v10, target);
                AddTri(v00, v01, v11, target);

                //bottom surface (flat at terrain base Y, reversed winding)
                Vector3 b00 = new Vector3(v00.x, bottomY, v00.z);
                Vector3 b10 = new Vector3(v10.x, bottomY, v10.z);
                Vector3 b01 = new Vector3(v01.x, bottomY, v01.z);
                Vector3 b11 = new Vector3(v11.x, bottomY, v11.z);
                AddTri(b00, b10, b11, target);
                AddTri(b00, b11, b01, target);
            }

        //side-wall skirts to seal the mesh when edges are raised.
        //without these, the sweep fill leaks through the gap between
        //the top heightmap edge and the flat bottom surface.

        //z-min edge (iz == 0, front face, winding faces outward -Z)
        for (int ix = 0; ix < xSteps; ix++)
        {
            int x0 = ix * step, x1 = Mathf.Min(x0 + step, hRes - 1);
            Vector3 top0 = HToW(x0, 0, heights[0, x0], hRes, tPos, tSize);
            Vector3 top1 = HToW(x1, 0, heights[0, x1], hRes, tPos, tSize);
            Vector3 bot0 = new Vector3(top0.x, bottomY, top0.z);
            Vector3 bot1 = new Vector3(top1.x, bottomY, top1.z);
            AddTri(top0, top1, bot1, target);
            AddTri(top0, bot1, bot0, target);
        }

        //z-max edge (iz == zSteps, back face, winding faces outward +Z)
        for (int ix = 0; ix < xSteps; ix++)
        {
            int x0 = ix * step, x1 = Mathf.Min(x0 + step, hRes - 1);
            int z = Mathf.Min(zSteps * step, hRes - 1);
            Vector3 top0 = HToW(x0, z, heights[z, x0], hRes, tPos, tSize); //convert hieght map to world indices
            Vector3 top1 = HToW(x1, z, heights[z, x1], hRes, tPos, tSize);
            Vector3 bot0 = new Vector3(top0.x, bottomY, top0.z);
            Vector3 bot1 = new Vector3(top1.x, bottomY, top1.z);
            AddTri(top0, bot1, top1, target);
            AddTri(top0, bot0, bot1, target);
        }

        //x-min edge (ix == 0, left face, winding faces outward -X)
        for (int iz = 0; iz < zSteps; iz++)
        {
            int z0 = iz * step, z1 = Mathf.Min(z0 + step, hRes - 1);
            Vector3 top0 = HToW(0, z0, heights[z0, 0], hRes, tPos, tSize);
            Vector3 top1 = HToW(0, z1, heights[z1, 0], hRes, tPos, tSize);
            Vector3 bot0 = new Vector3(top0.x, bottomY, top0.z);
            Vector3 bot1 = new Vector3(top1.x, bottomY, top1.z);
            AddTri(top0, bot0, bot1, target);
            AddTri(top0, bot1, top1, target);
        }

        //x-max edge (ix == xSteps, right face, winding faces outward +X)
        for (int iz = 0; iz < zSteps; iz++)
        {
            int z0 = iz * step, z1 = Mathf.Min(z0 + step, hRes - 1);
            int x = Mathf.Min(xSteps * step, hRes - 1);
            Vector3 top0 = HToW(x, z0, heights[z0, x], hRes, tPos, tSize);
            Vector3 top1 = HToW(x, z1, heights[z1, x], hRes, tPos, tSize);
            Vector3 bot0 = new Vector3(top0.x, bottomY, top0.z);
            Vector3 bot1 = new Vector3(top1.x, bottomY, top1.z);
            AddTri(top0, bot1, bot0, target);
            AddTri(top0, top1, bot1, target);
        }
    }

    static Vector3 HToW(int x, int z, float h, int hRes, Vector3 tPos, Vector3 tSize)
    {
        float fx = (float)x / (hRes - 1);
        float fz = (float)z / (hRes - 1);
        return new Vector3(tPos.x + fx * tSize.x,
                           tPos.y + h * tSize.y,
                           tPos.z + fz * tSize.z);
    }

    void AddTri(Vector3 a, Vector3 b, Vector3 c, List<Tri> target)
    {
        Vector3 cross = Vector3.Cross(b - a, c - a);
        if (cross.sqrMagnitude < 1e-20f) return;
        target.Add(new Tri { a = a, b = b, c = c });
    }

    //Bounds
    void ComputeBoundsFromBothLists(out Vector3 mn, out Vector3 mx)
    {
        if (boundsMode == BoundsMode.Manual)
        {
            mn = gridMin;
            mx = gridMax;
            return;
        }

        mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        //Size grid to STATIC geometry only. Dynamic objects move each frame,
        //so baking their initial position into the grid bounds wastes
        //resolution and can blow past the SDF voxel budget.
        ExpandBounds(_staticTriList, ref mn, ref mx);

        if (mn.x > mx.x) { mn = gridMin; mx = gridMax; return; }

        Vector3 pad = Vector3.one * autoFitPadding;
        mn -= pad;
        mx += pad;
    }

    static void ExpandBounds(List<Tri> list, ref Vector3 mn, ref Vector3 mx)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            mn = Vector3.Min(mn, Vector3.Min(t.a, Vector3.Min(t.b, t.c)));
            mx = Vector3.Max(mx, Vector3.Max(t.a, Vector3.Max(t.b, t.c)));
        }
    }

    //Grid sizing
    void ComputeGridSize(Vector3 mn, Vector3 mx, out int gx, out int gy, out int gz)
    {
        Vector3 size = mx - mn;
        gx = Mathf.Max(1, Mathf.CeilToInt(size.x / voxelSize));
        gy = Mathf.Max(1, Mathf.CeilToInt(size.y / voxelSize));
        gz = Mathf.Max(1, Mathf.CeilToInt(size.z / voxelSize));

        int cap = Mathf.Max(32, maxVoxelsPerAxis);
        gx = Mathf.Min(gx, cap);
        gy = Mathf.Min(gy, cap);
        gz = Mathf.Min(gz, cap);

        long total = (long)gx * gy * gz;
        long budget = (long)(maxVoxelCountMillions * 1_000_000);
        if (total > budget)
        {
            float scale = Mathf.Pow((float)budget / total, 1f / 3f);
            gx = Mathf.Max(1, Mathf.FloorToInt(gx * scale));
            gy = Mathf.Max(1, Mathf.FloorToInt(gy * scale));
            gz = Mathf.Max(1, Mathf.FloorToInt(gz * scale));
        }
    }


    //GPU resource management
    void CacheKernels() //finds them once and cache the kernel to avoid string lookup every time it's needed
    {
        KClear = coreCS.FindKernel("Clear");
        KSurface = coreCS.FindKernel("Surface");
        KSweepFill = coreCS.FindKernel("SweepFill");
        KBuildTexture = coreCS.FindKernel("BuildTexture");
        KComputeNormals = coreCS.FindKernel("ComputeNormals");
        KBlurFill = coreCS.FindKernel("BlurFill");
        KCopyAndClearFlood = coreCS.FindKernel("CopyAndClearFlood");
        KCopyAndClearFloodLinear = coreCS.FindKernel("CopyAndClearFloodLinear");
        KRestoreStaticFull = coreCS.FindKernel("RestoreStaticFull");
        KRestoreStaticFullLinear = coreCS.FindKernel("RestoreStaticFullLinear");
        KClearVoxelBuffer = coreCS.FindKernel("ClearVoxelBuffer");
        KCopyWorkingToStatic = coreCS.FindKernel("CopyWorkingToStatic");
        KWriteMaterialProperties = coreCS.FindKernel("WriteMaterialProperties");
        KWriteHeatSources = coreCS.FindKernel("WriteHeatSources");
        KSDFSeed = coreCS.FindKernel("SDFSeed");
        KSDFJumpFlood = coreCS.FindKernel("SDFJumpFlood");
        KSDFFinalize = coreCS.FindKernel("SDFFinalize");
        KComputeSDFNormals = coreCS.FindKernel("ComputeSDFNormals");
        _kernelsCached = true;
    }

    void AllocateResources(int gx, int gy, int gz)
    {
        ReleaseBuffers();
        ReleaseTextures();

        int totalVoxels = gx * gy * gz;
        _totalVoxels = totalVoxels;
        _voxelBuffer = new ComputeBuffer(totalVoxels, sizeof(uint));
        _staticVoxelBuf = new ComputeBuffer(totalVoxels, sizeof(uint));

        _fillTex = new RenderTexture(gx, gy, 0, RenderTextureFormat.RFloat)
        {
            dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
            volumeDepth = gz,
            enableRandomWrite = true,
            useMipMap = false,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        _fillTex.Create();

        //Material property textures; always allocated (used by external sim module)
        _temperatureTex = new RenderTexture(gx, gy, 0, RenderTextureFormat.RFloat)
        {
            dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
            volumeDepth = gz,
            enableRandomWrite = true,
            useMipMap = false,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        _temperatureTex.Create();

        _diffusivityTex = new RenderTexture(gx, gy, 0, RenderTextureFormat.RFloat)
        {
            dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
            volumeDepth = gz,
            enableRandomWrite = true,
            useMipMap = false,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        _diffusivityTex.Create();

        _phaseTex = new RenderTexture(gx, gy, 0, RenderTextureFormat.RFloat)
        {
            dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
            volumeDepth = gz,
            enableRandomWrite = true,
            useMipMap = false,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        _phaseTex.Create();

        _heatSourceTex = new RenderTexture(gx, gy, 0, RenderTextureFormat.RFloat)
        {
            dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
            volumeDepth = gz,
            enableRandomWrite = true,
            useMipMap = false,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        _heatSourceTex.Create();

        //Normals textures: allocate when computeNormals OR computeSDF is enabled
        //When SDF is on, normals come from SDF gradient (no blur needed), this is the recommended path
        bool needNormals = computeNormals || computeSDF;
        if (needNormals)
        {
            //blur texture only needed for legacy fill-based normals (non-SDF path)
            if (computeNormals && !computeSDF)
            {
                _blurredFillTex = new RenderTexture(gx, gy, 0, RenderTextureFormat.RFloat)
                {
                    dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                    volumeDepth = gz,
                    enableRandomWrite = true,
                    useMipMap = false,
                    autoGenerateMips = false,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Point
                };
                _blurredFillTex.Create();
            }

            _normalsTex = new RenderTexture(gx, gy, 0, RenderTextureFormat.ARGBFloat)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = gz,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            _normalsTex.Create();
        }

        //SDF texture + JFA buffers: only allocate when computeSDF is enabled
        if (computeSDF)
        {
            _sdfTex = new RenderTexture(gx, gy, 0, RenderTextureFormat.RFloat)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = gz,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            _sdfTex.Create();

            _jfaBufferA = new ComputeBuffer(totalVoxels, sizeof(uint));
            _jfaBufferB = new ComputeBuffer(totalVoxels, sizeof(uint));
        }
    }

    void UploadStaticTriangles()
    {
        _staticTriCount = _staticTriList.Count;
        if (_staticTriCount == 0) return;

        if (_staticTriBuffer != null && _staticTriBuffer.count != _staticTriCount)
        { _staticTriBuffer.Release(); _staticTriBuffer = null; }
        if (_staticTriBuffer == null)
            _staticTriBuffer = new ComputeBuffer(_staticTriCount, Marshal.SizeOf(typeof(Tri)));

        _staticTriBuffer.SetData(_staticTriList);
    }

    void UploadDynamicTriangles()
    {
        _dynamicTriCount = _dynamicTriList.Count;
        if (_dynamicTriCount == 0)
        {
            //Release to save memory when no dynamic objects
            if (_dynamicTriBuffer != null) { _dynamicTriBuffer.Release(); _dynamicTriBuffer = null; }
            return;
        }

        if (_dynamicTriBuffer != null && _dynamicTriBuffer.count < _dynamicTriCount)
        { _dynamicTriBuffer.Release(); _dynamicTriBuffer = null; }
        if (_dynamicTriBuffer == null)
            _dynamicTriBuffer = new ComputeBuffer(_dynamicTriCount, Marshal.SizeOf(typeof(Tri)));

        _dynamicTriBuffer.SetData(_dynamicTriList);
    }

    void ReleaseBuffers()
    {
        if (_voxelBuffer != null) { _voxelBuffer.Release(); _voxelBuffer = null; }
        if (_staticVoxelBuf != null) { _staticVoxelBuf.Release(); _staticVoxelBuf = null; }
        if (_jfaBufferA != null) { _jfaBufferA.Release(); _jfaBufferA = null; }
        if (_jfaBufferB != null) { _jfaBufferB.Release(); _jfaBufferB = null; }
    }

    void ReleaseTextures()
    {
        if (_fillTex != null) { _fillTex.Release(); Destroy(_fillTex); _fillTex = null; }
        if (_blurredFillTex != null) { _blurredFillTex.Release(); Destroy(_blurredFillTex); _blurredFillTex = null; }
        if (_normalsTex != null) { _normalsTex.Release(); Destroy(_normalsTex); _normalsTex = null; }
        if (_temperatureTex != null) { _temperatureTex.Release(); Destroy(_temperatureTex); _temperatureTex = null; }
        if (_diffusivityTex != null) { _diffusivityTex.Release(); Destroy(_diffusivityTex); _diffusivityTex = null; }
        if (_phaseTex != null) { _phaseTex.Release(); Destroy(_phaseTex); _phaseTex = null; }
        if (_heatSourceTex != null) { _heatSourceTex.Release(); Destroy(_heatSourceTex); _heatSourceTex = null; }
        if (_sdfTex != null) { _sdfTex.Release(); Destroy(_sdfTex); _sdfTex = null; }
    }

    void ReleaseTriBuffers()
    {
        if (_staticTriBuffer != null) { _staticTriBuffer.Release(); _staticTriBuffer = null; }
        if (_dynamicTriBuffer != null) { _dynamicTriBuffer.Release(); _dynamicTriBuffer = null; }
        _staticTriCount = 0;
        _dynamicTriCount = 0;
    }

    void ReleaseAll()
    {
        //wait for any in-flight async readback to avoid touching freed memory
        //from a callback after the buffer has been released.
        if (_voxelReadbackPending)
        {
            _voxelReadbackRequest.WaitForCompletion();
            _voxelReadbackPending = false;
        }
        _voxelSnapshotValid = false;

        ReleaseBuffers();
        ReleaseTextures();
        ReleaseTriBuffers();
        if (_materialSourceBuffer != null) { _materialSourceBuffer.Release(); _materialSourceBuffer = null; }
        if (_heatSourceMaterialBuffer != null) { _heatSourceMaterialBuffer.Release(); _heatSourceMaterialBuffer = null; }
        if (_bakedMesh != null) { Destroy(_bakedMesh); _bakedMesh = null; }
    }


    //SDF computation (Jump Flood Algorithm)


    //compute a signed distance field from the fill texture using JFA.
    //runs over the full grid (not region-scoped) since JFA is a global algorithm.
    void ComputeSDFJumpFlood(int gx, int gy, int gz)
    {
        if (_jfaBufferA == null || _jfaBufferB == null || _sdfTex == null) return;

        //1) Seed: surface voxels -> self-reference, others -> 0xFFFFFFFF
        coreCS.SetTexture(KSDFSeed, "_FillTex", _fillTex);
        coreCS.SetBuffer(KSDFSeed, "_JFABuffer", _jfaBufferA);
        Dispatch3D(KSDFSeed, gx, gy, gz);

        //2) JFA steps: starting from half the max dimension, halving each step
        int maxDim = Mathf.Max(gx, Mathf.Max(gy, gz));
        int step = Mathf.Max(1, Mathf.NextPowerOfTwo(maxDim) / 2);
        bool pingToA = false; //seed is in A, first step reads A writes B

        while (step >= 1)
        {
            coreCS.SetInt("_JFAStepSize", step);

            //Read from current source, write to current dest
            ComputeBuffer src = pingToA ? _jfaBufferB : _jfaBufferA;
            ComputeBuffer dst = pingToA ? _jfaBufferA : _jfaBufferB;

            coreCS.SetBuffer(KSDFJumpFlood, "_JFABuffer", src);
            coreCS.SetBuffer(KSDFJumpFlood, "_JFABufferB", dst);
            Dispatch3D(KSDFJumpFlood, gx, gy, gz);

            pingToA = !pingToA;
            step /= 2;
        }

        //3) Finalize: convert JFA result -> signed distance in world units
        //Result buffer is whichever was last written to
        ComputeBuffer resultBuf = pingToA ? _jfaBufferA : _jfaBufferB;
        coreCS.SetBuffer(KSDFFinalize, "_JFABuffer", resultBuf);
        coreCS.SetTexture(KSDFFinalize, "_FillTex", _fillTex);
        coreCS.SetTexture(KSDFFinalize, "_SDFTex", _sdfTex);
        Dispatch3D(KSDFFinalize, gx, gy, gz);

        //4) Compute normals from SDF gradient (always, since SDF is on)
        if (_normalsTex != null)
        {
            coreCS.SetTexture(KComputeSDFNormals, "_FillTex", _fillTex);
            coreCS.SetTexture(KComputeSDFNormals, "_SDFTex", _sdfTex);
            coreCS.SetTexture(KComputeSDFNormals, "_NormalTex", _normalsTex);
            Dispatch3D(KComputeSDFNormals, gx, gy, gz);
        }
    }


    //Material property stamping
    //build the material source list from registered heat sources,
    //fluid sources, and water bodies, then dispatch the GPU stamp kernel.
    void StampMaterialProperties(int gx, int gy, int gz, Vector3Int regMin, Vector3Int regMax)
    {
        if (_temperatureTex == null || _phaseTex == null || _diffusivityTex == null) return;

        BuildMaterialSourceList();

        //If a terrain has VoxelSolidMaterial, use its values as the grid-wide
        //defaults. This avoids a grid-spanning AABB source that would overwrite
        //every other object. Per-object sources then only need to cover their own voxels.
        float tempDefault = defaultSolidTemperature;
        float diffDefault = defaultSolidDiffusivity;
        foreach (var sm in _registeredSolidMaterials)
        {
            if (sm != null && sm.isActiveAndEnabled && sm.GetComponent<Terrain>() != null)
            {
                tempDefault = sm.temperature;
                diffDefault = sm.thermalDiffusivity;
                break;
            }
        }

        //DEBUG: log all material sources being uploaded
        if (Input.GetKey(KeyCode.F3))
        {
            for (int i = 0; i < _materialSourceList.Count; i++)
            {
                var s = _materialSourceList[i];
                Debug.Log($"[VoxelTracer] Source[{i}]: pos={s.position}, ext={s.extents}, " +
                          $"temp={s.temperature:F2}, diff={s.thermalDiffusivity:F4}, " +
                          $"phase={s.phase}, shape={s.shape}");
            }
            Debug.Log($"[VoxelTracer] Default: temp={tempDefault}, diff={diffDefault}, " +
                      $"sources={_materialSourceList.Count}, structSize={Marshal.SizeOf(typeof(MaterialSource))}");
        }

        UploadMaterialSources();

        Vector3Int regSize = regMax - regMin + Vector3Int.one;
        SetRegionMin(regMin.x, regMin.y, regMin.z);

        coreCS.SetFloat("_DefaultSolidTemperature", tempDefault);
        coreCS.SetFloat("_DefaultSolidDiffusivity", diffDefault);
        coreCS.SetInt("_MaterialSourceCount", _materialSourceList.Count);
        coreCS.SetTexture(KWriteMaterialProperties, "_FillTex", _fillTex);
        coreCS.SetTexture(KWriteMaterialProperties, "_TemperatureTex", _temperatureTex);
        coreCS.SetTexture(KWriteMaterialProperties, "_DiffusivityTex", _diffusivityTex);
        coreCS.SetTexture(KWriteMaterialProperties, "_PhaseTex", _phaseTex);
        if (_materialSourceBuffer != null)
            coreCS.SetBuffer(KWriteMaterialProperties, "_MaterialSources", _materialSourceBuffer);

        Dispatch3D(KWriteMaterialProperties, regSize.x, regSize.y, regSize.z);
    }
    //
    //Writes the HeatSourceTexture for the given dirty region.
    //The texture is first cleared to 0 across the region (so sources that
    //moved or deactivated since last frame don't leave stale hot spots).
    //Then every active VoxelHeatSource stamps its temperature value into the
    //voxels it overlaps.
    //
    void StampHeatSources(int gx, int gy, int gz, Vector3Int regMin, Vector3Int regMax)
    {
        if (_heatSourceTex == null) return;

        var heatSourceEntries = new List<MaterialSource>(8);

        //Stamp VoxelSolidMaterial objects flagged as permanent heat sources
        foreach (var sm in _registeredSolidMaterials)
        {
            if (sm == null || !sm.isActiveAndEnabled || !sm.isContinuousHeatSource) continue;
            if (sm.GetComponent<Terrain>() != null) continue;

            var r = sm.GetComponent<Renderer>();
            if (r != null)
            {
                heatSourceEntries.Add(new MaterialSource
                {
                    position = r.bounds.center,
                    extents = r.bounds.extents + Vector3.one * voxelSize,
                    temperature = sm.temperature,
                    thermalDiffusivity = 0f,
                    phase = 0f,
                    shape = 0
                });
            }
            else
            {
                heatSourceEntries.Add(new MaterialSource
                {
                    position = sm.transform.position,
                    extents = Vector3.one * 0.5f,
                    temperature = sm.temperature,
                    thermalDiffusivity = 0f,
                    phase = 0f,
                    shape = 1
                });
            }
        }

        //Always ensure the GPU buffer is valid and contains current data,
        //even when count == 0 (kernel still runs to clear the texture region).
        int count = heatSourceEntries.Count;
        if (_heatSourceMaterialBuffer == null || _heatSourceMaterialBuffer.count < Mathf.Max(1, count))
        {
            _heatSourceMaterialBuffer?.Release();
            _heatSourceMaterialBuffer = new ComputeBuffer(Mathf.Max(1, count), Marshal.SizeOf(typeof(MaterialSource)));
        }
        if (count > 0)
            _heatSourceMaterialBuffer.SetData(heatSourceEntries);

        Vector3Int regSize = regMax - regMin + Vector3Int.one;
        SetRegionMin(0, 0, 0);

        coreCS.SetInt("_MaterialSourceCount", count);   //correctly 0 when no sources
        coreCS.SetTexture(KWriteHeatSources, "_FillTex", _fillTex);
        coreCS.SetTexture(KWriteHeatSources, "_HeatSourceTex", _heatSourceTex);
        coreCS.SetBuffer(KWriteHeatSources, "_MaterialSources", _heatSourceMaterialBuffer);
        Dispatch3D(KWriteHeatSources, gx, gy, gz);
    }
    void BuildMaterialSourceList()
    {
        _materialSourceList.Clear();

        //Half-voxel padding: the SAT voxelizer marks voxels as filled up to
        //_HalfUnit beyond the mesh surface, so Renderer.bounds doesn't fully
        //cover the voxelized shell. Safe now that terrain uses defaults.
        Vector3 halfVoxelPad = Vector3.one * (voxelSize * 0.5f);

        //---- Priority order (lowest first, last wins): ----
        //Terrain VoxelSolidMaterial is not added as a source; it sets the
        //grid-wide defaults in StampMaterialProperties() instead, so it
        //cannot cross-contaminate other objects.
        //1. Non-terrain solid materials (per-object bounds)
        //2. Heat sources
        //3. Water bodies
        //4. Fluid sources (highest priority)

        //1) Non-terrain solid materials
        foreach (var sm in _registeredSolidMaterials)
        {
            if (sm == null || !sm.isActiveAndEnabled) continue;
            if (sm.GetComponent<Terrain>() != null) continue; //handled via defaults

            var r = sm.GetComponent<Renderer>();
            if (r != null)
            {
                _materialSourceList.Add(new MaterialSource
                {
                    position = r.bounds.center,
                    extents = r.bounds.extents + Vector3.one * voxelSize,
                    temperature = sm.temperature,
                    thermalDiffusivity = sm.thermalDiffusivity,
                    phase = 0f,
                    shape = 0
                });
            }
            else
            {
                _materialSourceList.Add(new MaterialSource
                {
                    position = sm.transform.position,
                    extents = Vector3.one * 0.5f,
                    temperature = sm.temperature,
                    thermalDiffusivity = sm.thermalDiffusivity,
                    phase = 0f,
                    shape = 1
                });
            }
        }

        //2) Heat sources
        //foreach (var hs in _registeredHeatSources)
        //{
        //    if (hs == null || !hs.isActiveAndEnabled || !hs.active) continue;

        //    if (hs.radius > 0f)
        //    {
        //        _materialSourceList.Add(new MaterialSource
        //        {
        //            position = hs.transform.position,
        //            extents = Vector3.one * hs.radius,
        //            temperature = hs.temperature,
        //            thermalDiffusivity = 0f,
        //            phase = 0f,
        //            shape = 1
        //        });
        //    }
        //    else
        //    {
        //        var r = hs.GetComponent<Renderer>();
        //        if (r != null)
        //        {
        //            _materialSourceList.Add(new MaterialSource
        //            {
        //                position = r.bounds.center,
        //                extents = r.bounds.extents + Vector3.one * voxelSize,
        //                temperature = hs.temperature,
        //                thermalDiffusivity = 0f,
        //                phase = 0f,
        //                shape = 0
        //            });
        //        }
        //        else
        //        {
        //            _materialSourceList.Add(new MaterialSource
        //            {
        //                position = hs.transform.position,
        //                extents = Vector3.one * 0.5f,
        //                temperature = hs.temperature,
        //                thermalDiffusivity = 0f,
        //                phase = 0f,
        //                shape = 1
        //            });
        //        }
        //    }
        //}

        //4) Water bodies
        foreach (var wb in _registeredWaterBodies)
        {
            if (wb == null || !wb.isActiveAndEnabled) continue;
            var fm = wb.GetComponent<VoxelFluidMaterial>();
            float temp = fm != null ? fm.temperature : wb.initialTemperature;
            float diff = fm != null ? fm.thermalDiffusivity : 0f;
            float phase = fm != null ? (float)fm.phase : 1f;
            _materialSourceList.Add(new MaterialSource
            {
                position = wb.transform.position,
                extents = wb.size * 0.5f,
                temperature = temp,
                thermalDiffusivity = diff,
                phase = phase,
                shape = 0   //AABB
            });
        }

        //Fluid sources: spherical emission volumes marked as fluid
        //Use VoxelFluidMaterial temperature if attached, otherwise fallback to initialTemperature
        foreach (var fs in _registeredFluidSources)
        {
            if (fs == null || !fs.isActiveAndEnabled) continue;
            var fm = fs.GetComponent<VoxelFluidMaterial>();
            float temp = fm != null ? fm.temperature : fs.initialTemperature;
            float diff = fm != null ? fm.thermalDiffusivity : 0f;
            float phase = fm != null ? (float)fm.phase : 1f;
            _materialSourceList.Add(new MaterialSource
            {
                position = fs.transform.position,
                extents = Vector3.one * fs.emissionRadius,
                temperature = temp,
                thermalDiffusivity = diff,
                phase = phase,
                shape = 1   //sphere
            });
        }
    }

    void UploadMaterialSources()
    {
        int count = _materialSourceList.Count;
        if (count == 0)
        {
            //Need at least a 1-element buffer to bind (GPU requires valid buffer)
            if (_materialSourceBuffer == null)
                _materialSourceBuffer = new ComputeBuffer(1, Marshal.SizeOf(typeof(MaterialSource)));
            return;
        }

        if (_materialSourceBuffer != null && _materialSourceBuffer.count < count)
        { _materialSourceBuffer.Release(); _materialSourceBuffer = null; }
        if (_materialSourceBuffer == null)
            _materialSourceBuffer = new ComputeBuffer(count, Marshal.SizeOf(typeof(MaterialSource)));

        _materialSourceBuffer.SetData(_materialSourceList);
    }


    //Dispatch helpers


    void Dispatch3D(int kernel, int gx, int gy, int gz)
    {
        if (gx <= 0 || gy <= 0 || gz <= 0) return;
        coreCS.GetKernelThreadGroupSizes(kernel, out uint tx, out uint ty, out uint tz);
        coreCS.Dispatch(kernel,
            Mathf.Max(1, Mathf.CeilToInt(gx / (float)tx)),
            Mathf.Max(1, Mathf.CeilToInt(gy / (float)ty)),
            Mathf.Max(1, Mathf.CeilToInt(gz / (float)tz)));
    }

    void Dispatch2D(int kernel, int gx, int gy)
    {
        if (gx <= 0 || gy <= 0) return;
        coreCS.GetKernelThreadGroupSizes(kernel, out uint tx, out uint ty, out _);
        coreCS.Dispatch(kernel,
            Mathf.CeilToInt(gx / (float)tx),
            Mathf.CeilToInt(gy / (float)ty),
            1);
    }

    void DispatchSweep(int axis, int planeA, int planeB)
    {
        coreCS.SetInt("_SweepAxis", axis);
        Dispatch2D(KSweepFill, planeA, planeB);
    }

    void DispatchSweepRegion(int axis, Vector3Int regMin, Vector3Int regMax)
    {
        coreCS.SetInt("_SweepAxis", axis);
        SetRegionMin(regMin.x, regMin.y, regMin.z);
        int planeA, planeB;
        if (axis == 0) { planeA = regMax.y - regMin.y + 1; planeB = regMax.z - regMin.z + 1; }
        else if (axis == 1) { planeA = regMax.x - regMin.x + 1; planeB = regMax.z - regMin.z + 1; }
        else { planeA = regMax.x - regMin.x + 1; planeB = regMax.y - regMin.y + 1; }
        Dispatch2D(KSweepFill, planeA, planeB);
    }

    void DispatchLinear(int kernel, int count)
    {
        if (count <= 0) return;
        coreCS.GetKernelThreadGroupSizes(kernel, out uint tx, out _, out _);
        coreCS.Dispatch(kernel, Mathf.CeilToInt(count / (float)tx), 1, 1);
    }


    //Editor gizmos


    void OnDrawGizmosSelected()
    {
        if (boundsMode == BoundsMode.Manual)
        {
            Gizmos.color = new Color(1, 1, 0, 0.3f);
            Vector3 s = gridMax - gridMin;
            Gizmos.DrawWireCube(gridMin + s * 0.5f, s);
        }
        else if (_nx > 0 && Application.isPlaying)
        {
            Gizmos.color = new Color(0, 1, 1, 0.3f);
            Vector3 s = _activeMax - _activeMin;
            Gizmos.DrawWireCube(_activeMin + s * 0.5f, s);
        }
    }


    //Debug: temperature & diffusivity readback


    //Read back a 3D RFloat RenderTexture into a flat float array (CPU-side).
    static float[] ReadBack3DTexture(RenderTexture rt, int nx, int ny, int nz)
    {
        float[] data = new float[nx * ny * nz];
        var tempRT = RenderTexture.GetTemporary(nx, ny, 0, RenderTextureFormat.RFloat);
        var tempTex = new Texture2D(nx, ny, TextureFormat.RFloat, false);

        for (int z = 0; z < nz; z++)
        {
            Graphics.CopyTexture(rt, z, 0, tempRT, 0, 0);
            var prev = RenderTexture.active;
            RenderTexture.active = tempRT;
            tempTex.ReadPixels(new Rect(0, 0, nx, ny), 0, 0, false);
            tempTex.Apply(false);
            RenderTexture.active = prev;

            var raw = tempTex.GetRawTextureData<float>();
            for (int i = 0; i < nx * ny; i++)
                data[z * (nx * ny) + i] = raw[i];
        }

        RenderTexture.ReleaseTemporary(tempRT);
        Destroy(tempTex);
        return data;
    }

    //
    //Logs a summary of temperature and diffusivity textures to the console.
    //Shows min/max/avg and lists all non-zero voxels (capped to avoid console flood).
    //Call from inspector context menu or script: GetComponent&lt;VoxelTracerSystem&gt;().DebugPrintMaterialTextures();
    //
    [ContextMenu("Debug Print Temperature & Diffusivity")]
    public void DebugPrintMaterialTextures()
    {
        if (_temperatureTex == null || _diffusivityTex == null)
        {
            Debug.LogWarning("[VoxelTracer] Textures not allocated yet.");
            return;
        }

        int nx = _nx, ny = _ny, nz = _nz;
        float[] tempData = ReadBack3DTexture(_temperatureTex, nx, ny, nz);
        float[] diffData = ReadBack3DTexture(_diffusivityTex, nx, ny, nz);

        float tMin = float.MaxValue, tMax = float.MinValue, tSum = 0f;
        float dMin = float.MaxValue, dMax = float.MinValue, dSum = 0f;
        int nonZeroTemp = 0, nonZeroDiff = 0;
        int total = nx * ny * nz;

        for (int i = 0; i < total; i++)
        {
            float t = tempData[i];
            float d = diffData[i];
            if (t < tMin) tMin = t;
            if (t > tMax) tMax = t;
            tSum += t;
            if (t != 0f) nonZeroTemp++;

            if (d < dMin) dMin = d;
            if (d > dMax) dMax = d;
            dSum += d;
            if (d != 0f) nonZeroDiff++;
        }

        Debug.Log($"[VoxelTracer] Grid {nx}x{ny}x{nz} = {total} voxels\n" +
                  $"  Temperature ; min: {tMin:F3}, max: {tMax:F3}, avg: {tSum / total:F3}, non-zero: {nonZeroTemp}\n" +
                  $"  Diffusivity ; min: {dMin:F3}, max: {dMax:F3}, avg: {dSum / total:F3}, non-zero: {nonZeroDiff}");

        //Print up to 50 sample non-zero voxels
        int logged = 0;
        const int maxSamples = 50;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[VoxelTracer] Non-zero voxel samples (x,y,z) -> temp, diff:");

        for (int idx = 0; idx < total && logged < maxSamples; idx++)
        {
            if (tempData[idx] == 0f && diffData[idx] == 0f) continue;
            int x = idx % nx;
            int y = (idx / nx) % ny;
            int z = idx / (nx * ny);
            sb.AppendLine($"  ({x},{y},{z}) -> temp={tempData[idx]:F2}, diff={diffData[idx]:F4}");
            logged++;
        }

        if (logged == 0)
            sb.AppendLine("  (none)");
        else if (nonZeroTemp > maxSamples || nonZeroDiff > maxSamples)
            sb.AppendLine($"  ... ({Mathf.Max(nonZeroTemp, nonZeroDiff) - maxSamples} more)");

        Debug.Log(sb.ToString());
    }
}
