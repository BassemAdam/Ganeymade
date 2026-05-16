using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public sealed class WaterScreenSpaceFluidFeature : ScriptableRendererFeature
{
    public static event Action<RasterCommandBuffer, Material> OnDrawDepth;
    public static event Action<RasterCommandBuffer, Material> OnDrawThickness;

    public static bool     IsActive;
    public static Material ActiveMaterial;

    public static Vector3 BoundsMin;
    public static Vector3 BoundsMax;
    public static bool    HasBounds;

    [SerializeField] private RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingTransparents;

    private WaterSSFRenderPass _pass;

    public override void Create()
    {
        _pass = new WaterSSFRenderPass
        {
            renderPassEvent = injectionPoint
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null || !IsActive || ActiveMaterial == null) return;

        var camType = renderingData.cameraData.cameraType;
        if (camType != CameraType.Game && camType != CameraType.SceneView) return;

        _pass.renderPassEvent = injectionPoint;
        _pass.OnDrawDepth     = OnDrawDepth;
        _pass.OnDrawThickness = OnDrawThickness;
        _pass.Material        = ActiveMaterial;
        _pass.HasBounds       = HasBounds;
        _pass.BoundsMin       = BoundsMin;
        _pass.BoundsMax       = BoundsMax;

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing) { }
}
