using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// URP RendererFeature implementing screen-space SPH water rendering
/// (van der Laan / Truong &amp; Yuksel narrow-range approach).
///
/// Three RenderGraph passes per frame, all gated on the ScreenSpaceWaterRegistry
/// having a valid particle buffer:
///   1. Particle Depth + Thickness  -> writes _SS_FluidDepth, _SS_FluidThickness
///   2. Narrow-Range Filter         -> ping-pong N iterations on depth
///   3. Composite                   -> blends shaded surface into camera colour
/// </summary>
public class ScreenSpaceWaterFeature : ScriptableRendererFeature
{
    [Serializable]
    public class Settings
    {
        [Header("Shaders")]
        public Shader particleDepthShader;     // Hidden/ScreenSpace/SS_ParticleDepth
        public Shader depthFilterShader;       // Hidden/ScreenSpace/SS_DepthFilter
        public Shader compositeShader;         // Hidden/ScreenSpace/SS_WaterComposite

        [Header("Rendering")]
        [Tooltip("World-space radius used to splat each particle as a sphere.")]
        [Range(0.005f, 1.0f)] public float sphereRadiusWS = 0.08f;
        [Tooltip("Multiplier on the integrated through-sphere thickness (tweak so Beer-Lambert reads sensibly).")]
        [Range(0.1f, 8.0f)]   public float thicknessScale = 1.0f;
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingSkybox;

        [Header("Narrow-Range Filter")]
        [Range(0, 6)]    public int   filterIterations = 3;
        [Range(1, 16)]   public int   filterRadius     = 6;
        [Range(0.1f, 8f)] public float spatialSigma    = 4.0f;
        [Range(0.005f, 1.0f)] public float depthSigma  = 0.08f;

        [Header("Surface Material")]
        [Tooltip("Per-channel absorption coefficient sigma_t (m^-1). Real water absorbs red fastest, blue slowest. Default ~ clean tropical water.")]
        [ColorUsage(false, true)] public Color liquidExtinction = new Color(0.45f, 0.09f, 0.04f, 1f);
        [Tooltip("In-scattered colour at thick depth: the colour you see when looking through a thick column of water.")]
        [ColorUsage(false, true)] public Color liquidScatterTint = new Color(0.05f, 0.35f, 0.45f, 1f);
        [Tooltip("Deep-water tint: extra colour added at grazing angles + thick water (creates that classic blue/teal sheen).")]
        [ColorUsage(false, true)] public Color liquidDeepTint    = new Color(0.02f, 0.10f, 0.18f, 1f);
        [Range(0f, 0.2f)]  public float refractionStrength    = 0.03f;
        [Range(0f, 2.0f)]  public float reflectionStrength    = 1.0f;
        [Range(8f, 512f)]  public float specularPower         = 220f;
        [Range(0.5f, 4f)]  public float specularIntensity     = 1.5f;
        [Range(0f, 0.1f)]  public float fresnelF0             = 0.02f;
        [Tooltip("Thickness (m) at which the surface becomes fully opaque from coverage alone. Higher = more transparent water.")]
        [Range(0.01f, 5f)] public float minThicknessForOpaque = 0.5f;

        [Header("Debug")]
        [Tooltip("0 = normal composite, 1 = depth heatmap, 2 = thickness heatmap, 3 = view-space normals, 4 = approximate particles-per-pixel heatmap, 5 = silhouette outline only.")]
        [Range(0, 5)] public int debugMode = 0;
        [Tooltip("Max eye-depth (m) mapped to red in debug mode 1. Tune to your scene scale.")]
        [Range(0.05f, 100f)] public float debugDepthRange = 5.0f;
        [Tooltip("Max thickness mapped to red in debug mode 2. Lower this until you see a gradient.")]
        [Range(0.0001f, 10f)] public float debugThicknessRange = 0.05f;
        [Tooltip("Log particle buffer / count once when the feature first enqueues.")]
        public bool verboseStartupLog = true;
    }

