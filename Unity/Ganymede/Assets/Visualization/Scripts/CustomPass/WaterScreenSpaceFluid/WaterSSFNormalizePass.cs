// ============================================================
// Water SSF — Normalize Depth pass (Pass 3)
//
// Maps raw eye-depth (metres) → normalised [0..1] using the
// [minDepth, maxDepth] stored in _WaterSSFDepthRange (1×1 RGHalf).
//
// Empty pixels (raw < 1e-5) are written as exactly 0.0 so the
// blur and normals passes can identify them reliably.
// ============================================================
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public static class WaterSSFNormalizePass
{
    private const int PASS_INDEX = 3; // ScreenSpaceFluidNormalizeDepth

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
        TextureHandle depthRange, // declared for dependency tracking only
        TextureHandle depthNorm)
    {
        using (var builder = rg.AddRasterRenderPass<Data>("Water SSF Normalize Depth", out var data))
        {
            data.source   = depthRaw;
            data.material = mat;

            builder.UseTexture(depthRaw,   AccessFlags.Read);
            builder.UseTexture(depthRange, AccessFlags.Read); // _WaterSSFDepthRange bound globally
            builder.SetRenderAttachment(depthNorm, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((Data d, RasterGraphContext ctx) =>
            {
                ctx.cmd.SetGlobalTexture(ID_WaterSSFInput, d.source);
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, PASS_INDEX);
            });
        }
    }
}
