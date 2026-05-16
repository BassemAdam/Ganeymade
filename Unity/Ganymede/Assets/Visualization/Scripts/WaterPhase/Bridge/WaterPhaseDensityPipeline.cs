using UnityEngine;

public sealed class WaterPhaseDensityPipeline
{
    private const float ParticleContribution = 1.0f;
    private const float FixedPointScale = 1024.0f;
    private const float LiquidInvDensityScale = 1.0f;

    private readonly ComputeShader _computeShader;
    public ComputeShader ComputeShader => _computeShader;

    private readonly int _clearKernel;
    private readonly int _splatKernel;
    private readonly int _normalizeKernel;
    private readonly int _blurDensityKernel;
    private readonly int _bakeNormalsKernel;
    private readonly int _clearVelocityKernel;
    private readonly int _splatVelocityKernel;
    private readonly int _normalizeVelocityKernel;
    private readonly int _advectVapourNoiseKernel;
    private int _lastBoundResourcesVersion = -1;
    // Cached group counts — only change when volumeDims changes (tied to resource version)
    private int _groupsX, _groupsY, _groupsZ;
    // Cached static parameters — computed once in BindSmoothingParameters, never change at runtime
    private float _cachedLoopRadius;
    private int _cachedMaxKernelRadiusVoxels = 1;
    private Vector3 _lastBoundsSize = new Vector3(float.NaN, 0f, 0f);

    private static readonly int ID_VolumeDims = Shader.PropertyToID("_VolumeDims");
    private static readonly int ID_ParticleCount = Shader.PropertyToID("_ParticleCount");
    private static readonly int ID_BoundsMinWS = Shader.PropertyToID("_BoundsMinWS");
    private static readonly int ID_BoundsMaxWS = Shader.PropertyToID("_BoundsMaxWS");
    private static readonly int ID_ParticleContribution = Shader.PropertyToID("_ParticleContribution");
    private static readonly int ID_FixedPointScale = Shader.PropertyToID("_FixedPointScale");
    private static readonly int ID_SmoothingRadiusWS_LiquidSmall = Shader.PropertyToID("_SmoothingRadiusWS_LiquidSmall");
    private static readonly int ID_SmoothingRadiusWS_LiquidBulk = Shader.PropertyToID("_SmoothingRadiusWS_LiquidBulk");
    private static readonly int ID_SmoothingRadiusWS_Vapour = Shader.PropertyToID("_SmoothingRadiusWS_Vapour");
    private static readonly int ID_AdaptiveDensitySurface = Shader.PropertyToID("_AdaptiveDensitySurface");
    private static readonly int ID_AdaptiveDensityBulk = Shader.PropertyToID("_AdaptiveDensityBulk");
    private static readonly int ID_AdaptiveDensityCurve = Shader.PropertyToID("_AdaptiveDensityCurve");
    private static readonly int ID_KernelRadiusVoxels = Shader.PropertyToID("_KernelRadiusVoxels");
    private static readonly int ID_RestDensity = Shader.PropertyToID("_RestDensity");
    private static readonly int ID_InvDensityScaleRG = Shader.PropertyToID("_InvDensityScaleRG");
    private static readonly int ID_BlurRadius = Shader.PropertyToID("_BlurRadius");
    private static readonly int ID_BlurSigma = Shader.PropertyToID("_BlurSigma");
    private static readonly int ID_BlurDetailPreserve = Shader.PropertyToID("_BlurDetailPreserve");
    private static readonly int ID_BlurChannel = Shader.PropertyToID("_BlurChannel");
    private static readonly int ID_VelocityTexture3D = Shader.PropertyToID("_VelocityTexture3D");
    private static readonly int ID_VapourNoiseSrc = Shader.PropertyToID("_VapourNoiseSrc");
    private static readonly int ID_VapourNoiseDst = Shader.PropertyToID("_VapourNoiseDst");
    private static readonly int ID_DeltaTime = Shader.PropertyToID("_DeltaTime");
    private static readonly int ID_NoiseAdvectSpeed = Shader.PropertyToID("_NoiseAdvectSpeed");
    private static readonly int ID_NoiseInjectionRate = Shader.PropertyToID("_NoiseInjectionRate");
    private static readonly int ID_VapourPresenceThresholdCompute = Shader.PropertyToID("_VapourPresenceThresholdCompute");

