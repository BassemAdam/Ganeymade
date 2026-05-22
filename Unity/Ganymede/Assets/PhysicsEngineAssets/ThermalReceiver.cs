using UnityEngine;
using System.Collections;
using System;
using System.Runtime.InteropServices;
using UnityEngine.Rendering;
using System.Collections.Generic;


public class ThermalReceiver : MonoBehaviour
{
    // ─── Native plugin imports ──────────────────────────────────────────

    [DllImport("RenderingPlugin")]
    private static extern void SetSolidComputeData(float[] data, int count);

    [DllImport("RenderingPlugin")]
    private static extern void SetMaskData(uint[] mask, int count);

    [DllImport("RenderingPlugin")]
    private static extern void SetSolidSimParams(float dt, uint gridWidth, uint gridHeight, uint gridDepth);

    [DllImport("RenderingPlugin")]
    private static extern void GetSolidComputeResult(float[] outData, int count);

    [DllImport("RenderingPlugin")]
    private static extern IntPtr GetRenderEventFunc();

    [DllImport("RenderingPlugin")]
    private static extern void SetDiffusivityData(float[] data, int count);

    [DllImport("RenderingPlugin")]
    private static extern void SetHeatSourceData(float[] pinTemperatures, int count);

    // ─── Configuration ──────────────────────────────────────────────────

    [Header("Temperature Visualization")]
    public float maxDisplayTemp = 300f;
    public float minDisplayTemp = 70f;
    public float defaultAmbientTemp = 70f;
    [Range(1, 10)] public int visualizationInterval = 2;

    [Header("Debug")]
    [Tooltip("Log diffusion stats every N frames. 0 = disabled.")]
    public int debugLogInterval = 60;

    [Tooltip("Enable verbose logging. Disable to suppress all info/warning logs.")]
    public bool verbose = true;

    [Header("Voxel Source")]
    public VoxelTracerSystem voxelTracer;

    [Header("Voxel Tracer")]
    public VoxelTracerCamera voxelTracerCamera;


    // --------------- private variables ----------

    private float[] gridData;
    private uint[] maskData;
    private float[] readbackData;
    private float[] diffusivityData;
    private float[] heatSourceData;
    private int frameCount = 0;
    private bool initialized = false;
    private Texture3D _tempTexture;

    // For diffusion-change detection
    private float _prevMaxTemp = float.NegativeInfinity;
    private float _prevMinTemp = float.PositiveInfinity;
    private float _prevAvgTemp = float.NegativeInfinity;
    private bool _refreshingHeatSources = false;

    private int gridWidth, gridHeight, gridDepth;

    // Tracks the set of sources baked into heatSourceData so we can detect changes and re-upload only when necessary.
    private List<HeatSourceSnapshot> _trackedSourceSnapshots = new List<HeatSourceSnapshot>();

    // Tracks the VoxelTracerSystem voxelize-frame counter so any dynamic-object
    // re-voxelization also triggers a GPU refresh
    private int _lastVoxelizeFrameCount = -1;
    private int _settleFramesRemaining = 0;

    // ------------------- public variables -------------------------

    // live diffused temperature array (read-only)
    // Null until initialised.
    public float[] LiveTemperatureData => initialized ? readbackData : null;
    public bool IsInitialized => initialized;
    public Texture3D tempTexture => _tempTexture;

    // --------- Lifecycle ---------------------------------------

    IEnumerator Start()
    {
        yield return new WaitUntil(() => voxelTracer != null && voxelTracer.IsReady);

        // Wait for at least one voxelization pass so textures have valid data
        yield return new WaitUntil(() => voxelTracer.VoxelizeFrameCount > 0);

        gridWidth = voxelTracer.Nx;
        gridHeight = voxelTracer.Ny;
        gridDepth = voxelTracer.Nz;

        int totalCells = gridWidth * gridHeight * gridDepth;
        gridData = new float[totalCells];
        maskData = new uint[totalCells];
        readbackData = new float[totalCells];
        diffusivityData = new float[totalCells];
        heatSourceData = new float[totalCells];

        Array.Fill(readbackData, defaultAmbientTemp);

        InitVisualization();

        yield return StartCoroutine(ReadTexturesAsync());
        DebugTextureReadback();

        SetSolidComputeData(gridData, totalCells);
        SetMaskData(maskData, totalCells);
        SetDiffusivityData(diffusivityData, totalCells);
        SetHeatSourceData(heatSourceData, totalCells);

        float dt = Time.deltaTime;

        SetSolidSimParams(dt, (uint)gridWidth, (uint)gridHeight, (uint)gridDepth);

        yield return new WaitForEndOfFrame();
        GL.IssuePluginEvent(GetRenderEventFunc(), 4);

        SnapshotCurrentSources();
        _lastVoxelizeFrameCount = voxelTracer.VoxelizeFrameCount;

        initialized = true;
    }

