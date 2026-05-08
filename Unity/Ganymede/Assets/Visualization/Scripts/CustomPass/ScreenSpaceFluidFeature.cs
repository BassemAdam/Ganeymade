using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// URP Scriptable Renderer Feature for Screen-Space Fluid rendering.
/// Manages the multi-pass pipeline: Depth → Thickness → NarrowRangeFilter → Composite.
/// Add to the URP Renderer asset and assign the required shaders.
/// </summary>
public class ScreenSpaceFluidFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        [Tooltip("Shader: Hidden/SSF_Depth")]
        public Shader depthShader;
        [Tooltip("Shader: Hidden/SSF_Thickness")]
        public Shader thicknessShader;
        [Tooltip("Shader: Hidden/SSF_NarrowRangeFilter")]
        public Shader filterShader;
        [Tooltip("Shader: Custom/ScreenSpaceFluidComposite")]
        public Shader compositeShader;
        [Tooltip("Cubemap for reflections (defaults to skybox if null)")]
        public Cubemap environmentCubemap;
        [Tooltip("Enable to output debug solid color (confirms pipeline is running)")]
        public bool debugMode;
    }

    public Settings settings = new Settings();
    private ScreenSpaceFluidPass _pass;
    private Material _depthMat;
    private Material _thicknessMat;
    private Material _filterMat;
    private Material _compositeMat;

    // Static config set by WaterPhaseScreenSpaceRenderer each frame
    internal static ComputeBuffer ParticleBuffer;
    internal static int ParticleCount;
    internal static float ParticleRadius = 0.15f;
    internal static int FilterSize = 32;
    internal static float SprayThreshold = 6f;
    internal static bool IsActive;
    internal static bool DebugMode;

    public override void Create()
    {
        _depthMat = CreateMat(settings.depthShader);
        _thicknessMat = CreateMat(settings.thicknessShader);
        _filterMat = CreateMat(settings.filterShader);
        _compositeMat = CreateMat(settings.compositeShader);

        if (_depthMat == null || _thicknessMat == null || _filterMat == null || _compositeMat == null)
            return;

        _pass = new ScreenSpaceFluidPass(_depthMat, _thicknessMat, _filterMat, _compositeMat, settings.environmentCubemap);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null)
        {
            Debug.LogWarning("[SSF] Pass is null — check that all 4 shaders are assigned in ScreenSpaceFluidFeature settings.");
            return;
        }

        if (!IsActive || ParticleBuffer == null || ParticleCount <= 0)
            return;

        DebugMode = settings.debugMode;

        if (renderingData.cameraData.cameraType == CameraType.Game ||
            renderingData.cameraData.cameraType == CameraType.SceneView)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            _pass.EnsureRTs(desc.width, desc.height);
            renderer.EnqueuePass(_pass);
        }
    }

    protected override void Dispose(bool disposing)
    {
        _pass?.Dispose();
        DestroyMat(ref _depthMat);
        DestroyMat(ref _thicknessMat);
        DestroyMat(ref _filterMat);
        DestroyMat(ref _compositeMat);
    }

    private static Material CreateMat(Shader shader)
    {
        if (shader == null) return null;
        return CoreUtils.CreateEngineMaterial(shader);
    }

    private static void DestroyMat(ref Material mat)
    {
        if (mat != null) { CoreUtils.Destroy(mat); mat = null; }
    }

    // =========================================================================
    class ScreenSpaceFluidPass : ScriptableRenderPass
    {
        private readonly Material _depthMat;
        private readonly Material _thickMat;
        private readonly Material _filterMat;
        private readonly Material _compMat;
        private readonly Cubemap _envCube;

        private static readonly int ID_ParticleBuffer = Shader.PropertyToID("_ParticleBuffer");
        private static readonly int ID_ParticleRadius = Shader.PropertyToID("_ParticleRadius");
        private static readonly int ID_SprayThreshold = Shader.PropertyToID("_SprayThreshold");
        private static readonly int ID_ViewMatrix = Shader.PropertyToID("_SSF_ViewMatrix");
        private static readonly int ID_ProjMatrix = Shader.PropertyToID("_SSF_ProjMatrix");
        private static readonly int ID_DepthTex = Shader.PropertyToID("_DepthTex");
        private static readonly int ID_DepthTex_TexelSize = Shader.PropertyToID("_DepthTex_TexelSize");
        private static readonly int ID_FilteredDepthTex = Shader.PropertyToID("_FilteredDepthTex");
        private static readonly int ID_ThicknessTex = Shader.PropertyToID("_ThicknessTex");
        private static readonly int ID_BlurDir = Shader.PropertyToID("_BlurDir");
        private static readonly int ID_FilterSize = Shader.PropertyToID("_FilterSize");
        private static readonly int ID_ProjectedParticleSize = Shader.PropertyToID("_ProjectedParticleSize");
        private static readonly int ID_InvProjectionMatrix = Shader.PropertyToID("_SSF_InvProjectionMatrix");
        private static readonly int ID_ProjectionMatrix = Shader.PropertyToID("_SSF_ProjectionMatrix");
        private static readonly int ID_InvViewMatrix = Shader.PropertyToID("_SSF_InvViewMat");
        private static readonly int ID_SSF_EnvCube = Shader.PropertyToID("_SSF_EnvCube");

        // Plain RenderTextures managed manually
        private RenderTexture _depthRT;
        private RenderTexture _thicknessRT;
        private RenderTexture _filterRT_A;
        private RenderTexture _filterRT_B;
        private int _rtWidth, _rtHeight;

        public ScreenSpaceFluidPass(Material depth, Material thick, Material filter, Material comp, Cubemap env)
        {
            _depthMat = depth;
            _thickMat = thick;
            _filterMat = filter;
            _compMat = comp;
            _envCube = env;
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        }

        public void EnsureRTs(int width, int height)
        {
            if (_depthRT != null && _rtWidth == width && _rtHeight == height)
                return;

            ReleaseRTs();
            _rtWidth = width;
            _rtHeight = height;

            _depthRT = new RenderTexture(width, height, 24, RenderTextureFormat.RFloat) { filterMode = FilterMode.Point };
            _depthRT.Create();

            _filterRT_A = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat) { filterMode = FilterMode.Point };
            _filterRT_A.Create();

            _filterRT_B = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat) { filterMode = FilterMode.Point };
            _filterRT_B.Create();

            _thicknessRT = new RenderTexture(width, height, 0, RenderTextureFormat.RHalf) { filterMode = FilterMode.Bilinear };
            _thicknessRT.Create();
        }

        private void ReleaseRTs()
        {
            if (_depthRT != null) { _depthRT.Release(); Object.DestroyImmediate(_depthRT); _depthRT = null; }
            if (_thicknessRT != null) { _thicknessRT.Release(); Object.DestroyImmediate(_thicknessRT); _thicknessRT = null; }
            if (_filterRT_A != null) { _filterRT_A.Release(); Object.DestroyImmediate(_filterRT_A); _filterRT_A = null; }
            if (_filterRT_B != null) { _filterRT_B.Release(); Object.DestroyImmediate(_filterRT_B); _filterRT_B = null; }
        }

        private class IntermediateData
        {
            public Material depthMat;
            public Material thickMat;
            public Material filterMat;
            public ComputeBuffer particleBuffer;
            public int particleCount;
            public float particleRadius;
            public float sprayThreshold;
            public int filterSize;
            public RenderTexture depthRT;
            public RenderTexture thicknessRT;
            public RenderTexture filterRT_A;
            public RenderTexture filterRT_B;
            public Camera camera;
        }

        private class CompositeData
        {
            public Material compMat;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (ParticleBuffer == null || ParticleCount <= 0 || _depthRT == null)
                return;

            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();

            // ---- Step 1: Unsafe pass — depth + thickness + filter ----
            using (var builder = renderGraph.AddUnsafePass<IntermediateData>("SSF Intermediate", out var iData))
            {
                iData.depthMat = _depthMat;
                iData.thickMat = _thickMat;
                iData.filterMat = _filterMat;
                iData.particleBuffer = ParticleBuffer;
                iData.particleCount = ParticleCount;
                iData.particleRadius = ParticleRadius;
                iData.sprayThreshold = SprayThreshold;
                iData.filterSize = FilterSize;
                iData.depthRT = _depthRT;
                iData.thicknessRT = _thicknessRT;
                iData.filterRT_A = _filterRT_A;
                iData.filterRT_B = _filterRT_B;
                iData.camera = cameraData.camera;

                builder.AllowPassCulling(false);

                builder.SetRenderFunc((IntermediateData data, UnsafeGraphContext ctx) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    ExecuteIntermediate(cmd, data);
                });
            }

            // ---- Step 2: Import results and set composite material ----
            RTHandle filterBHandle = RTHandles.Alloc(_filterRT_B);
            RTHandle thicknessHandle = RTHandles.Alloc(_thicknessRT);
            TextureHandle filteredDepthTH = renderGraph.ImportTexture(filterBHandle);
            TextureHandle thicknessTH = renderGraph.ImportTexture(thicknessHandle);

            Camera cam = cameraData.camera;
            Matrix4x4 viewMat = cam.worldToCameraMatrix;
            Matrix4x4 projMat = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);

            _compMat.SetTexture(ID_FilteredDepthTex, _filterRT_B);
            _compMat.SetTexture(ID_ThicknessTex, _thicknessRT);
            _compMat.SetMatrix(ID_InvProjectionMatrix, projMat.inverse);
            _compMat.SetMatrix(ID_ProjectionMatrix, projMat);
            _compMat.SetMatrix(ID_InvViewMatrix, viewMat.inverse);
            _compMat.SetMatrix(ID_ViewMatrix, viewMat);

            if (_envCube != null)
                _compMat.SetTexture(ID_SSF_EnvCube, _envCube);

            if (DebugMode)
                _compMat.EnableKeyword("_SSF_DEBUG");
            else
                _compMat.DisableKeyword("_SSF_DEBUG");

            // ---- Step 3: Raster pass — composite onto camera colour ----
            using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("SSF Composite", out var cData))
            {
                cData.compMat = _compMat;

                builder.UseTexture(filteredDepthTH, AccessFlags.Read);
                builder.UseTexture(thicknessTH, AccessFlags.Read);
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((CompositeData data, RasterGraphContext ctx) =>
                {
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, data.compMat, 0, MeshTopology.Triangles, 3);
                });
            }
        }

        private static void ExecuteIntermediate(CommandBuffer cmd, IntermediateData data)
        {
            Camera cam = data.camera;
            Matrix4x4 viewMat = cam.worldToCameraMatrix;
            Matrix4x4 projMat = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);

            // --- Depth ---
            cmd.SetRenderTarget(data.depthRT);
            cmd.ClearRenderTarget(true, true, new Color(1e5f, 0, 0, 1));

            data.depthMat.SetBuffer(ID_ParticleBuffer, data.particleBuffer);
            data.depthMat.SetFloat(ID_ParticleRadius, data.particleRadius);
            data.depthMat.SetFloat(ID_SprayThreshold, data.sprayThreshold);
            data.depthMat.SetMatrix(ID_ViewMatrix, viewMat);
            data.depthMat.SetMatrix(ID_ProjMatrix, projMat);

            cmd.DrawProcedural(Matrix4x4.identity, data.depthMat, 0, MeshTopology.Triangles, 6, data.particleCount);

            // --- Thickness ---
            cmd.SetRenderTarget(data.thicknessRT);
            cmd.ClearRenderTarget(false, true, Color.clear);

            data.thickMat.SetBuffer(ID_ParticleBuffer, data.particleBuffer);
            data.thickMat.SetFloat(ID_ParticleRadius, data.particleRadius);
            data.thickMat.SetFloat(ID_SprayThreshold, data.sprayThreshold);
            data.thickMat.SetMatrix(ID_ViewMatrix, viewMat);
            data.thickMat.SetMatrix(ID_ProjMatrix, projMat);

            cmd.DrawProcedural(Matrix4x4.identity, data.thickMat, 0, MeshTopology.Triangles, 6, data.particleCount);

            // --- Narrow-Range Filter (H then V) ---
            float projectedSize = data.particleRadius * projMat[1, 1] * data.depthRT.height * 0.5f;

            // Use cmd.SetGlobal* so each draw captures its own state (material.Set* won't work
            // correctly inside a CommandBuffer when the same property changes between draws).
            cmd.SetGlobalFloat(ID_FilterSize, data.filterSize);
            cmd.SetGlobalFloat(ID_ProjectedParticleSize, projectedSize);

            // Must manually set texel size since SetGlobalTexture doesn't auto-populate it
            Vector4 texelSize = new Vector4(1f / data.depthRT.width, 1f / data.depthRT.height, data.depthRT.width, data.depthRT.height);
            cmd.SetGlobalVector(ID_DepthTex_TexelSize, texelSize);

            // Horizontal pass: read depthRT → write filterRT_A
            cmd.SetGlobalTexture(ID_DepthTex, data.depthRT);
            cmd.SetGlobalVector(ID_BlurDir, new Vector4(1, 0, 0, 0));
            cmd.SetRenderTarget(data.filterRT_A);
            cmd.DrawProcedural(Matrix4x4.identity, data.filterMat, 0, MeshTopology.Triangles, 3);

            // Vertical pass: read filterRT_A → write filterRT_B
            cmd.SetGlobalTexture(ID_DepthTex, data.filterRT_A);
            cmd.SetGlobalVector(ID_BlurDir, new Vector4(0, 1, 0, 0));
            cmd.SetRenderTarget(data.filterRT_B);
            cmd.DrawProcedural(Matrix4x4.identity, data.filterMat, 0, MeshTopology.Triangles, 3);
        }

        public void Dispose()
        {
            ReleaseRTs();
        }
    }
}
