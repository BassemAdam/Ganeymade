// ============================================================
// Water Screen-Space Fluid — URP Renderer Feature (orchestrator)
//
// Active shader pass order:
//   0 Depth            (_WaterSSFDepthRaw + depth buffer)
//   1 Thickness        (_WaterSSFThickness)
//   4 Blur2D           (_WaterSSFDepthBlurA)
//   4 Blur2D           (_WaterSSFDepthSmooth)
//   5 Normals          (_WaterSSFNormals)
//   6 Composite        (to active color)
//
// Passes 2/3 remain in the shader as legacy helpers but are intentionally
// skipped here. Per-frame depth range normalization caused temporal instability
// (twitching / disappearing regions) for small moving fluid silhouettes.
// ============================================================
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public sealed class WaterScreenSpaceFluidFeature : ScriptableRendererFeature
{
    private static readonly int ID_BlurRadius     = Shader.PropertyToID("_BlurRadius");
    private static readonly int ID_BlurSigma      = Shader.PropertyToID("_BlurSigma");
    private static readonly int ID_BlurDepthSigma = Shader.PropertyToID("_BlurDepthSigma");

    // ---- Events raised by WaterPhaseScreenSpaceFluidRenderer ----
    public static event Action<RasterCommandBuffer, Material> OnDrawDepth;
    public static event Action<RasterCommandBuffer, Material> OnDrawThickness;

    // ---- State written each frame by WaterPhaseScreenSpaceFluidRenderer ----
    public static bool     IsActive;
    public static Material ActiveMaterial;

    private SSFRenderPass _pass;

    public override void Create()
    {
        _pass = new SSFRenderPass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingSkybox
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null || !IsActive || ActiveMaterial == null) return;

        var camType = renderingData.cameraData.cameraType;
        if (camType != CameraType.Game && camType != CameraType.SceneView) return;

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing) { }

    private sealed class SSFRenderPass : ScriptableRenderPass
    {
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!IsActive || ActiveMaterial == null) return;

            var cameraData   = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();

            // Events cannot be invoked outside this declaring class;
            // snapshot them here and pass delegates down into helper classes.
            var drawDepth     = OnDrawDepth;
            var drawThickness = OnDrawThickness;
            int   blurRadius     = Mathf.Clamp(Mathf.RoundToInt(ActiveMaterial.GetFloat(ID_BlurRadius)), 1, 8);
            float blurSigma      = Mathf.Clamp(ActiveMaterial.GetFloat(ID_BlurSigma), 0.1f, 16f);
            float blurDepthSigma = Mathf.Clamp(ActiveMaterial.GetFloat(ID_BlurDepthSigma), 0.001f, 1f);

            RenderTextureDescriptor baseDesc = cameraData.cameraTargetDescriptor;
            baseDesc.depthBufferBits = 0;
            baseDesc.msaaSamples     = 1;

            RenderTextureDescriptor depthDesc = baseDesc;
            depthDesc.colorFormat = RenderTextureFormat.RFloat;

            RenderTextureDescriptor thicknessDesc = baseDesc;
            thicknessDesc.colorFormat = RenderTextureFormat.RHalf;

            RenderTextureDescriptor normDesc = baseDesc;
            normDesc.colorFormat = RenderTextureFormat.ARGBHalf;

            RenderTextureDescriptor colorDesc = baseDesc;

            RenderTextureDescriptor depthOnlyDesc = baseDesc;
            depthOnlyDesc.colorFormat     = RenderTextureFormat.Depth;
            depthOnlyDesc.depthBufferBits = 24;

            // ---- Allocate graph textures ---------------------------------
            TextureHandle depthRaw    = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDesc,     "_WaterSSFDepthRaw",    false);
            TextureHandle depthBuffer = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthOnlyDesc, "_WaterSSFDepthBuffer", false);
            TextureHandle thickness   = UniversalRenderer.CreateRenderGraphTexture(renderGraph, thicknessDesc, "_WaterSSFThickness",   false);
            TextureHandle depthBlurA  = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDesc,     "_WaterSSFDepthBlurA",  false);
            TextureHandle depthSmooth = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDesc,     "_WaterSSFDepthSmooth", false);
            TextureHandle normals     = UniversalRenderer.CreateRenderGraphTexture(renderGraph, normDesc,      "_WaterSSFNormals",     false);
            TextureHandle sceneCopy   = UniversalRenderer.CreateRenderGraphTexture(renderGraph, colorDesc,     "_WaterSSFSceneCopy",   false);

            // ---- Record each stage ---------------------------------------
            WaterSSFDepthPass.RecordDepth(renderGraph, ActiveMaterial, depthRaw, depthBuffer, drawDepth);
            WaterSSFDepthPass.RecordThickness(renderGraph, ActiveMaterial, thickness, drawThickness);

            WaterSSFBlur2DPass.Record(
                renderGraph,
                ActiveMaterial,
                depthRaw,
                depthBlurA,
                blurRadius,
                blurSigma,
                blurDepthSigma,
                baseDesc.width,
                baseDesc.height);

            WaterSSFBlur2DPass.Record(
                renderGraph,
                ActiveMaterial,
                depthBlurA,
                depthSmooth,
                blurRadius,
                blurSigma,
                blurDepthSigma,
                baseDesc.width,
                baseDesc.height);

            WaterSSFNormalsPass.Record(
                renderGraph,
                ActiveMaterial,
                depthSmooth,
                thickness,
                normals,
                baseDesc.width,
                baseDesc.height);

            WaterSSFCompositePass.Record(
                renderGraph,
                ActiveMaterial,
                depthSmooth,
                normals,
                thickness,
                resourceData.activeColorTexture,
                sceneCopy,
                resourceData.activeDepthTexture);
        }
    }
}
