using UnityEngine;

/// <summary>
/// Attach to a GameObject to make it a fluid particle emitter (water tap).
/// The SPH plugin reads registered sources each frame and activates
/// dormant particles from the pool at the specified rate and direction.
/// </summary>
public class WaterSource : MonoBehaviour
{
    public enum SpawnMode { Sphere, Tap, Lattice }

    [Header("Mode")]
    [Tooltip("Sphere: pre-filled ball that recycles out-of-bounds particles. " +
             "Tap: one-shot stream that fills the pool and then stops. " +
             "Lattice: one-shot 3D grid of particles, visualized before runtime.")]
    public SpawnMode spawnMode = SpawnMode.Sphere;

    [HideInInspector] public bool tapExhausted = false;

    [Tooltip("Particles emitted per second when active (Sphere/Tap modes).")]
    [Min(0)] public float emissionRate = 200f;

    [Tooltip("World-space direction of emitted particles. Normalized at runtime.")]
    public Vector3 emissionDirection = Vector3.down;

    [Tooltip("Initial speed of emitted particles (m/s).")]
    [Min(0)] public float emissionSpeed = 3f;

    [Tooltip("Spawn radius around the source position (world units). " +
             "For Lattice mode this defines the half-extents of the grid box.")]
    [Min(0.01f)] public float emissionRadius = 0.15f;

    [Tooltip("Initial temperature assigned to emitted particles.")]
    public float initialTemperature = 25f;

    [Tooltip("Enable/disable this source at runtime.")]
    public bool isActive = true;

    [Header("Lattice Settings")]
    [Tooltip("Spacing between lattice particles (world units). " +
             "Should be ≥ smoothingRadius * 0.5 to avoid pressure explosion. " +
             "Leave at 0 to auto-calculate from simulation settings at runtime.")]
    [Min(0f)] public float latticeSpacing = 0f;

    [Tooltip("Size of the lattice box (world units). Particles fill a box of this size centered on the source.")]
    public Vector3 latticeSize = new Vector3(1f, 1f, 1f);

    /// <summary>Returns the effective spacing, using the provided smoothingRadius for auto (0).</summary>
    public float GetEffectiveSpacing(float smoothingRadius = 0.2f)
    {
        return latticeSpacing > 0f ? latticeSpacing : smoothingRadius * 0.5f;
    }

    /// <summary>Total lattice positions given current settings.</summary>
    public int LatticeCount
    {
        get
        {
            if (spawnMode != SpawnMode.Lattice) return 0;
            float sp = GetEffectiveSpacing();
            int nx = Mathf.Max(1, Mathf.FloorToInt(latticeSize.x / sp) + 1);
            int ny = Mathf.Max(1, Mathf.FloorToInt(latticeSize.y / sp) + 1);
            int nz = Mathf.Max(1, Mathf.FloorToInt(latticeSize.z / sp) + 1);
            return nx * ny * nz;
        }
    }

    /// <summary>
    /// Fills the provided list with world-space lattice positions.
    /// </summary>
    public void GetLatticePositions(System.Collections.Generic.List<Vector3> outPositions, float smoothingRadius = 0.2f)
    {
        outPositions.Clear();
        if (spawnMode != SpawnMode.Lattice) return;

        float sp = GetEffectiveSpacing(smoothingRadius);
        int nx = Mathf.Max(1, Mathf.FloorToInt(latticeSize.x / sp) + 1);
        int ny = Mathf.Max(1, Mathf.FloorToInt(latticeSize.y / sp) + 1);
        int nz = Mathf.Max(1, Mathf.FloorToInt(latticeSize.z / sp) + 1);

        Vector3 half = new Vector3((nx - 1) * sp * 0.5f,
                                   (ny - 1) * sp * 0.5f,
                                   (nz - 1) * sp * 0.5f);
        Vector3 origin = transform.position - half;

        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                    outPositions.Add(origin + new Vector3(x * sp, y * sp, z * sp));
    }

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
        else if (spawnMode == SpawnMode.Lattice)
        {
            DrawLatticeGizmos();
        }
    }

    void DrawLatticeGizmos()
    {
        float sp = GetEffectiveSpacing();
        int nx = Mathf.Max(1, Mathf.FloorToInt(latticeSize.x / sp) + 1);
        int ny = Mathf.Max(1, Mathf.FloorToInt(latticeSize.y / sp) + 1);
        int nz = Mathf.Max(1, Mathf.FloorToInt(latticeSize.z / sp) + 1);
        int total = nx * ny * nz;

        Vector3 half = new Vector3((nx - 1) * sp * 0.5f,
                                   (ny - 1) * sp * 0.5f,
                                   (nz - 1) * sp * 0.5f);
        Vector3 origin = transform.position - half;

        // Bounding box
        Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.3f);
        Gizmos.DrawWireCube(transform.position, latticeSize);

        // Individual particle positions (cap to avoid editor lag)
        float radius = sp * 0.15f;
        int maxDraw = Mathf.Min(total, 8000);
        int step = Mathf.Max(1, total / maxDraw);
        int drawn = 0;

        Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.6f);
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    if (drawn % step == 0)
                    {
                        Vector3 pos = origin + new Vector3(x * sp, y * sp, z * sp);
                        Gizmos.DrawSphere(pos, radius);
                    }
                    drawn++;
                }

        // Particle count label via Handles (editor only)
#if UNITY_EDITOR
        UnityEditor.Handles.Label(transform.position + Vector3.up * (half.y + 0.2f),
            $"Lattice: {total} particles\n{nx}×{ny}×{nz} @ {sp:F3}");
#endif
    }
}
