using System;
using UnityEngine;

// This renderer handles two things: running the marching cubes compute shader to extract
// a water surface mesh from the density grid, and optionally rendering a vapour/gas box
// on top of that using a separate raymarch volume shader.
public sealed class WaterPhaseMarchingCubesRenderer : IDisposable
{
    // Fixed name used to find or create the vapour box GameObject in the scene hierarchy.
    // Using a constant string means we can always locate it by name even if the reference is lost (e.g. after domain reload).
    private const string VapourBoxObjectName = "WaterVapourBox";

    // Cached shader property IDs for the vapour box MaterialPropertyBlock.
    // These let the vapour shader know where the density volume is in world space and which texture to sample.
    private static readonly int ID_PhysicsDensityGrid        = Shader.PropertyToID("_PhysicsDensityGrid");
    private static readonly int ID_PhysicsNormalGrid         = Shader.PropertyToID("_PhysicsNormalGrid");
    private static readonly int ID_PhysicsBoundsMinWS        = Shader.PropertyToID("_PhysicsBoundsMinWS");
    private static readonly int ID_PhysicsBoundsMaxWS        = Shader.PropertyToID("_PhysicsBoundsMaxWS");
    private static readonly int ID_PhysicsVolumeDims         = Shader.PropertyToID("_PhysicsVolumeDims");
    private static readonly int ID_PhysicsUseVapourChannel   = Shader.PropertyToID("_PhysicsUseVapourChannel");

    private readonly Transform _ownerTransform;
    private readonly MeshFilter _sourceMeshFilter;

    // The inner renderer that actually dispatches the marching cubes compute shader and submits the mesh draw.
    private MarchingCubesRenderer _marchingRenderer;

    // We track which assets the current renderer was built with. If any of them change at runtime
    // (e.g. a new compute shader is assigned in the inspector), we know to tear down and rebuild.
    private ComputeShader _activeComputeShader;
    private TextAsset _activeLookupTable;
    private Material _activeSurfaceMaterial;
    private Material _activeVapourMaterial;

    // Initialized to -1 so the first render call always sets the layer even if layer 0 is requested.
    private int _activeVapourLayer = -1;

    private GameObject _vapourBoxObject;
    private MeshRenderer _vapourBoxRenderer;
    private MaterialPropertyBlock _vapourPropertyBlock;

    // We track the resource version to avoid pushing the same textures into the property block every frame.
    // The version number increments whenever the GPU buffers are reallocated, which is the only time we need to rebind.
    private int _lastVapourResourceVersion = -1;

    // NaN is used as a sentinel to guarantee the bounds comparison fails on the first frame,
    // forcing an initial position and scale update for the vapour box.
    private Vector3 _lastVapourBoundsMin = new Vector3(float.NaN, 0f, 0f);
    private Vector3 _lastVapourBoundsMax = new Vector3(float.NaN, 0f, 0f);

    public WaterPhaseMarchingCubesRenderer(Transform ownerTransform, MeshFilter sourceMeshFilter)
    {
        _ownerTransform = ownerTransform;
        _sourceMeshFilter = sourceMeshFilter;
    }

