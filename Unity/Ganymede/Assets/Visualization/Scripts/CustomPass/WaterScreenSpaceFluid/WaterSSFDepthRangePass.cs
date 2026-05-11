// ============================================================
// Water SSF — Depth Range pass (Pass 2)
//
// Renders to a 1×1 RGHalf texture that holds [minDepth, maxDepth]
// found by sampling the raw depth in a sparse 16×16 grid.
//
// The result is exported globally as _WaterSSFDepthRange so the
// NormalizeDepth and Normals passes can un-normalise depth values.
// ============================================================
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public static class WaterSSFDepthRangePass
{
    private const int PASS_INDEX = 2; // ScreenSpaceFluidDepthRange

    private static readonly int ID_WaterSSFDepthRange =
        Shader.PropertyToID("_WaterSSFDepthRange");
    private static readonly int ID_WaterSSFInput =
        Shader.PropertyToID("_WaterSSFInput");

    private sealed class Data
    {
        public TextureHandle source;
        public Material      material;
    }

    // -----------------------------------------------------------------
    public static void Record(
        RenderGraph   rg,
        Material      mat,
        TextureHandle depthRaw,
        TextureHandle depthRange)
    {
        using (var builder = rg.AddRasterRenderPass<Data>("Water SSF Depth Range", out var data))
        {
            data.source   = depthRaw;
            data.material = mat;

            builder.UseTexture(depthRaw, AccessFlags.Read);
            builder.SetRenderAttachment(depthRange, 0, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(depthRange, ID_WaterSSFDepthRange);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((Data d, RasterGraphContext ctx) =>
            {
                // Bind source explicitly so the shader reads from _WaterSSFInput (plain TEXTURE2D),
                // not _BlitTexture (TEXTURE2D_X / stereo) which Blitter would set.
                ctx.cmd.SetGlobalTexture(ID_WaterSSFInput, d.source);
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, PASS_INDEX);
            });
        }
    }
}
