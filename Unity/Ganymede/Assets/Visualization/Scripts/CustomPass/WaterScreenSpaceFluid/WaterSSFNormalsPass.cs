// ============================================================
// Water SSF — Normals pass (Pass 5)
//
// Reconstructs view-space normals from the smoothed raw eye-depth
// texture via finite differences and exports them globally
// as _WaterSSFNormals (RGBAHalf, A=validity flag).
//
// Also exports _WaterSSFThickness globally so the Composite pass
// can read it without an explicit dependency link.
// ============================================================
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public static class WaterSSFNormalsPass
{
    private const int PASS_INDEX = 5; // ScreenSpaceFluidNormals

    private static readonly int ID_WaterSSFNormals        = Shader.PropertyToID("_WaterSSFNormals");
    private static readonly int ID_WaterSSFThickness      = Shader.PropertyToID("_WaterSSFThickness");
    private static readonly int ID_WaterSSFInput          = Shader.PropertyToID("_WaterSSFInput");
    private static readonly int ID_WaterSSFInputTexelSize = Shader.PropertyToID("_WaterSSFInputTexelSize");

    private sealed class Data
    {
        public TextureHandle source;
        public TextureHandle thickness;
        public Material      material;
        public Vector4       texelSize; // (1/w, 1/h, w, h)
    }

    // -----------------------------------------------------------------
    public static void Record(
        RenderGraph   rg,
        Material      mat,
        TextureHandle depthSmooth,
        TextureHandle thickness,   // re-exported globally from this pass
        TextureHandle normals,
        int texWidth,
        int texHeight)
    {
        using (var builder = rg.AddRasterRenderPass<Data>("Water SSF Normals", out var data))
        {
            data.source   = depthSmooth;
            data.thickness = thickness;
            data.material = mat;
            data.texelSize = new Vector4(1f / texWidth, 1f / texHeight, texWidth, texHeight);

            builder.UseTexture(depthSmooth, AccessFlags.Read);
            builder.UseTexture(thickness,   AccessFlags.Read);
            builder.SetRenderAttachment(normals, 0, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(normals,   ID_WaterSSFNormals);
            builder.SetGlobalTextureAfterPass(thickness, ID_WaterSSFThickness);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((Data d, RasterGraphContext ctx) =>
            {
                ctx.cmd.SetGlobalTexture(ID_WaterSSFInput, d.source);
                ctx.cmd.SetGlobalTexture(ID_WaterSSFThickness, d.thickness);
                ctx.cmd.SetGlobalVector(ID_WaterSSFInputTexelSize, d.texelSize);
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, PASS_INDEX);
            });
        }
    }
}