    public void Render(
        WaterPhaseBridgeSettings settings,
        WaterPhaseResources resources,
        Vector3 boundsMin,
        Vector3 boundsMax,
        int layer)
    {
        // Recreate the inner renderer only if needed (first call or asset references changed).
        EnsureMarchingRenderer(settings.References.marchingCubesCompute, settings.References.marchingCubesLUT, settings.Rendering.marchingCubesMaterial);
        if (_marchingRenderer == null)
            return;

        // Dispatch the compute shader to extract the isosurface and submit it for rendering this frame.
        _marchingRenderer.Render(
            resources.PhaseDensityTexture,
            resources.SurfaceNormalTexture,
            resources.VolumeDims,
            boundsMin,
            boundsMax,
            Mathf.Clamp01(settings.Rendering.marchingCubesIsoLevel),
            layer);

        // Vapour is optional. If no vapour material is assigned, hide the box and stop here.
        if (settings.Rendering.vapourRaymarchMaterial == null)
        {
            SetInactive();
            return;
        }

        // Create or reconfigure the vapour box GameObject and its renderer.
        EnsureVapourBox(settings.Rendering.vapourRaymarchMaterial, layer);
        if (_vapourBoxObject == null || _vapourBoxRenderer == null)
            return;

        // The vapour box is a world-space GameObject that we reposition to match the fluid's bounding volume each frame.
        // We only update transform and shader data when something actually changed to avoid unnecessary GPU uploads.
        bool boundsChanged = boundsMin != _lastVapourBoundsMin || boundsMax != _lastVapourBoundsMax;
        if (boundsChanged)
        {
            _lastVapourBoundsMin = boundsMin;
            _lastVapourBoundsMax = boundsMax;
            // Center the box in world space and scale it to exactly cover the fluid simulation volume.
            _vapourBoxObject.transform.position = (boundsMin + boundsMax) * 0.5f;
            _vapourBoxObject.transform.localScale = boundsMax - boundsMin;
            // Lock rotation to identity so the box stays axis-aligned and matches the simulation grid.
            _vapourBoxObject.transform.rotation = Quaternion.identity;
        }

        // If the GPU resources were reallocated this frame (particle count grew, etc.), we need to rebind
        // the new texture handles into the property block so the vapour shader samples the right data.
        bool resourceChanged = _lastVapourResourceVersion != resources.Version;
        if (resourceChanged)
        {
            _vapourPropertyBlock.SetTexture(ID_PhysicsDensityGrid, resources.PhaseDensityTexture);
            _vapourPropertyBlock.SetTexture(ID_PhysicsNormalGrid, resources.SurfaceNormalTexture);
            // Volume dimensions packed as a Vector4 so a single shader property can carry x/y/z.
            _vapourPropertyBlock.SetVector(
                ID_PhysicsVolumeDims,
                new Vector4(resources.VolumeDims.x, resources.VolumeDims.y, resources.VolumeDims.z, 0f));
            // Tell the shader this renderer is in vapour mode, not liquid mode.
            // The same material can handle both; this flag selects the vapour density channel.
            _vapourPropertyBlock.SetFloat(ID_PhysicsUseVapourChannel, 1.0f);
            _lastVapourResourceVersion = resources.Version;
        }

        // Bounds go into the property block separately from resources because they change more often.
        // The shader needs them in world space to map UV into the 3D density texture.
        if (boundsChanged)
        {
            _vapourPropertyBlock.SetVector(ID_PhysicsBoundsMinWS, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0f));
            _vapourPropertyBlock.SetVector(ID_PhysicsBoundsMaxWS, new Vector4(boundsMax.x, boundsMax.y, boundsMax.z, 0f));
        }

        // Apply the property block to the renderer only when something actually changed.
        // SetPropertyBlock itself is not free, so we skip it on frames where nothing is different.
        if (resourceChanged || boundsChanged)
            _vapourBoxRenderer.SetPropertyBlock(_vapourPropertyBlock);

