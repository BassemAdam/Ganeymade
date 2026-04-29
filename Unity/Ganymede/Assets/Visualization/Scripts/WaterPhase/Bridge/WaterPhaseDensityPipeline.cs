using UnityEngine;

public sealed class WaterPhaseDensityPipeline
{
    private const float ParticleContribution = 1.0f;
    private const float FixedPointScale = 1024.0f;
    private const float LiquidInvDensityScale = 1.0f;

    private readonly ComputeShader _computeShader;
    private readonly int _clearKernel;
    private readonly int _splatKernel;
    private readonly int _normalizeKernel;
    private readonly int _blurVapourKernel;
    private readonly int _blurLiquidKernel;
    private readonly int _enhanceVapourKernel;
    private readonly int _bakeNormalsKernel;

    private static readonly int ID_VolumeDims = Shader.PropertyToID("_VolumeDims");
    private static readonly int ID_ParticleCount = Shader.PropertyToID("_ParticleCount");
    private static readonly int ID_BoundsMinWS = Shader.PropertyToID("_BoundsMinWS");
    private static readonly int ID_BoundsMaxWS = Shader.PropertyToID("_BoundsMaxWS");
    private static readonly int ID_ParticleContribution = Shader.PropertyToID("_ParticleContribution");
    private static readonly int ID_FixedPointScale = Shader.PropertyToID("_FixedPointScale");
    private static readonly int ID_SmoothingRadiusWS = Shader.PropertyToID("_SmoothingRadiusWS");
    private static readonly int ID_KernelRadiusVoxels = Shader.PropertyToID("_KernelRadiusVoxels");
    private static readonly int ID_RestDensity = Shader.PropertyToID("_RestDensity");
    private static readonly int ID_InvDensityScaleRG = Shader.PropertyToID("_InvDensityScaleRG");
    private static readonly int ID_VapourNoiseScale = Shader.PropertyToID("_VapourNoiseScale");
    private static readonly int ID_VapourNoiseTime = Shader.PropertyToID("_VapourNoiseTime");
    private static readonly int ID_VapourNoiseDriftDir = Shader.PropertyToID("_VapourNoiseDriftDir");
    private static readonly int ID_VapourNoiseDriftSpeed = Shader.PropertyToID("_VapourNoiseDriftSpeed");
    private static readonly int ID_VapourNoiseOctaves = Shader.PropertyToID("_VapourNoiseOctaves");
    private static readonly int ID_VapourNoiseDomainWarpStrength = Shader.PropertyToID("_VapourNoiseDomainWarpStrength");
    private static readonly int ID_VapourNoiseDetailStrength = Shader.PropertyToID("_VapourNoiseDetailStrength");
    private static readonly int ID_BlurRadius = Shader.PropertyToID("_BlurRadius");
    private static readonly int ID_BlurSigma = Shader.PropertyToID("_BlurSigma");
    private static readonly int ID_BlurDetailPreserve = Shader.PropertyToID("_BlurDetailPreserve");
    private static readonly int ID_LiquidBlurRadius = Shader.PropertyToID("_LiquidBlurRadius");
    private static readonly int ID_LiquidBlurSigma = Shader.PropertyToID("_LiquidBlurSigma");
    private static readonly int ID_LiquidBlurDetailPreserve = Shader.PropertyToID("_LiquidBlurDetailPreserve");

    public WaterPhaseDensityPipeline(ComputeShader computeShader)
    {
        _computeShader = computeShader;
        _clearKernel = _computeShader.FindKernel("ClearGrid");
        _splatKernel = _computeShader.FindKernel("SplatParticles");
        _normalizeKernel = _computeShader.FindKernel("NormalizeToTexture");
        _blurVapourKernel = _computeShader.FindKernel("BlurVapourDensity");
        _blurLiquidKernel = _computeShader.FindKernel("BlurLiquidDensity");
        _enhanceVapourKernel = _computeShader.FindKernel("EnhanceVapourDensity");
        _bakeNormalsKernel = _computeShader.FindKernel("BakeNormals");
    }

    public ComputeShader ComputeShader => _computeShader;

