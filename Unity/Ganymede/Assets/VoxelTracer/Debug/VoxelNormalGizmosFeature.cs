using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// URP Renderer Feature that draws voxel normal gizmo lines on top of the
/// composited scene.  Add this to the active URP Renderer asset.
/// Requires <see cref="VoxelNormalGizmos"/> on the camera.
/// </summary>
public class VoxelNormalGizmosFeature : ScriptableRendererFeature
{
    VoxelNormalGizmosPass _pass;

    public override void Create()
    {
        _pass = new VoxelNormalGizmosPass
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

        var gizmos = renderingData.cameraData.camera.GetComponent<VoxelNormalGizmos>();
        if (gizmos == null || !gizmos.enabled) return;

        _pass.Setup(gizmos);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        _pass?.Dispose();
    }

    // =========================================================================
    sealed class VoxelNormalGizmosPass : ScriptableRenderPass
    {
        VoxelNormalGizmos _gizmos;

        public void Setup(VoxelNormalGizmos gizmos) => _gizmos = gizmos;

        class PassData
        {
            public VoxelNormalGizmos gizmos;
            public Matrix4x4 viewMatrix;
            public Matrix4x4 projMatrix;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph,
                                               ContextContainer frameData)
        {
            if (_gizmos == null) return;

            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                       "Voxel Normal Gizmos", out var passData))
            {
                passData.gizmos = _gizmos;
                passData.viewMatrix = cameraData.camera.worldToCameraMatrix;
                passData.projMatrix = cameraData.camera.projectionMatrix;

                builder.SetRenderAttachment(resourceData.activeColorTexture, 0);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                {
                    data.gizmos.DrawLines(ctx.cmd, data.viewMatrix, data.projMatrix);
                });
            }
        }

        public void Dispose() { }
    }
}