    public WaterPhaseDensityPipeline(ComputeShader computeShader)
    {
        _computeShader = computeShader;
        _clearKernel = _computeShader.FindKernel("ClearGrid");
        _splatKernel = _computeShader.FindKernel("SplatParticles");
        _normalizeKernel = _computeShader.FindKernel("NormalizeToTexture");
        _blurDensityKernel = _computeShader.FindKernel("BlurDensity");
        _bakeNormalsKernel = _computeShader.FindKernel("BakeNormals");
        _clearVelocityKernel = _computeShader.FindKernel("ClearVelocityGrid");
        _splatVelocityKernel = _computeShader.FindKernel("SplatVapourVelocity");
        _normalizeVelocityKernel = _computeShader.FindKernel("NormalizeVelocityToTexture");
        _advectVapourNoiseKernel = _computeShader.FindKernel("AdvectVapourNoise");

        // True constants — never change for the lifetime of this pipeline
        _computeShader.SetFloat(ID_NoiseAdvectSpeed, 6.0f);   // fast enough to see streaming within a second
        _computeShader.SetFloat(ID_NoiseInjectionRate, 0.5f); // fills the texture in ~5 frames so it dominates quickly
        _computeShader.SetFloat(ID_VapourPresenceThresholdCompute, 0.005f); // lower threshold catches sparse vapour

        // True constants — never change for the lifetime of this pipeline
        _computeShader.SetFloat(ID_ParticleContribution, ParticleContribution);
        _computeShader.SetFloat(ID_FixedPointScale, FixedPointScale);
        _computeShader.SetVector(ID_InvDensityScaleRG, new Vector4(LiquidInvDensityScale, 1.0f, 0f, 0f));
    }

    public void Execute(
        UseComputePlugin computePlugin,
        WaterPhaseBridgeSettings settings,
        WaterPhaseResources resources,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        int particleCount = Mathf.Max(1, computePlugin.particleCount);

        BindStaticResourcesIfNeeded(resources);
        BindPerFrameParameters(particleCount, resources.VolumeDims, boundsMin, boundsMax);

        int particleGroups = Mathf.Max(1, Mathf.CeilToInt(particleCount / 256.0f));

        _computeShader.Dispatch(_clearKernel, _groupsX, _groupsY, _groupsZ);
        _computeShader.Dispatch(_splatKernel, particleGroups, 1, 1);
        _computeShader.Dispatch(_normalizeKernel, _groupsX, _groupsY, _groupsZ);

        DispatchDensityBlurIfEnabled(settings.ActiveBlur, resources, _groupsX, _groupsY, _groupsZ, 0);
        DispatchDensityBlurIfEnabled(settings.ActiveBlur, resources, _groupsX, _groupsY, _groupsZ, 1);
        DispatchNormalBake(resources, _groupsX, _groupsY, _groupsZ);

        _computeShader.Dispatch(_clearVelocityKernel,     _groupsX, _groupsY, _groupsZ);
        _computeShader.Dispatch(_splatVelocityKernel,     particleGroups, 1, 1);
        _computeShader.Dispatch(_normalizeVelocityKernel, _groupsX, _groupsY, _groupsZ);

        // Semi-Lagrangian noise advection: back-trace each voxel along the freshly-written
        // velocity field, trilinear-sample the source texture, inject fresh noise where
        // vapour particles are present.  Source/destination ping-pong each frame.
        _computeShader.SetFloat(ID_DeltaTime, Time.deltaTime);
        _computeShader.SetTexture(_advectVapourNoiseKernel, ID_VapourNoiseSrc, resources.VapourNoiseSrcTex);
        _computeShader.SetTexture(_advectVapourNoiseKernel, ID_VapourNoiseDst, resources.VapourNoiseDstTex);
        _computeShader.SetTexture(_advectVapourNoiseKernel, ID_VelocityTexture3D, resources.VapourVelocityTexture);
        _computeShader.SetTexture(_advectVapourNoiseKernel, "_DensityTexture3D_Read", resources.PhaseDensityTexture);
        _computeShader.Dispatch(_advectVapourNoiseKernel, _groupsX, _groupsY, _groupsZ);
        resources.SwapVapourNoisePingPong();
    }

