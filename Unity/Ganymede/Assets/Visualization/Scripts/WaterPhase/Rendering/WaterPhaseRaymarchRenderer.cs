using UnityEngine;
using UnityEngine.Rendering;

public sealed class WaterPhaseRaymarchRenderer
{
    // Fixed name used to find or create the proxy GameObject in the hierarchy.
    // A constant string lets us locate it after a domain reload when C# state is reset but the scene is not.
    private const string DefaultProxyObjectName = "WaterVisualProxy";

    // Cached shader property IDs. Computed once at class load time instead of hashing strings every frame.
    private static readonly int ID_PhysicsDensityGrid = Shader.PropertyToID("_PhysicsDensityGrid");
    private static readonly int ID_PhysicsNormalGrid   = Shader.PropertyToID("_PhysicsNormalGrid");
    private static readonly int ID_PhysicsBoundsMinWS  = Shader.PropertyToID("_PhysicsBoundsMinWS");
    private static readonly int ID_PhysicsBoundsMaxWS  = Shader.PropertyToID("_PhysicsBoundsMaxWS");
    private static readonly int ID_PhysicsVolumeDims   = Shader.PropertyToID("_PhysicsVolumeDims");

    private readonly Transform _ownerTransform;
    private readonly MeshFilter _sourceMeshFilter;
    private readonly MeshRenderer _sourceMeshRenderer;

    // Allocated once and reused every frame. A property block lets multiple renderers share the same
    // material with different per-renderer values without creating material instances.
    // Material instances would break GPU instancing and batching.
    private readonly MaterialPropertyBlock _propertyBlock = new MaterialPropertyBlock();

    private Renderer _proxyRenderer;

    // Tracks whether the GPU resources (density textures, volume dims) are the same as last frame.
    // Version -1 guarantees the first render call always uploads the data.
    private int _lastBoundResourceVersion = -1;

    // Track the material separately so we only reassign sharedMaterial when it actually changes.
    // Reassigning the same material every frame marks the renderer dirty and breaks batching.
    private Material _lastAssignedMaterial;

    // NaN sentinel forces the bounds dirty check to fail on the first frame,
    // ensuring position and scale are always set before the first render.
    private Vector3 _lastBoundsMin = new Vector3(float.NaN, 0f, 0f);
    private Vector3 _lastBoundsMax = new Vector3(float.NaN, 0f, 0f);

    public WaterPhaseRaymarchRenderer(Transform ownerTransform, MeshFilter sourceMeshFilter, MeshRenderer sourceMeshRenderer)
    {
        _ownerTransform = ownerTransform;
        _sourceMeshFilter = sourceMeshFilter;
        _sourceMeshRenderer = sourceMeshRenderer;
    }

    // Creates or recovers the proxy GameObject and wires up its mesh, renderer, and probe settings.
    // Returns the proxy Transform so the caller can store and reuse it across frames.
    // This is called once in Start, not every frame.
    public Transform EnsureProxy(Transform currentProxyTransform)
    {
        // Make sure the source mesh exists before we try to copy it to the proxy.
        EnsureSourceMesh();

        Transform proxyTransform = currentProxyTransform;
        if (proxyTransform == null)
        {
            // Before creating a new object, check whether one already exists in the hierarchy.
            // This handles domain reloads (enter/exit play mode) where C# fields reset to null
            // but the scene hierarchy and its GameObjects survive.
            Transform existing = _ownerTransform.Find(DefaultProxyObjectName);
            if (existing != null)
            {
                proxyTransform = existing;
            }
            else
            {
                GameObject proxyObject = new GameObject(DefaultProxyObjectName);
                proxyTransform = proxyObject.transform;
                // worldPositionStays=false starts the proxy at local origin (0,0,0).
                // We'll set position and scale in world space each frame inside Render.
                proxyTransform.SetParent(_ownerTransform, false);
            }
        }

        // Add a MeshFilter if missing, then copy the mesh from the source object.
        // The proxy needs a mesh for Unity to submit draw calls, even though the raymarch shader
        // ignores vertex positions and works entirely from ray-box intersection logic.
        MeshFilter proxyFilter = proxyTransform.GetComponent<MeshFilter>();
        if (proxyFilter == null)
            proxyFilter = proxyTransform.gameObject.AddComponent<MeshFilter>();

        // Only assign if the proxy has no mesh yet. We don't overwrite a mesh that's already set,
        // in case the artist assigned a custom one directly on the proxy object.
        if (_sourceMeshFilter != null && proxyFilter.sharedMesh == null)
            proxyFilter.sharedMesh = _sourceMeshFilter.sharedMesh;

        MeshRenderer proxyMeshRenderer = proxyTransform.GetComponent<MeshRenderer>();
        if (proxyMeshRenderer == null)
            proxyMeshRenderer = proxyTransform.gameObject.AddComponent<MeshRenderer>();

        // Copy probe settings from the source renderer so the proxy receives the same lighting
        // as the original mesh. This matters for correct ambient and reflection probe blending on the water.
        if (_sourceMeshRenderer != null)
        {
            proxyMeshRenderer.reflectionProbeUsage = _sourceMeshRenderer.reflectionProbeUsage;
            proxyMeshRenderer.lightProbeUsage      = _sourceMeshRenderer.lightProbeUsage;
            proxyMeshRenderer.probeAnchor          = _sourceMeshRenderer.probeAnchor;
        }
        else
        {
            // No source renderer available, fall back to sensible defaults.
            // BlendProbes gives the best quality result and works in most environments.
            proxyMeshRenderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            proxyMeshRenderer.lightProbeUsage      = LightProbeUsage.BlendProbes;
            proxyMeshRenderer.probeAnchor          = null;
        }

        // Hide the original source mesh renderer so the scene does not render both the proxy
        // and the original object at the same location simultaneously.
        // The guard prevents disabling the renderer if the proxy IS the source (edge case).
        if (_sourceMeshRenderer != null && proxyMeshRenderer != _sourceMeshRenderer)
            _sourceMeshRenderer.enabled = false;

        _proxyRenderer = proxyMeshRenderer;
        return proxyTransform;
    }

