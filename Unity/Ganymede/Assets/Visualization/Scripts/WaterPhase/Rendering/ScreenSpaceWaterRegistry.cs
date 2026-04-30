using UnityEngine;

/// <summary>
/// Static publisher for the screen-space water RendererFeature.
///
/// The SPH particle ComputeBuffer lives on a MonoBehaviour
/// (PhysicsWaterPhaseBridge) but the URP feature needs to read it during
/// RecordRenderGraph, where MonoBehaviour state is awkward to access. The
/// bridge calls Publish(...) once per LateUpdate, the feature reads the
/// snapshot whenever it records a frame.
/// </summary>
public static class ScreenSpaceWaterRegistry
{
    public static ComputeBuffer ParticleBuffer { get; private set; }
    public static int           ParticleCount  { get; private set; }
    public static float         SphereRadiusWS { get; private set; } = 0.1f;
    public static Vector3       BoundsMinWS    { get; private set; }
    public static Vector3       BoundsMaxWS    { get; private set; }
    public static bool          IsValid        => ParticleBuffer != null && ParticleBuffer.IsValid() && ParticleCount > 0;

    public static void Publish(
        ComputeBuffer particleBuffer,
        int           particleCount,
        float         sphereRadiusWS,
        Vector3       boundsMinWS,
        Vector3       boundsMaxWS)
    {
        ParticleBuffer = particleBuffer;
        ParticleCount  = Mathf.Max(0, particleCount);
        SphereRadiusWS = Mathf.Max(1e-4f, sphereRadiusWS);
        BoundsMinWS    = boundsMinWS;
        BoundsMaxWS    = boundsMaxWS;
    }

    public static void Clear()
    {
        ParticleBuffer = null;
        ParticleCount  = 0;
    }
}