    private void BindStaticResourcesIfNeeded(WaterPhaseResources resources)
    {
        if (resources == null || _lastBoundResourcesVersion == resources.Version)
            return;

        _computeShader.SetBuffer(_clearKernel, "_DensityGrid", resources.DensityGridBuffer);
        _computeShader.SetBuffer(_splatKernel, "_DensityGrid", resources.DensityGridBuffer);
        _computeShader.SetBuffer(_splatKernel, "_ParticleBuffer", resources.ParticleOutputBuffer);
        _computeShader.SetBuffer(_normalizeKernel, "_DensityGrid", resources.DensityGridBuffer);
        _computeShader.SetTexture(_normalizeKernel, "_DensityTexture3D_RG", resources.PhaseDensityTexture);
        _computeShader.SetTexture(_blurDensityKernel, "_DensityTexture3D_Read", resources.PhaseDensityTexture);
        _computeShader.SetTexture(_blurDensityKernel, "_DensityTexture3D_RG", resources.PhaseDensityScratchTexture);
        _computeShader.SetTexture(_bakeNormalsKernel, "_DensityTexture3D_Read", resources.PhaseDensityTexture);
        _computeShader.SetTexture(_bakeNormalsKernel, "_NormalTexture3D", resources.SurfaceNormalTexture);

        // Velocity splat kernels
        _computeShader.SetBuffer(_clearVelocityKernel,     "_VelocityGrid", resources.VelocityGridBuffer);
        _computeShader.SetBuffer(_splatVelocityKernel,     "_VelocityGrid", resources.VelocityGridBuffer);
        _computeShader.SetBuffer(_splatVelocityKernel,     "_ParticleBuffer", resources.ParticleOutputBuffer);
        _computeShader.SetBuffer(_normalizeVelocityKernel, "_VelocityGrid", resources.VelocityGridBuffer);
        _computeShader.SetTexture(_normalizeVelocityKernel, "_VelocityTexture3D", resources.VapourVelocityTexture);
        // Advect kernel: static bindings (density read-texture; src/dst bound per-frame in Execute)
        _computeShader.SetTexture(_advectVapourNoiseKernel, "_DensityTexture3D_Read", resources.PhaseDensityTexture);

        // VolumeDims and group counts only change when resources are reallocated
        Vector3Int volumeDims = resources.VolumeDims;
        _computeShader.SetInts(ID_VolumeDims, volumeDims.x, volumeDims.y, volumeDims.z);
        _groupsX = Mathf.CeilToInt(volumeDims.x / 8.0f);
        _groupsY = Mathf.CeilToInt(volumeDims.y / 8.0f);
        _groupsZ = Mathf.CeilToInt(volumeDims.z / 8.0f);

        _lastBoundResourcesVersion = resources.Version;
    }

    /// <summary>
    /// Binds all static compute parameters that do not change at runtime.
    /// Call once from Start after EnsureDensityPipeline.
    /// </summary>
    public void BindSmoothingParameters(WaterPhaseBridgeSettings settings, float restDensity)
    {
        var grid = settings.DensityGrid;
        var smoothing = settings.ActiveSmoothing;

        float surfaceRadius = Mathf.Max(0f, smoothing.liquidSmoothingRadiusWS);
        float bulkRadius = smoothing.adaptiveRadiusEnabled
            ? smoothing.liquidBulkSmoothingRadiusWS
            : surfaceRadius;
        float vapourRadius = Mathf.Max(0f, grid.vapourSmoothingRadiusWS);

        _computeShader.SetFloat(ID_SmoothingRadiusWS_LiquidSmall, surfaceRadius);
        _computeShader.SetFloat(ID_SmoothingRadiusWS_LiquidBulk, bulkRadius);
        _computeShader.SetFloat(ID_SmoothingRadiusWS_Vapour, vapourRadius);
        _computeShader.SetFloat(ID_AdaptiveDensitySurface, Mathf.Max(0f, smoothing.adaptiveDensitySurface));
        _computeShader.SetFloat(ID_AdaptiveDensityBulk, Mathf.Max(smoothing.adaptiveDensitySurface + 0.01f, smoothing.adaptiveDensityBulk));
        _computeShader.SetFloat(ID_AdaptiveDensityCurve, Mathf.Max(0.01f, smoothing.adaptiveDensityCurve));

        // RestDensity and KernelRadius inputs are also static — bind once here.
        _computeShader.SetFloat(ID_RestDensity, Mathf.Max(0.01f, restDensity));
        _cachedLoopRadius = Mathf.Max(Mathf.Max(surfaceRadius, bulkRadius), vapourRadius);
        _cachedMaxKernelRadiusVoxels = Mathf.Max(1, grid.maxKernelRadiusVoxels);
        // Reset sentinel so KernelRadius is recomputed on the first Execute call.
        _lastBoundsSize = new Vector3(float.NaN, 0f, 0f);
    }

