using UnityEngine;

/// Marker component: attach to any GameObject with a MeshRenderer to flag it
/// as a boundary collision object for the SPH solver.
/// Only meshes with this component will generate fixed boundary particles.
/// Self-registers with VoxelTracerSystem for efficient per-frame lookup.

public sealed class VoxelBoundaryCollider : MonoBehaviour
{
    [Header("Normal Filter")]
    [Tooltip("If enabled, only surface voxels whose estimated normal aligns with this direction will generate boundary particles.")]
    public bool useNormalFilter = false;

    [Tooltip("Direction to keep (in world space). E.g. (0,1,0) = top surface only, (0,-1,0) = bottom only.")]
    public Vector3 filterDirection = Vector3.up;

    [Tooltip("Dot product threshold (0 = hemisphere, 0.5 = 60deg cone, -1 = all). Voxels whose normal dot direction >= this are kept.")]
    [Range(-1f, 1f)]
    public float filterThreshold = 0f;

    void OnEnable() => VoxelTracerSystem.RegisterBoundaryCollider(this);
    void OnDisable() => VoxelTracerSystem.UnregisterBoundaryCollider(this);
}