    void Update()
    {
        if (!initialized) return;

        // Check if the grid size changed 
        if (voxelTracer.Nx != gridWidth || voxelTracer.Ny != gridHeight || voxelTracer.Nz != gridDepth)
        {
            if (verbose) Debug.Log("[ThermalReceiver] Voxel grid changed — reinitializing diffusion.");
            StartCoroutine(Reinitialize());
            return;
        }
        // Re-read HeatSourceTexture whenever sources appear, disappear, move, or change temperature.  
        bool voxelFrameChanged = false;
        if (voxelTracer.VoxelizeFrameCount != _lastVoxelizeFrameCount)
        {
            foreach (var dv in VoxelTracerSystem.DynamicObjects)
            {
                if (dv != null && dv.HasMoved())
                {
                    voxelFrameChanged = true;
                    _settleFramesRemaining = 3; // refresh for 3 extra frames after movement stops
                    break;
                }
            }
        }

        // Drain the settling counter even when nothing is moving anymore
        if (!voxelFrameChanged && _settleFramesRemaining > 0)
        {
            _settleFramesRemaining--;
            voxelFrameChanged = true;
            if (verbose) Debug.Log($"[ThermalReceiver] Post-move settle refresh — {_settleFramesRemaining} remaining.");
        }

        if ((HeatSourcesChanged() || voxelFrameChanged) && !_refreshingHeatSources)
        {
            if (verbose)
            {
                if (voxelFrameChanged)
                    Debug.Log("[ThermalReceiver] Voxel grid updated (dynamic objects) — refreshing GPU data.");
                else
                    Debug.Log("[ThermalReceiver] start heat source refresh");
            }
            StartCoroutine(RefreshSolidData());
        }

        float dt = Time.deltaTime;
        SetSolidSimParams(dt, (uint)gridWidth, (uint)gridHeight, (uint)gridDepth);
        GL.IssuePluginEvent(GetRenderEventFunc(), 4);
        frameCount++;

        if (frameCount % visualizationInterval == 0)
            UpdateVisualization();

        // Periodic diffusion health log 
        if (debugLogInterval > 0 && frameCount % debugLogInterval == 0)
            LogDiffusionStats(dt);
    }

