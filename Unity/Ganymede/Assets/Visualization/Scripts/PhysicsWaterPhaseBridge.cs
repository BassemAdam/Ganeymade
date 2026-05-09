using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Scene-facing coordinator for the water phase bridge.
/// It validates configuration, keeps shared helpers alive, and orchestrates the
/// per-frame flow: native GPU particle output → density pipeline → active renderer.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(UseComputePlugin))]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PhysicsWaterPhaseBridge : MonoBehaviour
{
    [SerializeField] private WaterPhaseBridgeSettings settings = new WaterPhaseBridgeSettings();

    [HideInInspector] public Transform visualProxyTransform;

    private UseComputePlugin _computePlugin;
    private MeshFilter _sourceMeshFilter;
    private MeshRenderer _sourceMeshRenderer;
    private int _particleStride;

    private WaterPhaseResources _resources;
    private WaterPhaseDensityPipeline _densityPipeline;
    private WaterPhaseRaymarchRenderer _raymarchRenderer;
    private WaterPhaseMarchingCubesRenderer _marchingRenderer;
    private UnityParticleOutputBridge _particleOutputBridge;
    private WaterSurfaceRenderMode _lastRenderMode;
    private bool _initialized;

    private void Awake()
    {
        CacheComponents();
        CreateHelpers();
        _particleStride = Marshal.SizeOf<Particle>();

        if (_raymarchRenderer != null)
            visualProxyTransform = _raymarchRenderer.EnsureProxy(visualProxyTransform);
    }

    private void OnEnable()
    {
        CacheComponents();
        CreateHelpers();

        if (_particleStride <= 0)
            _particleStride = Marshal.SizeOf<Particle>();

        if (_raymarchRenderer != null)
            visualProxyTransform = _raymarchRenderer.EnsureProxy(visualProxyTransform);
    }

    private void Start()
    {
        if (!TryInitializeBridge())
        {
            enabled = false;
            return;
        }

        PrimeResources();
    }

    private void LateUpdate()
    {
        if (!isActiveAndEnabled)
            return;

        if (!TryInitializeBridge())
            return;

        _resources.Ensure(settings.DensityGrid.volumeDims, _computePlugin.particleCount, _particleStride);
        _particleOutputBridge.RegisterIfNeeded(_resources.ParticleOutputBuffer);

        _computePlugin.GetBoundsWS(out Vector3 boundsMin, out Vector3 boundsMax);
        _densityPipeline.Execute(_computePlugin, settings, _resources, boundsMin, boundsMax);
        RenderActivePresentation(boundsMin, boundsMax);
    }

    private void OnDestroy()
    {
        if (_particleOutputBridge != null)
            _particleOutputBridge.ClearRegistration();

        if (_resources != null)
            _resources.Release();

        if (_marchingRenderer != null)
            _marchingRenderer.Release();
    }

    private bool TryInitializeBridge()
    {
        CacheComponents();
        CreateHelpers();

        if (_computePlugin == null || _sourceMeshFilter == null || _sourceMeshRenderer == null)
            return false;

        if (_particleStride <= 0)
            _particleStride = Marshal.SizeOf<Particle>();

        if (!ValidateConfiguration())
            return false;

        EnsureDensityPipeline();
        if (_densityPipeline == null)
            return false;

        if (!_initialized)
        {
            _lastRenderMode = settings.Rendering.mode;
            _initialized = true;
        }

        return true;
    }

    private void PrimeResources()
    {
        _resources.Ensure(settings.DensityGrid.volumeDims, _computePlugin.particleCount, _particleStride);
        _particleOutputBridge.RegisterIfNeeded(_resources.ParticleOutputBuffer);
    }

    private void CacheComponents()
    {
        if (_computePlugin == null)
            _computePlugin = GetComponent<UseComputePlugin>();
        if (_sourceMeshFilter == null)
            _sourceMeshFilter = GetComponent<MeshFilter>();
        if (_sourceMeshRenderer == null)
            _sourceMeshRenderer = GetComponent<MeshRenderer>();
    }

    private void CreateHelpers()
    {
        if (_resources == null)
            _resources = new WaterPhaseResources();

        if (_particleOutputBridge == null)
            _particleOutputBridge = new UnityParticleOutputBridge();

        if (_raymarchRenderer == null && _sourceMeshFilter != null && _sourceMeshRenderer != null)
            _raymarchRenderer = new WaterPhaseRaymarchRenderer(transform, _sourceMeshFilter, _sourceMeshRenderer);

        if (_marchingRenderer == null && _sourceMeshFilter != null)
            _marchingRenderer = new WaterPhaseMarchingCubesRenderer(transform, _sourceMeshFilter);
    }

    private bool ValidateConfiguration()
    {
        if (settings == null)
        {
            Debug.LogError("[PhysicsWaterPhaseBridge] Missing bridge settings instance.");
            return false;
        }

        if (settings.References.particlesToDensityCompute == null)
        {
            Debug.LogError("[PhysicsWaterPhaseBridge] Missing particles-to-density compute shader.");
            return false;
        }

        if (settings.Rendering.mode == WaterSurfaceRenderMode.RaymarchVolume)
        {
            if (settings.Rendering.rayMarchingMaterial == null)
            {
                Debug.LogError("[PhysicsWaterPhaseBridge] Raymarch mode requires a ray marching material.");
                return false;
            }

            return true;
        }

        if (settings.References.marchingCubesCompute == null ||
            settings.References.marchingCubesLUT == null ||
            settings.Rendering.marchingCubesMaterial == null)
        {
            Debug.LogError("[PhysicsWaterPhaseBridge] Marching cubes mode requires marchingCubesCompute, marchingCubesLUT, and marchingCubesMaterial.");
            return false;
        }

        return true;
    }

    private void EnsureDensityPipeline()
    {
        ComputeShader computeShader = settings.References.particlesToDensityCompute;
        if (computeShader == null)
        {
            _densityPipeline = null;
            return;
        }

        if (_densityPipeline == null || _densityPipeline.ComputeShader != computeShader)
            _densityPipeline = new WaterPhaseDensityPipeline(computeShader);
    }

    private void RenderActivePresentation(Vector3 boundsMin, Vector3 boundsMax)
    {
        if (settings.Rendering.mode != _lastRenderMode)
        {
            if (_raymarchRenderer != null)
                _raymarchRenderer.SetInactive();
            if (_marchingRenderer != null)
                _marchingRenderer.SetInactive();

            _lastRenderMode = settings.Rendering.mode;
        }

        if (settings.Rendering.mode == WaterSurfaceRenderMode.RaymarchVolume)
        {
            if (_marchingRenderer != null)
                _marchingRenderer.SetInactive();

            if (_raymarchRenderer != null)
            {
                visualProxyTransform = _raymarchRenderer.EnsureProxy(visualProxyTransform);
                _raymarchRenderer.Render(
                    visualProxyTransform,
                    settings.Rendering.rayMarchingMaterial,
                    _computePlugin,
                    _resources,
                    boundsMin,
                    boundsMax);
            }

            return;
        }

        if (_raymarchRenderer != null)
            _raymarchRenderer.SetInactive();

        if (_marchingRenderer != null)
            _marchingRenderer.Render(settings, _resources, boundsMin, boundsMax, gameObject.layer);
    }

    private void OnValidate()
    {
        settings.DensityGrid.volumeDims.x = Mathf.Max(1, settings.DensityGrid.volumeDims.x);
        settings.DensityGrid.volumeDims.y = Mathf.Max(1, settings.DensityGrid.volumeDims.y);
        settings.DensityGrid.volumeDims.z = Mathf.Max(1, settings.DensityGrid.volumeDims.z);
        settings.DensityGrid.vapourSmoothingRadiusWS = Mathf.Max(0f, settings.DensityGrid.vapourSmoothingRadiusWS);
        settings.DensityGrid.maxKernelRadiusVoxels = Mathf.Clamp(settings.DensityGrid.maxKernelRadiusVoxels, 1, 8);

        settings.Blur.radius = Mathf.Clamp(settings.Blur.radius, 1, 4);
        settings.Blur.sigma = Mathf.Clamp(settings.Blur.sigma, 0.1f, 4.0f);
        settings.Blur.detailPreserve = Mathf.Clamp01(settings.Blur.detailPreserve);

        settings.MarchingCubesBlur.radius = Mathf.Clamp(settings.MarchingCubesBlur.radius, 1, 4);
        settings.MarchingCubesBlur.sigma = Mathf.Clamp(settings.MarchingCubesBlur.sigma, 0.1f, 4.0f);
        settings.MarchingCubesBlur.detailPreserve = Mathf.Clamp01(settings.MarchingCubesBlur.detailPreserve);

        ValidateAdaptiveSmoothing(settings.RaymarchSmoothing);
        ValidateAdaptiveSmoothing(settings.MarchingCubesSmoothing);

        settings.Rendering.marchingCubesIsoLevel = Mathf.Clamp01(settings.Rendering.marchingCubesIsoLevel);
    }

    private static void ValidateAdaptiveSmoothing(WaterPhaseAdaptiveSmoothingSettings smoothing)
    {
        if (smoothing == null)
            return;

        if (smoothing.liquidSmoothingRadiusWS >= 0f)
            smoothing.liquidSmoothingRadiusWS = Mathf.Max(0f, smoothing.liquidSmoothingRadiusWS);

        smoothing.liquidBulkSmoothingRadiusWS = Mathf.Max(0f, smoothing.liquidBulkSmoothingRadiusWS);
        smoothing.adaptiveDensitySurface = Mathf.Max(0f, smoothing.adaptiveDensitySurface);
        smoothing.adaptiveDensityBulk = Mathf.Max(smoothing.adaptiveDensitySurface + 0.01f, smoothing.adaptiveDensityBulk);
        smoothing.adaptiveDensityCurve = Mathf.Clamp(smoothing.adaptiveDensityCurve, 0.05f, 4f);
    }
}