    public void Execute(
        UseComputePlugin computePlugin,
        WaterPhaseBridgeSettings settings,
        WaterPhaseResources resources,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        Vector3Int volumeDims = resources.VolumeDims;
        int particleCount = Mathf.Max(1, computePlugin.particleCount);

        BindStaticResources(resources);
        BindSharedFrameParameters(computePlugin, settings, volumeDims, particleCount, boundsMin, boundsMax);

        int groupsX = Mathf.CeilToInt(volumeDims.x / 8.0f);
        int groupsY = Mathf.CeilToInt(volumeDims.y / 8.0f);
        int groupsZ = Mathf.CeilToInt(volumeDims.z / 8.0f);
        int particleGroups = Mathf.Max(1, Mathf.CeilToInt(particleCount / 256.0f));

        _computeShader.Dispatch(_clearKernel, groupsX, groupsY, groupsZ);
        _computeShader.Dispatch(_splatKernel, particleGroups, 1, 1);
        _computeShader.Dispatch(_normalizeKernel, groupsX, groupsY, groupsZ);

        DispatchLiquidBlurIfEnabled(settings, resources, groupsX, groupsY, groupsZ);
        DispatchVapourPipelineIfEnabled(settings, resources, groupsX, groupsY, groupsZ);
        DispatchNormalBake(resources, groupsX, groupsY, groupsZ);
    }

    private void BindStaticResources(WaterPhaseResources resources)
    {
        _computeShader.SetBuffer(_clearKernel, "_DensityGrid", resources.DensityGridBuffer);
        _computeShader.SetBuffer(_splatKernel, "_DensityGrid", resources.DensityGridBuffer);
        _computeShader.SetBuffer(_splatKernel, "_ParticleBuffer", resources.ParticleOutputBuffer);
        _computeShader.SetBuffer(_normalizeKernel, "_DensityGrid", resources.DensityGridBuffer);
        _computeShader.SetTexture(_normalizeKernel, "_DensityTexture3D_RG", resources.PhaseDensityTexture);
        _computeShader.SetTexture(_bakeNormalsKernel, "_NormalTexture3D", resources.SurfaceNormalTexture);
    }

