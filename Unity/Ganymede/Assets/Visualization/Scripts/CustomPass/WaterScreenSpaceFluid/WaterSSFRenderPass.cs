using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// This is the actual render pass that does all the heavy lifting for screen space fluid rendering.
// It runs every frame and orchestrates the full pipeline: depth, blur, normals, thickness, and final composite.
public sealed class WaterSSFRenderPass : ScriptableRenderPass
{
    // These are the shader pass indices. Each number maps to a specific sub-shader inside the material.
    // Keeping them as named constants makes it obvious what each call is actually doing.
    private const int PASS_DEPTH           = 0;
    private const int PASS_THICKNESS       = 1;
    private const int PASS_BLUR            = 2;
    private const int PASS_NORMALS         = 3;
    private const int PASS_COMPOSITE       = 4;
    private const int PASS_THICKNESS_BLUR  = 5;
    private const int PASS_NORMALS_BLUR    = 6;

    // Caching shader property IDs upfront is a Unity performance best practice.
    // Shader.PropertyToID does a string hash lookup, so doing it once and reusing the int is much faster than passing strings every frame.
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
    private static readonly int ID_WaterSSFNormalsSource  = Shader.PropertyToID("_WaterSSFNormalsSource");
    private static readonly int ID_WaterSSFSceneCopy      = Shader.PropertyToID("_WaterSSFSceneCopy");
    private static readonly int ID_SSFViewMatrix          = Shader.PropertyToID("_SSFViewMatrix");
    private static readonly int ID_SSFProjMatrix          = Shader.PropertyToID("_SSFProjMatrix");
    private static readonly int ID_SSFUseOverrideMatrices = Shader.PropertyToID("_SSFUseOverrideMatrices");

    // The material holding all the shader passes for this effect.
    public Material Material;

    // These are callbacks set by the fluid system. The particle renderer calls back here
    // to issue draw calls for depth and thickness into our custom render targets.
    public Action<RasterCommandBuffer, Material> OnDrawDepth;
    public Action<RasterCommandBuffer, Material> OnDrawThickness;

    // Optional world-space bounds for the fluid simulation. Not used in rendering directly here,
    // but passed along in case shader logic needs to clip or cull outside the volume.
    public bool    HasBounds;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;

    public override void RecordRenderGraph(RenderGraph rg, ContextContainer frameData)
    {
        if (Material == null) return;

        var cameraData   = frameData.Get<UniversalCameraData>();
        var resourceData = frameData.Get<UniversalResourceData>();

        // Pull the NRF (Normalized Radius Filter) parameters from the material.
        // These control how the depth blur behaves, shaping the smooth surface look.
        float nrfMaxSize  = Material.GetFloat(ID_NRF_MaxFilterSize);
        float nrfMu       = Material.GetFloat(ID_NRF_Mu);
        float nrfThresh   = Material.GetFloat(ID_NRF_DepthThreshold);

        // nrfProjK is the projected screen-space size of a particle kernel.
        // If an override is set in the material, use that directly. Otherwise derive it
        // from the particle radius and the camera FOV so it stays correct regardless of resolution or zoom level.
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

        // Start from the camera's descriptor and strip things we don't need.
        // No depth bits on intermediate targets and no MSAA
        var baseDesc = cameraData.cameraTargetDescriptor;
        baseDesc.depthBufferBits = 0;
        baseDesc.msaaSamples     = 1;

        // Each texture gets its own format tuned to its content.
        // RFloat for depth (full precision), RHalf for thickness and normals (cheaper, enough precision).
        // The hardware depth buffer gets 24 bits so the GPU can do proper occlusion during particle drawing.
        var depthDesc     = baseDesc; depthDesc.colorFormat     = RenderTextureFormat.RFloat;
        var thicknessDesc = baseDesc; thicknessDesc.colorFormat = RenderTextureFormat.RHalf;
        var normalsDesc   = baseDesc; normalsDesc.colorFormat   = RenderTextureFormat.RGHalf;
        var colorDesc     = baseDesc;
        var hwDepthDesc   = baseDesc; hwDepthDesc.colorFormat   = RenderTextureFormat.Depth; hwDepthDesc.depthBufferBits = 24;

        // Allocate all the intermediate render textures the pipeline needs.
        // These live only for this frame, RenderGraph manages their lifetime automatically.
        TextureHandle depthRaw        = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc,     "_WaterSSFDepthRaw",        false);
        TextureHandle depthHWBuf      = UniversalRenderer.CreateRenderGraphTexture(rg, hwDepthDesc,   "_WaterSSFDepthBuffer",     false);
        TextureHandle thickness       = UniversalRenderer.CreateRenderGraphTexture(rg, thicknessDesc, "_WaterSSFThickness",       false);
        TextureHandle thicknessBlurA  = UniversalRenderer.CreateRenderGraphTexture(rg, thicknessDesc, "_WaterSSFThicknessBlurA",  false);
        TextureHandle thicknessSmooth = UniversalRenderer.CreateRenderGraphTexture(rg, thicknessDesc, "_WaterSSFThicknessSmooth", false);

