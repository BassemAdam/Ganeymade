using UnityEngine;

/// <summary>
/// Attach to any GameObject to mark it as a solid fluid boundary.
/// The SPH plugin will generate an SDF from all FluidBoundary objects
/// and use it for particle collision on the GPU.
/// </summary>
public class FluidBoundary : MonoBehaviour
{
    public enum BoundaryShape { Box, Sphere, Mesh }

    [Tooltip("Static boundaries bake SDF once at startup. Dynamic boundaries regenerate SDF when they move.")]
    public bool isDynamic = false;

    [Tooltip("Shape used for analytic SDF generation. Mesh requires VoxelTracerSystem.")]
    public BoundaryShape shape = BoundaryShape.Box;

    [Tooltip("Override collider-derived size. If zero, auto-detected from Collider.")]
    public Vector3 halfExtents = Vector3.zero;

    /// <summary>Returns world-space center of the boundary.</summary>
    public Vector3 Center => transform.position;

    /// <summary>
    /// Returns effective half-extents in world space.
    /// Auto-detected from attached Collider if halfExtents is zero.
    /// </summary>
    public Vector3 GetWorldHalfExtents()
    {
        if (halfExtents != Vector3.zero)
        {
            Vector3 scale = transform.lossyScale;
            return Vector3.Scale(halfExtents, scale);
        }

        Collider col = GetComponent<Collider>();
        if (col == null)
            return transform.lossyScale * 0.5f;

        if (col is BoxCollider box)
        {
            Vector3 scale = transform.lossyScale;
            return Vector3.Scale(box.size * 0.5f, scale);
        }
        if (col is SphereCollider sphere)
        {
            float maxScale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
            float r = sphere.radius * maxScale;
            return new Vector3(r, r, r);
        }
        if (col is CapsuleCollider capsule)
        {
            float maxScale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
            float r = capsule.radius * maxScale;
            float h = capsule.height * 0.5f * maxScale;
            return new Vector3(r, h, r);
        }

        // Fallback: use collider bounds
        return col.bounds.extents;
    }

    /// <summary>Returns the world-space radius for Sphere shapes.</summary>
    public float GetWorldRadius()
    {
        if (halfExtents != Vector3.zero)
            return halfExtents.x * Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);

        SphereCollider sphere = GetComponent<SphereCollider>();
        if (sphere != null)
            return sphere.radius * Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);

        return GetWorldHalfExtents().x;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = isDynamic ? new Color(1f, 0.5f, 0f, 0.35f) : new Color(0f, 0.8f, 0.2f, 0.35f);

        switch (shape)
        {
            case BoundaryShape.Box:
                Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
                Gizmos.DrawWireCube(Vector3.zero, GetWorldHalfExtents() * 2f);
                Gizmos.matrix = Matrix4x4.identity;
                break;
            case BoundaryShape.Sphere:
                Gizmos.DrawWireSphere(transform.position, GetWorldRadius());
                break;
            case BoundaryShape.Mesh:
                Gizmos.DrawWireCube(transform.position, GetWorldHalfExtents() * 2f);
                break;
        }
    }
}