    public void Render(
        Transform currentProxyTransform,
        Material raymarchMaterial,
        UseComputePlugin computePlugin,
        WaterPhaseResources resources,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        // EnsureProxy is called once in Start so _proxyRenderer should be stable by this point.
        // The null check here is a safety net for cases where Start failed or the proxy was destroyed externally. thats happens only if artist added proxy mesh by himself
        if (_proxyRenderer == null || raymarchMaterial == null)
            return;

        Transform proxyTransform = currentProxyTransform;

        _proxyRenderer.enabled = true;

        // Only reassign the material when it changes. Assigning sharedMaterial every frame
        // is not free and marks the renderer dirty even if the value did not change.
        if (_lastAssignedMaterial != raymarchMaterial)
        {
            _proxyRenderer.sharedMaterial = raymarchMaterial;
            _lastAssignedMaterial = raymarchMaterial;
        }

        // Some simulation setups have a moving root transform where the bounds are expressed
        // in local space rather than world space. In that case, we only scale the proxy and
        // let the parent transform drive its world position, so we don't overwrite it here.
        bool movingSimulationRoot = computePlugin != null &&
                                    computePlugin.boundsAreLocalToTransform &&
                                    proxyTransform == computePlugin.transform;

        bool boundsChanged = boundsMin != _lastBoundsMin || boundsMax != _lastBoundsMax;
        if (boundsChanged)
        {
            _lastBoundsMin = boundsMin;
            _lastBoundsMax = boundsMax;

            // Scale the proxy to cover exactly the simulation bounding volume.
            // The raymarch shader uses the object's local space to set up the ray-box intersection,
            // so the scale must match the actual world-space size of the density grid.
            proxyTransform.localScale = boundsMax - boundsMin;

            // Only set position when the root is not already driving it via parent transform.
            if (!movingSimulationRoot)
                proxyTransform.position = (boundsMin + boundsMax) * 0.5f;
        }

        // Rebind GPU textures and volume dimensions only when the resource version changed.
        // Resources reallocate when the particle count grows beyond the buffer capacity,
        // which produces new texture handles that must be re-pushed into the property block.
        bool resourceChanged = _lastBoundResourceVersion != resources.Version;
        if (resourceChanged)
        {
            _propertyBlock.SetTexture(ID_PhysicsDensityGrid, resources.PhaseDensityTexture);
            _propertyBlock.SetTexture(ID_PhysicsNormalGrid, resources.SurfaceNormalTexture);
            // Volume dimensions packed as Vector4 so a single property carries all three axes.
            _propertyBlock.SetVector(
                ID_PhysicsVolumeDims,
                new Vector4(resources.VolumeDims.x, resources.VolumeDims.y, resources.VolumeDims.z, 0f));
            _lastBoundResourceVersion = resources.Version;
        }

        // Bounds are updated separately from resources because they change more often.
        // The shader needs world-space min and max to convert the ray hit position back into
        // a UV coordinate inside the 3D density texture.
        if (boundsChanged)
        {
            _propertyBlock.SetVector(ID_PhysicsBoundsMinWS, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0f));
            _propertyBlock.SetVector(ID_PhysicsBoundsMaxWS, new Vector4(boundsMax.x, boundsMax.y, boundsMax.z, 0f));
        }

        // SetPropertyBlock is not free. Only call it when something actually changed.
        if (resourceChanged || boundsChanged)
            _proxyRenderer.SetPropertyBlock(_propertyBlock);
    }

    // Hides the proxy without destroying it. Called when the renderer mode switches away from raymarch.
    // We keep the proxy alive so it can be re-enabled immediately if the mode switches back.
    public void SetInactive()
    {
        if (_proxyRenderer != null)
            _proxyRenderer.enabled = false;
    }

    // Ensures the source MeshFilter has a mesh assigned before we try to copy it to the proxy.
    // If no mesh was assigned by the artist we fall back to a unit cube, which is the minimal
    // geometry the raymarch shader needs to receive fragment invocations across the volume.
    private void EnsureSourceMesh()
    {
        // Nothing to do if there's no source filter, or if it already has a mesh.
        if (_sourceMeshFilter == null || _sourceMeshFilter.sharedMesh != null)
            return;

        // Create a primitive purely to steal its built-in mesh, then destroy the temporary GameObject.
        // We only keep the mesh asset, not the scene object. This avoids any artist setup for this fallback.
        GameObject tempCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _sourceMeshFilter.sharedMesh = tempCube.GetComponent<MeshFilter>().sharedMesh;
        Object.Destroy(tempCube);
    }
}
