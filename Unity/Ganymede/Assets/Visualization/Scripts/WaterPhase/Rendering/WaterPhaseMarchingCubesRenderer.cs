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

    private readonly Transform _ownerTransform;
    private readonly MeshFilter _sourceMeshFilter;

    private MarchingCubesRenderer _marchingRenderer;
    private ComputeShader _activeComputeShader;
    private TextAsset _activeLookupTable;
    private Material _activeSurfaceMaterial;

    private GameObject _vapourBoxObject;
    private MeshRenderer _vapourBoxRenderer;
    private MaterialPropertyBlock _vapourPropertyBlock;

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

        Vector3 center = (boundsMin + boundsMax) * 0.5f;
        Vector3 size = boundsMax - boundsMin;
        _vapourBoxObject.transform.position = center;
        _vapourBoxObject.transform.localScale = size;
        _vapourBoxObject.transform.rotation = Quaternion.identity;

        _vapourPropertyBlock.Clear();
        _vapourPropertyBlock.SetTexture(ID_PhysicsDensityGrid, resources.PhaseDensityTexture);
        _vapourPropertyBlock.SetTexture(ID_PhysicsNormalGrid, resources.SurfaceNormalTexture);
        _vapourPropertyBlock.SetVector(ID_PhysicsBoundsMinWS, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0f));
        _vapourPropertyBlock.SetVector(ID_PhysicsBoundsMaxWS, new Vector4(boundsMax.x, boundsMax.y, boundsMax.z, 0f));
        _vapourPropertyBlock.SetVector(
            ID_PhysicsVolumeDims,
            new Vector4(resources.VolumeDims.x, resources.VolumeDims.y, resources.VolumeDims.z, 0f));
        _vapourPropertyBlock.SetFloat(ID_PhysicsUseVapourChannel, 1.0f);
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
        _marchingRenderer.RenderInThicknessPass = true;
        _marchingRenderer.RenderInProxyIntervalPass = false;
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

        _vapourBoxRenderer.sharedMaterial = vapourMaterial;
        _vapourBoxRenderer.gameObject.layer = layer;

        if (_vapourPropertyBlock == null)
            _vapourPropertyBlock = new MaterialPropertyBlock();
    }
}
