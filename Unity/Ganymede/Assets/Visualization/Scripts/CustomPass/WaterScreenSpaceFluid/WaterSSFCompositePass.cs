// ============================================================
// Water SSF — Composite pass (Pass 6)
//
// Two internal sub-passes:
//   a) Scene copy  : plain blit of the current camera colour → _WaterSSFSceneCopy
//   b) Composite   : Fresnel + Beer-Lambert + Phong over the active colour target
//
// Reads _WaterSSFNormals, _WaterSSFThickness, _WaterSSFSceneCopy (all globally bound).
// Source blit texture = smoothed eye-depth (used as the reconstructed surface depth).
// ============================================================
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public static class WaterSSFCompositePass
{
    private const int PASS_INDEX = 6; // ScreenSpaceFluidComposite

    private static readonly int ID_WaterSSFSceneCopy = Shader.PropertyToID("_WaterSSFSceneCopy");
    private static readonly int ID_WaterSSFInput     = Shader.PropertyToID("_WaterSSFInput");

    // ----- Scene copy sub-pass -----
    private sealed class SceneCopyData
    {
        public TextureHandle source;
    }

    // ----- Composite sub-pass -----
    private sealed class CompositeData
    {
        public TextureHandle smoothDepth;
        public Material      material;
        public TextureHandle sceneDepth;
    }

    // -----------------------------------------------------------------
    public static void Record(
        RenderGraph   rg,
        Material      mat,
        TextureHandle depthSmooth,
        TextureHandle normals,     // dependency tracking (_WaterSSFNormals globally bound)
        TextureHandle thickness,   // dependency tracking (_WaterSSFThickness globally bound)
        TextureHandle activeColor,
        TextureHandle sceneCopy,
        TextureHandle sceneDepth)  // camera depth buffer — read for ZTest, written for depth write-back
    {
        // ---- Sub-pass A: copy scene to sceneCopy -------------------------
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

        // ---- Sub-pass B: composite fluid over scene -----------------------
        using (var builder = rg.AddRasterRenderPass<CompositeData>("Water SSF Composite", out var data))
        {
            data.smoothDepth = depthSmooth;
            data.material    = mat;
            data.sceneDepth  = sceneDepth;

            builder.UseTexture(depthSmooth, AccessFlags.Read);
            builder.UseTexture(normals,     AccessFlags.Read);
            builder.UseTexture(thickness,   AccessFlags.Read);
            builder.UseTexture(sceneCopy,   AccessFlags.Read);
            builder.SetRenderAttachment(activeColor, 0, AccessFlags.Write);
            // Bind the camera depth buffer as ReadWrite so the hardware depth test
            // runs (ZTest LEqual — water occluded by closer opaques) and the water
            // surface depth is written back (ZWrite On) for correct scene occlusion.
            builder.SetRenderAttachmentDepth(sceneDepth, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((CompositeData d, RasterGraphContext ctx) =>
            {
                // Bind smooth depth as _WaterSSFInput (plain TEXTURE2D) rather than
                // relying on Blitter setting _BlitTexture (TEXTURE2D_X / stereo type).
                ctx.cmd.SetGlobalTexture(ID_WaterSSFInput, d.smoothDepth);
                Blitter.BlitTexture(ctx.cmd, d.smoothDepth, new Vector4(1, 1, 0, 0), d.material, PASS_INDEX);
            });
        }
    }
}
