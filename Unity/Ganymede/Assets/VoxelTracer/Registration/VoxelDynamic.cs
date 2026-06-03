using UnityEngine;

//Marker component: attach to any GameObject to flag its MeshRenderer
// as dynamic for the voxelizer. Dynamic objects are re-voxelized every
//frame while static objects are voxelized once and cached on the GPU.
//Self-registers with VoxelTracerSystem to avoid per-frame scene scans.
//Also controls fluid interaction: buoyancy, drag, and two-way coupling
// with SPH particles when a Rigidbody is present.
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

    [Tooltip("Constrain this rigidbody inside the simulation AABB bounds (same as fluid particles).")]
    public bool constrainToBounds = true;

    [Tooltip("Bounciness when hitting the AABB walls (0 = stop, 1 = full reflect).")]
    [Range(0f, 1f)]
    public float boundsBounce = 0.3f;

    [Tooltip("Reference to the UseComputePlugin whose bounds constrain this object. Auto-finds if empty.")]
    public UseComputePlugin simReference;

    [Tooltip("Auto-set Rigidbody mass from objectDensity * approximateVolume on Start.")]
    public bool autoSetMass = true;

    // Runtime state
    [HideInInspector] public float submergedFraction;
    [HideInInspector] public Vector3 lastBuoyancyForce;
    [HideInInspector] public Vector3 lastDragForce;
    [HideInInspector] public Vector3 lastPosition = Vector3.positiveInfinity;
    [HideInInspector] public Quaternion lastRotation;
    [HideInInspector] public Vector3 lastScale;

    // Cached references
    [System.NonSerialized] public Rigidbody rb;
    [System.NonSerialized] public Bounds worldBounds;
    private MeshRenderer _cachedRenderer;
    private Collider _cachedCollider;
    private bool _boundsCacheInit;
    private UseComputePlugin _simRef;
    private bool _simSearched;

    void OnEnable()
    {
        VoxelTracerSystem.RegisterDynamic(this);
        rb = GetComponent<Rigidbody>();
        CacheBoundsSource();
    }

    void Start()
    {
        if (autoSetMass && rb != null)
        {
            float vol = ApproximateVolume();
            rb.mass = objectDensity * vol;
        }
    }

    void FixedUpdate()
    {
        if (!constrainToBounds || rb == null) return;

        // Find sim reference once
        if (!_simSearched)
        {
            if (simReference != null)
                _simRef = simReference;
            else
                _simRef = Object.FindAnyObjectByType<UseComputePlugin>();
            _simSearched = true;
        }
        if (_simRef == null) return;

        _simRef.GetBoundsWS(out Vector3 bMin, out Vector3 bMax);
        RefreshBounds();
        Vector3 extents = worldBounds.extents;
        Vector3 pos = rb.position;
        Vector3 vel = rb.linearVelocity;
        bool clamped = false;

        for (int axis = 0; axis < 3; axis++)
        {
            float lo = bMin[axis] + extents[axis];
            float hi = bMax[axis] - extents[axis];
            if (lo >= hi) continue;

            if (pos[axis] < lo)
            {
                pos[axis] = lo;
                if (vel[axis] < 0f) vel[axis] = -vel[axis] * boundsBounce;
                clamped = true;
            }
            else if (pos[axis] > hi)
            {
                pos[axis] = hi;
                if (vel[axis] > 0f) vel[axis] = -vel[axis] * boundsBounce;
                clamped = true;
            }
        }

        if (clamped)
        {
            rb.MovePosition(pos);
            rb.linearVelocity = vel;
        }
    }

    void OnDisable() => VoxelTracerSystem.UnregisterDynamic(this);

    private void CacheBoundsSource()
    {
        if (_boundsCacheInit) return;
        _cachedRenderer = GetComponent<MeshRenderer>();
        if (_cachedRenderer == null)
            _cachedCollider = GetComponent<Collider>();
        _boundsCacheInit = true;
    }


    // Refresh cached world bounds from renderer or collider.

    public void RefreshBounds()
    {
        if (!_boundsCacheInit) CacheBoundsSource();

        if (_cachedRenderer != null)
            worldBounds = _cachedRenderer.bounds;
        else if (_cachedCollider != null)
            worldBounds = _cachedCollider.bounds;
        else
            worldBounds = new Bounds(transform.position, Vector3.one);
    }


    // Approximate volume in m^3 from mesh bounds.

    public float ApproximateVolume()
    {
        RefreshBounds();
        Vector3 s = worldBounds.size;
        // Approximate as 60% of bounding box (accounts for mesh not filling full AABB)
        return s.x * s.y * s.z * 0.6f;
    }

    public bool HasMoved()
    {
        Transform t = transform;
        if (t.position == lastPosition && t.rotation == lastRotation && t.localScale == lastScale)
            return false;
        lastPosition = t.position;
        lastRotation = t.rotation;
        lastScale = t.localScale;
        return true;
    }

    void OnDrawGizmosSelected()
    {
        if (!constrainToBounds) return;
        var sim = _simRef != null ? _simRef : Object.FindAnyObjectByType<UseComputePlugin>();
        if (sim == null) return;
        sim.GetBoundsWS(out Vector3 mn, out Vector3 mx);
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.5f);
        Vector3 center = (mn + mx) * 0.5f;
        Vector3 size = mx - mn;
        Gizmos.DrawWireCube(center, size);
    }
}
