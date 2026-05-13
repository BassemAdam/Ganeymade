using UnityEngine;

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Rocket-League-style boat controller with a fully decoupled 3rd-person
/// orbit camera.
///
/// Movement (boat-relative, NOT camera-relative):
///   W / S   — slow forward / reverse thrust
///   A / D   — light lateral drift (and a small yaw nudge so the bow follows)
///   Space   — handbrake (cuts forward speed quickly)
///
/// Camera:
///   Mouse   — orbits freely around the boat. The boat keeps its own heading
///             regardless of where the camera looks.
///   Scroll  — zoom in/out
///   Esc / L — release / re-lock the cursor
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class BoatController : MonoBehaviour
{
    // ──────────────────────────────────────────────────────────────────
    // Movement
    // ──────────────────────────────────────────────────────────────────
    [Header("Forward / Reverse")]
    [Tooltip("Forward thrust force.")]
    public float thrustForce = 250f;

    [Tooltip("Reverse thrust as a fraction of forward thrust.")]
    [Range(0f, 1f)] public float reverseScale = 0.4f;

    [Tooltip("Soft cruising speed (m/s). Thrust fades as speed approaches this.")]
    public float cruiseSpeed = 4f;

    [Header("Drift (A/D)")]
    [Tooltip("Sideways force applied while A/D is held — gives a gentle lateral slide.")]
    public float driftForce = 80f;

    [Tooltip("Yaw torque applied while A/D is held — turns the bow slowly.")]
    public float driftYawTorque = 25f;

    [Tooltip("Lateral drag (higher = boat resists side-slipping).")]
    [Range(0f, 5f)] public float lateralDrag = 1.2f;

    [Tooltip("Forward drag (higher = boat slows down faster when no input).")]
    [Range(0f, 2f)] public float forwardDrag = 0.4f;

    [Tooltip("Yaw drag (higher = rotation stops faster when no input).")]
    [Range(0f, 5f)] public float yawDrag = 1.5f;

    [Header("Handbrake")]
    [Tooltip("Extra drag applied while Space is held.")]
    public float handbrakeDrag = 4f;

    // ──────────────────────────────────────────────────────────────────
    // Camera
    // ──────────────────────────────────────────────────────────────────
    [Header("3rd-Person Camera")]
    [Tooltip("Camera to control. Auto-uses Camera.main if empty.")]
    public Camera cam;

    [Tooltip("Offset from boat origin that the camera looks at (head/cockpit area).")]
    public Vector3 lookAtOffset = new Vector3(0f, 1.2f, 0f);

    [Tooltip("Distance from the boat.")]
    public float camDistance = 7f;
    public float camDistanceMin = 3f;
    public float camDistanceMax = 20f;

    [Tooltip("Mouse sensitivity (yaw + pitch).")]
    public float camSensitivity = 2f;

    [Tooltip("Mouse-wheel zoom step.")]
    public float zoomStep = 1f;

    [Tooltip("How quickly the camera follows the boat (1 = instant, lower = laggier).")]
    [Range(1f, 20f)] public float camFollowSmoothing = 12f;

    [Range(-30f, 89f)] public float camMaxPitch = 75f;
    [Range(-89f, 30f)] public float camMinPitch = -10f;

    [Tooltip("Skip a frame whenever camera distance + small offset would clip into geometry on this layer mask.")]
    public LayerMask camCollisionMask = ~0;

    [Tooltip("Radius used for the camera collision sphere-cast.")]
    public float camCollisionRadius = 0.3f;

    // ──────────────────────────────────────────────────────────────────
    // Internal
    // ──────────────────────────────────────────────────────────────────
    Rigidbody _rb;
    float _camYaw;
    float _camPitch = 15f;
    Vector3 _camVel; // for SmoothDamp

    void Start()
    {
        _rb = GetComponent<Rigidbody>();
        // Stable feel: keep boat upright, light angular damping handled in code.
        _rb.constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        if (cam == null) cam = Camera.main;

        // Start the camera looking from behind the boat.
        _camYaw = transform.eulerAngles.y;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    // ──────────────────────────────────────────────────────────────────
    // Physics — boat-local controls, fully independent of camera yaw
    // ──────────────────────────────────────────────────────────────────
    void FixedUpdate()
    {
        Vector3 fwd = transform.forward;
        Vector3 right = transform.right;

        Vector3 vel = _rb.linearVelocity;
        float forwardV = Vector3.Dot(vel, fwd);
        float lateralV = Vector3.Dot(vel, right);

        // ── Forward / reverse thrust, fading as we approach cruiseSpeed ──
        float throttle = 0f;
        if (GetKey(KeyCode.W)) throttle = 1f;
        else if (GetKey(KeyCode.S)) throttle = -reverseScale;

        if (throttle > 0f)
        {
            float speedRatio = Mathf.Clamp01(forwardV / Mathf.Max(cruiseSpeed, 0.01f));
            _rb.AddForce(fwd * thrustForce * throttle * (1f - speedRatio), ForceMode.Force);
        }
        else if (throttle < 0f)
        {
            float speedRatio = Mathf.Clamp01(-forwardV / Mathf.Max(cruiseSpeed * reverseScale, 0.01f));
            _rb.AddForce(fwd * thrustForce * throttle * (1f - speedRatio), ForceMode.Force);
        }

        // ── A / D = lateral drift + slow yaw nudge ──
        float driftInput = 0f;
        if (GetKey(KeyCode.A)) driftInput -= 1f;
        if (GetKey(KeyCode.D)) driftInput += 1f;

        if (Mathf.Abs(driftInput) > 0.01f)
        {
            _rb.AddForce(right * driftForce * driftInput, ForceMode.Force);
            _rb.AddTorque(Vector3.up * driftYawTorque * driftInput, ForceMode.Force);
        }

        // ── Drag terms (manually applied so we can keep them anisotropic) ──
        // Lateral drag — water resists side-slipping much more than forward motion.
        _rb.AddForce(-right * lateralV * _rb.mass * lateralDrag, ForceMode.Force);

        // Forward drag — only when no throttle (so we glide naturally to a stop).
        if (Mathf.Abs(throttle) < 0.01f)
            _rb.AddForce(-fwd * forwardV * _rb.mass * forwardDrag, ForceMode.Force);

        // Handbrake — strong forward drag when Space held.
        if (GetKey(KeyCode.Space))
            _rb.AddForce(-vel * _rb.mass * handbrakeDrag, ForceMode.Force);

        // Yaw drag — kills residual spin when not drifting.
        if (Mathf.Abs(driftInput) < 0.01f)
        {
            Vector3 av = _rb.angularVelocity;
            _rb.AddTorque(-Vector3.up * av.y * _rb.mass * yawDrag, ForceMode.Force);
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Camera — free orbit, never affects boat heading
    // ──────────────────────────────────────────────────────────────────
    void LateUpdate()
    {
        HandleCursorToggle();
        if (cam == null) return;

        // Mouse orbit (only when cursor is locked, so UI clicks don't spin it).
        if (Cursor.lockState == CursorLockMode.Locked)
        {
            _camYaw += GetMouseAxisX() * camSensitivity;
            _camPitch -= GetMouseAxisY() * camSensitivity;
            _camPitch = Mathf.Clamp(_camPitch, camMinPitch, camMaxPitch);
        }

        // Mouse-wheel zoom.
        float wheel = GetMouseScroll();
        if (Mathf.Abs(wheel) > 0.0001f)
            camDistance = Mathf.Clamp(camDistance - wheel * zoomStep, camDistanceMin, camDistanceMax);

        // Compute desired pose.
        Vector3 pivot = transform.position + lookAtOffset;
        Quaternion orbit = Quaternion.Euler(_camPitch, _camYaw, 0f);
        Vector3 desiredDir = orbit * Vector3.back;
        float desiredDist = camDistance;

        // Optional collision: pull camera in if there's geometry behind it.
        if (camCollisionMask.value != 0 &&
            Physics.SphereCast(pivot, camCollisionRadius, desiredDir, out var hit,
                               camDistance, camCollisionMask, QueryTriggerInteraction.Ignore))
        {
            desiredDist = Mathf.Max(camDistanceMin * 0.5f, hit.distance - camCollisionRadius);
        }

        Vector3 targetPos = pivot + desiredDir * desiredDist;

        // Smoothed follow.
        cam.transform.position = Vector3.SmoothDamp(
            cam.transform.position, targetPos, ref _camVel,
            1f / Mathf.Max(camFollowSmoothing, 0.001f));
        cam.transform.rotation = Quaternion.LookRotation(pivot - cam.transform.position, Vector3.up);
    }

    void HandleCursorToggle()
    {
        if (GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        if (Cursor.lockState == CursorLockMode.None && GetKeyDown(KeyCode.L))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Input wrappers (legacy + new Input System)
    // ──────────────────────────────────────────────────────────────────
    static bool GetKey(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var kb = Keyboard.current;
        if (kb == null) return false;
        return key switch
        {
            KeyCode.W         => kb.wKey.isPressed,
            KeyCode.A         => kb.aKey.isPressed,
            KeyCode.S         => kb.sKey.isPressed,
            KeyCode.D         => kb.dKey.isPressed,
            KeyCode.Space     => kb.spaceKey.isPressed,
            KeyCode.LeftShift => kb.leftShiftKey.isPressed,
            _ => false
        };
#else
        return Input.GetKey(key);
#endif
    }

    static bool GetKeyDown(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var kb = Keyboard.current;
        if (kb == null) return false;
        return key switch
        {
            KeyCode.Escape => kb.escapeKey.wasPressedThisFrame,
            KeyCode.L      => kb.lKey.wasPressedThisFrame,
            _ => false
        };
#else
        return Input.GetKeyDown(key);
#endif
    }

    static float GetMouseAxisX()
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var m = Mouse.current;
        return (m != null ? m.delta.ReadValue().x : 0f) * 0.1f;
#else
        return Input.GetAxis("Mouse X");
#endif
    }

    static float GetMouseAxisY()
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var m = Mouse.current;
        return (m != null ? m.delta.ReadValue().y : 0f) * 0.1f;
#else
        return Input.GetAxis("Mouse Y");
#endif
    }

    static float GetMouseScroll()
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var m = Mouse.current;
        return (m != null ? m.scroll.ReadValue().y : 0f) * 0.01f;
#else
        return Input.GetAxis("Mouse ScrollWheel");
#endif
    }
}