    private IEnumerator ReadTexturesAsync()
    {
        int gx = voxelTracer.Nx;
        int gy = voxelTracer.Ny;
        int gz = voxelTracer.Nz;
        int sliceSize = gx * gy;

        // read fill texture to mask data
        {
            var req = AsyncGPUReadback.Request(voxelTracer.FillTexture);
            yield return new WaitUntil(() => req.done);
            if (req.hasError)
            {
                Debug.LogError("[ThermalReceiver] FillTexture readback error");
            }
            else
            {
                int slices = req.layerCount > 0 ? req.layerCount : gz;
                for (int z = 0; z < gz && z < slices; z++)
                {
                    var slice = req.GetData<float>(z);
                    int n = Mathf.Min(slice.Length, sliceSize);
                    int dstBase = z * sliceSize;
                    for (int i = 0; i < n; i++)
                        maskData[dstBase + i] = slice[i] > 0.5f ? 1u : 0u;
                }
            }
        }

        // read emperature texture to grid data
        {
            var req = AsyncGPUReadback.Request(voxelTracer.TemperatureTexture);
            yield return new WaitUntil(() => req.done);
            if (req.hasError)
            {
                Debug.LogError("[ThermalReceiver] TemperatureTexture readback error");
            }
            else
            {
                int slices = req.layerCount > 0 ? req.layerCount : gz;
                for (int z = 0; z < gz && z < slices; z++)
                {
                    var slice = req.GetData<float>(z);
                    int n = Mathf.Min(slice.Length, sliceSize);
                    int dstBase = z * sliceSize;
                    for (int i = 0; i < n; i++)
                        gridData[dstBase + i] = slice[i];
                }
            }
        }

        // read diffusivity texture tp diffusivity data 
        {
            var req = AsyncGPUReadback.Request(voxelTracer.DiffusivityTexture);
            yield return new WaitUntil(() => req.done);
            if (req.hasError)
            {
                Debug.LogError("[ThermalReceiver] DiffusivityTexture readback error");
            }
            else
            {
                int slices = req.layerCount > 0 ? req.layerCount : gz;
                for (int z = 0; z < gz && z < slices; z++)
                {
                    var slice = req.GetData<float>(z);
                    int n = Mathf.Min(slice.Length, sliceSize);
                    int dstBase = z * sliceSize;
                    for (int i = 0; i < n; i++)
                        diffusivityData[dstBase + i] = Mathf.Clamp(slice[i], 0f, 30f);
                }
            }
        }

        // read HeatSourceTexture to heatSource data
        if (voxelTracer.HeatSourceTexture != null)
        {
            var req = AsyncGPUReadback.Request(voxelTracer.HeatSourceTexture);
            yield return new WaitUntil(() => req.done);
            if (req.hasError)
            {
                Debug.LogError("[ThermalReceiver] HeatSourceTexture readback error");
            }
            else
            {
                int slices = req.layerCount > 0 ? req.layerCount : gz;
                for (int z = 0; z < gz && z < slices; z++)
                {
                    var slice = req.GetData<float>(z);
                    int n = Mathf.Min(slice.Length, sliceSize);
                    int dstBase = z * sliceSize;
                    for (int i = 0; i < n; i++)
                        heatSourceData[dstBase + i] = slice[i];
                }
            }
        }
        else
        {
            if (verbose) Debug.LogWarning("[ThermalReceiver] HeatSourceTexture is null — " +
                             "no heat sources will be pinned.  " +
                             "Apply the VoxelTracerSystem patch to generate this texture.");
            Array.Clear(heatSourceData, 0, heatSourceData.Length);
        }

        if (verbose) Debug.Log("[ThermalReceiver] Texture readback complete. Uploading data to diffusion shader.");

        SetDiffusivityData(diffusivityData, gx * gy * gz);
    }
    //Re-reads only the HeatSourceTexture and re-uploads heatSourceData.
    private IEnumerator RefreshSolidData()
    {
        if (_refreshingHeatSources) 
            yield break;
        _refreshingHeatSources = true;

        yield return null;

        int gx = voxelTracer.Nx, gy = voxelTracer.Ny, gz = voxelTracer.Nz;
        int sliceSize = gx * gy;
        int total = gx * gy * gz;

        // Re-read fill texture to maskData
        var fillReq = AsyncGPUReadback.Request(voxelTracer.FillTexture);
        yield return new WaitUntil(() => fillReq.done);
        if (!fillReq.hasError)
        {
            int slices = fillReq.layerCount > 0 ? fillReq.layerCount : gz;
            for (int z = 0; z < gz && z < slices; z++)
            {
                var slice = fillReq.GetData<float>(z);
                int n = Mathf.Min(slice.Length, sliceSize);
                int dstBase = z * sliceSize;
                for (int i = 0; i < n; i++)
                    maskData[dstBase + i] = slice[i] > 0.5f ? 1u : 0u;
            }
        }

        // Re-read diffusivity texture to diffusivityData
        var diffReq = AsyncGPUReadback.Request(voxelTracer.DiffusivityTexture);
        yield return new WaitUntil(() => diffReq.done);
        if (!diffReq.hasError)
        {
            int slices = diffReq.layerCount > 0 ? diffReq.layerCount : gz;
            for (int z = 0; z < gz && z < slices; z++)
            {
                var slice = diffReq.GetData<float>(z);
                int n = Mathf.Min(slice.Length, sliceSize);
                int dstBase = z * sliceSize;
                for (int i = 0; i < n; i++)
                    diffusivityData[dstBase + i] = Mathf.Clamp(slice[i], 0f, 30f);
            }
        }

        // Re-read heat source texture to heatSourceData
        if (voxelTracer.HeatSourceTexture != null)
        {
            var hsReq = AsyncGPUReadback.Request(voxelTracer.HeatSourceTexture);
            yield return new WaitUntil(() => hsReq.done);
            if (!hsReq.hasError)
            {
                int slices = hsReq.layerCount > 0 ? hsReq.layerCount : gz;
                for (int z = 0; z < gz && z < slices; z++)
                {
                    var slice = hsReq.GetData<float>(z);
                    int n = Mathf.Min(slice.Length, sliceSize);
                    int dstBase = z * sliceSize;
                    for (int i = 0; i < n; i++)
                        heatSourceData[dstBase + i] = slice[i];
                }
            }
        }

        int pinnedAfterReadback = 0;
        foreach (float v in heatSourceData) 
            if (v > 0f) 
                pinnedAfterReadback++;
        // if (verbose)
        //     Debug.Log($"[ThermalReceiver] [STAGE 1] After HeatSourceTexture GPU readback — pinnedVoxels={pinnedAfterReadback}");

        //preserve the existing temps for fixed solid voxels, seed new solid voxels at ambient
        GetSolidComputeResult(readbackData, total);
        for (int i = 0; i < total; i++)
            gridData[i] = maskData[i] == 1u ? (readbackData[i] > 0f ? readbackData[i] : defaultAmbientTemp): 0f;

        // Upload the three textures to the native plugin
        SetSolidComputeData(gridData, total);
        SetMaskData(maskData, total);
        SetDiffusivityData(diffusivityData, total);

        int pinnedBeforeUpload = 0;
        foreach (float v in heatSourceData) 
            if (v > 0f) 
                pinnedBeforeUpload++;
        // if (verbose)
        //     Debug.Log($"[ThermalReceiver] [STAGE 2] About to call SetHeatSourceData — pinnedVoxels={pinnedBeforeUpload}");

        SetHeatSourceData(heatSourceData, total);

        SnapshotCurrentSources();
        _lastVoxelizeFrameCount = voxelTracer.VoxelizeFrameCount;
        _refreshingHeatSources = false;

        if (verbose)
        {
            int pinnedCount = 0;
            foreach (float v in heatSourceData) 
                if (v > 0f) 
                    pinnedCount++;
            int solidCount = 0;
            foreach (uint v in maskData) 
                if (v > 0u) 
                    solidCount++;
            Debug.Log($"[ThermalReceiver] Solid data refreshed — solidVoxels={solidCount}  pinnedVoxels={pinnedCount}");
        }
    }