    private void BindSharedFrameParameters(
        UseComputePlugin computePlugin,
        WaterPhaseBridgeSettings settings,
        Vector3Int volumeDims,
        int particleCount,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        _computeShader.SetInts(ID_VolumeDims, volumeDims.x, volumeDims.y, volumeDims.z);
        _computeShader.SetInt(ID_ParticleCount, particleCount);
        _computeShader.SetVector(ID_BoundsMinWS, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0f));
        _computeShader.SetVector(ID_BoundsMaxWS, new Vector4(boundsMax.x, boundsMax.y, boundsMax.z, 0f));
        _computeShader.SetFloat(ID_ParticleContribution, ParticleContribution);
        _computeShader.SetFloat(ID_FixedPointScale, FixedPointScale);
        _computeShader.SetVector(
            ID_InvDensityScaleRG,
            new Vector4(LiquidInvDensityScale, 1.0f, 0f, 0f));
        BindSplatParameters(computePlugin, settings, volumeDims, boundsMin, boundsMax);
    }

    private void BindSplatParameters(
        UseComputePlugin computePlugin,
        WaterPhaseBridgeSettings settings,
        Vector3Int volumeDims,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        float requestedRadius = settings.DensityGrid.smoothingRadiusWS >= 0f
            ? settings.DensityGrid.smoothingRadiusWS
            : computePlugin.smoothingRadius;

        _computeShader.SetFloat(ID_SmoothingRadiusWS, Mathf.Max(0f, requestedRadius));
        _computeShader.SetInt(
            ID_KernelRadiusVoxels,
            ComputeKernelRadiusVoxels(requestedRadius, volumeDims, boundsMin, boundsMax));
        _computeShader.SetFloat(ID_RestDensity, Mathf.Max(0.01f, computePlugin.restDensity));
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

    private void DispatchLiquidBlurIfEnabled(
        WaterPhaseBridgeSettings settings,
        WaterPhaseResources resources,
        int groupsX,
        int groupsY,
        int groupsZ)
    {
        if (!settings.LiquidBlur.enabled)
            return;

        _computeShader.SetInt(ID_LiquidBlurRadius, settings.LiquidBlur.radius);
        _computeShader.SetFloat(ID_LiquidBlurSigma, settings.LiquidBlur.sigma);
        _computeShader.SetFloat(ID_LiquidBlurDetailPreserve, settings.LiquidBlur.detailPreserve);
        _computeShader.SetTexture(_blurLiquidKernel, "_DensityTexture3D_Read", resources.PhaseDensityTexture);
        _computeShader.SetTexture(_blurLiquidKernel, "_DensityTexture3D_RG", resources.PhaseDensityScratchTexture);
        _computeShader.Dispatch(_blurLiquidKernel, groupsX, groupsY, groupsZ);
        Graphics.CopyTexture(resources.PhaseDensityScratchTexture, resources.PhaseDensityTexture);
    }

    private void DispatchVapourPipelineIfEnabled(
        WaterPhaseBridgeSettings settings,
        WaterPhaseResources resources,
        int groupsX,
        int groupsY,
        int groupsZ)
    {
        if (!settings.Vapour.enabled)
            return;

        Vector3 driftDirection = settings.Vapour.noiseDriftDirection.sqrMagnitude > 1e-6f
            ? settings.Vapour.noiseDriftDirection.normalized
            : Vector3.up;

        _computeShader.SetFloat(ID_VapourNoiseScale, settings.Vapour.noiseScale);
        _computeShader.SetFloat(ID_VapourNoiseTime, Time.time);
        _computeShader.SetVector(ID_VapourNoiseDriftDir, new Vector4(driftDirection.x, driftDirection.y, driftDirection.z, 0f));
        _computeShader.SetFloat(ID_VapourNoiseDriftSpeed, settings.Vapour.noiseDriftSpeed);
        _computeShader.SetInt(ID_VapourNoiseOctaves, settings.Vapour.noiseOctaves);
        _computeShader.SetFloat(ID_VapourNoiseDomainWarpStrength, settings.Vapour.domainWarpStrength);
        _computeShader.SetFloat(ID_VapourNoiseDetailStrength, settings.Vapour.detailStrength);

        if (settings.Vapour.blurBeforeEnhance)
        {
            _computeShader.SetInt(ID_BlurRadius, settings.Vapour.blurRadius);
            _computeShader.SetFloat(ID_BlurSigma, settings.Vapour.blurSigma);
            _computeShader.SetFloat(ID_BlurDetailPreserve, settings.Vapour.blurDetailPreserve);
            _computeShader.SetTexture(_blurVapourKernel, "_DensityTexture3D_Read", resources.PhaseDensityTexture);
            _computeShader.SetTexture(_blurVapourKernel, "_DensityTexture3D_RG", resources.PhaseDensityScratchTexture);
            _computeShader.Dispatch(_blurVapourKernel, groupsX, groupsY, groupsZ);

            _computeShader.SetTexture(_enhanceVapourKernel, "_DensityTexture3D_Read", resources.PhaseDensityScratchTexture);
            _computeShader.SetTexture(_enhanceVapourKernel, "_DensityTexture3D_RG", resources.PhaseDensityTexture);
            _computeShader.Dispatch(_enhanceVapourKernel, groupsX, groupsY, groupsZ);
            return;
        }

        _computeShader.SetTexture(_enhanceVapourKernel, "_DensityTexture3D_Read", resources.PhaseDensityTexture);
        _computeShader.SetTexture(_enhanceVapourKernel, "_DensityTexture3D_RG", resources.PhaseDensityScratchTexture);
        _computeShader.Dispatch(_enhanceVapourKernel, groupsX, groupsY, groupsZ);
        Graphics.CopyTexture(resources.PhaseDensityScratchTexture, resources.PhaseDensityTexture);
    }

    private void DispatchNormalBake(WaterPhaseResources resources, int groupsX, int groupsY, int groupsZ)
    {
        _computeShader.SetTexture(_bakeNormalsKernel, "_DensityTexture3D_Read", resources.PhaseDensityTexture);
        _computeShader.Dispatch(_bakeNormalsKernel, groupsX, groupsY, groupsZ);
    }
}
