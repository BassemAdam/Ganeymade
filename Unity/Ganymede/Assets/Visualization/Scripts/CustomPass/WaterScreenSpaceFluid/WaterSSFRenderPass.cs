// ============================================================
// Water SSF — Single ScriptableRenderPass that records the whole
// Simon Green pipeline into the URP RenderGraph.
//
// Stages (all in this file for clarity):
//   1) Allocate textures
//   2) Particle depth      → eye-depth + HW Z
//   3) Particle thickness  → additive Gaussian splat
//   4) Particle light-depth (optional)
//   5) Bilateral blur 2D   → blurred-eye-depth (single pass, no artefacts)
//   6) Normals from blurred depth
//   7) Scene-color copy
//   8) Composite over active colour (writes water HW Z)
//   9) Caustics projection (optional)
// ============================================================
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public sealed class WaterSSFRenderPass : ScriptableRenderPass
{
    // ---- Pass indices in WaterScreenSpaceFluid.shader ----
    private const int PASS_DEPTH           = 0;
    private const int PASS_THICKNESS       = 1;
    private const int PASS_LIGHT_DEPTH     = 2;
    private const int PASS_BLUR            = 3;
    private const int PASS_NORMALS         = 4;
    private const int PASS_COMPOSITE       = 5;
    private const int PASS_CAUSTICS        = 6;
    private const int PASS_THICKNESS_BLUR  = 7;

    // ---- Shader property IDs ----
    private static readonly int ID_NRF_MaxFilterSize       = Shader.PropertyToID("_NRF_MaxFilterSize");
    private static readonly int ID_NRF_ProjectedParticleK  = Shader.PropertyToID("_NRF_ProjectedParticleK");
    private static readonly int ID_NRF_Mu                  = Shader.PropertyToID("_NRF_Mu");
    private static readonly int ID_NRF_DepthThreshold      = Shader.PropertyToID("_NRF_DepthThreshold");
    private static readonly int ID_ParticleRadius          = Shader.PropertyToID("_ParticleRadius");
    private static readonly int ID_WaterSSFThicknessSource = Shader.PropertyToID("_WaterSSFThicknessSource");
    private static readonly int ID_WaterSSFDepthRaw        = Shader.PropertyToID("_WaterSSFDepthRaw");
    private static readonly int ID_WaterSSFDepthSource     = Shader.PropertyToID("_WaterSSFDepthSource");
    private static readonly int ID_WaterSSFDepthSmooth     = Shader.PropertyToID("_WaterSSFDepthSmooth");
    private static readonly int ID_WaterSSFDepthTexelSize  = Shader.PropertyToID("_WaterSSFDepthTexelSize");
    private static readonly int ID_WaterSSFBlurDirection   = Shader.PropertyToID("_WaterSSFBlurDirection");
    private static readonly int ID_WaterSSFThickness       = Shader.PropertyToID("_WaterSSFThickness");
    private static readonly int ID_WaterSSFNormals        = Shader.PropertyToID("_WaterSSFNormals");
    private static readonly int ID_WaterSSFSceneCopy      = Shader.PropertyToID("_WaterSSFSceneCopy");
    private static readonly int ID_WaterSSFLightDepth     = Shader.PropertyToID("_WaterSSFLightDepth");
    private static readonly int ID_WaterSSFLightVP        = Shader.PropertyToID("_WaterSSFLightVP");
    private static readonly int ID_WaterSSFLightShadowEnabled  = Shader.PropertyToID("_WaterSSFLightShadowEnabled");
    private static readonly int ID_WaterSSFLightShadowStrength = Shader.PropertyToID("_WaterSSFLightShadowStrength");
    private static readonly int ID_WaterSSFLightShadowBias     = Shader.PropertyToID("_WaterSSFLightShadowBias");
    private static readonly int ID_SSFViewMatrix          = Shader.PropertyToID("_SSFViewMatrix");
    private static readonly int ID_SSFProjMatrix          = Shader.PropertyToID("_SSFProjMatrix");
    private static readonly int ID_SSFUseOverrideMatrices = Shader.PropertyToID("_SSFUseOverrideMatrices");
    private static readonly int ID_CameraDepthTexture     = Shader.PropertyToID("_CameraDepthTexture");

    // ---- Configuration set by the feature each frame ----
    public Material Material;
    public Action<RasterCommandBuffer, Material> OnDrawDepth;
    public Action<RasterCommandBuffer, Material> OnDrawThickness;
    public Action<RasterCommandBuffer, Material> OnDrawLightDepth;
    public bool    EnableLightShadow;
    public int     LightShadowResolution;
    public float   LightShadowStrength;
    public float   LightShadowBias;
    public float   LightShadowExtra;
    public bool    EnableCaustics;
    public bool    HasBounds;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;

    // ---------------------------------------------------------------
    public override void RecordRenderGraph(RenderGraph rg, ContextContainer frameData)
    {
        if (Material == null) return;

        var cameraData   = frameData.Get<UniversalCameraData>();
        var resourceData = frameData.Get<UniversalResourceData>();

        // -- Snapshot live parameters --
        float nrfMaxSize  = Material.GetFloat(ID_NRF_MaxFilterSize);
        float nrfMu       = Material.GetFloat(ID_NRF_Mu);
        float nrfThresh   = Material.GetFloat(ID_NRF_DepthThreshold);

        // Auto-compute projected particle K from camera FOV + screen height + particle radius.
        // Formula (matches reference fluidRender.ts):
        //   K = 0.6 * diameter * (screenH/2) / tan(fov/2)
        // At depth d: filterRadius = K/d = 0.6 * projectedDiameter(pixels)
        // Override by setting _NRF_ProjectedParticleK > 0 in the material inspector.
        float kOverride = Material.GetFloat(ID_NRF_ProjectedParticleK);
        float nrfProjK;
        if (kOverride > 0f)
        {
            nrfProjK = kOverride;
        }
        else
        {
            float particleRadius = Material.GetFloat(ID_ParticleRadius);
            float fovRad         = cameraData.camera.fieldOfView * Mathf.Deg2Rad;
            int   screenH        = cameraData.cameraTargetDescriptor.height;
            nrfProjK             = 0.6f * (2f * particleRadius)
                                   * (screenH * 0.5f)
                                   / Mathf.Tan(fovRad * 0.5f);
        }

        // ============================================================
        // 1) Texture descriptors + handles
        // ============================================================
        var baseDesc = cameraData.cameraTargetDescriptor;
        baseDesc.depthBufferBits = 0;
        baseDesc.msaaSamples     = 1;

        var depthDesc     = baseDesc; depthDesc.colorFormat     = RenderTextureFormat.RFloat;
        var thicknessDesc = baseDesc; thicknessDesc.colorFormat = RenderTextureFormat.RHalf;
        var normalsDesc   = baseDesc; normalsDesc.colorFormat   = RenderTextureFormat.ARGBHalf;
        var colorDesc     = baseDesc;
        var hwDepthDesc   = baseDesc; hwDepthDesc.colorFormat   = RenderTextureFormat.Depth; hwDepthDesc.depthBufferBits = 24;

        TextureHandle depthRaw        = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc,     "_WaterSSFDepthRaw",        false);
        TextureHandle depthHWBuf      = UniversalRenderer.CreateRenderGraphTexture(rg, hwDepthDesc,   "_WaterSSFDepthBuffer",     false);
        TextureHandle thickness       = UniversalRenderer.CreateRenderGraphTexture(rg, thicknessDesc, "_WaterSSFThickness",       false);
        TextureHandle thicknessBlurA  = UniversalRenderer.CreateRenderGraphTexture(rg, thicknessDesc, "_WaterSSFThicknessBlurA",  false);
        TextureHandle thicknessSmooth = UniversalRenderer.CreateRenderGraphTexture(rg, thicknessDesc, "_WaterSSFThicknessSmooth", false);
        // NRF depth blur: one unique handle per intermediate step — URP RenderGraph requires
        // each transient handle to be written by exactly one pass.
        TextureHandle depthBlur1X     = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc, "_WaterSSFDepthBlur1X", false); // iter1 X out
        TextureHandle depthBlur1Y     = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc, "_WaterSSFDepthBlur1Y", false); // iter1 Y out
        TextureHandle depthBlur2X     = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc, "_WaterSSFDepthBlur2X", false); // iter2 X out
        TextureHandle depthBlur2Y     = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc, "_WaterSSFDepthBlur2Y", false); // iter2 Y out
        TextureHandle depthBlur3X     = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc, "_WaterSSFDepthBlur3X", false); // iter3 X out
        TextureHandle depthSmooth     = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc, "_WaterSSFDepthSmooth",  false); // iter3 Y out (final)
        TextureHandle normalsTex      = UniversalRenderer.CreateRenderGraphTexture(rg, normalsDesc,   "_WaterSSFNormals",         false);
        TextureHandle sceneCopy       = UniversalRenderer.CreateRenderGraphTexture(rg, colorDesc,     "_WaterSSFSceneCopy",       false);

        TextureHandle lightDepth    = TextureHandle.nullHandle;
        TextureHandle lightDepthBuf = TextureHandle.nullHandle;
        Matrix4x4 lightVP = Matrix4x4.identity;
        Matrix4x4 lightV  = Matrix4x4.identity;
        Matrix4x4 lightP  = Matrix4x4.identity;
        bool useLightShadow = EnableLightShadow && OnDrawLightDepth != null;
        if (useLightShadow)
        {
            useLightShadow = TryBuildLightMatrices(out lightV, out lightP);
            if (useLightShadow)
            {
                lightVP = lightP * lightV;
                var lightDesc = new RenderTextureDescriptor(LightShadowResolution, LightShadowResolution, RenderTextureFormat.RFloat, 0);
                lightDesc.msaaSamples = 1;
                var lightHWDesc = new RenderTextureDescriptor(LightShadowResolution, LightShadowResolution, RenderTextureFormat.Depth, 24);
                lightHWDesc.msaaSamples = 1;
                lightDepth    = UniversalRenderer.CreateRenderGraphTexture(rg, lightDesc,   "_WaterSSFLightDepth",       false);
                lightDepthBuf = UniversalRenderer.CreateRenderGraphTexture(rg, lightHWDesc, "_WaterSSFLightDepthBuffer", false);
            }
        }

        // ============================================================
        // 2) Particle depth pass — sphere impostor, HW Z, eye-depth
        // ============================================================
        RecordParticleDraw(rg, "Water SSF Depth", PASS_DEPTH,
            colorTarget: depthRaw, depthTarget: depthHWBuf,
            clearColor: true, clearDepth: true, useLightMatrices: false,
            lightV: Matrix4x4.identity, lightP: Matrix4x4.identity,
            drawCallback: OnDrawDepth,
            exposeColorAsGlobalID: ID_WaterSSFDepthRaw);

        // ============================================================
        // 3) Particle thickness pass — additive splat
        // ============================================================
        RecordParticleDraw(rg, "Water SSF Thickness", PASS_THICKNESS,
            colorTarget: thickness, depthTarget: TextureHandle.nullHandle,
            clearColor: true, clearDepth: false, useLightMatrices: false,
            lightV: Matrix4x4.identity, lightP: Matrix4x4.identity,
            drawCallback: OnDrawThickness,
            exposeColorAsGlobalID: ID_WaterSSFThickness);

        // ============================================================
        // 3.5) Gaussian thickness blur X+Y
        //      Matches reference gaussian.wgsl (filterSize=15, sigma=filterSize/3).
        //      Smooths out the per-particle additive splats so Beer-Lambert
        //      absorption doesn't create dark halos at particle centres.
        // ============================================================
        RecordThicknessBlur(rg, "Water SSF Thickness Blur X", thickness,      thicknessBlurA,  baseDesc.width, baseDesc.height, new Vector2(1f / baseDesc.width,  0f), false);
        RecordThicknessBlur(rg, "Water SSF Thickness Blur Y", thicknessBlurA, thicknessSmooth, baseDesc.width, baseDesc.height, new Vector2(0f, 1f / baseDesc.height), true);

        // ============================================================
        // 4) Particle light-depth pass — optional
        // ============================================================
        if (useLightShadow)
        {
            RecordParticleDraw(rg, "Water SSF Light Depth", PASS_LIGHT_DEPTH,
                colorTarget: lightDepth, depthTarget: lightDepthBuf,
                clearColor: true, clearDepth: true, useLightMatrices: true,
                lightV: lightV, lightP: lightP,
                drawCallback: OnDrawLightDepth);
        }

        // ============================================================
        // 5) Narrow-Range Filter — 3 iterations (matches reference pipeline)
        //    Reference: 2× 1D(X+Y) + 1× 2D(X+Y) = 6 blur passes total.
        //    We run 3× 1D(X+Y) which achieves equivalent smoothing.
        //    Ping-pong: depthSmoothA (temp) ↔ depthSmooth (accumulated result).
        // ============================================================
        // Each src→dst pair uses a UNIQUE destination handle — required by URP RenderGraph
        // (a transient texture may only be the render attachment of exactly one pass).
        // Iteration 1
        RecordBlur(rg, "Water SSF Blur X1", depthRaw,    depthBlur1X, nrfMaxSize, nrfProjK, nrfMu, nrfThresh, baseDesc.width, baseDesc.height, new Vector2(1f / baseDesc.width,  0f), false);
        RecordBlur(rg, "Water SSF Blur Y1", depthBlur1X, depthBlur1Y, nrfMaxSize, nrfProjK, nrfMu, nrfThresh, baseDesc.width, baseDesc.height, new Vector2(0f, 1f / baseDesc.height), false);
        // Iteration 2
        RecordBlur(rg, "Water SSF Blur X2", depthBlur1Y, depthBlur2X, nrfMaxSize, nrfProjK, nrfMu, nrfThresh, baseDesc.width, baseDesc.height, new Vector2(1f / baseDesc.width,  0f), false);
        RecordBlur(rg, "Water SSF Blur Y2", depthBlur2X, depthBlur2Y, nrfMaxSize, nrfProjK, nrfMu, nrfThresh, baseDesc.width, baseDesc.height, new Vector2(0f, 1f / baseDesc.height), false);
        // Iteration 3 (expose final result as global _WaterSSFDepthSmooth)
        RecordBlur(rg, "Water SSF Blur X3", depthBlur2Y, depthBlur3X, nrfMaxSize, nrfProjK, nrfMu, nrfThresh, baseDesc.width, baseDesc.height, new Vector2(1f / baseDesc.width,  0f), false);
        RecordBlur(rg, "Water SSF Blur Y3", depthBlur3X, depthSmooth,  nrfMaxSize, nrfProjK, nrfMu, nrfThresh, baseDesc.width, baseDesc.height, new Vector2(0f, 1f / baseDesc.height), true);

        // ============================================================
        // 6) Normals from smoothed depth
        // ============================================================
        RecordNormals(rg, depthSmooth, thicknessSmooth, normalsTex, baseDesc.width, baseDesc.height);

        // ============================================================
        // 7) Scene copy (needed by composite)
        // ============================================================
        RecordSceneCopy(rg, resourceData.activeColorTexture, sceneCopy);

        // ============================================================
        // 8) Composite — DEBUG: shows blurred depth as greyscale
        // ============================================================
        RecordComposite(rg, depthSmooth, normalsTex, thicknessSmooth, sceneCopy,
            lightDepth, lightVP, useLightShadow,
            resourceData.activeColorTexture, resourceData.activeDepthTexture);
    }

    // ----------------------------------------------------------------
    // Particle draw helper — used by depth, thickness, light-depth.
    // ----------------------------------------------------------------
    private sealed class ParticleDrawData
    {
        public Material material;
        public Action<RasterCommandBuffer, Material> draw;
        public bool      clearColor;
        public bool      clearDepth;
        public bool      useLightMatrices;
        public Matrix4x4 viewMatrix;
        public Matrix4x4 projMatrix;
    }

    private void RecordParticleDraw(
        RenderGraph rg, string name, int passIndex,
        TextureHandle colorTarget, TextureHandle depthTarget,
        bool clearColor, bool clearDepth, bool useLightMatrices,
        Matrix4x4 lightV, Matrix4x4 lightP,
        Action<RasterCommandBuffer, Material> drawCallback,
        int exposeColorAsGlobalID = -1)
    {
        if (drawCallback == null) return;

        using (var builder = rg.AddRasterRenderPass<ParticleDrawData>(name, out var data))
        {
            data.material         = Material;
            data.draw             = drawCallback;
            data.clearColor       = clearColor;
            data.clearDepth       = clearDepth;
            data.useLightMatrices = useLightMatrices;
            data.viewMatrix       = lightV;
            data.projMatrix       = lightP;

            builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);
            if (depthTarget.IsValid())
                builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.Write);
            if (exposeColorAsGlobalID >= 0)
                builder.SetGlobalTextureAfterPass(colorTarget, exposeColorAsGlobalID);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((ParticleDrawData d, RasterGraphContext ctx) =>
            {
                if (d.useLightMatrices)
                {
                    ctx.cmd.SetGlobalMatrix(ID_SSFViewMatrix, d.viewMatrix);
                    ctx.cmd.SetGlobalMatrix(ID_SSFProjMatrix, d.projMatrix);
                    ctx.cmd.SetGlobalInt   (ID_SSFUseOverrideMatrices, 1);
                }
                else
                {
                    ctx.cmd.SetGlobalInt(ID_SSFUseOverrideMatrices, 0);
                }

                if (d.clearColor || d.clearDepth)
                    ctx.cmd.ClearRenderTarget(d.clearDepth, d.clearColor, Color.clear);

                d.draw?.Invoke(ctx.cmd, d.material);

                // Always reset override flag so subsequent passes use camera matrices.
                ctx.cmd.SetGlobalInt(ID_SSFUseOverrideMatrices, 0);
            });
        }
    }

    // ----------------------------------------------------------------
    // Bilateral blur (one direction).
    // ----------------------------------------------------------------
    private sealed class BlurData
    {
        public TextureHandle source;
        public Material      material;
        public float         nrfMaxFilterSize;
        public float         nrfProjK;
        public float         nrfMu;
        public float         nrfDepthThreshold;
        public Vector4       texelSize;
        public Vector2       blurDirection;
    }

    private void RecordBlur(RenderGraph rg, string passName, TextureHandle src, TextureHandle dst,
        float maxSize, float projK, float mu, float thresh, int w, int h,
        Vector2 blurDirection, bool exposeAsSmooth)
    {
        using (var builder = rg.AddRasterRenderPass<BlurData>(passName, out var data))
        {
            data.source            = src;
            data.material          = Material;
            data.nrfMaxFilterSize  = maxSize;
            data.nrfProjK          = projK;
            data.nrfMu             = mu;
            data.nrfDepthThreshold = thresh;
            data.texelSize         = new Vector4(1f / w, 1f / h, w, h);
            data.blurDirection     = blurDirection;

            builder.UseTexture(src, AccessFlags.Read);
            builder.SetRenderAttachment(dst, 0, AccessFlags.Write);
            if (exposeAsSmooth)
                builder.SetGlobalTextureAfterPass(dst, ID_WaterSSFDepthSmooth);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((BlurData d, RasterGraphContext ctx) =>
            {
                ctx.cmd.SetGlobalTexture(ID_WaterSSFDepthSource,       d.source);
                ctx.cmd.SetGlobalVector (ID_WaterSSFDepthTexelSize,    d.texelSize);
                ctx.cmd.SetGlobalVector (ID_WaterSSFBlurDirection,     d.blurDirection);
                // Use SetGlobalFloat — material.SetFloat inside a render func does NOT
                // propagate to GPU shader constants in URP RenderGraph.
                ctx.cmd.SetGlobalFloat  (ID_NRF_MaxFilterSize,      d.nrfMaxFilterSize);
                ctx.cmd.SetGlobalFloat  (ID_NRF_ProjectedParticleK, d.nrfProjK);
                ctx.cmd.SetGlobalFloat  (ID_NRF_Mu,                 d.nrfMu);
                ctx.cmd.SetGlobalFloat  (ID_NRF_DepthThreshold,     d.nrfDepthThreshold);
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, PASS_BLUR);
            });
        }
    }

    // ----------------------------------------------------------------
    // Gaussian thickness blur (one direction — run X then Y).
    // Matches reference gaussian.wgsl (filterSize=15, sigma=filterSize/3).
    // ----------------------------------------------------------------
    private sealed class ThicknessBlurData
    {
        public TextureHandle source;
        public Material      material;
        public Vector4       texelSize;
        public Vector2       blurDirection;
    }

    private void RecordThicknessBlur(RenderGraph rg, string passName, TextureHandle src, TextureHandle dst,
        int w, int h, Vector2 blurDirection, bool exposeAsThickness)
    {
        using (var builder = rg.AddRasterRenderPass<ThicknessBlurData>(passName, out var data))
        {
            data.source        = src;
            data.material      = Material;
            data.texelSize     = new Vector4(1f / w, 1f / h, w, h);
            data.blurDirection = blurDirection;

            builder.UseTexture(src, AccessFlags.Read);
            builder.SetRenderAttachment(dst, 0, AccessFlags.Write);
            if (exposeAsThickness)
                builder.SetGlobalTextureAfterPass(dst, ID_WaterSSFThickness);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((ThicknessBlurData d, RasterGraphContext ctx) =>
            {
                ctx.cmd.SetGlobalTexture(ID_WaterSSFThicknessSource, d.source);
                ctx.cmd.SetGlobalVector (ID_WaterSSFDepthTexelSize,  d.texelSize);
                ctx.cmd.SetGlobalVector (ID_WaterSSFBlurDirection,   d.blurDirection);
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, PASS_THICKNESS_BLUR);
            });
        }
    }

    // ----------------------------------------------------------------
    // Normals.
    // ----------------------------------------------------------------
    private sealed class NormalsData
    {
        public TextureHandle smoothDepth;
        public Material      material;
        public Vector4       texelSize;
    }

    private void RecordNormals(RenderGraph rg, TextureHandle smoothDepth, TextureHandle thickness, TextureHandle normalsOut, int w, int h)
    {
        using (var builder = rg.AddRasterRenderPass<NormalsData>("Water SSF Normals", out var data))
        {
            data.smoothDepth = smoothDepth;
            data.material    = Material;
            data.texelSize   = new Vector4(1f / w, 1f / h, w, h);

            builder.UseTexture(smoothDepth, AccessFlags.Read);
            builder.SetRenderAttachment(normalsOut, 0, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(normalsOut, ID_WaterSSFNormals);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((NormalsData d, RasterGraphContext ctx) =>
            {
                ctx.cmd.SetGlobalTexture(ID_WaterSSFDepthSmooth, d.smoothDepth);
                ctx.cmd.SetGlobalVector(ID_WaterSSFDepthTexelSize, d.texelSize);
                Blitter.BlitTexture(ctx.cmd, d.smoothDepth, new Vector4(1, 1, 0, 0), d.material, PASS_NORMALS);
            });
        }
    }

    // ----------------------------------------------------------------
    // Scene-color copy.
    // ----------------------------------------------------------------
    private sealed class SceneCopyData { public TextureHandle source; }

    private void RecordSceneCopy(RenderGraph rg, TextureHandle activeColor, TextureHandle sceneCopy)
    {
        using (var builder = rg.AddRasterRenderPass<SceneCopyData>("Water SSF Scene Copy", out var data))
        {
            data.source = activeColor;
            builder.UseTexture(activeColor, AccessFlags.Read);
            builder.SetRenderAttachment(sceneCopy, 0, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(sceneCopy, ID_WaterSSFSceneCopy);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((SceneCopyData d, RasterGraphContext ctx) =>
            {
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), 0, false);
            });
        }
    }

    // ----------------------------------------------------------------
    // Debug display.
    // ----------------------------------------------------------------
    private sealed class DebugDisplayData { public TextureHandle source; }

    private void RecordDebugDisplay(RenderGraph rg, TextureHandle source, TextureHandle activeColor)
    {
        using (var builder = rg.AddRasterRenderPass<DebugDisplayData>("Water SSF Debug Display", out var data))
        {
            data.source = source;
            builder.UseTexture(source, AccessFlags.Read);
            builder.SetRenderAttachment(activeColor, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((DebugDisplayData d, RasterGraphContext ctx) =>
            {
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), 0, false);
            });
        }
    }

    // ----------------------------------------------------------------
    // Composite.
    // ----------------------------------------------------------------
    private sealed class CompositeData
    {
        public TextureHandle smoothDepth;
        public TextureHandle lightDepth;
        public Material      material;
        public Matrix4x4     lightVP;
        public bool          shadowEnabled;
        public float         shadowStrength;
        public float         shadowBias;
    }

    private void RecordComposite(RenderGraph rg,
        TextureHandle smoothDepth, TextureHandle normals, TextureHandle thickness, TextureHandle sceneCopy,
        TextureHandle lightDepth, Matrix4x4 lightVP, bool shadowEnabled,
        TextureHandle activeColor, TextureHandle activeDepth)
    {
        using (var builder = rg.AddRasterRenderPass<CompositeData>("Water SSF Composite", out var data))
        {
            data.smoothDepth    = smoothDepth;
            data.lightDepth     = lightDepth;
            data.material       = Material;
            data.lightVP        = lightVP;
            data.shadowEnabled  = shadowEnabled;
            data.shadowStrength = LightShadowStrength;
            data.shadowBias     = LightShadowBias;

            builder.UseTexture(smoothDepth, AccessFlags.Read);
            builder.UseTexture(normals,     AccessFlags.Read);
            builder.UseTexture(thickness,   AccessFlags.Read);
            builder.UseTexture(sceneCopy,   AccessFlags.Read);
            if (shadowEnabled && lightDepth.IsValid())
                builder.UseTexture(lightDepth, AccessFlags.Read);

            builder.SetRenderAttachment(activeColor, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(activeDepth, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((CompositeData d, RasterGraphContext ctx) =>
            {
                ctx.cmd.SetGlobalFloat  (ID_WaterSSFLightShadowEnabled,  d.shadowEnabled ? 1f : 0f);
                ctx.cmd.SetGlobalFloat  (ID_WaterSSFLightShadowStrength, d.shadowStrength);
                ctx.cmd.SetGlobalFloat  (ID_WaterSSFLightShadowBias,     d.shadowBias);
                if (d.shadowEnabled && d.lightDepth.IsValid())
                {
                    ctx.cmd.SetGlobalMatrix (ID_WaterSSFLightVP,    d.lightVP);
                    ctx.cmd.SetGlobalTexture(ID_WaterSSFLightDepth, d.lightDepth);
                }
                Blitter.BlitTexture(ctx.cmd, d.smoothDepth, new Vector4(1, 1, 0, 0), d.material, PASS_COMPOSITE);
            });
        }
    }

    // ----------------------------------------------------------------
    // Caustics.
    // ----------------------------------------------------------------
    private sealed class CausticsData
    {
        public TextureHandle smoothDepth;
        public TextureHandle thickness;
        public TextureHandle sceneDepth;
        public TextureHandle lightDepth;
        public Material      material;
        public bool          shadowEnabled;
    }

    private void RecordCaustics(RenderGraph rg,
        TextureHandle smoothDepth, TextureHandle thickness,
        TextureHandle lightDepth, Matrix4x4 lightVP, bool shadowEnabled,
        TextureHandle activeColor, TextureHandle activeDepth)
    {
        using (var builder = rg.AddRasterRenderPass<CausticsData>("Water SSF Caustics", out var data))
        {
            data.smoothDepth   = smoothDepth;
            data.thickness     = thickness;
            data.sceneDepth    = activeDepth;
            data.lightDepth    = lightDepth;
            data.material      = Material;
            data.shadowEnabled = shadowEnabled;

            builder.UseTexture(smoothDepth, AccessFlags.Read);
            builder.UseTexture(thickness,   AccessFlags.Read);
            builder.UseTexture(activeDepth, AccessFlags.Read);
            if (shadowEnabled && lightDepth.IsValid())
                builder.UseTexture(lightDepth, AccessFlags.Read);

            builder.SetRenderAttachment(activeColor, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((CausticsData d, RasterGraphContext ctx) =>
            {
                ctx.cmd.SetGlobalTexture(ID_CameraDepthTexture, d.sceneDepth);
                if (d.shadowEnabled && d.lightDepth.IsValid())
                    ctx.cmd.SetGlobalTexture(ID_WaterSSFLightDepth, d.lightDepth);
                Blitter.BlitTexture(ctx.cmd, d.smoothDepth, new Vector4(1, 1, 0, 0), d.material, PASS_CAUSTICS);
            });
        }
    }

    // ----------------------------------------------------------------
    // Build orthographic light view / projection that fits the bounds.
    // ----------------------------------------------------------------
    private bool TryBuildLightMatrices(out Matrix4x4 view, out Matrix4x4 proj)
    {
        view = Matrix4x4.identity;
        proj = Matrix4x4.identity;

        Light sun = RenderSettings.sun;
        if (sun == null)
        {
            // Fallback: scan loaded directional lights.
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (var l in lights)
            {
                if (l != null && l.isActiveAndEnabled && l.type == LightType.Directional)
                {
                    sun = l;
                    break;
                }
            }
        }
        if (sun == null) return false;

        Vector3 lightDir = sun.transform.forward;
        if (lightDir.sqrMagnitude < 1e-6f) return false;
        lightDir.Normalize();

        Vector3 boundsCenter;
        Vector3 boundsExtent;
        if (HasBounds)
        {
            boundsCenter = (BoundsMin + BoundsMax) * 0.5f;
            boundsExtent = (BoundsMax - BoundsMin) * 0.5f;
        }
        else
        {
            boundsCenter = Vector3.zero;
            boundsExtent = new Vector3(10f, 10f, 10f);
        }

        float diag = boundsExtent.magnitude + LightShadowExtra;
        Vector3 lightPos = boundsCenter - lightDir * (diag * 2f);

        view = Matrix4x4.LookAt(lightPos, boundsCenter, Mathf.Abs(Vector3.Dot(lightDir, Vector3.up)) > 0.99f ? Vector3.right : Vector3.up).inverse;
        // Matrix4x4.LookAt returns world→camera-space, but Unity wants camera→world for the inverse used as view. Correct it:
        view = view; // Already world→view when using LookAt(...).inverse
        // Use LookAt directly (camera → world) and invert for view matrix:
        Matrix4x4 lookAt = Matrix4x4.LookAt(lightPos, boundsCenter, Vector3.up);
        view = lookAt.inverse;

        proj = Matrix4x4.Ortho(-diag, diag, -diag, diag, 0.01f, diag * 4f);
        // Convert to GPU projection (handles flipped Y, reversed Z, etc.) so light VP
        // matches what shaders expect when constructing clip-space coordinates.
        proj = GL.GetGPUProjectionMatrix(proj, true);
        return true;
    }
}
