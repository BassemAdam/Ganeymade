using UnityEngine;

/// <summary>
/// Attach to a GameObject to create a drain zone that destroys fluid particles.
/// Particles entering the drain region are deactivated and their indices
/// are recycled back to the emission pool.
/// </summary>
public class WaterDrain : MonoBehaviour
{
    public enum DrainShape { Sphere, Box }

    [Tooltip("Radius of the drain zone (Sphere shape) or half-extent uniform size (Box).")]
    [Min(0.01f)] public float drainRadius = 0.5f;

    [Tooltip("Shape of the drain region.")]
    public DrainShape drainShape = DrainShape.Sphere;

    [Tooltip("Half-extents for Box drain shape. Ignored for Sphere.")]
    public Vector3 boxHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

    [Tooltip("Enable/disable this drain at runtime.")]
    public bool isActive = true;

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.2f, 0.1f, 0.4f);

        switch (drainShape)
        {
            case DrainShape.Sphere:
                Gizmos.DrawWireSphere(transform.position, drainRadius);
                break;
            case DrainShape.Box:
                Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
                Gizmos.DrawWireCube(Vector3.zero, boxHalfExtents * 2f);
                Gizmos.matrix = Matrix4x4.identity;
                break;
        }
    }
}