        // Six intermediate buffers for the depth blur. We run three full horizontal+vertical passes
        // to progressively smooth the depth surface, which gives us that clean fluid look.
        TextureHandle depthBlur1X     = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc, "_WaterSSFDepthBlur1X", false);
        TextureHandle depthBlur1Y     = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc, "_WaterSSFDepthBlur1Y", false);
        TextureHandle depthBlur2X     = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc, "_WaterSSFDepthBlur2X", false);
        TextureHandle depthBlur2Y     = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc, "_WaterSSFDepthBlur2Y", false);
        TextureHandle depthBlur3X     = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc, "_WaterSSFDepthBlur3X", false);
        TextureHandle depthBlur3Y     = UniversalRenderer.CreateRenderGraphTexture(rg, depthDesc, "_WaterSSFDepthBlur3Y", false);

        TextureHandle normalsTex      = UniversalRenderer.CreateRenderGraphTexture(rg, normalsDesc,   "_WaterSSFNormals",         false);
        TextureHandle normalsBlurA    = UniversalRenderer.CreateRenderGraphTexture(rg, normalsDesc,   "_WaterSSFNormalsBlurA",    false);
        TextureHandle normalsSmooth   = UniversalRenderer.CreateRenderGraphTexture(rg, normalsDesc,   "_WaterSSFNormalsSmooth",   false);
        TextureHandle sceneCopy       = UniversalRenderer.CreateRenderGraphTexture(rg, colorDesc,     "_WaterSSFSceneCopy",       false);

        // Step 1: Render the raw particle depth into a floating point buffer.
        // We also write to a hardware depth buffer so the GPU can reject occluded fragments cheaply.
        RecordParticleDraw(rg, "Water SSF Depth", PASS_DEPTH,
            colorTarget: depthRaw, depthTarget: depthHWBuf,
            clearColor: true, clearDepth: true,
            drawCallback: OnDrawDepth,
            exposeColorAsGlobalID: ID_WaterSSFDepthRaw);

        // Step 2: Render particle thickness into a separate buffer.
        // Thickness tells us how much fluid a ray passes through, used later for opacity and color absorption.
        // No depth buffer needed here since we're accumulating additive values, not doing occlusion.
        RecordParticleDraw(rg, "Water SSF Thickness", PASS_THICKNESS,
            colorTarget: thickness, depthTarget: TextureHandle.nullHandle,
            clearColor: true, clearDepth: false,
            drawCallback: OnDrawThickness,
            exposeColorAsGlobalID: ID_WaterSSFThickness);

        // Step 3: Blur the thickness with a simple separable Gaussian.
        // Thickness doesn't need the fancy NRF blur, a regular smooth is enough.
        RecordThicknessBlur(rg, "Water SSF Thickness Blur X", thickness,      thicknessBlurA,  baseDesc.width, baseDesc.height, new Vector2(1f / baseDesc.width,  0f), false);
        RecordThicknessBlur(rg, "Water SSF Thickness Blur Y", thicknessBlurA, thicknessSmooth, baseDesc.width, baseDesc.height, new Vector2(0f, 1f / baseDesc.height), true);

        // Step 4: Smooth the depth with three separable NRF blur passes.
        // Three passes progressively build up a wide smooth kernel without blowing up the per-pass kernel radius.
        // The NRF filter is depth-aware so edges between water and background stay sharp.
        RecordBlur(rg, "Water SSF Blur X1", depthRaw,    depthBlur1X, nrfMaxSize, nrfProjK, nrfMu, nrfThresh, baseDesc.width, baseDesc.height, new Vector2(1f / baseDesc.width,  0f), false);
        RecordBlur(rg, "Water SSF Blur Y1", depthBlur1X, depthBlur1Y, nrfMaxSize, nrfProjK, nrfMu, nrfThresh, baseDesc.width, baseDesc.height, new Vector2(0f, 1f / baseDesc.height), false);
        RecordBlur(rg, "Water SSF Blur X2", depthBlur1Y, depthBlur2X, nrfMaxSize, nrfProjK, nrfMu, nrfThresh, baseDesc.width, baseDesc.height, new Vector2(1f / baseDesc.width,  0f), false);
        RecordBlur(rg, "Water SSF Blur Y2", depthBlur2X, depthBlur2Y, nrfMaxSize, nrfProjK, nrfMu, nrfThresh, baseDesc.width, baseDesc.height, new Vector2(0f, 1f / baseDesc.height), false);
        RecordBlur(rg, "Water SSF Blur X3", depthBlur2Y, depthBlur3X, nrfMaxSize, nrfProjK, nrfMu, nrfThresh, baseDesc.width, baseDesc.height, new Vector2(1f / baseDesc.width,  0f), false);
        RecordBlur(rg, "Water SSF Blur Y3", depthBlur3X, depthBlur3Y, nrfMaxSize, nrfProjK, nrfMu, nrfThresh, baseDesc.width, baseDesc.height, new Vector2(0f, 1f / baseDesc.height), true);

        // Step 5: Reconstruct surface normals from the smooth depth.
        // We sample neighboring depth pixels and compute the cross product to get a normal vector.
        RecordNormals(rg, depthBlur3Y, thicknessSmooth, normalsTex, baseDesc.width, baseDesc.height);

        // Step 6: Blur the normals to soften any hard edges that came out of normal reconstruction.
        // We use the smooth depth as a guide so we don't blur across water/background boundaries.
        RecordNormalsBlur(rg, "Water SSF Normals Blur X", normalsTex,   normalsBlurA, depthBlur3Y, baseDesc.width, baseDesc.height, new Vector2(1f / baseDesc.width,  0f), false);
        RecordNormalsBlur(rg, "Water SSF Normals Blur Y", normalsBlurA, normalsSmooth, depthBlur3Y, baseDesc.width, baseDesc.height, new Vector2(0f, 1f / baseDesc.height), true);

        // Step 7: Copy the scene color before we draw on top of it.
        // The composite pass reads this to sample refracted background pixels behind the water.
        RecordSceneCopy(rg, resourceData.activeColorTexture, sceneCopy);

        // Step 8: Draw the final water surface onto the active color buffer.
        // This blends depth, normals, thickness, and the scene copy together to produce the shaded fluid.
        RecordComposite(rg, depthBlur3Y, normalsSmooth, thicknessSmooth, sceneCopy,
            resourceData.activeColorTexture, resourceData.activeDepthTexture);
    }

    // Holds the data passed into the particle draw render func.
    // RenderGraph requires all pass data to live in a sealed class like this.
    private sealed class ParticleDrawData
    {
        public Material material;
        public Action<RasterCommandBuffer, Material> draw;
        public bool clearColor;
        public bool clearDepth;
    }

    // rg               the current frame's RenderGraph instance, just pass it straight through
    // name             a label that shows up in the RenderGraph debugger, keep it descriptive so you can find this pass easily
    // passIndex        which sub-shader pass inside the material to use (PASS_DEPTH, PASS_THICKNESS, etc.)
    // colorTarget      the render texture this pass will write its output into
    // depthTarget      a hardware depth buffer for GPU occlusion testing, pass TextureHandle.nullHandle if you don't need occlusion (e.g. thickness)
    // clearColor       whether to wipe the colorTarget to black before drawing, set true for the first pass that writes to a fresh texture
    // clearDepth       whether to reset the depth buffer before drawing, only meaningful when depthTarget is valid
    // drawCallback     the function that actually issues the particle draw calls, this is what OnDrawDepth or OnDrawThickness point to
    // exposeColorAsGlobalID  optional shader property ID, if provided the colorTarget becomes readable as a global texture by all later passes, pass -1 to skip
    private void RecordParticleDraw(
        RenderGraph rg, string name, int passIndex,
        TextureHandle colorTarget, TextureHandle depthTarget,
        bool clearColor, bool clearDepth,
        Action<RasterCommandBuffer, Material> drawCallback,
        int exposeColorAsGlobalID = -1)
    {
        if (drawCallback == null) return;

        // AddRasterRenderPass registers a new GPU pass with RenderGraph. The graph just records the intent
        // here, it does not execute anything yet. Actual GPU commands run later when the graph is compiled and played back.
        // The generic type ParticleDrawData is a plain data container we fill below and RenderGraph
        // hands back to us inside SetRenderFunc at execution time. This is how we pass CPU data into the GPU lambda.
        using (var builder = rg.AddRasterRenderPass<ParticleDrawData>(name, out var data))
        {
            // Fill the data bag. Everything the render func needs must live here because the lambda
            // captures this struct, not the outer method scope. Capturing outer variables directly
            // is not allowed by RenderGraph since the method returns before the GPU actually runs.
            data.material   = Material;
            data.draw       = drawCallback;
            data.clearColor = clearColor;
            data.clearDepth = clearDepth;

            // Tell RenderGraph which texture this pass renders into. The 0 is the MRT slot index.
            // AccessFlags.Write means we own this texture for the duration of this pass.
            // RenderGraph uses this declaration to build its dependency graph and ensure
            // previous passes that wrote to this texture are complete before we start.
            builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);

            // Only bind a hardware depth buffer if the caller provided one.
            // The depth buffer enables GPU occlusion so front-facing fragments win over rear ones.
            // For thickness we skip this entirely because we want all particles to accumulate additively.
            if (depthTarget.IsValid())
                builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.Write);

            // SetGlobalTextureAfterPass promotes our private render texture into a globally accessible
            // shader property the moment this pass finishes. Any later pass or shader can then sample
            // it by name without us having to manually bind it. This is the main propagation mechanism.
            // Output travels: colorTarget texture -> bound to exposeColorAsGlobalID shader property -> readable by all subsequent passes.
            if (exposeColorAsGlobalID >= 0)
                builder.SetGlobalTextureAfterPass(colorTarget, exposeColorAsGlobalID);

            // AllowPassCulling(false) forces RenderGraph to keep this pass even if it cannot prove
            // that anything downstream reads the output. Without this, the graph optimizer might
            // remove the particle draw entirely thinking it is dead work.
            builder.AllowPassCulling(false);

            // AllowGlobalStateModification(true) is required whenever the render func calls SetGlobalTexture
            // or SetGlobalInt, since those affect state outside the pass boundary. RenderGraph needs to know
            // this so it does not reorder or merge the pass in a way that breaks global state.
            builder.AllowGlobalStateModification(true);

            // SetRenderFunc is the only place real GPU commands go. Everything above was just declarations.
            // By the time this lambda runs, the GPU render target is already switched to colorTarget
            // so whatever the drawCallback draws lands exactly there.
            builder.SetRenderFunc((ParticleDrawData d, RasterGraphContext ctx) =>
            {
                // Reset the override matrix flag so particles use the normal camera view/projection.
                // This matters if a previous frame or pass left this flag set to 1.
                ctx.cmd.SetGlobalInt(ID_SSFUseOverrideMatrices, 0);

                // Clear before drawing so leftover pixels from the previous frame do not contaminate this one.
                if (d.clearColor || d.clearDepth)
                    ctx.cmd.ClearRenderTarget(d.clearDepth, d.clearColor, Color.clear);

                // Fire the callback. The particle renderer on the other end issues DrawProcedural calls
                // that write into whichever render target is currently bound, which is colorTarget.
                d.draw?.Invoke(ctx.cmd, d.material);
            });
        }
    }

    // Data bag for the depth NRF blur pass.
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

    // Records a single separable NRF blur pass in one direction (X or Y).
    // Call this twice per full blur, once horizontal then once vertical, to get a 2D smooth.
    private void RecordBlur(RenderGraph rg, string passName, TextureHandle src, TextureHandle dst,
        float maxSize, float projK, float mu, float thresh, int w, int h,
        Vector2 blurDirection, bool exposeAsSmooth)
    {
        // Each blur call is one directional pass. Horizontal (1,0) reads src and writes to dst.
        // The next call then reads that dst as its src and blurs vertically (0,1).
        // Together they form one complete 2D blur. We chain six of these to get three full passes.
        using (var builder = rg.AddRasterRenderPass<BlurData>(passName, out var data))
        {
            data.source            = src;
            data.material          = Material;
            data.nrfMaxFilterSize  = maxSize;
            data.nrfProjK          = projK;
            data.nrfMu             = mu;
            data.nrfDepthThreshold = thresh;
            // Pack texel size as Vector4 (1/w, 1/h, w, h) so the shader can do both pixel-size math and resolution math with one value.
            data.texelSize         = new Vector4(1f / w, 1f / h, w, h);
            data.blurDirection     = blurDirection;

            // UseTexture tells RenderGraph this pass reads src but does not render into it.
            // This is how RenderGraph knows to schedule this pass after whoever last wrote src.
            builder.UseTexture(src, AccessFlags.Read);

            // dst is our output. RenderGraph will allocate or reuse memory for it and ensure
            // no other pass is writing to it at the same time.
            builder.SetRenderAttachment(dst, 0, AccessFlags.Write);

            // Only the very last blur pass in the chain sets this flag.
            // That promotes the final smooth depth result into a global shader property
            // so the normals pass and composite can find it without any extra binding.
            // Output flow: dst texture -> _WaterSSFDepthSmooth global property -> available to all subsequent passes.
            if (exposeAsSmooth)
                builder.SetGlobalTextureAfterPass(dst, ID_WaterSSFDepthSmooth);

            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((BlurData d, RasterGraphContext ctx) =>
            {
                // Push all NRF parameters and the source texture as globals so the blur shader can read them.
                // We cannot bind src as an attachment here because it is read-only input, not a render target.
                // SetGlobalTexture is the way to hand a non-attachment texture to the shader.
                ctx.cmd.SetGlobalTexture(ID_WaterSSFDepthSource,    d.source);
                ctx.cmd.SetGlobalVector (ID_WaterSSFDepthTexelSize, d.texelSize);
                ctx.cmd.SetGlobalVector (ID_WaterSSFBlurDirection,  d.blurDirection);
                ctx.cmd.SetGlobalFloat  (ID_NRF_MaxFilterSize,      d.nrfMaxFilterSize);
                ctx.cmd.SetGlobalFloat  (ID_NRF_ProjectedParticleK, d.nrfProjK);
                ctx.cmd.SetGlobalFloat  (ID_NRF_Mu,                 d.nrfMu);
                ctx.cmd.SetGlobalFloat  (ID_NRF_DepthThreshold,     d.nrfDepthThreshold);

                // BlitTexture renders a fullscreen quad using PASS_BLUR.
                // The shader samples src using the blur direction and NRF weights,
                // and writes the smoothed result into dst which is already bound as the render target.
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, PASS_BLUR);
            });
        }
    }

    // Data bag for the thickness blur pass.
    private sealed class ThicknessBlurData
    {
        public TextureHandle source;
        public Material      material;
        public Vector4       texelSize;
        public Vector2       blurDirection;
    }

    // Records a separable Gaussian blur on the thickness buffer.
    // Thickness gets a simpler blur than depth because small inaccuracies there are less noticeable visually.
    private void RecordThicknessBlur(RenderGraph rg, string passName, TextureHandle src, TextureHandle dst,
        int w, int h, Vector2 blurDirection, bool exposeAsThickness)
    {
        // Same separable pattern as depth blur but using a simpler Gaussian kernel (PASS_THICKNESS_BLUR).
        // Thickness does not need the NRF depth-awareness because we are just softening the accumulation,
        // not trying to preserve sharp water edges. A regular Gaussian is cheaper and good enough here.
        using (var builder = rg.AddRasterRenderPass<ThicknessBlurData>(passName, out var data))
        {
            data.source        = src;
            data.material      = Material;
            data.texelSize     = new Vector4(1f / w, 1f / h, w, h);
            data.blurDirection = blurDirection;

            // Declare the read dependency so RenderGraph schedules this after whoever produced src.
            builder.UseTexture(src, AccessFlags.Read);
            builder.SetRenderAttachment(dst, 0, AccessFlags.Write);

            // On the second (Y) pass, exposeAsThickness is true.
            // At that point dst holds the fully blurred thickness and we promote it to a global
            // so the composite shader can sample _WaterSSFThickness without any extra binding step.
            // Output flow: dst texture -> _WaterSSFThickness global property -> composite reads it.
            if (exposeAsThickness)
                builder.SetGlobalTextureAfterPass(dst, ID_WaterSSFThickness);

            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((ThicknessBlurData d, RasterGraphContext ctx) =>
            {
                // Bind the source as a global because it is an input texture, not a render target.
                // The blur shader reads it through _WaterSSFThicknessSource.
                ctx.cmd.SetGlobalTexture(ID_WaterSSFThicknessSource, d.source);
                ctx.cmd.SetGlobalVector (ID_WaterSSFDepthTexelSize,  d.texelSize);
                ctx.cmd.SetGlobalVector (ID_WaterSSFBlurDirection,   d.blurDirection);

                // Fullscreen blit into dst which is already the active render target at this point.
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, PASS_THICKNESS_BLUR);
            });
        }
    }

    // Data bag for the normal reconstruction pass.
    private sealed class NormalsData
    {
        public TextureHandle smoothDepth;
        public Material      material;
        public Vector4       texelSize;
    }

    // Derives screen space normals from the smooth depth texture by sampling neighboring pixels.
    // The thickness texture is bound globally so the normals shader can optionally use it for masking.
    private void RecordNormals(RenderGraph rg, TextureHandle smoothDepth, TextureHandle thickness, TextureHandle normalsOut, int w, int h)
    {
        // Normal reconstruction works by sampling the smooth depth at neighboring pixels,
        // reconstructing 3D view-space positions from those depths, then taking the cross product
        // to get a surface normal. This is why we need the smooth depth and the texel size:
        // we need to know how far apart those neighbor samples are in world space.
        using (var builder = rg.AddRasterRenderPass<NormalsData>("Water SSF Normals", out var data))
        {
            data.smoothDepth = smoothDepth;
            data.material    = Material;
            data.texelSize   = new Vector4(1f / w, 1f / h, w, h);

            // Declare the smooth depth as a read dependency. RenderGraph will ensure all three
            // depth blur passes have finished before this normals pass begins.
            builder.UseTexture(smoothDepth, AccessFlags.Read);

            // normalsOut is a fresh RG texture. This pass is the one that first writes to it.
            builder.SetRenderAttachment(normalsOut, 0, AccessFlags.Write);

            // Immediately promote the output to a global property so the normals blur
            // and composite passes can read it. The blur will then overwrite this binding
            // with the smoothed version at the end of its own chain.
            // Output flow: normalsOut texture -> _WaterSSFNormals global property -> normals blur reads it.
            builder.SetGlobalTextureAfterPass(normalsOut, ID_WaterSSFNormals);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((NormalsData d, RasterGraphContext ctx) =>
            {
                // Bind the smooth depth as a global so the PASS_NORMALS shader can read it.
                // The texel size tells the shader how far to step when sampling neighbors.
                ctx.cmd.SetGlobalTexture(ID_WaterSSFDepthSmooth, d.smoothDepth);
                ctx.cmd.SetGlobalVector(ID_WaterSSFDepthTexelSize, d.texelSize);

                // Blit runs PASS_NORMALS which samples the smooth depth, reconstructs positions,
                // computes the cross product, and writes the resulting XY normal into normalsOut.
                Blitter.BlitTexture(ctx.cmd, d.smoothDepth, new Vector4(1, 1, 0, 0), d.material, PASS_NORMALS);
            });
        }
    }

    // Just holds the source texture for the scene copy blit.
    private sealed class SceneCopyData { public TextureHandle source; }

    // Captures the scene color right before we draw on top of it.
    // This snapshot is what the water uses as a background for refraction sampling.
    private void RecordSceneCopy(RenderGraph rg, TextureHandle activeColor, TextureHandle sceneCopy)
    {
        // We cannot read and write the active color texture at the same time in the composite pass.
        // So we take a snapshot of it here first, then the composite reads this copy as the refraction background.
        // This also means the water is composited over the fully rendered opaque scene, which is correct.
        using (var builder = rg.AddRasterRenderPass<SceneCopyData>("Water SSF Scene Copy", out var data))
        {
            data.source = activeColor;

            // Read from the live scene color. RenderGraph ensures all opaque and skybox passes
            // that write to activeColor have completed before this pass runs.
            builder.UseTexture(activeColor, AccessFlags.Read);

            // Write into our own sceneCopy texture, leaving activeColor untouched for the composite to write into later.
            builder.SetRenderAttachment(sceneCopy, 0, AccessFlags.Write);

            // Promote the copy to a global so the composite shader can sample _WaterSSFSceneCopy
            // for refraction without needing an explicit binding.
            // Output flow: sceneCopy texture -> _WaterSSFSceneCopy global property -> composite reads it.
            builder.SetGlobalTextureAfterPass(sceneCopy, ID_WaterSSFSceneCopy);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((SceneCopyData d, RasterGraphContext ctx) =>
            {
                // A simple fullscreen blit with pass index 0 and no material, just a straight pixel copy.
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), 0, false);
            });
        }
    }

    // Debug helper to blit any intermediate texture directly to the screen.
    // Useful when you want to visually inspect depth, normals, or thickness mid-pipeline.
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

    // Data bag for the composite pass.
    private sealed class CompositeData
    {
        public TextureHandle smoothDepth;
        public Material      material;
    }

    // The final pass. Takes everything we computed and draws the water surface onto the live scene.
    // It writes back to the active color and also reads/writes the depth buffer so the water
    // correctly occludes geometry behind it.
    private void RecordComposite(RenderGraph rg,
        TextureHandle smoothDepth, TextureHandle normals, TextureHandle thickness, TextureHandle sceneCopy,
        TextureHandle activeColor, TextureHandle activeDepth)
    {
        // This is the terminal pass in the pipeline. Everything computed so far feeds into it.
        // It reads: smooth depth (for water surface position), normals (for lighting and reflections),
        // thickness (for opacity and color tint), scene copy (for refraction background).
        // It writes: directly onto the active scene color so the result is what the camera sees.
        using (var builder = rg.AddRasterRenderPass<CompositeData>("Water SSF Composite", out var data))
        {
            data.smoothDepth = smoothDepth;
            data.material    = Material;

            // Declare every input texture so RenderGraph knows this pass must run after all the blur,
            // normals, and scene copy passes. If we skip any UseTexture call here,
            // RenderGraph might run this too early before that data is ready.
            // Note: these textures are already bound as globals via SetGlobalTextureAfterPass earlier,
            // but we still declare them here so RenderGraph can order the passes correctly.
            builder.UseTexture(smoothDepth, AccessFlags.Read);
            builder.UseTexture(normals,     AccessFlags.Read);
            builder.UseTexture(thickness,   AccessFlags.Read);
            builder.UseTexture(sceneCopy,   AccessFlags.Read);

            // Write directly into the camera's active color buffer. This is the final output.
            // No SetGlobalTextureAfterPass here because nothing comes after this in our pipeline.
            builder.SetRenderAttachment(activeColor, 0, AccessFlags.Write);

            // ReadWrite on depth allows the shader to do depth testing (so water behind geometry is hidden)
            // and also write the water surface depth into the buffer so later Unity passes
            // such as post-processing know where the water surface is.
            builder.SetRenderAttachmentDepth(activeDepth, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((CompositeData d, RasterGraphContext ctx) =>
            {
                // PASS_COMPOSITE in the shader reads all the global textures set by previous passes
                // and blends refraction, reflection, depth-based tint, and Fresnel into the final color.
                // The smooth depth is passed as the blit source so the shader can reconstruct
                // the water surface position in clip space and write depth correctly.
                Blitter.BlitTexture(ctx.cmd, d.smoothDepth, new Vector4(1, 1, 0, 0), d.material, PASS_COMPOSITE);
            });
        }
    }

    // Data bag for the normals blur pass. Needs both the normals source and smooth depth
    // so the blur can use depth as a guide to avoid bleeding across the water edge.
    private sealed class NormalsBlurData
    {
        public TextureHandle source;
        public TextureHandle smoothDepth;
        public Material      material;
        public Vector4       texelSize;
        public Vector2       blurDirection;
    }

    // Separable blur on the normals buffer, guided by the smooth depth.
    // Without the depth guide, normals at the water silhouette would bleed into background pixels
    // and create an ugly halo. The depth comparison in the shader rejects those samples.
    private void RecordNormalsBlur(RenderGraph rg, string passName,
        TextureHandle src, TextureHandle dst, TextureHandle smoothDepth,
        int w, int h, Vector2 blurDirection, bool exposeAsNormals)
    {
        // This follows the same separable two-pass structure as depth blur.
        // The key difference is that the blur shader also receives the smooth depth so it can reject
        // neighbors whose depth differs too much. Without this guard, a normal sample from a water pixel
        // near the silhouette would pull in background normals and smear them onto the water surface.
        using (var builder = rg.AddRasterRenderPass<NormalsBlurData>(passName, out var data))
        {
            data.source        = src;
            data.smoothDepth   = smoothDepth;
            data.material      = Material;
            data.texelSize     = new Vector4(1f / w, 1f / h, w, h);
            data.blurDirection = blurDirection;

            // Declare both input textures so RenderGraph can order this pass after
            // both the normals reconstruction pass and the final depth blur pass.
            builder.UseTexture(src,         AccessFlags.Read);
            builder.UseTexture(smoothDepth, AccessFlags.Read);
            builder.SetRenderAttachment(dst, 0, AccessFlags.Write);

            // On the Y pass exposeAsNormals is true. At that point dst contains the fully smoothed normals.
            // Binding it to _WaterSSFNormals overwrites the earlier raw normals binding from RecordNormals,
            // so the composite shader automatically picks up the polished version.
            // Output flow: dst texture -> _WaterSSFNormals global property -> composite reads it.
            if (exposeAsNormals)
                builder.SetGlobalTextureAfterPass(dst, ID_WaterSSFNormals);

            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((NormalsBlurData d, RasterGraphContext ctx) =>
            {
                // Bind both the normals source and the smooth depth as globals.
                // The blur shader uses the depth to compare each neighbor against the center pixel.
                // If the depth difference is too large the neighbor is skipped, keeping the edge crisp.
                ctx.cmd.SetGlobalTexture(ID_WaterSSFNormalsSource,  d.source);
                ctx.cmd.SetGlobalTexture(ID_WaterSSFDepthSmooth,    d.smoothDepth);
                ctx.cmd.SetGlobalVector (ID_WaterSSFDepthTexelSize, d.texelSize);
                ctx.cmd.SetGlobalVector (ID_WaterSSFBlurDirection,  d.blurDirection);

                // PASS_NORMALS_BLUR samples src in one direction, weights samples by depth similarity,
                // and writes the softened normal into dst.
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, PASS_NORMALS_BLUR);
            });
        }
    }
}
