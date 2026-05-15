using UnityEngine;
using UnityEngine.Rendering;

public sealed class WaterPhaseRaymarchRenderer
{
    private const string DefaultProxyObjectName = "WaterVisualProxy";

    private static readonly int ID_PhysicsDensityGrid = Shader.PropertyToID("_PhysicsDensityGrid");
    private static readonly int ID_PhysicsNormalGrid = Shader.PropertyToID("_PhysicsNormalGrid");
    private static readonly int ID_PhysicsBoundsMinWS = Shader.PropertyToID("_PhysicsBoundsMinWS");
    private static readonly int ID_PhysicsBoundsMaxWS = Shader.PropertyToID("_PhysicsBoundsMaxWS");
    private static readonly int ID_PhysicsVolumeDims = Shader.PropertyToID("_PhysicsVolumeDims");

    private readonly Transform _ownerTransform;
    private readonly MeshFilter _sourceMeshFilter;
    private readonly MeshRenderer _sourceMeshRenderer;
    private readonly MaterialPropertyBlock _propertyBlock = new MaterialPropertyBlock();

    private Renderer _proxyRenderer;
    private int _lastBoundResourceVersion = -1;
    private Material _lastAssignedMaterial;

    public WaterPhaseRaymarchRenderer(Transform ownerTransform, MeshFilter sourceMeshFilter, MeshRenderer sourceMeshRenderer)
    {
        _ownerTransform = ownerTransform;
        _sourceMeshFilter = sourceMeshFilter;
        _sourceMeshRenderer = sourceMeshRenderer;
    }

    public Transform EnsureProxy(Transform currentProxyTransform)
    {
        EnsureSourceMesh();

        Transform proxyTransform = currentProxyTransform;
        if (proxyTransform == null)
        {
            Transform existing = _ownerTransform.Find(DefaultProxyObjectName);
            if (existing != null)
            {
                proxyTransform = existing;
            }
            else
            {
                GameObject proxyObject = new GameObject(DefaultProxyObjectName);
                proxyTransform = proxyObject.transform;
                proxyTransform.SetParent(_ownerTransform, false);
            }
        }

        MeshFilter proxyFilter = proxyTransform.GetComponent<MeshFilter>();
        if (proxyFilter == null)
            proxyFilter = proxyTransform.gameObject.AddComponent<MeshFilter>();

        if (_sourceMeshFilter != null && proxyFilter.sharedMesh == null)
            proxyFilter.sharedMesh = _sourceMeshFilter.sharedMesh;

        MeshRenderer proxyMeshRenderer = proxyTransform.GetComponent<MeshRenderer>();
        if (proxyMeshRenderer == null)
            proxyMeshRenderer = proxyTransform.gameObject.AddComponent<MeshRenderer>();

        if (_sourceMeshRenderer != null)
        {
            proxyMeshRenderer.reflectionProbeUsage = _sourceMeshRenderer.reflectionProbeUsage;
            proxyMeshRenderer.lightProbeUsage = _sourceMeshRenderer.lightProbeUsage;
            proxyMeshRenderer.probeAnchor = _sourceMeshRenderer.probeAnchor;
        }
        else
        {
            proxyMeshRenderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            proxyMeshRenderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            proxyMeshRenderer.probeAnchor = null;
        }

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
        // EnsureProxy is called once in Start; _proxyRenderer is stable after that.
        if (_proxyRenderer == null || raymarchMaterial == null)
            return;
        Transform proxyTransform = currentProxyTransform;

        _proxyRenderer.enabled = true;
        if (_lastAssignedMaterial != raymarchMaterial)
        {
            _proxyRenderer.sharedMaterial = raymarchMaterial;
            _lastAssignedMaterial = raymarchMaterial;
        }

        Vector3 center = (boundsMin + boundsMax) * 0.5f;
        Vector3 size = boundsMax - boundsMin;
        bool movingSimulationRoot = computePlugin != null &&
                                    computePlugin.boundsAreLocalToTransform &&
                                    proxyTransform == computePlugin.transform;

        if (!movingSimulationRoot)
            proxyTransform.position = center;

        proxyTransform.localScale = size;

        if (_lastBoundResourceVersion != resources.Version)
        {
            _propertyBlock.SetTexture(ID_PhysicsDensityGrid, resources.PhaseDensityTexture);
            _propertyBlock.SetTexture(ID_PhysicsNormalGrid, resources.SurfaceNormalTexture);
            _propertyBlock.SetVector(
                ID_PhysicsVolumeDims,
                new Vector4(resources.VolumeDims.x, resources.VolumeDims.y, resources.VolumeDims.z, 0f));
            _lastBoundResourceVersion = resources.Version;
        }

        _propertyBlock.SetVector(ID_PhysicsBoundsMinWS, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0f));
        _propertyBlock.SetVector(ID_PhysicsBoundsMaxWS, new Vector4(boundsMax.x, boundsMax.y, boundsMax.z, 0f));
        _proxyRenderer.SetPropertyBlock(_propertyBlock);
    }

    public void SetInactive()
    {
        if (_proxyRenderer != null)
            _proxyRenderer.enabled = false;
    }

    private void EnsureSourceMesh()
    {
        if (_sourceMeshFilter == null || _sourceMeshFilter.sharedMesh != null)
            return;

        GameObject tempCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _sourceMeshFilter.sharedMesh = tempCube.GetComponent<MeshFilter>().sharedMesh;
        Object.Destroy(tempCube);
    }
}
