using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class WaterThicknessFeature : ScriptableRendererFeature
{
    // Global event so procedural drawers (like Marching Cubes) can inject draw calls into this pass
    public static event System.Action<RasterCommandBuffer, Material> OnDrawWaterProcedural;
    public static event System.Action<RasterCommandBuffer, Material, int> OnDrawWaterProxyProcedural;

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
    private Material proxyDistanceMaterial;

    public override void Create()
    {
        if (settings.thicknessShader == null) return;
        
        // Create the material purely in memory
        if (thicknessMaterial == null)
        {
            thicknessMaterial = CoreUtils.CreateEngineMaterial(settings.thicknessShader);
        }

        if (proxyDistanceMaterial == null)
        {
            Shader proxyShader = Shader.Find("Hidden/WaterProxyIntervalGen");
            if (proxyShader != null)
                proxyDistanceMaterial = CoreUtils.CreateEngineMaterial(proxyShader);
        }

        thicknessPass = new WaterThicknessPass(settings.waterLayer, thicknessMaterial, proxyDistanceMaterial);
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

        if (proxyDistanceMaterial != null)
        {
            CoreUtils.Destroy(proxyDistanceMaterial);
        }
    }

    // =========================================================================
    // THE RENDER PASS
    // =========================================================================
    class WaterThicknessPass : ScriptableRenderPass
    {
        private FilteringSettings filteringSettings;
        private Material thicknessMaterial;
        private Material proxyDistanceMaterial;
        private List<ShaderTagId> shaderTagIdList = new List<ShaderTagId>();
        private static readonly int ID_WaterThicknessMap = Shader.PropertyToID("_WaterThicknessMap");
        private static readonly int ID_WaterProxyEntryDistanceMap = Shader.PropertyToID("_WaterProxyEntryDistanceMap");
        private static readonly int ID_WaterProxyExitDistanceMap = Shader.PropertyToID("_WaterProxyExitDistanceMap");

        public WaterThicknessPass(LayerMask waterLayer, Material material, Material proxyMaterial)
        {
            // Filter strictly for the Water layer
            filteringSettings = new FilteringSettings(RenderQueueRange.all, waterLayer);
            thicknessMaterial = material;
            proxyDistanceMaterial = proxyMaterial;
            
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
            public Material thicknessMaterial;
        }

        private class ProxyPassData
        {
            public Material proxyMaterial;
            public int passIndex;
            public Color clearColor;
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
                passData.thicknessMaterial = thicknessMaterial;

                // Assign our new texture as the render target, and clear it to black
                builder.SetRenderAttachment(thicknessTexture, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

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
                builder.SetGlobalTextureAfterPass(thicknessTexture, ID_WaterThicknessMap);

                // 4. The actual rendering execution code
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    // Clear the target to blank/zero internally first
                    context.cmd.ClearRenderTarget(false, true, Color.clear);
                    
                    // Draw all the actual water voxels using the list
                    context.cmd.DrawRendererList(data.rendererList);

                    // Execute procedural draws (like Marching Cubes) using the thickness material
                    if (WaterThicknessFeature.OnDrawWaterProcedural != null)
                    {
                        WaterThicknessFeature.OnDrawWaterProcedural.Invoke(context.cmd, data.thicknessMaterial);
                    }
                });
            }

            if (proxyDistanceMaterial == null)
                return;

            RenderTextureDescriptor proxyDesc = cameraData.cameraTargetDescriptor;
            proxyDesc.colorFormat = RenderTextureFormat.RFloat;
            proxyDesc.depthBufferBits = 0;
            proxyDesc.msaaSamples = 1;

            TextureHandle entryTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, proxyDesc, "_WaterProxyEntryDistanceMap", false);
            TextureHandle exitTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, proxyDesc, "_WaterProxyExitDistanceMap", false);

            RecordProxyDistancePass(
                renderGraph,
                entryTexture,
                proxyDistanceMaterial,
                proxyDistanceMaterial.FindPass("ProxyEntryDistance"),
                new Color(1000000f, 0f, 0f, 0f),
                ID_WaterProxyEntryDistanceMap,
                "Capture Water Proxy Entry Distances");

            RecordProxyDistancePass(
                renderGraph,
                exitTexture,
                proxyDistanceMaterial,
                proxyDistanceMaterial.FindPass("ProxyExitDistance"),
                Color.clear,
                ID_WaterProxyExitDistanceMap,
                "Capture Water Proxy Exit Distances");
        }

        private static void RecordProxyDistancePass(
            RenderGraph renderGraph,
            TextureHandle targetTexture,
            Material proxyMaterial,
            int passIndex,
            Color clearColor,
            int globalTextureId,
            string passName)
        {
            if (proxyMaterial == null || passIndex < 0)
                return;

            using (var builder = renderGraph.AddRasterRenderPass<ProxyPassData>(passName, out var passData))
            {
                passData.proxyMaterial = proxyMaterial;
                passData.passIndex = passIndex;
                passData.clearColor = clearColor;

                builder.SetRenderAttachment(targetTexture, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetGlobalTextureAfterPass(targetTexture, globalTextureId);

                builder.SetRenderFunc((ProxyPassData data, RasterGraphContext context) =>
                {
                    context.cmd.ClearRenderTarget(false, true, data.clearColor);

                    if (WaterThicknessFeature.OnDrawWaterProxyProcedural != null)
                        WaterThicknessFeature.OnDrawWaterProxyProcedural.Invoke(context.cmd, data.proxyMaterial, data.passIndex);
                });
            }
        }

        public void Dispose()
        {
            // Render Graph automatically manages texture handles so we don't have to manually Release() an RTHandle anymore!
        }
    }
}