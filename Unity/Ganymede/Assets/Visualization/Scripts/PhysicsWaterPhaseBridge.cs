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

    // Legacy serialized fields kept temporarily so existing scene/prefab values can migrate into
    // the grouped settings object without manual re-assignment.
    [SerializeField, HideInInspector] private ComputeShader particlesToDensityCompute;
    [SerializeField, HideInInspector] private ComputeShader marchingCubesCompute;
    [SerializeField, HideInInspector] private TextAsset marchingCubesLUT;
    [SerializeField, HideInInspector] private Vector3Int volumeDims = new Vector3Int(64, 64, 64);
    [SerializeField, HideInInspector] private float smoothingRadiusWS = 1f;
    [SerializeField, HideInInspector] private Material rayMarchingMaterial;
    [SerializeField, HideInInspector] private Material marchingCubesMaterial;
    [SerializeField, HideInInspector] private Material vapourRaymarchMaterial;
    [SerializeField, HideInInspector] private bool useMarchingCubes;
    [SerializeField, HideInInspector] private float marchingCubesIsoLevel = 0.2f;
    [SerializeField, HideInInspector] private bool enhanceVapourDensity = true;
    [SerializeField, HideInInspector] private bool blurVapourDensity = true;
    [SerializeField, HideInInspector] private bool blurLiquidDensity;
    [SerializeField, HideInInspector] private int liquidBlurRadius = 1;
    [SerializeField, HideInInspector] private float liquidBlurSigma = 1.0f;
    [SerializeField, HideInInspector] private float liquidBlurDetailPreserve;
    [SerializeField, HideInInspector] private int blurRadius = 1;
    [SerializeField, HideInInspector] private float blurSigma = 1.0f;
    [SerializeField, HideInInspector] private float blurDetailPreserve = 0.25f;
    [SerializeField, HideInInspector] private float vapourNoiseScale = 3.0f;
    [SerializeField, HideInInspector] private Vector3 vapourNoiseDriftDir = new Vector3(0f, -1f, 0f);
    [SerializeField, HideInInspector] private float vapourNoiseDriftSpeed = 0.85f;
    [SerializeField, HideInInspector] private int vapourNoiseOctaves = 4;
    [SerializeField, HideInInspector] private float vapourNoiseDomainWarpStrength = 0.8f;
    [SerializeField, HideInInspector] private float vapourNoiseDetailStrength = 0.5f;
    [SerializeField, HideInInspector] private bool _settingsMigrated;

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
        EnsureSettingsMigrated();
        CacheComponents();
        CreateHelpers();
        _particleStride = Marshal.SizeOf<Particle>();

        if (_raymarchRenderer != null)
            visualProxyTransform = _raymarchRenderer.EnsureProxy(visualProxyTransform);
    }

    private void OnEnable()
    {
        EnsureSettingsMigrated();
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
        EnsureSettingsMigrated();
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

    private void EnsureSettingsMigrated()
    {
        if (settings == null)
            settings = new WaterPhaseBridgeSettings();

        if (_settingsMigrated)
            return;

        settings.References.particlesToDensityCompute = particlesToDensityCompute;
        settings.References.marchingCubesCompute = marchingCubesCompute;
        settings.References.marchingCubesLUT = marchingCubesLUT;

        settings.DensityGrid.volumeDims = volumeDims;
        settings.DensityGrid.smoothingRadiusWS = smoothingRadiusWS;

        settings.LiquidBlur.enabled = blurLiquidDensity;
        settings.LiquidBlur.radius = liquidBlurRadius;
        settings.LiquidBlur.sigma = liquidBlurSigma;
        settings.LiquidBlur.detailPreserve = liquidBlurDetailPreserve;

        settings.Vapour.enabled = enhanceVapourDensity;
        settings.Vapour.blurBeforeEnhance = blurVapourDensity;
        settings.Vapour.blurRadius = blurRadius;
        settings.Vapour.blurSigma = blurSigma;
        settings.Vapour.blurDetailPreserve = blurDetailPreserve;
        settings.Vapour.noiseScale = vapourNoiseScale;
        settings.Vapour.noiseDriftDirection = vapourNoiseDriftDir;
        settings.Vapour.noiseDriftSpeed = vapourNoiseDriftSpeed;
        settings.Vapour.noiseOctaves = vapourNoiseOctaves;
        settings.Vapour.domainWarpStrength = vapourNoiseDomainWarpStrength;
        settings.Vapour.detailStrength = vapourNoiseDetailStrength;

        settings.Rendering.mode = useMarchingCubes
            ? WaterSurfaceRenderMode.MarchingCubesLiquidWithVapour
            : WaterSurfaceRenderMode.RaymarchVolume;
        settings.Rendering.rayMarchingMaterial = rayMarchingMaterial;
        settings.Rendering.marchingCubesMaterial = marchingCubesMaterial;
        settings.Rendering.vapourRaymarchMaterial = vapourRaymarchMaterial;
        settings.Rendering.marchingCubesIsoLevel = marchingCubesIsoLevel;

        _settingsMigrated = true;
    }

    private void OnValidate()
    {
        EnsureSettingsMigrated();

        settings.DensityGrid.volumeDims.x = Mathf.Max(1, settings.DensityGrid.volumeDims.x);
        settings.DensityGrid.volumeDims.y = Mathf.Max(1, settings.DensityGrid.volumeDims.y);
        settings.DensityGrid.volumeDims.z = Mathf.Max(1, settings.DensityGrid.volumeDims.z);

        settings.LiquidBlur.radius = Mathf.Clamp(settings.LiquidBlur.radius, 1, 4);
        settings.LiquidBlur.sigma = Mathf.Clamp(settings.LiquidBlur.sigma, 0.1f, 4.0f);
        settings.LiquidBlur.detailPreserve = Mathf.Clamp01(settings.LiquidBlur.detailPreserve);

        settings.Vapour.blurRadius = Mathf.Clamp(settings.Vapour.blurRadius, 1, 4);
        settings.Vapour.blurSigma = Mathf.Clamp(settings.Vapour.blurSigma, 0.1f, 4.0f);
        settings.Vapour.blurDetailPreserve = Mathf.Clamp01(settings.Vapour.blurDetailPreserve);
        settings.Vapour.noiseScale = Mathf.Max(0.1f, settings.Vapour.noiseScale);
        settings.Vapour.noiseDriftSpeed = Mathf.Max(0f, settings.Vapour.noiseDriftSpeed);
        settings.Vapour.noiseOctaves = Mathf.Clamp(settings.Vapour.noiseOctaves, 1, 8);
        settings.Vapour.domainWarpStrength = Mathf.Clamp(settings.Vapour.domainWarpStrength, 0f, 2f);
        settings.Vapour.detailStrength = Mathf.Clamp01(settings.Vapour.detailStrength);

        settings.Rendering.marchingCubesIsoLevel = Mathf.Clamp01(settings.Rendering.marchingCubesIsoLevel);
    }
}
