using System;
using UnityEngine;
using UnityEngine.Serialization;

public enum WaterSurfaceRenderMode
{
    RaymarchVolume,
    MarchingCubesLiquidWithVapour,
    ScreenSpaceFluid
}

[Serializable]
public class WaterPhaseBridgeSettings
{
    [SerializeField] private WaterPhaseComputeAssetReferences references = new WaterPhaseComputeAssetReferences();
    [SerializeField] private WaterPhaseDensityGridSettings densityGrid = new WaterPhaseDensityGridSettings();
    [Header("Raymarch density blur")]
    [FormerlySerializedAs("blur")]
    [FormerlySerializedAs("vapour")]
    [SerializeField] private WaterPhaseDensityBlurSettings raymarchBlur = new WaterPhaseDensityBlurSettings();
    [Header("Marching cubes density blur")]
    [FormerlySerializedAs("marchingCubesLiquidBlur")]
    [SerializeField] private WaterPhaseDensityBlurSettings marchingCubesBlur = new WaterPhaseDensityBlurSettings();
    [Header("Raymarch adaptive liquid smoothing")]
    [SerializeField] private WaterPhaseAdaptiveSmoothingSettings raymarchSmoothing = new WaterPhaseAdaptiveSmoothingSettings();
    [Header("Marching cubes adaptive liquid smoothing")]
    [SerializeField] private WaterPhaseAdaptiveSmoothingSettings marchingCubesSmoothing = new WaterPhaseAdaptiveSmoothingSettings();
    [SerializeField] private WaterPhaseRenderingSettings rendering = new WaterPhaseRenderingSettings();

    public WaterPhaseComputeAssetReferences References => references ?? (references = new WaterPhaseComputeAssetReferences());
    public WaterPhaseDensityGridSettings DensityGrid => densityGrid ?? (densityGrid = new WaterPhaseDensityGridSettings());
    public WaterPhaseDensityBlurSettings RaymarchBlur => raymarchBlur ?? (raymarchBlur = new WaterPhaseDensityBlurSettings());
    public WaterPhaseDensityBlurSettings Blur => RaymarchBlur;
    public WaterPhaseDensityBlurSettings MarchingCubesBlur => marchingCubesBlur ?? (marchingCubesBlur = new WaterPhaseDensityBlurSettings());
    public WaterPhaseAdaptiveSmoothingSettings RaymarchSmoothing => raymarchSmoothing ?? (raymarchSmoothing = new WaterPhaseAdaptiveSmoothingSettings());
    public WaterPhaseAdaptiveSmoothingSettings MarchingCubesSmoothing => marchingCubesSmoothing ?? (marchingCubesSmoothing = new WaterPhaseAdaptiveSmoothingSettings());
    public WaterPhaseRenderingSettings Rendering => rendering ?? (rendering = new WaterPhaseRenderingSettings());

    public WaterPhaseDensityBlurSettings ActiveBlur => Rendering.mode == WaterSurfaceRenderMode.MarchingCubesLiquidWithVapour
        ? MarchingCubesBlur
        : RaymarchBlur;

    public WaterPhaseAdaptiveSmoothingSettings ActiveSmoothing => Rendering.mode == WaterSurfaceRenderMode.MarchingCubesLiquidWithVapour
        ? MarchingCubesSmoothing
        : RaymarchSmoothing;
}

[Serializable]
public class WaterPhaseComputeAssetReferences
{
    [Tooltip("Compute shader that clears + splats particles into a density grid (ParticlesToDensityGrid.compute)")]
    public ComputeShader particlesToDensityCompute;

    [Tooltip("Compute shader that runs marching cubes on the density grid (MarchingCubesCompute.compute)")]
    public ComputeShader marchingCubesCompute;

    [Tooltip("Flat LUT text file for marching cubes triangle table (MarchingCubesLUT.txt)")]
    public TextAsset marchingCubesLUT;
}

[Serializable]
public class WaterPhaseDensityGridSettings
{
    [Tooltip("Voxel resolution of the physics density volume.")]
    public Vector3Int volumeDims = new Vector3Int(64, 64, 64);