        _vapourBoxRenderer.enabled = true;
    }

    // Hides the vapour box without destroying it. Called when vapour material is removed at runtime.
    // We keep the GameObject alive so we don't have to recreate it if vapour is re-enabled later.
    public void SetInactive()
    {
        if (_vapourBoxRenderer != null)
            _vapourBoxRenderer.enabled = false;
    }

    // Full teardown. Called when the owning MonoBehaviour is destroyed.
    public void Release()
    {
        if (_marchingRenderer != null)
        {
            _marchingRenderer.Release();
            _marchingRenderer = null;
        }

        // Clear cached asset references so EnsureMarchingRenderer rebuilds cleanly next time if needed.
        _activeComputeShader = null;
        _activeLookupTable = null;
        _activeSurfaceMaterial = null;
        _activeVapourMaterial = null;
        _activeVapourLayer = -1;

        // Reset the version and bounds sentinels so a fresh renderer starts with no stale state.
        _lastVapourResourceVersion = -1;
        _lastVapourBoundsMin = new Vector3(float.NaN, 0f, 0f);
        _lastVapourBoundsMax = new Vector3(float.NaN, 0f, 0f);

        // Destroy the vapour box GameObject from the scene. Null out both references so
        // the next Render call goes through EnsureVapourBox from scratch if needed.
        if (_vapourBoxObject != null)
        {
            UnityEngine.Object.Destroy(_vapourBoxObject);
            _vapourBoxObject = null;
            _vapourBoxRenderer = null;
        }
    }

    // IDisposable support so this class works correctly in using blocks and with dependency injection containers.
    public void Dispose()
    {
        Release();
    }

    // Creates the MarchingCubesRenderer if it doesn't exist or if any of its core assets changed.
    // The asset change check matters because in the Unity editor, the user can hot-swap materials or
    // compute shaders at runtime without restarting play mode.
    private void EnsureMarchingRenderer(ComputeShader computeShader, TextAsset lookupTable, Material surfaceMaterial)
    {
        // Can't build without all three required assets. Caller will null-check _marchingRenderer and skip.
        if (computeShader == null || lookupTable == null || surfaceMaterial == null)
            return;

        bool requiresRebuild = _marchingRenderer == null ||
                               _activeComputeShader != computeShader ||
                               _activeLookupTable != lookupTable ||
                               _activeSurfaceMaterial != surfaceMaterial;

        if (!requiresRebuild)
            return;

        // Release the old renderer before creating a new one to avoid GPU resource leaks.
        if (_marchingRenderer != null)
            _marchingRenderer.Release();

        _marchingRenderer = new MarchingCubesRenderer(computeShader, lookupTable, surfaceMaterial);
        _activeComputeShader = computeShader;
        _activeLookupTable = lookupTable;
        _activeSurfaceMaterial = surfaceMaterial;
    }

    // Sets up the vapour box GameObject, its MeshFilter, MeshRenderer, and MaterialPropertyBlock.
    // This method is idempotent: safe to call every frame, only does work when something is missing or changed.
    private void EnsureVapourBox(Material vapourMaterial, int layer)
    {
        if (_vapourBoxObject == null)
        {
            // Before creating a new object, check if one already exists in the hierarchy.
            // This can happen after a domain reload (entering/exiting play mode) where the C# state
            // is reset but the scene hierarchy is preserved. Reusing the existing object avoids duplicates.
            Transform existing = _ownerTransform.Find(VapourBoxObjectName);
            if (existing != null)
            {
                _vapourBoxObject = existing.gameObject;
            }
            else
            {
                _vapourBoxObject = new GameObject(VapourBoxObjectName);
                // SetParent with worldPositionStays=false means the box starts at local origin (0,0,0)
                // relative to the owner. We'll position it in world space manually each frame.
                _vapourBoxObject.transform.SetParent(_ownerTransform, false);
            }
        }

        // Ensure a MeshFilter exists. We need a mesh for the renderer to draw,
        // even though the vapour shader ignores vertex positions and does everything in screen space rays.
        MeshFilter meshFilter = _vapourBoxObject.GetComponent<MeshFilter>();
        if (meshFilter == null)
            meshFilter = _vapourBoxObject.AddComponent<MeshFilter>();

        if (meshFilter.sharedMesh == null)
        {
            if (_sourceMeshFilter != null && _sourceMeshFilter.sharedMesh != null)
            {
                // Reuse the source mesh (typically the particle mesh assigned by the user) to avoid creating assets.
                meshFilter.sharedMesh = _sourceMeshFilter.sharedMesh;
            }
            else
            {
                // Fallback: spin up a primitive cube, steal its mesh, then immediately destroy the temporary object.
                // We only keep the mesh data, not the GameObject. This is a last resort when no source mesh is available.
                GameObject tempCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                meshFilter.sharedMesh = tempCube.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.Destroy(tempCube);
            }
        }

        // Get or add the renderer. Two null checks are needed: first try GetComponent in case
        // the component already exists (e.g. after reusing an existing scene object), then AddComponent if not.
        if (_vapourBoxRenderer == null)
            _vapourBoxRenderer = _vapourBoxObject.GetComponent<MeshRenderer>();
        if (_vapourBoxRenderer == null)
            _vapourBoxRenderer = _vapourBoxObject.AddComponent<MeshRenderer>();

        // Only reassign the material when it actually changed. Assigning sharedMaterial even with the same value
        // marks the renderer as dirty and triggers unnecessary re-batching.
        if (_activeVapourMaterial != vapourMaterial)
        {
            _vapourBoxRenderer.sharedMaterial = vapourMaterial;
            _activeVapourMaterial = vapourMaterial;
        }

        // Keep the vapour box on the same layer as the particle simulation so it respects
        // the same camera culling masks and reflection probe settings.
        if (_activeVapourLayer != layer)
        {
            _vapourBoxRenderer.gameObject.layer = layer;
            _activeVapourLayer = layer;
        }

        // Create the property block lazily. We use a property block instead of setting material properties
        // directly because property blocks let different renderers use the same material with different values
        // without creating material instances, which would break GPU instancing and batching.
        if (_vapourPropertyBlock == null)
            _vapourPropertyBlock = new MaterialPropertyBlock();
    }
}
