using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// This is the URP Renderer Feature that hooks the fluid rendering into the pipeline.
// Think of it as the entry point. It creates the render pass and feeds it the data
// it needs from the particle system each frame.
public sealed class WaterScreenSpaceFluidFeature : ScriptableRendererFeature
{
    // These static events are how the particle system tells the render pass what to draw.
    // Any particle renderer that wants to participate in the fluid effect subscribes to these
    // and issues its draw calls when they're invoked.
    public static event Action<RasterCommandBuffer, Material> OnDrawDepth;
    public static event Action<RasterCommandBuffer, Material> OnDrawThickness;

    // Static flags and references set by the particle system at runtime.
    // IsActive lets the system globally disable the effect without destroying anything.
    public static bool     IsActive;
    public static Material ActiveMaterial;

    // Optional simulation bounds. Passed into the render pass in case the shader needs them for masking.
    public static Vector3 BoundsMin;
    public static Vector3 BoundsMax;
    public static bool    HasBounds;

    // Where in the frame this effect runs. BeforeRenderingTransparents is the right spot
    // because water is a transparent surface and we need the opaque scene color as a base.
    [SerializeField] private RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingTransparents;

    private WaterSSFRenderPass _pass;

    // Create is called once when the feature initializes.
    // We create the pass here instead of every frame to avoid unnecessary allocations.
    public override void Create()
    {
        _pass = new WaterSSFRenderPass
        {
            renderPassEvent = injectionPoint
        };
    }

    // Called every frame for each camera. We decide here whether to enqueue the pass.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // Skip if the pass isn't ready or the effect is turned off.
        if (_pass == null || !IsActive || ActiveMaterial == null) return;

        // Only run on Game and SceneView cameras. Preview cameras and reflection probes
        // don't need the fluid effect and running it on them wastes GPU time.
        var camType = renderingData.cameraData.cameraType;
        if (camType != CameraType.Game && camType != CameraType.SceneView) return;

        // Sync settings from the feature onto the pass before enqueuing.
        // The injection point might have changed in the inspector at runtime, so we always update it here.
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