    [Tooltip("World-space radius used for VAPOUR particles in both render modes. The vapour grid is consumed as a " +
             "low-frequency presence mask (procedural detail is added per-fragment), so a wider " +
             "radius produces a smoother, more uniform mask independent of the liquid smoothing profiles.")]
    [Min(0f)]
    public float vapourSmoothingRadiusWS = 2.5f;

    [Tooltip("Hard cap on the splat loop half-size in voxels. The loop runs (2r+1)^3 times per particle, " +
             "so r=4 → 729 taps, r=6 → 2197 taps, r=8 → 4913 taps. Larger radii get TRUNCATED to this " +
             "cap rather than blowing up the GPU cost. If the truncation looks too small, raise this carefully.")]
    [Range(1, 8)]
    public int maxKernelRadiusVoxels = 4;
}

[Serializable]
public class WaterPhaseAdaptiveSmoothingSettings
{
    [Tooltip("World-space radius used for LIQUID SURFACE / lonely particles (low SPH density). " +
             "Keep small so splash droplets and thin sheets stay crisp. " +
             "If < 0, falls back to UseComputePlugin.smoothingRadius.")]
    [FormerlySerializedAs("smoothingRadiusWS")]
    public float liquidSmoothingRadiusWS = 1f;

    [Header("Adaptive (density-driven) smoothing")]
    [Tooltip("When ON, per-particle smoothing radius is lerped between 'liquidSmoothingRadiusWS' " +
             "(at adaptiveDensitySurface) and 'liquidBulkSmoothingRadiusWS' (at adaptiveDensityBulk). " +
             "This gives splashes/droplets a small footprint and bulk water a wide, smooth one.")]
    public bool adaptiveRadiusEnabled = true;

    [Tooltip("World-space radius used for LIQUID BULK particles (high SPH density). " +
             "Make this noticeably larger than 'liquidSmoothingRadiusWS' for a smoother body and normals. " +
             "Ignored when 'adaptiveRadiusEnabled' is OFF.")]
    [Min(0f)]
    [FormerlySerializedAs("bulkSmoothingRadiusWS")]
    public float liquidBulkSmoothingRadiusWS = 2.5f;

    [Tooltip("SPH density value treated as 'surface / lonely' (uses surface radius). " +
             "Tune from your visualizer: pick the density observed on splash / surface particles (e.g. ~175).")]
    [Min(0f)]
    public float adaptiveDensitySurface = 175f;

    [Tooltip("SPH density value treated as 'fully bulk' (uses bulk radius). " +
             "Tune from your visualizer: pick the density observed deep inside the water body (e.g. ~300).")]
    [Min(0f)]
    public float adaptiveDensityBulk = 300f;

    [Tooltip("Gamma curve applied to the density→radius mapping.\n" +
             " 1.0  = linear interpolation (default).\n" +
             " < 1  = EXAGGERATE differences among LOW-density particles (splashes, lonely / moving particles, " +
                     "thin sheets). Recommended 0.25–0.5 to give visible radius variation across particles whose " +
                     "densities cluster tightly just above the rest density.\n" +
             " > 1  = exaggerate differences among HIGH-density bulk particles instead.")]
    [Range(0.05f, 4f)]
    public float adaptiveDensityCurve = 0.4f;
}

[Serializable]
public class WaterPhaseDensityBlurSettings
{
    [Header("Liquid density blur")]
    [Tooltip("Gaussian-blur the LIQUID (R) density channel for this render mode after normalization and before normal baking.")]
    [FormerlySerializedAs("blurLiquidDensity")]
    public bool liquidBlurEnabled = false;

    [Tooltip("Kernel half-size in voxels for the LIQUID channel in this render mode (1=3^3 taps, 2=5^3 taps, 3=7^3 taps). Larger = smoother but heavier.")]
    [Range(1, 4)]
    public int liquidBlurRadius = 1;