    // ─── Change detection ────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the registered heat sources differ from the last snapshot.
    /// Checks: count, instance IDs, temperatures, active state, positions, radii.
    /// </summary>
    private bool HeatSourcesChanged()
    {
        var sources = VoxelTracerSystem.SolidMaterials;

        // Count all active solid materials, not just heat sources in case a material moves 
        int count = 0;
        foreach (var sm in sources)
            if (sm != null && sm.isActiveAndEnabled) 
                count++;

        if ( verbose && count != _trackedSourceSnapshots.Count) 
        {
            //Debug.Log($"[ThermalReceiver] [CHANGED] Source count changed: tracked={_trackedSourceSnapshots.Count} current={count}");
            return true;
        }

        var snapshotMap = new Dictionary<int, HeatSourceSnapshot>(_trackedSourceSnapshots.Count);
        foreach (var snap in _trackedSourceSnapshots)
            snapshotMap[snap.instanceID] = snap;

        foreach (var sm in sources)
        {
            if (sm == null || !sm.isActiveAndEnabled) 
                continue;
            if (!snapshotMap.TryGetValue(sm.GetInstanceID(), out var snap)) 
                {
                    if (verbose) Debug.Log($"[ThermalReceiver] [CHANGED] New untracked source: {sm.name}");
                    return true;
                }
            if (snap.isContinuousHeatSource != sm.isContinuousHeatSource) 
                {
                    if (verbose) Debug.Log($"[ThermalReceiver] [CHANGED] isContinuousHeatSource toggled on {sm.name}");
                    return true;
                }
            if (!Mathf.Approximately(snap.temperature, sm.temperature)) 
                {
                    if (verbose) Debug.Log($"[ThermalReceiver] [CHANGED] Temperature changed on {sm.name}: {snap.temperature} to {sm.temperature}");
                    return true;
                }

            // Check movement via renderer bounds center 
            var r = sm.GetComponent<Renderer>();
            Vector3 currentCenter = r != null ? r.bounds.center : sm.transform.position;
            if ((snap.boundsCenter - currentCenter).sqrMagnitude > 1e-6f) 
               {
                    //Debug.Log($"[ThermalReceiver] [CHANGED] Position changed on {sm.name}: {snap.boundsCenter} to {currentCenter}");
                    return true;
                }
        }
        return false;
    }

