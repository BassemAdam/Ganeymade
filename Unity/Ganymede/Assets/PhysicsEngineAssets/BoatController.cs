using UnityEngine;

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Physics-based boat controller with 3rd-person camera.
/// Applies thrust and rudder torque to a Rigidbody via W/S and A/D.
/// Mouse orbits the camera around the boat.
/// Attach to the boat GameObject (must have a Rigidbody).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class BoatController : MonoBehaviour
{
    [Header("Thrust")]
    [Tooltip("Forward thrust force.")]
    public float thrustForce = 500f;

    [Tooltip("Reverse thrust (fraction of forward thrust).")]
    [Range(0f, 1f)]
    public float reverseScale = 0.4f;

    [Tooltip("Maximum forward speed (m/s). Thrust fades as speed approaches this.")]
    public float maxSpeed = 12f;

    [Header("Steering")]
    [Tooltip("How fast the boat aligns to the camera direction.")]
    public float steerSpeed = 3f;

    [Header("3rd Person Camera")]
    [Tooltip("Camera to control. Auto-finds Main Camera if empty.")]
    public Camera cam;

    [Tooltip("Distance behind the boat.")]
    public float camDistance = 5f;

    [Tooltip("Height above the boat.")]
    public float camHeight = 2f;

    [Tooltip("Mouse orbit sensitivity.")]
    public float camSensitivity = 2f;

    [Tooltip("Camera follow smoothing (lower = more lag).")]
    [Range(1f, 20f)]
    public float camSmoothing = 8f;

    [Tooltip("Vertical orbit clamp (degrees).")]
    [Range(5f, 85f)]
    public float camMaxPitch = 60f;

    [Tooltip("Minimum vertical angle (degrees).")]
    [Range(-20f, 30f)]
    public float camMinPitch = -5f;

    private Rigidbody _rb;
    private float _camYaw;
    private float _camPitch = 15f;

    void Start()
    {
        _rb = GetComponent<Rigidbody>();

        if (cam == null)
            cam = Camera.main;

        _camYaw = transform.eulerAngles.y;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void FixedUpdate()
    {
        float throttle = 0f;
        if (GetKey(KeyCode.W)) throttle = 1f;
        else if (GetKey(KeyCode.S)) throttle = -reverseScale;

        // Speed along forward axis
        float forwardSpeed = Vector3.Dot(_rb.linearVelocity, transform.forward);
        float speedRatio = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / Mathf.Max(maxSpeed, 0.1f));

        // Thrust fades near max speed
        if (throttle > 0f)
        {
            float thrustMul = 1f - speedRatio;
            _rb.AddForce(transform.forward * thrustForce * throttle * thrustMul, ForceMode.Force);
        }
        else if (throttle < 0f)
        {
            _rb.AddForce(transform.forward * thrustForce * throttle, ForceMode.Force);
        }

        // Steer toward camera yaw only when going forward
        if (throttle > 0.01f)
        {
            Quaternion targetRot = Quaternion.Euler(0f, _camYaw, 0f);
            Quaternion currentRot = Quaternion.Euler(0f, _rb.rotation.eulerAngles.y, 0f);
            Quaternion smoothed = Quaternion.Slerp(currentRot, targetRot,
                                                    Time.fixedDeltaTime * steerSpeed);
            _rb.MoveRotation(Quaternion.Euler(
                _rb.rotation.eulerAngles.x, smoothed.eulerAngles.y, _rb.rotation.eulerAngles.z));
        }

        // Water drag (lateral resistance much higher than forward)
        Vector3 vel = _rb.linearVelocity;
        float lateralSpeed = Vector3.Dot(vel, transform.right);
        _rb.AddForce(-transform.right * lateralSpeed * _rb.mass * 2f, ForceMode.Force);
    }

    void LateUpdate()
    {
        if (cam == null) return;

        HandleCursorToggle();
        HandleCameraOrbit();
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

    void HandleCameraOrbit()
    {
        if (Cursor.lockState == CursorLockMode.Locked)
        {
            _camYaw += GetMouseAxisX() * camSensitivity;
            _camPitch -= GetMouseAxisY() * camSensitivity;
            _camPitch = Mathf.Clamp(_camPitch, camMinPitch, camMaxPitch);
        }

        // Camera sits behind the boat's forward axis at fixed distance.
        // Mouse X rotates the steering yaw, Mouse Y adjusts height only.
        Vector3 boatPos = transform.position;
        Vector3 behindOffset = Quaternion.Euler(0f, _camYaw, 0f) * Vector3.back * camDistance;
        Vector3 targetPos = boatPos + behindOffset;
        targetPos.y = boatPos.y + camHeight + _camPitch * 0.1f; // pitch controls height

        cam.transform.position = Vector3.Lerp(cam.transform.position, targetPos,
                                               Time.deltaTime * camSmoothing);
        cam.transform.LookAt(boatPos + Vector3.up * 1.5f);
    }

    // ------------------------------------------------------------------
    // Input wrappers (same pattern as FirstPersonCamera)

    private static bool GetKey(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var kb = Keyboard.current;
        if (kb == null) return false;
        return key switch
        {
            KeyCode.W => kb.wKey.isPressed,
            KeyCode.S => kb.sKey.isPressed,
            KeyCode.LeftShift => kb.leftShiftKey.isPressed,
            _ => false
        };
#else
        return Input.GetKey(key);
#endif
    }

    private static bool GetKeyDown(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var kb = Keyboard.current;
        if (kb == null) return false;
        return key switch
        {
            KeyCode.Escape => kb.escapeKey.wasPressedThisFrame,
            KeyCode.L => kb.lKey.wasPressedThisFrame,
            _ => false
        };
#else
        return Input.GetKeyDown(key);
#endif
    }

    private static float GetMouseAxisX()
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var m = Mouse.current;
        return (m != null ? m.delta.ReadValue().x : 0f) * 0.1f;
#else
        return Input.GetAxis("Mouse X");
#endif
    }

    private static float GetMouseAxisY()
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var m = Mouse.current;
        return (m != null ? m.delta.ReadValue().y : 0f) * 0.1f;
#else
        return Input.GetAxis("Mouse Y");
#endif
    }
}
