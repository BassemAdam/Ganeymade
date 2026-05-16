using UnityEngine;

/// <summary>
/// Marker component: attach to any GameObject to flag its MeshRenderer
/// as dynamic for the voxelizer. Dynamic objects are re-voxelized every
/// frame while static objects are voxelized once and cached on the GPU.
/// Self-registers with VoxelTracerSystem to avoid per-frame scene scans.
/// 
/// Also controls fluid interaction: buoyancy, drag, and two-way coupling
/// with SPH particles when a Rigidbody is present.
/// </summary>
public sealed class VoxelDynamic : MonoBehaviour
{
    public enum BuoyancyMode { Analytical, GPUParticleSum }

    [Header("Fluid Interaction")]
    [Tooltip("Enable fluid forces (buoyancy + drag) on this object's Rigidbody.")]
    public bool enableFluidForces = true;

    [Tooltip("Analytical: estimates submerged volume from particle sampling (cheap). " +
             "GPUParticleSum: sums actual SPH pressure from nearby particles (accurate, costly).")]
    public BuoyancyMode buoyancyMode = BuoyancyMode.Analytical;

    [Tooltip("Object density relative to the fluid's restDensity. " +
             "Below restDensity = floats, above = sinks. Example: restDensity=30, set to 15 for floating, 60 for sinking.")]
    [Min(0.01f)]
    public float objectDensity = 15f;

    [Tooltip("Drag coefficient when submerged. Higher = more resistance to motion through fluid.")]
    [Range(0f, 10f)]
    public float dragCoefficient = 1.0f;

    [Tooltip("Angular drag multiplier when submerged.")]
    [Range(0f, 5f)]
    public float angularDragCoefficient = 0.5f;

    [Tooltip("Target waterline: 0 = sits on surface, 0.5 = half submerged, 1 = fully sunk.")]
    [Range(0f, 1f)]
    public float sinkFactor = 0.3f;

    [Tooltip("Keep the object upright (no pitch/roll). Yaw rotation is preserved.")]
    public bool stayUpright = false;

    [Tooltip("Auto-set Rigidbody mass from objectDensity * approximateVolume on Start.")]
    public bool autoSetMass = true;

    // Runtime state
    [HideInInspector] public float submergedFraction;
    [HideInInspector] public Vector3 lastBuoyancyForce;
    [HideInInspector] public Vector3 lastDragForce;

    // Cached references
    [System.NonSerialized] public Rigidbody rb;
    [System.NonSerialized] public Bounds worldBounds;

    void OnEnable()
    {
        VoxelTracerSystem.RegisterDynamic(this);
        rb = GetComponent<Rigidbody>();
    }

    void Start()
    {
        if (autoSetMass && rb != null)
        {
            float vol = ApproximateVolume();
            rb.mass = objectDensity * vol;
        }
    }

    void OnDisable() => VoxelTracerSystem.UnregisterDynamic(this);

    /// <summary>
    /// Refresh cached world bounds from renderer or collider.
    /// </summary>
    public void RefreshBounds()
    {
        var mr = GetComponent<MeshRenderer>();
        if (mr != null)
            worldBounds = mr.bounds;
        else
        {
            var col = GetComponent<Collider>();
            if (col != null)
                worldBounds = col.bounds;
            else
                worldBounds = new Bounds(transform.position, Vector3.one);
        }
    }

    /// <summary>
    /// Approximate volume in m³ from mesh bounds.
    /// </summary>
    public float ApproximateVolume()
    {
        RefreshBounds();
        Vector3 s = worldBounds.size;
        // Approximate as 60% of bounding box (accounts for mesh not filling full AABB)
        return s.x * s.y * s.z * 0.6f;
    }
}
