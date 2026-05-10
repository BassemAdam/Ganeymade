using UnityEngine;

/// <summary>
/// Makes a rubber duck follow the mouse cursor across the fluid surface.
/// Movement is only allowed when:
///   1. <see cref="isEnabled"/> is true (toggled from the UI menu)
///   2. The linked tap (<see cref="tapSource"/>) is OFF
///   3. The duck is floating on fluid (submergedFraction > 0)
///
/// Attach to the duck GameObject which must also have VoxelDynamic + Rigidbody.
/// </summary>
[RequireComponent(typeof(VoxelDynamic))]
[RequireComponent(typeof(Rigidbody))]
public class DuckMouseFollow : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The tap water source. Movement is blocked while this is active.")]
    public WaterSource tapSource;

    [Tooltip("Camera used for screen-to-world raycasting. Auto-finds Main Camera if empty.")]
    public Camera viewCamera;

    [Header("Movement")]
    [Tooltip("Force applied toward the mouse target position.")]
    public float moveForce = 5f;

    [Tooltip("Maximum speed the duck can reach while following the mouse.")]
    public float maxSpeed = 2f;

    [Tooltip("Distance to target below which force is not applied (prevents jitter).")]
    public float arrivalThreshold = 0.15f;

    [Tooltip("Minimum submerged fraction to consider the duck 'on fluid'.")]
    [Range(0.01f, 0.5f)]
    public float minSubmergedFraction = 0.05f;

    [Header("State")]
    [Tooltip("Master toggle — controlled by the UI menu.")]
    public bool isEnabled = false;

    // Cached
    VoxelDynamic _voxelDynamic;
    Rigidbody _rb;
    Plane _movePlane;

    void Start()
    {
        _voxelDynamic = GetComponent<VoxelDynamic>();
        _rb = GetComponent<Rigidbody>();

        if (viewCamera == null)
            viewCamera = Camera.main;

        // Horizontal plane at the duck's starting Y
        _movePlane = new Plane(Vector3.up, transform.position);
    }

    void FixedUpdate()
    {
        if (!CanMove()) return;

        // Keep the movement plane at the duck's current Y so it tracks the fluid surface
        _movePlane = new Plane(Vector3.up, new Vector3(0f, transform.position.y, 0f));

        Vector3? target = GetMouseWorldPosition();
        if (target == null) return;

        Vector3 toTarget = target.Value - transform.position;
        toTarget.y = 0f; // only horizontal movement

        float dist = toTarget.magnitude;
        if (dist < arrivalThreshold) return;

        // Scale force down as we approach
        float forceMul = Mathf.Clamp01(dist / (arrivalThreshold * 5f));
        Vector3 force = toTarget.normalized * moveForce * forceMul;

        // Clamp velocity
        Vector3 horizVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
        if (horizVel.magnitude < maxSpeed)
            _rb.AddForce(force, ForceMode.Force);

        // Gently rotate duck to face movement direction
        if (toTarget.sqrMagnitude > 0.01f)
        {
            Quaternion targetRot = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
            float yaw = targetRot.eulerAngles.y;
            _rb.MoveRotation(Quaternion.Slerp(
                _rb.rotation,
                Quaternion.Euler(0f, yaw, 0f),
                Time.fixedDeltaTime * 4f));
        }
    }

    bool CanMove()
    {
        if (!isEnabled) return false;
        if (viewCamera == null) return false;

        // Tap must be OFF
        if (tapSource != null && tapSource.isActive) return false;

        // Duck must be on fluid
        if (_voxelDynamic == null) return false;
        if (_voxelDynamic.submergedFraction < minSubmergedFraction) return false;

        return true;
    }

    Vector3? GetMouseWorldPosition()
    {
        Ray ray = viewCamera.ScreenPointToRay(Input.mousePosition);
        if (_movePlane.Raycast(ray, out float enter))
            return ray.GetPoint(enter);
        return null;
    }
}