    private void SnapshotCurrentSources()
    {
        _trackedSourceSnapshots.Clear();
        foreach (var sm in VoxelTracerSystem.SolidMaterials)
        {
            if (sm == null || !sm.isActiveAndEnabled) 
                continue;
            var r = sm.GetComponent<Renderer>();
            _trackedSourceSnapshots.Add(new HeatSourceSnapshot
            {
                instanceID = sm.GetInstanceID(),
                temperature = sm.temperature,
                isContinuousHeatSource = sm.isContinuousHeatSource,
                position = sm.transform.position,
                boundsCenter = r != null ? r.bounds.center : sm.transform.position,
            });
        }
    }

    private IEnumerator Reinitialize()
    {
        initialized = false;

        //save  the current temperatures before wiping arrays to read back whatever the GPU has to reseed after resize.
        float[] oldReadback = null;
        int oldTotal = gridWidth * gridHeight * gridDepth;
        if (readbackData != null && oldTotal > 0)
        {
            oldReadback = new float[oldTotal];
            GetSolidComputeResult(oldReadback, oldTotal);
        }
        int oldWidth = gridWidth;
        int oldHeight = gridHeight;
        int oldDepth = gridDepth;

        gridWidth = voxelTracer.Nx;
        gridHeight = voxelTracer.Ny;
        gridDepth = voxelTracer.Nz;

        int totalCells = gridWidth * gridHeight * gridDepth;
        gridData = new float[totalCells];
        maskData = new uint[totalCells];
        readbackData = new float[totalCells];
        diffusivityData = new float[totalCells];
        heatSourceData = new float[totalCells];

        yield return StartCoroutine(ReadTexturesAsync());
        DebugTextureReadback();

        // reseed gridData to preserve temps where grid coords match
        bool sameSize = gridWidth == oldWidth && gridHeight == oldHeight && gridDepth == oldDepth;
        if (oldReadback != null && sameSize)
        {
            // Grid didn't actually resize (just a re-init ping) hence copy temps directly.
            for (int i = 0; i < totalCells; i++)
                gridData[i] = maskData[i] == 1u? (oldReadback[i] > 0f ? oldReadback[i] : defaultAmbientTemp):0f;
        }
        else if (oldReadback != null)
        {
            // Grid resized , remap by voxel coordinate.
            for (int z = 0; z < gridDepth; z++)
            for (int y = 0; y < gridHeight; y++)
            for (int x = 0; x < gridWidth; x++)
            {
                int newi = z * gridWidth * gridHeight + y * gridWidth + x;
                if (maskData[newi] == 0u) 
                    continue;
                if (x < oldWidth && y < oldHeight && z < oldDepth)
                {
                    int oldi = z * oldWidth * oldHeight + y * oldWidth + x;
                    gridData[newi] = oldReadback[oldi] > 0f ? oldReadback[oldi] : defaultAmbientTemp;
                }
                else
                {
                    gridData[newi] = defaultAmbientTemp;
                }
            }
        }
        else
        {
            // No previous data , seed all solid voxels at ambient.
            for (int i = 0; i < totalCells; i++)
                gridData[i] = maskData[i] == 1u ? defaultAmbientTemp : 0f;
        }
        // ────────────────────────────────────────────────────────────────────

        SetSolidComputeData(gridData, totalCells);
        SetMaskData(maskData, totalCells);
        SetDiffusivityData(diffusivityData, totalCells);
        SetHeatSourceData(heatSourceData, totalCells);
        SetSolidSimParams(0.016f, (uint)gridWidth, (uint)gridHeight, (uint)gridDepth);

        yield return new WaitForEndOfFrame();

        frameCount  = 0;
        _lastVoxelizeFrameCount = voxelTracer.VoxelizeFrameCount;
        initialized = true;
    }

    // ─── Diffusion health logging ────────────────────────────────────────

    /// <summary>
    /// Reads back the current temperature buffer and logs min / max / avg
    /// so you can confirm heat is spreading frame-over-frame.
    /// Also warns if the buffer is all-zero (upload never reached the GPU)
    /// or completely static (diffusion is stalled / shader not running).
    /// </summary>
    private void LogDiffusionStats(float dt)
    {
        GetSolidComputeResult(readbackData, readbackData.Length);

        int pinnedAtLogTime = 0;
        foreach (float v in heatSourceData) 
            if (v > 0f) 
                pinnedAtLogTime++;
        // if (verbose)
        //     Debug.Log($"[ThermalReceiver] [STAGE 3] heatSourceData at log time — pinnedVoxels={pinnedAtLogTime}");

        float minT = float.MaxValue;
        float maxT = float.MinValue;
        double sumT = 0;
        int solidCount = 0;
        int pinnedCount = 0;

        for (int i = 0; i < readbackData.Length; i++)
        {
            if (maskData[i] == 0) continue;
            float t = readbackData[i];
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
            sumT += t;
            solidCount++;
            if (heatSourceData[i] > 0f) pinnedCount++;
        }

        float avgT = solidCount > 0 ? (float)(sumT / solidCount) : 0f;

        // Per-source stats — CPU-side overlap check mirroring WriteHeatSources kernel
        var sources = VoxelTracerSystem.SolidMaterials;
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"[ThermalReceiver] Frame {frameCount}  dt={dt:F4}  " +
                    $"min={minT:F2}  max={maxT:F2}  avg={avgT:F4}  " +
                    $"solidCells={solidCount}/{readbackData.Length}  pinnedVoxels={pinnedCount}");

