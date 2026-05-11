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

    public WaterPhaseDensityPipeline(ComputeShader computeShader)
    {
        _computeShader = computeShader;
        _clearKernel = _computeShader.FindKernel("ClearGrid");
        _splatKernel = _computeShader.FindKernel("SplatParticles");
        _normalizeKernel = _computeShader.FindKernel("NormalizeToTexture");
        _blurDensityKernel = _computeShader.FindKernel("BlurDensity");
        _bakeNormalsKernel = _computeShader.FindKernel("BakeNormals");
    }

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

        DispatchDensityBlurIfEnabled(settings.ActiveBlur, resources, groupsX, groupsY, groupsZ, 0);
        DispatchDensityBlurIfEnabled(settings.ActiveBlur, resources, groupsX, groupsY, groupsZ, 1);
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
        var grid = settings.DensityGrid;
        var smoothing = settings.ActiveSmoothing;

        float surfaceRadius = Mathf.Max(0f, smoothing.liquidSmoothingRadiusWS);


        float bulkRadius = smoothing.adaptiveRadiusEnabled
            ? smoothing.liquidBulkSmoothingRadiusWS
            : surfaceRadius;

        float vapourRadius = Mathf.Max(0f, grid.vapourSmoothingRadiusWS);

        float loopRadius = Mathf.Max(Mathf.Max(surfaceRadius, bulkRadius), vapourRadius);

        _computeShader.SetFloat(ID_SmoothingRadiusWS_LiquidSmall, surfaceRadius);
        _computeShader.SetFloat(ID_SmoothingRadiusWS_LiquidBulk, bulkRadius);
        _computeShader.SetFloat(ID_SmoothingRadiusWS_Vapour, vapourRadius);
        _computeShader.SetFloat(ID_AdaptiveDensitySurface, Mathf.Max(0f, smoothing.adaptiveDensitySurface));
        _computeShader.SetFloat(ID_AdaptiveDensityBulk, Mathf.Max(smoothing.adaptiveDensitySurface + 0.01f, smoothing.adaptiveDensityBulk));
        _computeShader.SetFloat(ID_AdaptiveDensityCurve, Mathf.Max(0.01f, smoothing.adaptiveDensityCurve));

        _computeShader.SetInt(
            ID_KernelRadiusVoxels,
            Mathf.Min(
                Mathf.Max(1, grid.maxKernelRadiusVoxels),
                ComputeKernelRadiusVoxels(loopRadius, volumeDims, boundsMin, boundsMax)));

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
        _computeShader.SetTexture(_blurDensityKernel, "_DensityTexture3D_Read", resources.PhaseDensityTexture);
        _computeShader.SetTexture(_blurDensityKernel, "_DensityTexture3D_RG", resources.PhaseDensityScratchTexture);
        _computeShader.Dispatch(_blurDensityKernel, groupsX, groupsY, groupsZ);
        // https://docs.unity3d.com/6000.4/Documentation/ScriptReference/Graphics.CopyTexture.html
        Graphics.CopyTexture(resources.PhaseDensityScratchTexture, resources.PhaseDensityTexture);
    }

    private void DispatchNormalBake(WaterPhaseResources resources, int groupsX, int groupsY, int groupsZ)
    {
        _computeShader.SetTexture(_bakeNormalsKernel, "_DensityTexture3D_Read", resources.PhaseDensityTexture);
        _computeShader.Dispatch(_bakeNormalsKernel, groupsX, groupsY, groupsZ);
    }
}
