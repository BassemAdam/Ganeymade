using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class WaterThicknessFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        [Tooltip("The layer your water voxels are on.")]
        public LayerMask waterLayer;
        
        [Tooltip("The Hidden/WaterThicknessGen shader we created.")]
        public Shader thicknessShader;
    }

    public Settings settings = new Settings();
    private WaterThicknessPass thicknessPass;
    private Material thicknessMaterial;

    public override void Create()
    {
        if (settings.thicknessShader == null) return;
        
        // Create the material purely in memory
        if (thicknessMaterial == null)
        {
            thicknessMaterial = CoreUtils.CreateEngineMaterial(settings.thicknessShader);
        }

        thicknessPass = new WaterThicknessPass(settings.waterLayer, thicknessMaterial);
    }

    // call back runs every frame before renderer executes passes
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.thicknessShader == null || thicknessPass == null)
        {
            Debug.LogWarning("Water Thickness Feature: Missing Shader in settings!");
            return;
        }

        // Only run this pass for standard rendering, not in reflection probes or shadow passes
        if (renderingData.cameraData.cameraType == CameraType.Game || renderingData.cameraData.cameraType == CameraType.SceneView)
        {
            renderer.EnqueuePass(thicknessPass);
        }
    }

    protected override void Dispose(bool disposing)
    {
        thicknessPass?.Dispose();
        if (thicknessMaterial != null)
        {
            CoreUtils.Destroy(thicknessMaterial);
        }
    }

    // =========================================================================
    // THE RENDER PASS
    // =========================================================================
    class WaterThicknessPass : ScriptableRenderPass
    {
        private FilteringSettings filteringSettings;
        private Material thicknessMaterial;
        private List<ShaderTagId> shaderTagIdList = new List<ShaderTagId>();

        public WaterThicknessPass(LayerMask waterLayer, Material material)
        {
            // Filter strictly for the Water layer
            filteringSettings = new FilteringSettings(RenderQueueRange.all, waterLayer);
            thicknessMaterial = material;
            
            // Run exactly after the skybox/opaques, so SceneDepth is ready, but before Transparents
            this.renderPassEvent = RenderPassEvent.AfterRenderingSkybox; 

            // URP standard shader tags
            shaderTagIdList.Add(new ShaderTagId("UniversalForward"));
            shaderTagIdList.Add(new ShaderTagId("LightweightForward"));
            shaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
        }

        // Data needed inside the Render Function
        private class PassData
        {
            public RendererListHandle rendererList;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (thicknessMaterial == null) return;
            
            // Fetch Context
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            // 1. Describe the texture format we need
            RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
            desc.colorFormat = RenderTextureFormat.RHalf; 
            desc.depthBufferBits = 0; // We only need the red float channel

            // 2. Ask Render Graph to allocate this texture
            TextureHandle thicknessTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_WaterThicknessMap", false);

            // 3. Begin adding our custom pass
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Calculate Water Thickness", out var passData))
            {
                // Assign our new texture as the render target, and clear it to black
                builder.SetRenderAttachment(thicknessTexture, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);

                // Setup instructions for what to draw (our water voxels)
                // Construct DrawingSettings directly (most reliable in Unity 6 RenderGraph)
                var sortingSettings = new SortingSettings(cameraData.camera) { criteria = SortingCriteria.CommonOpaque };
                var drawingSettings = new DrawingSettings(shaderTagIdList[0], sortingSettings);
                for (int i = 1; i < shaderTagIdList.Count; i++)
                {
                    drawingSettings.SetShaderPassName(i, shaderTagIdList[i]);
                }
                drawingSettings.overrideMaterial = thicknessMaterial;
                drawingSettings.overrideMaterialPassIndex = thicknessMaterial.FindPass("WaterThicknessGen");

                // Make the renderer list based on our filter criteria
                var rendererListParams = new RendererListParams(renderingData.cullResults, drawingSettings, filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                builder.UseRendererList(passData.rendererList);

                // Publish this texture globally to shaders AFTER the pass finishes
                builder.SetGlobalTextureAfterPass(thicknessTexture, Shader.PropertyToID("_WaterThicknessMap"));

                // 4. The actual rendering execution code
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    // Clear the target to blank/zero internally first
                    context.cmd.ClearRenderTarget(false, true, Color.clear);
                    
                    // Draw all the actual water voxels using the list
                    context.cmd.DrawRendererList(data.rendererList);
                });
            }
        }

        public void Dispose()
        {
            // Render Graph automatically manages texture handles so we don't have to manually Release() an RTHandle anymore!
        }
    }
}