        foreach (var sm in sources)
        {
            if (sm == null || !sm.isActiveAndEnabled || !sm.isContinuousHeatSource) continue;

            // Reconstruct source bounds exactly as StampHeatSources does
            Vector3 srcPos;
            Vector3 srcExtents;
            bool isSphere;

            var r = sm.GetComponent<Renderer>();
            if (r != null)
            {
                srcPos = r.bounds.center;
                srcExtents = r.bounds.extents + Vector3.one * (voxelTracer.ActiveVoxelSize * 0.5f);
            }
            else
            {
                srcPos = sm.transform.position;
                srcExtents = Vector3.one * 0.5f;
            }
            isSphere = false;

            int pinnedBySource = 0;
            float srcMinT = float.MaxValue;
            float srcMaxT = float.MinValue;
            double srcSumT = 0;
            int srcSolidCount = 0;

            for (int z = 0; z < gridDepth; z++)
                for (int y = 0; y < gridHeight; y++)
                    for (int x = 0; x < gridWidth; x++)
                    {
                        int i = z * gridWidth * gridHeight + y * gridWidth + x;
                        if (maskData[i] == 0) continue;

                        // World position of voxel centre — mirrors the kernel's worldPos calculation
                        Vector3 worldPos = voxelTracer.ActiveGridMin +
                                        (new Vector3(x, y, z) + Vector3.one * 0.5f) * voxelTracer.ActiveVoxelSize;

                        bool inside;
                        if (isSphere)
                        {
                            Vector3 delta = worldPos - srcPos;
                            inside = delta.sqrMagnitude <= srcExtents.x * srcExtents.x;
                        }
                        else
                        {
                            Vector3 d = new Vector3(
                                Mathf.Abs(worldPos.x - srcPos.x) - srcExtents.x,
                                Mathf.Abs(worldPos.y - srcPos.y) - srcExtents.y,
                                Mathf.Abs(worldPos.z - srcPos.z) - srcExtents.z);
                            inside = d.x <= 0f && d.y <= 0f && d.z <= 0f;
                        }

                        if (!inside) continue;

                        pinnedBySource++;

                        // Neighbouring solid voxels that aren't pinned — these show diffusion effect
                        float t = readbackData[i];
                        srcSumT += t;
                        srcSolidCount++;
                        if (t < srcMinT) srcMinT = t;
                        if (t > srcMaxT) srcMaxT = t;
                    }

            // Measure diffusion spread: solid voxels adjacent to this source's pinned region
            // whose temperature is above ambient but not pinned (heat has diffused into them)
            int diffusedNeighbours = 0;
            float diffusedAvg = 0f;
            double diffusedSum = 0;
            float srcAvgT = srcSolidCount > 0 ? (float)(srcSumT / srcSolidCount) : 0f;


            for (int z = 0; z < gridDepth; z++)
                for (int y = 0; y < gridHeight; y++)
                    for (int x = 0; x < gridWidth; x++)
                    {
                        int i = z * gridWidth * gridHeight + y * gridWidth + x;
                        if (maskData[i] == 0) continue;
                        if (heatSourceData[i] > 0f) continue;  // skip pinned voxels

                        float t = readbackData[i];
                        if (pinnedBySource == 0)
                        {
                            sb.AppendLine($" WARNING: 0 pinned voxels found in heatSourceData. " +
                                        $"Source bounds may not overlap any solid voxel, or heatSourceData " +
                                        $"is stale. regionAvg={srcAvgT:F2}° (reading diffused temp, not pinned).");
                            continue;
                        }
                        // Check if any face-neighbour of this voxel is pinned by this source
                        bool adjacentToSource = false;
                        int[] dx = { -1, 1, 0, 0, 0, 0 };
                        int[] dy = { 0, 0, -1, 1, 0, 0 };
                        int[] dz = { 0, 0, 0, 0, -1, 1 };
                        for (int n = 0; n < 6; n++)
                        {
                            int nx = x + dx[n], ny = y + dy[n], nz = z + dz[n];
                            if (nx < 0 || nx >= gridWidth ||
                                ny < 0 || ny >= gridHeight ||
                                nz < 0 || nz >= gridDepth) continue;

                            int ni = nz * gridWidth * gridHeight + ny * gridWidth + nx;
                            if (heatSourceData[ni] <= 0f) continue;

                            // Check if that neighbour belongs to this source
                            Vector3 nWorldPos = voxelTracer.ActiveGridMin +
                                                (new Vector3(nx, ny, nz) + Vector3.one * 0.5f) * voxelTracer.ActiveVoxelSize;
                            bool nInside;
                            if (isSphere)
                            {
                                Vector3 delta = nWorldPos - srcPos;
                                nInside = delta.sqrMagnitude <= srcExtents.x * srcExtents.x;
                            }
                            else
                            {
                                Vector3 d = new Vector3(
                                    Mathf.Abs(nWorldPos.x - srcPos.x) - srcExtents.x,
                                    Mathf.Abs(nWorldPos.y - srcPos.y) - srcExtents.y,
                                    Mathf.Abs(nWorldPos.z - srcPos.z) - srcExtents.z);
                                nInside = d.x <= 0f && d.y <= 0f && d.z <= 0f;
                            }
                            if (nInside) { adjacentToSource = true; break; }
                        }

                        if (!adjacentToSource) continue;
                        diffusedNeighbours++;
                        diffusedSum += t;
                    }
            
            int solidAdjacentToSource = 0;
            int nonSolidAdjacentToSource = 0;
            for (int z = 0; z < gridDepth; z++)
            for (int y = 0; y < gridHeight; y++)
            for (int x = 0; x < gridWidth; x++)
            {
                int i = z * gridWidth * gridHeight + y * gridWidth + x;
                if (heatSourceData[i] <= 0f) continue;          // not a pinned voxel
                int[] dx = { -1,1,0,0,0,0 };
                int[] dy = { 0,0,-1,1,0,0 };
                int[] dz = { 0,0,0,0,-1,1 };
                for (int n = 0; n < 6; n++)
                {
                    int nx = x+dx[n], ny = y+dy[n], nz = z+dz[n];
                    if (nx<0||nx>=gridWidth||ny<0||ny>=gridHeight||nz<0||nz>=gridDepth) continue;
                    int ni = nz*gridWidth*gridHeight + ny*gridWidth + nx;
                    if (heatSourceData[ni] > 0f) continue;      // skip other pinned voxels
                    if (maskData[ni] == 1u) solidAdjacentToSource++;
                    else nonSolidAdjacentToSource++;
                }
            }
            Debug.Log($"[ThermalReceiver] [ADJACENCY] Voxels neighbouring any pinned cell — solid(mask=1)={solidAdjacentToSource}  air(mask=0)={nonSolidAdjacentToSource}");
            diffusedAvg = diffusedNeighbours > 0 ? (float)(diffusedSum / diffusedNeighbours) : 0f;
            sb.AppendLine($"  └ {sm.name} ({(isSphere ? "sphere" : "AABB")})  " +
                        $"pinTemp={sm.temperature:F1}°  pinnedVoxels={pinnedBySource}  " +
                        $"regionAvg={srcAvgT:F2}°  " +
                        $"diffusedNeighbours={diffusedNeighbours}  diffusedAvg={diffusedAvg:F2}°");
        }

