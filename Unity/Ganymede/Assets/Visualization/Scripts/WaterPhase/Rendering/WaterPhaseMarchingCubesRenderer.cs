using System;
using UnityEngine;

public sealed class WaterPhaseMarchingCubesRenderer : IDisposable
{
    private const string VapourBoxObjectName = "WaterVapourBox";

    private static readonly int ID_PhysicsDensityGrid = Shader.PropertyToID("_PhysicsDensityGrid");
    private static readonly int ID_PhysicsNormalGrid = Shader.PropertyToID("_PhysicsNormalGrid");
    private static readonly int ID_PhysicsBoundsMinWS = Shader.PropertyToID("_PhysicsBoundsMinWS");
    private static readonly int ID_PhysicsBoundsMaxWS = Shader.PropertyToID("_PhysicsBoundsMaxWS");
    private static readonly int ID_PhysicsVolumeDims = Shader.PropertyToID("_PhysicsVolumeDims");
    private static readonly int ID_PhysicsUseVapourChannel = Shader.PropertyToID("_PhysicsUseVapourChannel");
    private static readonly int ID_VapourVelocityTex = Shader.PropertyToID("_VapourVelocityTex");
    private static readonly int ID_VapourNoiseTex    = Shader.PropertyToID("_VapourNoiseTex");

    private readonly Transform _ownerTransform;
    private readonly MeshFilter _sourceMeshFilter;

    private MarchingCubesRenderer _marchingRenderer;
    private ComputeShader _activeComputeShader;
    private TextAsset _activeLookupTable;
    private Material _activeSurfaceMaterial;
    private Material _activeVapourMaterial;
    private int _activeVapourLayer = -1;

    private GameObject _vapourBoxObject;
    private MeshRenderer _vapourBoxRenderer;
    private MaterialPropertyBlock _vapourPropertyBlock;
    private int _lastVapourResourceVersion = -1;
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
        EnsureMarchingRenderer(settings.References.marchingCubesCompute, settings.References.marchingCubesLUT, settings.Rendering.marchingCubesMaterial);
        if (_marchingRenderer == null)
            return;

        _marchingRenderer.Render(
            resources.PhaseDensityTexture,
            resources.SurfaceNormalTexture,
            resources.VolumeDims,
            boundsMin,
            boundsMax,
            Mathf.Clamp01(settings.Rendering.marchingCubesIsoLevel),
            layer);

        if (settings.Rendering.vapourRaymarchMaterial == null)
        {
            SetInactive();
            return;
        }

        EnsureVapourBox(settings.Rendering.vapourRaymarchMaterial, layer);
        if (_vapourBoxObject == null || _vapourBoxRenderer == null)
            return;

        bool boundsChanged = boundsMin != _lastVapourBoundsMin || boundsMax != _lastVapourBoundsMax;
        if (boundsChanged)
        {
            _lastVapourBoundsMin = boundsMin;
            _lastVapourBoundsMax = boundsMax;
            _vapourBoxObject.transform.position = (boundsMin + boundsMax) * 0.5f;
            _vapourBoxObject.transform.localScale = boundsMax - boundsMin;
            _vapourBoxObject.transform.rotation = Quaternion.identity;
        }

        bool resourceChanged = _lastVapourResourceVersion != resources.Version;
        if (resourceChanged)
        {
            _vapourPropertyBlock.SetTexture(ID_PhysicsDensityGrid, resources.PhaseDensityTexture);
            _vapourPropertyBlock.SetTexture(ID_PhysicsNormalGrid, resources.SurfaceNormalTexture);
            _vapourPropertyBlock.SetVector(
                ID_PhysicsVolumeDims,
                new Vector4(resources.VolumeDims.x, resources.VolumeDims.y, resources.VolumeDims.z, 0f));
            _vapourPropertyBlock.SetFloat(ID_PhysicsUseVapourChannel, 1.0f);
            _vapourPropertyBlock.SetTexture(ID_VapourVelocityTex, resources.VapourVelocityTexture);
            _lastVapourResourceVersion = resources.Version;
        }

        // Bind the advected noise texture every frame: it ping-pongs, so the pointer changes each frame.
        _vapourPropertyBlock.SetTexture(ID_VapourNoiseTex, resources.VapourNoiseSrcTex);