    public Settings settings = new Settings();

    private Material _particleDepthMaterial;
    private Material _depthFilterMaterial;
    private Material _compositeMaterial;
    private ScreenSpaceWaterPass _pass;
    private bool _loggedFirstFrame;

    public override void Create()
    {
        _particleDepthMaterial = EnsureMaterial(_particleDepthMaterial, settings.particleDepthShader);
        _depthFilterMaterial   = EnsureMaterial(_depthFilterMaterial,   settings.depthFilterShader);
        _compositeMaterial     = EnsureMaterial(_compositeMaterial,     settings.compositeShader);

        _pass = new ScreenSpaceWaterPass(_particleDepthMaterial, _depthFilterMaterial, _compositeMaterial, settings)
        {
            renderPassEvent = settings.renderPassEvent
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null) return;
        if (renderingData.cameraData.cameraType != CameraType.Game &&
            renderingData.cameraData.cameraType != CameraType.SceneView)
            return;
        if (_particleDepthMaterial == null || _depthFilterMaterial == null || _compositeMaterial == null)
            return;
        if (!ScreenSpaceWaterRegistry.IsValid)
            return;

        if (settings.verboseStartupLog && !_loggedFirstFrame)
        {
            _loggedFirstFrame = true;
            var buf = ScreenSpaceWaterRegistry.ParticleBuffer;
            Debug.Log($"[ScreenSpaceWater] First enqueue. ParticleCount={ScreenSpaceWaterRegistry.ParticleCount}, " +
                      $"buffer.count={(buf != null ? buf.count : -1)}, buffer.stride={(buf != null ? buf.stride : -1)}, " +
                      $"buffer.IsValid={(buf != null && buf.IsValid())}, sphereRadiusWS={ScreenSpaceWaterRegistry.SphereRadiusWS}");
        }

        _pass.RefreshSettings(settings);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_particleDepthMaterial);
        CoreUtils.Destroy(_depthFilterMaterial);
        CoreUtils.Destroy(_compositeMaterial);
        _particleDepthMaterial = _depthFilterMaterial = _compositeMaterial = null;
        _pass = null;
    }

    private static Material EnsureMaterial(Material existing, Shader shader)
    {
        if (existing != null) return existing;
        if (shader == null)   return null;
        return CoreUtils.CreateEngineMaterial(shader);
    }

    // =========================================================================
    //  PASS
    // =========================================================================
    private class ScreenSpaceWaterPass : ScriptableRenderPass
    {
        // Property IDs
        private static readonly int ID_ParticleBuffer        = Shader.PropertyToID("_ParticleBuffer");
        private static readonly int ID_ParticleCount         = Shader.PropertyToID("_ParticleCount");
        private static readonly int ID_SphereRadiusWS        = Shader.PropertyToID("_SphereRadiusWS");
        private static readonly int ID_ThicknessScale        = Shader.PropertyToID("_ThicknessScale");
        private static readonly int ID_SourceDepth           = Shader.PropertyToID("_SourceDepth");
        private static readonly int ID_SourceDepth_TS        = Shader.PropertyToID("_SourceDepth_TexelSize");
        private static readonly int ID_DepthFilterDirection  = Shader.PropertyToID("_DepthFilterDirection");
        private static readonly int ID_DepthSigma            = Shader.PropertyToID("_DepthSigma");
        private static readonly int ID_SpatialSigma          = Shader.PropertyToID("_SpatialSigma");
        private static readonly int ID_FilterRadius          = Shader.PropertyToID("_FilterRadius");
        private static readonly int ID_FluidDepth            = Shader.PropertyToID("_FluidDepth");
        private static readonly int ID_FluidDepth_TS         = Shader.PropertyToID("_FluidDepth_TexelSize");
        private static readonly int ID_FluidThickness        = Shader.PropertyToID("_FluidThickness");
        private static readonly int ID_LiquidExtinction      = Shader.PropertyToID("_LiquidExtinction");
        private static readonly int ID_LiquidScatterTint     = Shader.PropertyToID("_LiquidScatterTint");
        private static readonly int ID_LiquidDeepTint        = Shader.PropertyToID("_LiquidDeepTint");
        private static readonly int ID_RefractionStrengthSS  = Shader.PropertyToID("_RefractionStrengthSS");
        private static readonly int ID_ReflectionStrengthSS  = Shader.PropertyToID("_ReflectionStrengthSS");
        private static readonly int ID_SpecularPower         = Shader.PropertyToID("_SpecularPower");
        private static readonly int ID_SpecularIntensity     = Shader.PropertyToID("_SpecularIntensity");
        private static readonly int ID_F0                    = Shader.PropertyToID("_F0");
        private static readonly int ID_MinThicknessForOpaque = Shader.PropertyToID("_MinThicknessForOpaque");
        private static readonly int ID_DebugMode             = Shader.PropertyToID("_DebugMode");
        private static readonly int ID_DebugDepthRange       = Shader.PropertyToID("_DebugDepthRange");
        private static readonly int ID_DebugThicknessRange   = Shader.PropertyToID("_DebugThicknessRange");

        private readonly Material _particleDepthMaterial;
        private readonly Material _depthFilterMaterial;
        private readonly Material _compositeMaterial;
        private Settings _settings;

        public ScreenSpaceWaterPass(Material particleDepthMaterial, Material depthFilterMaterial, Material compositeMaterial, Settings settings)
        {
            _particleDepthMaterial = particleDepthMaterial;
            _depthFilterMaterial   = depthFilterMaterial;
            _compositeMaterial     = compositeMaterial;
            _settings              = settings;
        }

        public void RefreshSettings(Settings settings) => _settings = settings;

        // ---------- Pass data containers ----------
        private class DepthPassData
        {
            public Material material;
            public int      particleCount;
            public float    sphereRadiusWS;
            public float    thicknessScale;
            public ComputeBuffer particleBuffer;
        }

        private class FilterPassData
        {
            public Material      material;
            public TextureHandle source;
            public Vector2       direction;
            public int           radius;
            public float         spatialSigma;
            public float         depthSigma;
            public Vector4       sourceTexelSize;
        }

        private class CompositePassData
        {
            public Material      material;
            public TextureHandle fluidDepth;
            public TextureHandle fluidThickness;
            public Vector4       fluidDepthTexelSize;
            public Settings      settings;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!ScreenSpaceWaterRegistry.IsValid) return;
            if (_particleDepthMaterial == null || _depthFilterMaterial == null || _compositeMaterial == null) return;

            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData   = frameData.Get<UniversalCameraData>();

            int width  = cameraData.cameraTargetDescriptor.width;
            int height = cameraData.cameraTargetDescriptor.height;
            Vector4 texelSize = new Vector4(1f / width, 1f / height, width, height);

            // ---- Allocate transient targets -----------------------------------
            var depthDesc = new RenderTextureDescriptor(width, height, RenderTextureFormat.RFloat, 0)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = false
            };
            var thicknessDesc = new RenderTextureDescriptor(width, height, RenderTextureFormat.RHalf, 0)
            {
                msaaSamples = 1
            };
            var depthBufferDesc = new RenderTextureDescriptor(width, height, GraphicsFormat.None, GraphicsFormat.D32_SFloat, 0)
            {
                msaaSamples = 1
            };

            TextureHandle fluidDepthA = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDesc,    "_SS_FluidDepth_A", true);
            TextureHandle fluidDepthB = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDesc,    "_SS_FluidDepth_B", true);
            TextureHandle thickness   = UniversalRenderer.CreateRenderGraphTexture(renderGraph, thicknessDesc,"_SS_FluidThickness", true);
            TextureHandle depthBuf    = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthBufferDesc, "_SS_FluidZ", true);

            // ---- PASS 1a: particle depth -------------------------------------
            using (var builder = renderGraph.AddRasterRenderPass<DepthPassData>("SS Water — Particle Depth", out var data))
            {
                data.material       = _particleDepthMaterial;
                data.particleCount  = ScreenSpaceWaterRegistry.ParticleCount;
                // Source of truth for splat size: the feature setting. The registry value is
                // only used as a fallback if the user has cleared the feature value to <=0.
                data.sphereRadiusWS = _settings.sphereRadiusWS > 0 ? _settings.sphereRadiusWS : ScreenSpaceWaterRegistry.SphereRadiusWS;
                data.thicknessScale = _settings.thicknessScale;
                data.particleBuffer = ScreenSpaceWaterRegistry.ParticleBuffer;

                builder.SetRenderAttachment(fluidDepthA, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depthBuf, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((DepthPassData d, RasterGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.ClearRenderTarget(RTClearFlags.All, Color.black, 1f, 0);

                    // NB: bind via cmd.SetGlobal* (not MaterialPropertyBlock). In URP 17 RenderGraph,
                    // MPB-bound StructuredBuffers passed to RasterCommandBuffer.DrawProcedural can
                    // silently fail to propagate, leaving the SRV unbound — which reads as zeros
                    // (all particles at world origin -> typically off-screen -> nothing drawn).
                    cmd.SetGlobalBuffer(ID_ParticleBuffer, d.particleBuffer);
                    cmd.SetGlobalInt   (ID_ParticleCount,  d.particleCount);
                    cmd.SetGlobalFloat (ID_SphereRadiusWS, d.sphereRadiusWS);

                    cmd.DrawProcedural(Matrix4x4.identity, d.material, 0, MeshTopology.Triangles,
                        d.particleCount * 6, 1);
                });
            }

            // ---- PASS 1b: particle thickness (additive) ----------------------
            using (var builder = renderGraph.AddRasterRenderPass<DepthPassData>("SS Water — Particle Thickness", out var data))
            {
                data.material       = _particleDepthMaterial;
                data.particleCount  = ScreenSpaceWaterRegistry.ParticleCount;
                data.sphereRadiusWS = _settings.sphereRadiusWS > 0 ? _settings.sphereRadiusWS : ScreenSpaceWaterRegistry.SphereRadiusWS;
                data.thicknessScale = _settings.thicknessScale;
                data.particleBuffer = ScreenSpaceWaterRegistry.ParticleBuffer;

                builder.SetRenderAttachment(thickness, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((DepthPassData d, RasterGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.ClearRenderTarget(RTClearFlags.Color, Color.black, 1f, 0);

                    cmd.SetGlobalBuffer(ID_ParticleBuffer, d.particleBuffer);
                    cmd.SetGlobalInt   (ID_ParticleCount,  d.particleCount);
                    cmd.SetGlobalFloat (ID_SphereRadiusWS, d.sphereRadiusWS);
                    cmd.SetGlobalFloat (ID_ThicknessScale, d.thicknessScale);

                    cmd.DrawProcedural(Matrix4x4.identity, d.material, 1, MeshTopology.Triangles,
                        d.particleCount * 6, 1);
                });
            }

            // ---- PASS 2: narrow-range filter (ping-pong) ---------------------
            TextureHandle filterSrc = fluidDepthA;
            TextureHandle filterDst = fluidDepthB;
            int iterations = Mathf.Max(0, _settings.filterIterations);
            for (int i = 0; i < iterations; ++i)
            {
                // Horizontal
                AddFilterPass(renderGraph, filterSrc, filterDst, new Vector2(1f, 0f), texelSize);
                Swap(ref filterSrc, ref filterDst);

                // Vertical
                AddFilterPass(renderGraph, filterSrc, filterDst, new Vector2(0f, 1f), texelSize);
                Swap(ref filterSrc, ref filterDst);
            }
            TextureHandle finalDepth = filterSrc;

            // ---- PASS 3: composite over camera colour ------------------------
            using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>("SS Water — Composite", out var data))
            {
                data.material            = _compositeMaterial;
                data.fluidDepth          = finalDepth;
                data.fluidThickness      = thickness;
                data.fluidDepthTexelSize = texelSize;
                data.settings            = _settings;

                builder.UseTexture(finalDepth, AccessFlags.Read);
                builder.UseTexture(thickness,  AccessFlags.Read);
                if (resourceData.cameraDepthTexture.IsValid())
                    builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                if (resourceData.cameraOpaqueTexture.IsValid())
                    builder.UseTexture(resourceData.cameraOpaqueTexture, AccessFlags.Read);

                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((CompositePassData d, RasterGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    var mat = d.material;
                    // TextureHandle values must be bound via cmd.SetGlobalTexture so the
                    // RenderGraph can resolve the handle to the real GPU texture at execute time.
                    // mat.SetTexture(id, textureHandle) silently binds null.
                    cmd.SetGlobalTexture(ID_FluidDepth,     d.fluidDepth);
                    cmd.SetGlobalTexture(ID_FluidThickness, d.fluidThickness);
                    mat.SetVector (ID_FluidDepth_TS,         d.fluidDepthTexelSize);
                    mat.SetColor  (ID_LiquidExtinction,      d.settings.liquidExtinction);
                    mat.SetColor  (ID_LiquidScatterTint,     d.settings.liquidScatterTint);
                    mat.SetColor  (ID_LiquidDeepTint,        d.settings.liquidDeepTint);
                    mat.SetFloat  (ID_RefractionStrengthSS,  d.settings.refractionStrength);
                    mat.SetFloat  (ID_ReflectionStrengthSS,  d.settings.reflectionStrength);
                    mat.SetFloat  (ID_SpecularPower,         d.settings.specularPower);
                    mat.SetFloat  (ID_SpecularIntensity,     d.settings.specularIntensity);
                    mat.SetFloat  (ID_F0,                    d.settings.fresnelF0);
                    mat.SetFloat  (ID_MinThicknessForOpaque, d.settings.minThicknessForOpaque);
                    mat.SetInt    (ID_DebugMode,             d.settings.debugMode);
                    mat.SetFloat  (ID_DebugDepthRange,       d.settings.debugDepthRange);
                    mat.SetFloat  (ID_DebugThicknessRange,   d.settings.debugThicknessRange);

                    Blitter.BlitTexture(cmd, d.fluidDepth, new Vector4(1f, 1f, 0f, 0f), mat, 0);
                });
            }
        }

        private void AddFilterPass(RenderGraph renderGraph, TextureHandle src, TextureHandle dst, Vector2 dir, Vector4 texelSize)
        {
            using (var builder = renderGraph.AddRasterRenderPass<FilterPassData>("SS Water — Depth Filter", out var data))
            {
                data.material         = _depthFilterMaterial;
                data.source           = src;
                data.direction        = dir;
                data.radius           = _settings.filterRadius;
                data.spatialSigma     = _settings.spatialSigma;
                data.depthSigma       = _settings.depthSigma;
                data.sourceTexelSize  = texelSize;

                builder.UseTexture(src, AccessFlags.Read);
                builder.SetRenderAttachment(dst, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((FilterPassData d, RasterGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    var mat = d.material;
                    // TextureHandle must be bound via cmd.SetGlobalTexture, not mat.SetTexture.
                    cmd.SetGlobalTexture(ID_SourceDepth,    d.source);
                    mat.SetVector (ID_SourceDepth_TS,       d.sourceTexelSize);
                    mat.SetVector (ID_DepthFilterDirection, d.direction);
                    mat.SetFloat  (ID_SpatialSigma,         d.spatialSigma);
                    mat.SetFloat  (ID_DepthSigma,           d.depthSigma);
                    mat.SetInt    (ID_FilterRadius,         d.radius);

                    Blitter.BlitTexture(cmd, d.source, new Vector4(1f, 1f, 0f, 0f), mat, 0);
                });
            }
        }

        private static void Swap(ref TextureHandle a, ref TextureHandle b)
        {
            (a, b) = (b, a);
        }
    }
}