    [Tooltip("Gaussian sigma in voxels for the LIQUID channel in this render mode. Small values give tight, localized blur; large values spread widely.")]
    [Range(0.1f, 4.0f)]
    public float liquidBlurSigma = 1.0f;

    [Tooltip("0 = pure smooth liquid, 1 = original liquid high-frequency detail fully added back on top of the blur for this render mode.")]
    [Range(0f, 1f)]
    public float liquidBlurDetailPreserve = 0.0f;

    [Header("Vapour density blur")]
    [Tooltip("Gaussian-blur the VAPOUR (G) density channel for this render mode after normalization and before normal baking.")]
    [FormerlySerializedAs("blurVapourDensity")]
    [FormerlySerializedAs("enabled")]
    public bool vapourBlurEnabled = true;

    [Tooltip("Kernel half-size in voxels for the VAPOUR channel in this render mode (1=3^3 taps, 2=5^3 taps, 3=7^3 taps). Larger = smoother but heavier.")]
    [Range(1, 4)]
    [FormerlySerializedAs("blurRadius")]
    [FormerlySerializedAs("radius")]
    public int vapourBlurRadius = 1;

    [Tooltip("Gaussian sigma in voxels for the VAPOUR channel in this render mode. Small values give tight, localized blur; large values spread widely.")]
    [Range(0.1f, 4.0f)]
    [FormerlySerializedAs("blurSigma")]
    [FormerlySerializedAs("sigma")]
    public float vapourBlurSigma = 1.0f;

    [Tooltip("0 = pure smooth vapour, 1 = original vapour high-frequency detail fully added back on top of the blur for this render mode.")]
    [Range(0f, 1f)]
    [FormerlySerializedAs("blurDetailPreserve")]
    [FormerlySerializedAs("detailPreserve")]
    public float vapourBlurDetailPreserve = 0.25f;

    public bool IsChannelEnabled(int channel)
    {
        return channel == 0 ? liquidBlurEnabled : vapourBlurEnabled;
    }

    public int GetRadius(int channel)
    {
        return channel == 0 ? liquidBlurRadius : vapourBlurRadius;
    }

    public float GetSigma(int channel)
    {
        return channel == 0 ? liquidBlurSigma : vapourBlurSigma;
    }

    public float GetDetailPreserve(int channel)
    {
        return channel == 0 ? liquidBlurDetailPreserve : vapourBlurDetailPreserve;
    }

    public void Clamp()
    {
        liquidBlurRadius = Mathf.Clamp(liquidBlurRadius, 1, 4);
        liquidBlurSigma = Mathf.Clamp(liquidBlurSigma, 0.1f, 4.0f);
        liquidBlurDetailPreserve = Mathf.Clamp01(liquidBlurDetailPreserve);

        vapourBlurRadius = Mathf.Clamp(vapourBlurRadius, 1, 4);
        vapourBlurSigma = Mathf.Clamp(vapourBlurSigma, 0.1f, 4.0f);
        vapourBlurDetailPreserve = Mathf.Clamp01(vapourBlurDetailPreserve);
    }
}

[Serializable]
public class WaterPhaseRenderingSettings
{
    [Tooltip("Controls which presentation path renders the liquid volume.")]
    public WaterSurfaceRenderMode mode = WaterSurfaceRenderMode.RaymarchVolume;

    [Tooltip("Material used for the raymarched volume presentation.")]
    public Material rayMarchingMaterial;

    [Tooltip("Material used for the marching cubes liquid surface.")]
    public Material marchingCubesMaterial;

    [Tooltip("Material used for the vapour volumetric box when marching cubes mode is active.")]
    public Material vapourRaymarchMaterial;

    [Tooltip("Material used for screen-space fluid rendering. Particle radius, blur, smoothness, refraction, and reflection are configured on this material/shader.")]
    public Material screenSpaceFluidMaterial;

    [Tooltip("Iso threshold applied to normalized density (0..1-ish). Higher values make the surface shrink.")]
    [Range(0f, 1f)]
    public float marchingCubesIsoLevel = 0.2f;
}