        if (boundsChanged)
        {
            _vapourPropertyBlock.SetVector(ID_PhysicsBoundsMinWS, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0f));
            _vapourPropertyBlock.SetVector(ID_PhysicsBoundsMaxWS, new Vector4(boundsMax.x, boundsMax.y, boundsMax.z, 0f));
        }

        if (resourceChanged || boundsChanged)
            _vapourBoxRenderer.SetPropertyBlock(_vapourPropertyBlock);
        _vapourBoxRenderer.enabled = true;
    }

    public void SetInactive()
    {
        if (_vapourBoxRenderer != null)
            _vapourBoxRenderer.enabled = false;
    }

    public void Release()
    {
        if (_marchingRenderer != null)
        {
            _marchingRenderer.Release();
            _marchingRenderer = null;
        }

        _activeComputeShader = null;
        _activeLookupTable = null;
        _activeSurfaceMaterial = null;
        _activeVapourMaterial = null;
        _activeVapourLayer = -1;
        _lastVapourResourceVersion = -1;
        _lastVapourBoundsMin = new Vector3(float.NaN, 0f, 0f);
        _lastVapourBoundsMax = new Vector3(float.NaN, 0f, 0f);

        if (_vapourBoxObject != null)
        {
            UnityEngine.Object.Destroy(_vapourBoxObject);
            _vapourBoxObject = null;
            _vapourBoxRenderer = null;
        }
    }

    public void Dispose()
    {
        Release();
    }

    private void EnsureMarchingRenderer(ComputeShader computeShader, TextAsset lookupTable, Material surfaceMaterial)
    {
        if (computeShader == null || lookupTable == null || surfaceMaterial == null)
            return;

        bool requiresRebuild = _marchingRenderer == null ||
                               _activeComputeShader != computeShader ||
                               _activeLookupTable != lookupTable ||
                               _activeSurfaceMaterial != surfaceMaterial;

        if (!requiresRebuild)
            return;

        if (_marchingRenderer != null)
            _marchingRenderer.Release();

        _marchingRenderer = new MarchingCubesRenderer(computeShader, lookupTable, surfaceMaterial);
        _activeComputeShader = computeShader;
        _activeLookupTable = lookupTable;
        _activeSurfaceMaterial = surfaceMaterial;
    }

    private void EnsureVapourBox(Material vapourMaterial, int layer)
    {
        if (_vapourBoxObject == null)
        {
            Transform existing = _ownerTransform.Find(VapourBoxObjectName);
            if (existing != null)
            {
                _vapourBoxObject = existing.gameObject;
            }
            else
            {
                _vapourBoxObject = new GameObject(VapourBoxObjectName);
                _vapourBoxObject.transform.SetParent(_ownerTransform, false);
            }
        }

        MeshFilter meshFilter = _vapourBoxObject.GetComponent<MeshFilter>();
        if (meshFilter == null)
            meshFilter = _vapourBoxObject.AddComponent<MeshFilter>();

        if (meshFilter.sharedMesh == null)
        {
            if (_sourceMeshFilter != null && _sourceMeshFilter.sharedMesh != null)
            {
                meshFilter.sharedMesh = _sourceMeshFilter.sharedMesh;
            }
            else
            {
                GameObject tempCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                meshFilter.sharedMesh = tempCube.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.Destroy(tempCube);
            }
        }

        if (_vapourBoxRenderer == null)
            _vapourBoxRenderer = _vapourBoxObject.GetComponent<MeshRenderer>();
        if (_vapourBoxRenderer == null)
            _vapourBoxRenderer = _vapourBoxObject.AddComponent<MeshRenderer>();

        if (_activeVapourMaterial != vapourMaterial)
        {
            _vapourBoxRenderer.sharedMaterial = vapourMaterial;
            _activeVapourMaterial = vapourMaterial;
        }

        if (_activeVapourLayer != layer)
        {
            _vapourBoxRenderer.gameObject.layer = layer;
            _activeVapourLayer = layer;
        }

        if (_vapourPropertyBlock == null)
            _vapourPropertyBlock = new MaterialPropertyBlock();
    }
}
