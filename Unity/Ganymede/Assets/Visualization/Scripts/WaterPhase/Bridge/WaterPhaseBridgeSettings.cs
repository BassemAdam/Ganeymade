using System;
using UnityEngine;

public enum WaterSurfaceRenderMode
{
    RaymarchVolume,
    MarchingCubesLiquidWithVapour
}

[Serializable]
public class WaterPhaseBridgeSettings
{
    [SerializeField] private WaterPhaseComputeAssetReferences references = new WaterPhaseComputeAssetReferences();
    [SerializeField] private WaterPhaseDensityGridSettings densityGrid = new WaterPhaseDensityGridSettings();
    [SerializeField] private WaterPhaseLiquidBlurSettings liquidBlur = new WaterPhaseLiquidBlurSettings();
    [SerializeField] private WaterPhaseVapourEnhancementSettings vapour = new WaterPhaseVapourEnhancementSettings();
    [SerializeField] private WaterPhaseRenderingSettings rendering = new WaterPhaseRenderingSettings();

    public WaterPhaseComputeAssetReferences References => references;
    public WaterPhaseDensityGridSettings DensityGrid => densityGrid;
    public WaterPhaseLiquidBlurSettings LiquidBlur => liquidBlur;
    public WaterPhaseVapourEnhancementSettings Vapour => vapour;
    public WaterPhaseRenderingSettings Rendering => rendering;
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

    [Tooltip("World-space radius used for SURFACE / lonely particles (low SPH density). " +
             "Keep small so splash droplets and thin sheets stay crisp. " +
             "If < 0, falls back to UseComputePlugin.smoothingRadius.")]
    public float smoothingRadiusWS = 1f;

    [Header("Adaptive (density-driven) smoothing")]
    [Tooltip("When ON, per-particle smoothing radius is lerped between 'smoothingRadiusWS' " +
             "(at adaptiveDensitySurface) and 'bulkSmoothingRadiusWS' (at adaptiveDensityBulk). " +
             "This gives splashes/droplets a small footprint and bulk water a wide, smooth one.")]
    public bool adaptiveRadiusEnabled = true;

    [Tooltip("World-space radius used for BULK particles (high SPH density). " +
             "Make this noticeably larger than 'smoothingRadiusWS' for a smoother body and normals. " +
             "Ignored when 'adaptiveRadiusEnabled' is OFF.")]
    [Min(0f)]
    public float bulkSmoothingRadiusWS = 2.5f;

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

    [Tooltip("Hard cap on the splat loop half-size in voxels. The loop runs (2r+1)^3 times per particle, " +
             "so r=4 → 729 taps, r=6 → 2197 taps, r=8 → 4913 taps. Larger bulk radii get TRUNCATED to this " +
             "cap rather than blowing up the GPU cost. If the truncation looks too small, raise this carefully.")]
    [Range(1, 8)]
    public int maxKernelRadiusVoxels = 4;
}

[Serializable]
public class WaterPhaseLiquidBlurSettings
{
    [Tooltip("Gaussian-blur the liquid slab after normalization to smooth out individual particle dots.")]
    public bool enabled;

    [Tooltip("Kernel half-size in voxels (1=3^3 taps, 2=5^3 taps, 3=7^3 taps). Larger = smoother but heavier.")]
    [Range(1, 4)]
    public int radius = 1;

    [Tooltip("Gaussian sigma in voxels. Small values give tight, localized blur; large values spread widely.")]
    [Range(0.1f, 4.0f)]
    public float sigma = 1.0f;

    [Tooltip("0 = pure smooth, 1 = original high-frequency detail fully added back on top of the blur.")]
    [Range(0f, 1f)]
    public float detailPreserve = 0.0f;
}

[Serializable]
public class WaterPhaseVapourEnhancementSettings
{
    [Tooltip("Enable GPU-side domain-warped FBM on the vapour density slab before raymarching.")]
    public bool enabled = true;

    [Tooltip("Gaussian-blur the vapour slab before noise enhancement to smooth out individual particle dots.")]
    public bool blurBeforeEnhance = true;

    [Tooltip("Kernel half-size in voxels (1=3^3 taps, 2=5^3 taps, 3=7^3 taps). Larger = smoother but heavier.")]
    [Range(1, 4)]
    public int blurRadius = 1;

    [Tooltip("Gaussian sigma in voxels. Small values give tight, localized blur; large values spread widely.")]
    [Range(0.1f, 4.0f)]
    public float blurSigma = 1.0f;

    [Tooltip("0 = pure smooth, 1 = original high-frequency detail fully added back on top of the blur.")]
    [Range(0f, 1f)]
    public float blurDetailPreserve = 0.25f;

    [Tooltip("Noise periods across the volume (e.g. 3 = three waves side-to-side). Higher = finer detail.")]
    [Min(0.1f)]
    public float noiseScale = 3.0f;

    [Tooltip("Direction the noise pattern drifts over time (will be normalized).")]
    public Vector3 noiseDriftDirection = new Vector3(0f, -1f, 0f);

    [Tooltip("Speed the noise drifts.")]
    [Min(0f)]
    public float noiseDriftSpeed = 0.85f;

    [Tooltip("Number of FBM octaves (1 = cheap, 6 = detailed).")]
    [Range(1, 8)]
    public int noiseOctaves = 4;

    [Tooltip("How strongly the domain warp displaces the sample position. Larger = more curling.")]
    [Range(0f, 2f)]
    public float domainWarpStrength = 0.8f;

    [Tooltip("Blend weight of the noise on top of the raw physics density. 0 = pure physics, 1 = maximum shaping.")]
    [Range(0f, 1f)]
    public float detailStrength = 0.5f;
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

    [Tooltip("Iso threshold applied to normalized density (0..1-ish). Higher values make the surface shrink.")]
    [Range(0f, 1f)]
    public float marchingCubesIsoLevel = 0.2f;
}