        if (verbose) Debug.Log(sb.ToString());
        _prevMaxTemp = maxT;
        _prevAvgTemp = avgT;
    }

    private void DebugTextureReadback()
    {
        int totalCells = gridWidth * gridHeight * gridDepth;

        int filledVoxels = 0;
        int sourceVoxels = 0;
        int filledWithNonZeroDiff = 0;
        int filledWithZeroDiff = 0;
        int filledWithNonZeroTemp = 0;
        int filledWithZeroTemp = 0;

        float minDiff = float.MaxValue, maxDiff = float.MinValue, sumDiff = 0f;
        float minTempFilled = float.MaxValue, maxTempFilled = float.MinValue;
        float minDiffFilled = float.MaxValue, maxDiffFilled = float.MinValue;

        for (int i = 0; i < totalCells; i++)
        {
            float diff = diffusivityData[i];
            float temp = gridData[i];
            float pin = heatSourceData[i];

            if (diff < minDiff) minDiff = diff;
            if (diff > maxDiff) maxDiff = diff;
            sumDiff += diff;

            if (maskData[i] == 1u)
            {
                filledVoxels++;
                if (pin > 0f) sourceVoxels++;

                if (temp > 0f)
                {
                    filledWithNonZeroTemp++;
                    if (temp < minTempFilled) minTempFilled = temp;
                    if (temp > maxTempFilled) maxTempFilled = temp;
                }
                else { filledWithZeroTemp++; }

                if (diff > 0f)
                {
                    filledWithNonZeroDiff++;
                    if (diff < minDiffFilled) minDiffFilled = diff;
                    if (diff > maxDiffFilled) maxDiffFilled = diff;
                }
                else { filledWithZeroDiff++; }
            }
        }

        float avgDiff = sumDiff / totalCells;

        if (!verbose) return;

        Debug.Log($"[ThermalReceiver] === Texture Readback Debug ===");
        Debug.Log($"[ThermalReceiver] Grid: {gridWidth}x{gridHeight}x{gridDepth} = {totalCells} total cells");
        Debug.Log($"[ThermalReceiver] Mask  — filled: {filledVoxels}/{totalCells} " +
                  $"({100f * filledVoxels / totalCells:F1}%)  " +
                  $"of which are pinned sources: {sourceVoxels}");
        Debug.Log($"[ThermalReceiver] Diff  — all cells:   min={minDiff:F4}  max={maxDiff:F4}  avg={avgDiff:F4}");

        if (filledVoxels > 0)
        {
            Debug.Log($"[ThermalReceiver] Diff  — filled only: " +
                      $"nonZero={filledWithNonZeroDiff}/{filledVoxels}, " +
                      $"zero={filledWithZeroDiff}/{filledVoxels}, " +
                      $"min={minDiffFilled:F4}  max={maxDiffFilled:F4}");
            Debug.Log($"[ThermalReceiver] Temp  — filled only: " +
                      $"nonZero={filledWithNonZeroTemp}/{filledVoxels}, " +
                      $"zero={filledWithZeroTemp}/{filledVoxels}, " +
                      $"min={minTempFilled:F4}  max={maxTempFilled:F4}");
        }
        else
        {
            Debug.LogWarning("[ThermalReceiver] ⚠ No filled voxels found — mask is all zero.");
        }

        if (sourceVoxels == 0)
            Debug.LogWarning("[ThermalReceiver] ⚠ No pinned source voxels found. " +
                             "Either no VoxelSolidMaterial has isContinuousHeatSource enabled " +
                             "or the HeatSourceTexture has not been stamped.");

        if (maxDiff == 0f)
            Debug.LogWarning("[ThermalReceiver] ⚠ Diffusivity buffer is entirely zero.");
    }
    // ─── Visualization ───────────────────────────────────────────────────

    private void InitVisualization()
    {
        _tempTexture = new Texture3D(gridWidth, gridHeight, gridDepth, TextureFormat.RFloat, false);
        _tempTexture.wrapMode = TextureWrapMode.Clamp;
        _tempTexture.filterMode = FilterMode.Bilinear;

        _tempTexture.SetPixelData(readbackData, 0);
        _tempTexture.Apply();

        // Push to the ray marcher instead of the old cube material
        voxelTracerCamera.tempTexture = _tempTexture;
        voxelTracerCamera.minDisplayTemp = minDisplayTemp;
        voxelTracerCamera.maxDisplayTemp = maxDisplayTemp;
    }

    private void UpdateVisualization()
    {
        GetSolidComputeResult(readbackData, readbackData.Length);
        _tempTexture.SetPixelData(readbackData, 0);
        _tempTexture.Apply();

        // Keep min/max in sync every frame
        if (voxelTracerCamera != null)
        {
            voxelTracerCamera.minDisplayTemp = minDisplayTemp;
            voxelTracerCamera.maxDisplayTemp = _prevMaxTemp > 0f ? _prevMaxTemp : maxDisplayTemp;
        }
    }

    // ─── Structs ────────────────────────────────────────────────────────
    private struct HeatSourceSnapshot
    {
        public int instanceID;
        public float temperature;
        public bool isContinuousHeatSource;
        public Vector3 position;
        public Vector3 boundsCenter; 
    }
}