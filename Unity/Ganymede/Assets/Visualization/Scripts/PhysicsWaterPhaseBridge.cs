using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Scene-facing coordinator for the water phase bridge.
/// It validates configuration, keeps shared helpers alive, and orchestrates the
/// per-frame flow: native GPU particle output -> density pipeline -> active renderer.
/// </summary>
[DefaultExecutionOrder(-10)] // Ensures Awake runs before ParticleRenderer.Start so computePlugin is wired in time
[DisallowMultipleComponent]
[RequireComponent(typeof(UseComputePlugin))]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PhysicsWaterPhaseBridge : MonoBehaviour
{
    [SerializeField] private WaterPhaseBridgeSettings settings = new WaterPhaseBridgeSettings();

    [Header("Debug")]
    [Tooltip("Switches the active renderer to raw particle spheres. Raymarching pauses completely. Toggle off to return to raymarching.")]
    [SerializeField] private bool _showDebugParticles = false;
    [Tooltip("ParticleRenderer component to use as the debug view.")]
    [SerializeField] private ParticleRenderer _debugParticleRenderer;

    [HideInInspector] public Transform visualProxyTransform;

    private UseComputePlugin _computePlugin;
    private MeshFilter _sourceMeshFilter;
    private MeshRenderer _sourceMeshRenderer;
    private int _particleStride;

    private WaterPhaseResources _resources;
    private WaterPhaseDensityPipeline _densityPipeline;
    private WaterPhaseRaymarchRenderer _raymarchRenderer;
    private WaterPhaseMarchingCubesRenderer _marchingRenderer;
    private WaterPhaseScreenSpaceFluidRenderer _screenSpaceFluidRenderer;
    private UnityParticleOutputBridge _particleOutputBridge;
    private bool _usesDensityPipeline;
    private System.Action<Vector3, Vector3> _renderAction;

    private void Awake()
    {
        CacheComponents();
        CreateHelpers();
        _particleStride = Marshal.SizeOf<Particle>();

        // Wire computePlugin before ParticleRenderer.Start() runs so it can initialize its GPU buffers.
        if (_debugParticleRenderer != null && _debugParticleRenderer.computePlugin == null)
            _debugParticleRenderer.computePlugin = _computePlugin;
    }

    private void OnEnable()
    {
        CacheComponents();
        if (_particleStride <= 0)
            _particleStride = Marshal.SizeOf<Particle>();
    }

    private void Start()
    {
        if (_computePlugin == null || _sourceMeshFilter == null || _sourceMeshRenderer == null)
        {
            Debug.LogError("[PhysicsWaterPhaseBridge] Missing required components.");
            enabled = false;
            return;
        }

        if (!ValidateConfiguration())
        {
            enabled = false;
            return;
        }

        _usesDensityPipeline = settings.Rendering.mode != WaterSurfaceRenderMode.ScreenSpaceFluid;

        if (_usesDensityPipeline)
        {
            EnsureDensityPipeline();
            if (_densityPipeline == null)
            {
                enabled = false;
                return;
            }
            _densityPipeline.BindSmoothingParameters(settings, _computePlugin.restDensity);
        }

        if (settings.Rendering.mode == WaterSurfaceRenderMode.RaymarchVolume && _raymarchRenderer != null)
            visualProxyTransform = _raymarchRenderer.EnsureProxy(visualProxyTransform);

        PrimeResources();

        _renderAction = settings.Rendering.mode switch
        {
            WaterSurfaceRenderMode.RaymarchVolume    => RenderRaymarch,
            WaterSurfaceRenderMode.ScreenSpaceFluid  => RenderScreenSpaceFluid,
            _                                        => RenderMarchingCubes
        };
    }

    private void Update()
    {
        PrimeResources();
        _computePlugin.GetBoundsWS(out Vector3 boundsMin, out Vector3 boundsMax);

        // In debug mode skip the density pipeline and renderer entirely — we only want particles.
        if (!_showDebugParticles)
        {
            if (_usesDensityPipeline)
                _densityPipeline.Execute(_computePlugin, settings, _resources, boundsMin, boundsMax);

            _renderAction(boundsMin, boundsMax);
        }

        // Safe to set enabled here: by the time LateUpdate runs all Start()s have already fired.
        SyncDebugParticleRenderer();
    }

    private void OnDestroy()
    {
        if (_particleOutputBridge != null)
            _particleOutputBridge.ClearRegistration();

        if (_resources != null)
            _resources.Release();

        if (_screenSpaceFluidRenderer != null)
            _screenSpaceFluidRenderer.Release();

        if (_marchingRenderer != null)
            _marchingRenderer.Release();
    }

    private void PrimeResources()
    {
        bool resourcesChanged = _resources.Ensure(settings.DensityGrid.volumeDims, _computePlugin.particleCount, _particleStride);
        if (resourcesChanged)
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

        if (_screenSpaceFluidRenderer == null)
            _screenSpaceFluidRenderer = new WaterPhaseScreenSpaceFluidRenderer(_sourceMeshRenderer);
    }

    private bool ValidateConfiguration()
    {
        if (settings == null)
        {
            Debug.LogError("[PhysicsWaterPhaseBridge] Missing bridge settings instance.");
            return false;
        }

        if (settings.Rendering.mode == WaterSurfaceRenderMode.ScreenSpaceFluid)
        {
            if (settings.Rendering.screenSpaceFluidMaterial == null)
            {
                Debug.LogError("[PhysicsWaterPhaseBridge] Screen-space fluid mode requires a screenSpaceFluidMaterial.");
                return false;
            }

            return true;
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

    private void RenderRaymarch(Vector3 boundsMin, Vector3 boundsMax)
    {
        if (_raymarchRenderer == null) return;
        _raymarchRenderer.Render(
            visualProxyTransform,
            settings.Rendering.rayMarchingMaterial,
            _computePlugin,
            _resources,
            boundsMin,
            boundsMax);
    }

    private void RenderScreenSpaceFluid(Vector3 boundsMin, Vector3 boundsMax)
    {
        if (_screenSpaceFluidRenderer == null) return;
        _screenSpaceFluidRenderer.Render(settings, _computePlugin, _resources, boundsMin, boundsMax, gameObject.layer);
    }

    private void RenderMarchingCubes(Vector3 boundsMin, Vector3 boundsMax)
    {
        if (_marchingRenderer == null) return;
        _marchingRenderer.Render(settings, _resources, boundsMin, boundsMax, gameObject.layer);
    }

    private void OnValidate()
    {
        settings.DensityGrid.volumeDims.x = Mathf.Max(1, settings.DensityGrid.volumeDims.x);
        settings.DensityGrid.volumeDims.y = Mathf.Max(1, settings.DensityGrid.volumeDims.y);
        settings.DensityGrid.volumeDims.z = Mathf.Max(1, settings.DensityGrid.volumeDims.z);
        settings.DensityGrid.vapourSmoothingRadiusWS = Mathf.Max(0f, settings.DensityGrid.vapourSmoothingRadiusWS);
        settings.DensityGrid.maxKernelRadiusVoxels = Mathf.Clamp(settings.DensityGrid.maxKernelRadiusVoxels, 1, 8);

        settings.RaymarchBlur.Clamp();
        settings.MarchingCubesBlur.Clamp();

        ValidateAdaptiveSmoothing(settings.RaymarchSmoothing);
        ValidateAdaptiveSmoothing(settings.MarchingCubesSmoothing);

        settings.Rendering.marchingCubesIsoLevel = Mathf.Clamp01(settings.Rendering.marchingCubesIsoLevel);

        if (Application.isPlaying)
            SyncDebugParticleRenderer();
    }

    private void SyncDebugParticleRenderer()
    {
        if (_debugParticleRenderer != null)
            _debugParticleRenderer.enabled = _showDebugParticles;

        // Show/hide the raymarching proxy so it doesn't occlude the debug view.
        if (settings.Rendering.mode == WaterSurfaceRenderMode.RaymarchVolume && visualProxyTransform != null)
        {
            var proxyMr = visualProxyTransform.GetComponent<MeshRenderer>();
            if (proxyMr != null)
                proxyMr.enabled = !_showDebugParticles;
        }
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