    private void BindPerFrameParameters(
        int particleCount,
        Vector3Int volumeDims,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        _computeShader.SetInt(ID_ParticleCount, particleCount);
        _computeShader.SetVector(ID_BoundsMinWS, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0f));
        _computeShader.SetVector(ID_BoundsMaxWS, new Vector4(boundsMax.x, boundsMax.y, boundsMax.z, 0f));

        // KernelRadius depends only on bounds SIZE (not position) and volumeDims.
        // Skip the float math and SetInt when the bounds box hasn't been resized.
        Vector3 boundsSize = boundsMax - boundsMin;
        if (boundsSize != _lastBoundsSize)
        {
            _lastBoundsSize = boundsSize;
            _computeShader.SetInt(
                ID_KernelRadiusVoxels,
                Mathf.Min(
                    _cachedMaxKernelRadiusVoxels,
                    ComputeKernelRadiusVoxels(_cachedLoopRadius, volumeDims, boundsMin, boundsMax)));
        }
    }

    private static int ComputeKernelRadiusVoxels(float smoothingRadiusWS, Vector3Int volumeDims, Vector3 boundsMin, Vector3 boundsMax)
    {
        if (smoothingRadiusWS <= 1e-5f)
            return 0;

        Vector3 sizeWS = boundsMax - boundsMin;

        float voxelSizeX = Mathf.Abs(sizeWS.x) / Mathf.Max(1, volumeDims.x);
        float voxelSizeY = Mathf.Abs(sizeWS.y) / Mathf.Max(1, volumeDims.y);
        float voxelSizeZ = Mathf.Abs(sizeWS.z) / Mathf.Max(1, volumeDims.z);
        float minVoxelSize = Mathf.Min(voxelSizeX, Mathf.Min(voxelSizeY, voxelSizeZ));

        if (minVoxelSize <= 1e-6f)
            return 0;

        int rawRadius = Mathf.CeilToInt(smoothingRadiusWS / minVoxelSize);
        return Mathf.Clamp(rawRadius, 0, Mathf.Max(volumeDims.x, Mathf.Max(volumeDims.y, volumeDims.z)));
    }

    private void DispatchDensityBlurIfEnabled(
        WaterPhaseDensityBlurSettings blur,
        WaterPhaseResources resources,
        int groupsX,
        int groupsY,
        int groupsZ,
        int channel)
    {
        if (!blur.IsChannelEnabled(channel))
            return;

        _computeShader.SetInt(ID_BlurRadius, blur.GetRadius(channel));
        _computeShader.SetFloat(ID_BlurSigma, blur.GetSigma(channel));
        _computeShader.SetFloat(ID_BlurDetailPreserve, blur.GetDetailPreserve(channel));
        _computeShader.SetInt(ID_BlurChannel, channel);
        _computeShader.Dispatch(_blurDensityKernel, groupsX, groupsY, groupsZ);
        // https://docs.unity3d.com/6000.4/Documentation/ScriptReference/Graphics.CopyTexture.html
        Graphics.CopyTexture(resources.PhaseDensityScratchTexture, resources.PhaseDensityTexture);
    }

    private void DispatchNormalBake(WaterPhaseResources resources, int groupsX, int groupsY, int groupsZ)
    {
        _computeShader.Dispatch(_bakeNormalsKernel, groupsX, groupsY, groupsZ);
    }
}
