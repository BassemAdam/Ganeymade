using UnityEngine;

/// <summary>
/// Attach to a GameObject to make it a fluid particle emitter (water tap).
/// The SPH plugin reads registered sources each frame and activates
/// dormant particles from the pool at the specified rate and direction.
/// </summary>
public class WaterSource : MonoBehaviour
{
    public enum SpawnMode { Sphere, Tap }

    [Header("Mode")]
    [Tooltip("Sphere: pre-filled ball that recycles out-of-bounds particles. " +
             "Tap: one-shot stream that fills the pool and then stops.")]
    public SpawnMode spawnMode = SpawnMode.Sphere;

    [HideInInspector] public bool tapExhausted = false;

    [Tooltip("Particles emitted per second when active.")]
    [Min(0)] public float emissionRate = 200f;

    [Tooltip("World-space direction of emitted particles. Normalized at runtime.")]
    public Vector3 emissionDirection = Vector3.down;

    [Tooltip("Initial speed of emitted particles (m/s).")]
    [Min(0)] public float emissionSpeed = 3f;

    [Tooltip("Spawn radius around the source position (world units).")]
    [Min(0.01f)] public float emissionRadius = 0.15f;

    [Tooltip("Initial temperature assigned to emitted particles.")]
    public float initialTemperature = 25f;

    [Tooltip("Enable/disable this source at runtime.")]
    public bool isActive = true;

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0f, 0.5f, 1f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, emissionRadius);

        // Flow direction arrow
        Gizmos.color = new Color(0f, 0.8f, 1f, 0.9f);
        Vector3 dir = emissionDirection.normalized;
        Gizmos.DrawRay(transform.position, dir * emissionRadius * 3f);

        if (spawnMode == SpawnMode.Tap)
        {
            Gizmos.color = new Color(0f, 1f, 0.4f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, emissionRadius);
        }
    }
}
