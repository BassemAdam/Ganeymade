using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// URP Renderer Feature that composites voxel ray-march results on top of the
/// camera colour buffer.  Add this to the active URP Renderer asset.
/// Works with <see cref="VoxelTracerCamera"/> (MonoBehaviour on the camera).
/// </summary>
public class VoxelCompositeFeature : ScriptableRendererFeature
{
    VoxelCompositePass _pass;

    public override void Create()
    {
        _pass = new VoxelCompositePass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null) return;

        if (renderingData.cameraData.cameraType != CameraType.Game &&
            renderingData.cameraData.cameraType != CameraType.SceneView)
            return;

        var vtc = renderingData.cameraData.camera.GetComponent<VoxelTracerCamera>();
        if (vtc == null || !vtc.IsReadyToRender) return;

        _pass.Setup(vtc);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        _pass?.Dispose();
    }

    // =========================================================================
    // Render Pass
    // =========================================================================
    sealed class VoxelCompositePass : ScriptableRenderPass
    {
        VoxelTracerCamera _vtc;
        static readonly int _VoxTexId = Shader.PropertyToID("_VoxTex");

        public void Setup(VoxelTracerCamera vtc) => _vtc = vtc;

        // -- Pass data structs --------------------------------------------------
        class ComputePassData
        {
            public VoxelTracerCamera vtc;
            public int width;
            public int height;
        }

        class BlitPassData
        {
            public TextureHandle source;
            public Material material;
        }

        // -- Render Graph recording ---------------------------------------------
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_vtc == null || !_vtc.IsReadyToRender) return;

            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();

            int w = cameraData.cameraTargetDescriptor.width;
            int h = cameraData.cameraTargetDescriptor.height;

            // Ensure voxel colour RT exists at the right size
            _vtc.EnsureColorRT(w, h);

            // ---- Step 1: Dispatch compute ray-march (unsafe - needs random write) ----
            using (var builder = renderGraph.AddUnsafePass<ComputePassData>(
                       "Voxel Ray March", out var computeData))
            {
                computeData.vtc = _vtc;
                computeData.width = w;
                computeData.height = h;

                builder.AllowPassCulling(false);

                builder.SetRenderFunc((ComputePassData data, UnsafeGraphContext ctx) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    data.vtc.DispatchRayMarch(cmd, data.width, data.height);
                });
            }

            // ---- Step 2: Copy scene colour to a temporary texture ----
            var desc = cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            TextureHandle sceneCopy = UniversalRenderer.CreateRenderGraphTexture(
                renderGraph, desc, "_VoxelSceneCopy", false);

            using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>(
                       "Voxel Copy Scene", out var copyData))
            {
                copyData.source = resourceData.activeColorTexture;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.Read);
                builder.SetRenderAttachment(sceneCopy, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((BlitPassData data, RasterGraphContext ctx) =>
                {
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1f, 1f, 0f, 0f), 0, false);
                });
            }

            // ---- Step 3: Composite blit - sceneCopy (via Blitter -> _BlitTexture) + _VoxTex -> camera colour ----
            _vtc.CompositeMaterial.SetTexture(_VoxTexId, _vtc.ColorRT);

            using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>(
                       "Voxel Composite Blit", out var blitData))
            {
                blitData.source = sceneCopy;
                blitData.material = _vtc.CompositeMaterial;

                builder.UseTexture(sceneCopy, AccessFlags.Read);
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((BlitPassData data, RasterGraphContext ctx) =>
                {
                    Blitter.BlitTexture(ctx.cmd, data.source,
                        new Vector4(1f, 1f, 0f, 0f), data.material, 0);
                });
            }
        }

        public void Dispose() { }
    }
}
