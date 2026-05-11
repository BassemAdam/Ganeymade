// ============================================================
// Water SSF — Depth & Thickness particle-draw passes
//
// Pass 0 ScreenSpaceFluidDepth     : sphere impostors → RHalf eye-depth
//                                    + hardware Z for correct occlusion
// Pass 1 ScreenSpaceFluidThickness : sphere impostors → RHalf chord (additive)
// ============================================================
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public static class WaterSSFDepthPass
{
    private sealed class Data
    {
        public Material                              material;
        public Action<RasterCommandBuffer, Material> onDraw;
    }

    // -----------------------------------------------------------------
    // Pass 0 — depth impostor with hardware Z buffer
    // -----------------------------------------------------------------
    public static void RecordDepth(
        RenderGraph   rg,
        Material      mat,
        TextureHandle depthRaw,
        TextureHandle depthBuffer,
        Action<RasterCommandBuffer, Material> onDraw)
    {
        using (var builder = rg.AddRasterRenderPass<Data>("Water SSF Depth", out var data))
        {
            data.material = mat;
            data.onDraw   = onDraw;

            builder.SetRenderAttachment(depthRaw, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depthBuffer, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((Data d, RasterGraphContext ctx) =>
            {
                ctx.cmd.ClearRenderTarget(true, true, Color.clear);
                d.onDraw?.Invoke(ctx.cmd, d.material);
            });
        }
    }

    // -----------------------------------------------------------------
    // Pass 1 — additive thickness accumulation (no depth buffer needed)
    // -----------------------------------------------------------------
    public static void RecordThickness(
        RenderGraph   rg,
        Material      mat,
        TextureHandle thickness,
        Action<RasterCommandBuffer, Material> onDraw)
    {
        using (var builder = rg.AddRasterRenderPass<Data>("Water SSF Thickness", out var data))
        {
            data.material = mat;
            data.onDraw   = onDraw;

            builder.SetRenderAttachment(thickness, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((Data d, RasterGraphContext ctx) =>
            {
                ctx.cmd.ClearRenderTarget(false, true, Color.clear);
                d.onDraw?.Invoke(ctx.cmd, d.material);
            });
        }
    }
}
