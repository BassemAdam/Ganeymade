// ============================================================
// Water SSF — 2D Bilateral Blur pass (Pass 4)
//
// Single-pass 2D bilateral Gaussian operating on raw eye-depth
// (metres). Blur parameters are set on the material at
// draw time so inspector changes take effect without recompile.
//
// BlurRadius   : kernel half-extent in pixels  (clamped 1..8)
// BlurSigma    : spatial Gaussian standard deviation (pixels)
// BlurDepthSigma : relative range Gaussian sigma. The shader converts
//                  it to a per-pixel eye-depth sigma using:
//                  sigmaZ = max(centerDepth * BlurDepthSigma, 1e-4)
// ============================================================
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public static class WaterSSFBlur2DPass
{
    private const int PASS_INDEX = 4; // ScreenSpaceFluidBlur2D

    private static readonly int ID_BlurRadius          = Shader.PropertyToID("_BlurRadius");
    private static readonly int ID_BlurSigma           = Shader.PropertyToID("_BlurSigma");
    private static readonly int ID_BlurDepthSigma      = Shader.PropertyToID("_BlurDepthSigma");
    private static readonly int ID_WaterSSFInput       = Shader.PropertyToID("_WaterSSFInput");
    private static readonly int ID_WaterSSFInputTexelSize = Shader.PropertyToID("_WaterSSFInputTexelSize");

    private sealed class Data
    {
        public TextureHandle source;
        public Material      material;
        public int           blurRadius;
        public float         blurSigma;
        public float         blurDepthSigma;
        public Vector4       texelSize;     // (1/w, 1/h, w, h)
    }

    // -----------------------------------------------------------------
    public static void Record(
        RenderGraph   rg,
        Material      mat,
        TextureHandle depthRaw,
        TextureHandle depthSmooth,
        int   blurRadius,
        float blurSigma,
        float blurDepthSigma,
        int   texWidth,
        int   texHeight)
    {
        using (var builder = rg.AddRasterRenderPass<Data>("Water SSF Blur 2D", out var data))
        {
            data.source         = depthRaw;
            data.material       = mat;
            data.blurRadius     = Mathf.Clamp(blurRadius, 1, 8);
            data.blurSigma      = Mathf.Clamp(blurSigma, 0.1f, 16f);
            data.blurDepthSigma = Mathf.Clamp(blurDepthSigma, 0.001f, 1f);
            data.texelSize      = new Vector4(1f / texWidth, 1f / texHeight, texWidth, texHeight);

            builder.UseTexture(depthRaw, AccessFlags.Read);
            builder.SetRenderAttachment(depthSmooth, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((Data d, RasterGraphContext ctx) =>
            {
                // Bind source texture explicitly under a plain TEXTURE2D name.
                // _BlitTexture is TEXTURE2D_X (stereo array) and its TexelSize is never
                // set by RenderGraph — both cause the blur kernel to read the centre pixel
                // for every offset, producing output identical to input.
                ctx.cmd.SetGlobalTexture(ID_WaterSSFInput, d.source);
                ctx.cmd.SetGlobalVector(ID_WaterSSFInputTexelSize, d.texelSize);
                // Set params at draw time so inspector changes take effect without recompile
                d.material.SetFloat(ID_BlurRadius,     d.blurRadius);
                d.material.SetFloat(ID_BlurSigma,      d.blurSigma);
                d.material.SetFloat(ID_BlurDepthSigma, d.blurDepthSigma);
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, PASS_INDEX);
            });
        }
    }
}
