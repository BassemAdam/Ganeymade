using UnityEngine;

/// <summary>
/// Renderer adapter that feeds particle data to ScreenSpaceFluidFeature.
/// Instantiated by PhysicsWaterPhaseBridge when ScreenSpaceFluid mode is active.
/// Unlike the other renderers, this doesn't create proxy geometry — it drives
/// a ScriptableRendererFeature via static state.
/// </summary>
public sealed class WaterPhaseScreenSpaceRenderer
{
    public void Render(
        UseComputePlugin computePlugin,
        WaterPhaseResources resources,
        WaterPhaseRenderingSettings renderSettings)
    {
        if (computePlugin == null || resources == null || resources.ParticleOutputBuffer == null)
        {
            ScreenSpaceFluidFeature.IsActive = false;
            return;
        }

        ScreenSpaceFluidFeature.ParticleBuffer = resources.ParticleOutputBuffer;
        ScreenSpaceFluidFeature.ParticleCount = computePlugin.particleCount;
        ScreenSpaceFluidFeature.ParticleRadius = renderSettings.screenSpaceParticleRadius;
        ScreenSpaceFluidFeature.FilterSize = renderSettings.screenSpaceFilterSize;
        ScreenSpaceFluidFeature.SprayThreshold = renderSettings.screenSpaceSprayThreshold;
        ScreenSpaceFluidFeature.IsActive = true;
    }

    public void SetInactive()
    {
        ScreenSpaceFluidFeature.IsActive = false;
    }
}
