using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Scene-facing coordinator for the water phase bridge.
/// It validates configuration, keeps shared helpers alive, and orchestrates the
/// per-frame flow: native GPU particle output -> density pipeline -> active renderer.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(UseComputePlugin))]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PhysicsWaterPhaseBridge : MonoBehaviour
{
    [SerializeField] private WaterPhaseBridgeSettings settings = new WaterPhaseBridgeSettings();

    // The visual proxy is a GameObject used by the raymarch renderer to host a mesh that the volume shader renders through.
    // It's public so the renderer can hand it back after creating it, and HideInInspector keeps it out of the Inspector clutter.
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

    // This stores whichever render method matches the current mode so LateUpdate can call it
    // without an if/switch every frame. It gets assigned once in Start and stays valid for the lifetime of the component.
    private System.Action<Vector3, Vector3> _renderAction;

    private void Awake()
    {
        CacheComponents();
        CreateHelpers();
        // Compute the byte size of one Particle struct as it would appear in unmanaged GPU memory.
        // We need this to create the compute buffer with the right stride so the GPU reads particle data correctly.
        _particleStride = Marshal.SizeOf<Particle>();
    }

    private void OnEnable()
    {
        // CacheComponents runs again here because the component could be re-enabled after being disabled at runtime,
        // at which point Awake won't run again but we still need valid references.
        CacheComponents();
        // Guard in case Awake somehow didn't run or the value got reset. Stride must never be zero or the compute buffer would be invalid.
        if (_particleStride <= 0)
            _particleStride = Marshal.SizeOf<Particle>();
    }

    private void Start()
    {
        if (_computePlugin == null || _sourceMeshFilter == null || _sourceMeshRenderer == null)
        {
            Debug.LogError("[PhysicsWaterPhaseBridge] Missing required components.");
            // Setting enabled to false stops Unity from calling Update and LateUpdate, which would otherwise
            // crash or produce garbage output since the required components aren't there.
            enabled = false;
            return;
        }

        if (!ValidateConfiguration())
        {
            // ValidateConfiguration already logged the specific reason. We just disable here so the
            // component goes silent rather than spamming errors every frame in LateUpdate.
            enabled = false;
            return;
        }

        // Screen space fluid skips the density pipeline entirely. All other modes (raymarch, marching cubes)
        // need particles converted to a density grid first, so they go through the pipeline.
        _usesDensityPipeline = settings.Rendering.mode != WaterSurfaceRenderMode.ScreenSpaceFluid;

        if (_usesDensityPipeline)
        {
            EnsureDensityPipeline();
            if (_densityPipeline == null)
            {
                // EnsureDensityPipeline failed, likely because the compute shader reference is missing.
                // Disable rather than let LateUpdate call Execute on a null pipeline.
                enabled = false;
                return;
            }
            _densityPipeline.BindSmoothingParameters(settings, _computePlugin.restDensity);
        }

        // The raymarch renderer needs a proxy mesh GameObject to exist before it can render.
        // EnsureProxy either reuses the one passed in or creates a new one, then returns whichever it used.
        // We store it back so future calls to EnsureProxy keep reusing the same object instead of spawning new ones.
        if (settings.Rendering.mode == WaterSurfaceRenderMode.RaymarchVolume && _raymarchRenderer != null)
            visualProxyTransform = _raymarchRenderer.EnsureProxy(visualProxyTransform);

        // Allocate GPU buffers and register them with the particle output bridge.
        // This must happen before the first render call so the buffers exist when shaders try to sample them.
        PrimeResources();

        // Lock in the render function based on the mode so we don't branch every frame in LateUpdate.
        // The _ wildcard falls back to marching cubes for any mode not explicitly listed.
        _renderAction = settings.Rendering.mode switch
        {
            WaterSurfaceRenderMode.RaymarchVolume    => RenderRaymarch,
            WaterSurfaceRenderMode.ScreenSpaceFluid  => RenderScreenSpaceFluid,
            _                                        => RenderMarchingCubes
        };
    }

    private void LateUpdate()
    {
        // PrimeResources checks whether buffer dimensions have changed (e.g. particle count grew)
        // and reallocates if needed. We call it every frame because the simulation can change size at runtime.
        PrimeResources();
        _computePlugin.GetBoundsWS(out Vector3 boundsMin, out Vector3 boundsMax);

        // Only modes that use a density grid need the pipeline to run. SSF skips straight to rendering.
        if (_usesDensityPipeline)
            _densityPipeline.Execute(_computePlugin, settings, _resources, boundsMin, boundsMax);

        _renderAction(boundsMin, boundsMax);
    }

    private void OnDestroy()
    {
        // The particle output bridge registers a compute buffer with an external system.
        // We must unregister when destroyed or the external system holds a dangling reference to a released buffer.
        if (_particleOutputBridge != null)
            _particleOutputBridge.ClearRegistration();

        // GPU buffers must be explicitly released. Unity's garbage collector does not free GPU memory.
        // If we skip this, the buffers leak until the process exits.
        if (_resources != null)
            _resources.Release();

        // The SSF renderer subscribes to global render events. Release unsubscribes it so it stops
        // receiving callbacks after the GameObject is destroyed.
        if (_screenSpaceFluidRenderer != null)
            _screenSpaceFluidRenderer.Release();

        if (_marchingRenderer != null)
            _marchingRenderer.Release();
    }

    private void PrimeResources()
    {
        // Ensure returns true only when buffers were reallocated (first call or size change).
        // We only re-register the buffer in that case to avoid unnecessary overhead every frame.
        bool resourcesChanged = _resources.Ensure(settings.DensityGrid.volumeDims, _computePlugin.particleCount, _particleStride);
        if (resourcesChanged)
            _particleOutputBridge.RegisterIfNeeded(_resources.ParticleOutputBuffer);
    }

    private void CacheComponents()
    {
        // Each check guards against overwriting a valid reference. Awake and OnEnable both call this,
        // so without the guard the second call would do a redundant GetComponent lookup every time.
        if (_computePlugin == null)
            _computePlugin = GetComponent<UseComputePlugin>();
        if (_sourceMeshFilter == null)
            _sourceMeshFilter = GetComponent<MeshFilter>();
        if (_sourceMeshRenderer == null)
            _sourceMeshRenderer = GetComponent<MeshRenderer>();
    }

    private void CreateHelpers()
    {
        // Each helper is created only once. These are plain C# objects (not MonoBehaviours) so Unity won't
        // create them for us. The null checks prevent recreating them if CreateHelpers is ever called again.
        if (_resources == null)
            _resources = new WaterPhaseResources();

        if (_particleOutputBridge == null)
            _particleOutputBridge = new UnityParticleOutputBridge();

        // Raymarch and marching cubes renderers need the mesh components to initialize correctly.
        // If those components aren't cached yet we skip creation here and they'll be null-guarded later.
        if (_raymarchRenderer == null && _sourceMeshFilter != null && _sourceMeshRenderer != null)
            _raymarchRenderer = new WaterPhaseRaymarchRenderer(transform, _sourceMeshFilter, _sourceMeshRenderer);

        if (_marchingRenderer == null && _sourceMeshFilter != null)
            _marchingRenderer = new WaterPhaseMarchingCubesRenderer(transform, _sourceMeshFilter);

        // SSF renderer subscribes to global draw events in its constructor.
        // Creating it unconditionally is fine because it only produces output when IsActive is set to true.
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

        // SSF mode only needs its own material. It doesn't use the density pipeline so we can return
        // early here without checking for compute shaders that aren't relevant to this mode.
        if (settings.Rendering.mode == WaterSurfaceRenderMode.ScreenSpaceFluid)
        {
            if (settings.Rendering.screenSpaceFluidMaterial == null)
            {
                Debug.LogError("[PhysicsWaterPhaseBridge] Screen-space fluid mode requires a screenSpaceFluidMaterial.");
                return false;
            }

            return true;
        }

        // All non-SSF modes need the particles-to-density compute shader to build the density grid.
        if (settings.References.particlesToDensityCompute == null)
        {
            Debug.LogError("[PhysicsWaterPhaseBridge] Missing particles-to-density compute shader.");
            return false;
        }

        // Raymarch only needs its material on top of the density compute. Return early so we don't
        // wrongly require marching cubes assets when this mode doesn't use them.
        if (settings.Rendering.mode == WaterSurfaceRenderMode.RaymarchVolume)
        {
            if (settings.Rendering.rayMarchingMaterial == null)
            {
                Debug.LogError("[PhysicsWaterPhaseBridge] Raymarch mode requires a ray marching material.");
                return false;
            }

            return true;
        }

        // Marching cubes needs all three: the compute shader that extracts the mesh,
        // the LUT that encodes which triangles to generate per cube configuration,
        // and the material to render the resulting mesh.
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

        // Only recreate the pipeline if it doesn't exist yet or if the compute shader reference changed.
        // Creating a new pipeline is expensive so we reuse the existing one whenever possible.
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
        // Clamp grid dimensions to at least 1 to prevent a zero-size compute dispatch which would crash the GPU driver.
        settings.DensityGrid.volumeDims.x = Mathf.Max(1, settings.DensityGrid.volumeDims.x);
        settings.DensityGrid.volumeDims.y = Mathf.Max(1, settings.DensityGrid.volumeDims.y);
        settings.DensityGrid.volumeDims.z = Mathf.Max(1, settings.DensityGrid.volumeDims.z);
        settings.DensityGrid.vapourSmoothingRadiusWS = Mathf.Max(0f, settings.DensityGrid.vapourSmoothingRadiusWS);
        settings.DensityGrid.maxKernelRadiusVoxels = Mathf.Clamp(settings.DensityGrid.maxKernelRadiusVoxels, 1, 8);

        // Clamp blur and smoothing settings to their valid ranges to avoid negative or nonsensical values
        // that would produce visual artifacts or divide-by-zero in the shaders.
        settings.RaymarchBlur.Clamp();
        settings.MarchingCubesBlur.Clamp();

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
        // Bulk density must always be strictly greater than surface density so the adaptive blend has a valid range.
        // The 0.01f gap prevents them from being equal which would cause a divide-by-zero in the blend curve.
        smoothing.adaptiveDensityBulk = Mathf.Max(smoothing.adaptiveDensitySurface + 0.01f, smoothing.adaptiveDensityBulk);
        smoothing.adaptiveDensityCurve = Mathf.Clamp(smoothing.adaptiveDensityCurve, 0.05f, 4f);
    }
}
