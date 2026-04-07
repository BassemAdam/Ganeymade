using UnityEngine;

// This script supports both the legacy Input Manager and the newer Input System.
// When "Active Input Handling" is set to "Input System Package (New)" only,
// calling UnityEngine.Input APIs throws InvalidOperationException.
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

/// <summary>
/// First-person camera controller with WASD movement, mouse look,
/// and particle interaction (left-click attract, right-click repulse).
/// Attach to the Main Camera. Automatically finds UseComputePlugin if not assigned.
/// </summary>
[DefaultExecutionOrder(-100)]
public class FirstPersonCamera : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Auto-found if left empty")]
    public UseComputePlugin computePlugin;

    [Header("Movement")]
    [Tooltip("Base movement speed")]
    public float moveSpeed = 5f;

    [Tooltip("Sprint multiplier (hold Shift)")]
    public float sprintMultiplier = 2.5f;

    [Header("Mouse Look")]
    [Tooltip("Mouse sensitivity")]
    public float mouseSensitivity = 2f;

    [Tooltip("Vertical look clamp (degrees)")]
    [Range(60f, 90f)]
    public float maxPitch = 85f;

    [Header("Interaction")]
    [Tooltip("How far in front of the camera the interaction point is placed")]
    public float interactionDistance = 5f;

    [Tooltip("Radius of the interaction sphere")]
    public float interactionRadius = 4f;

    [Tooltip("Strength of the attract force (left click)")]
    public float attractStrength = 200f;

    [Tooltip("Strength of the repulse force (right click)")]
    public float repulseStrength = 200f;

    [Tooltip("If enabled, snaps the interaction point to the simulation bounds along the camera ray when possible. Useful when the camera is far from the fluid volume.")]
    public bool snapInteractionToSimBounds = true;

    private float pitch;
    private float yaw;

    void Start()
    {
        // Auto-find UseComputePlugin if not assigned
        if (computePlugin == null)
        {
            #if UNITY_2023_1_OR_NEWER
            computePlugin = FindAnyObjectByType<UseComputePlugin>();
            #else
            computePlugin = FindObjectOfType<UseComputePlugin>();
            #endif
        }

        if (computePlugin == null)
            Debug.LogError("[FirstPersonCamera] No UseComputePlugin found in scene!");
        else
            Debug.Log("[FirstPersonCamera] Linked to UseComputePlugin");

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        Vector3 euler = transform.eulerAngles;
        yaw = euler.y;
        pitch = euler.x;
        if (pitch > 180f) pitch -= 360f;
    }

    void Update()
    {
        HandleMouseLook();
        HandleMovement();
        HandleInteraction();

        if (GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // Right-click to re-lock cursor (left-click is reserved for attract)
        if (Cursor.lockState == CursorLockMode.None && GetKeyDown(KeyCode.L))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void HandleMouseLook()
    {
        if (Cursor.lockState != CursorLockMode.Locked) return;

        float mx = GetMouseAxisX() * mouseSensitivity;
        float my = GetMouseAxisY() * mouseSensitivity;

        yaw += mx;
        pitch -= my;
        pitch = Mathf.Clamp(pitch, -maxPitch, maxPitch);

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    void HandleMovement()
    {
        float speed = moveSpeed;
        if (GetKey(KeyCode.LeftShift) || GetKey(KeyCode.RightShift))
            speed *= sprintMultiplier;

        Vector3 input = Vector3.zero;
        if (GetKey(KeyCode.W)) input += transform.forward;
        if (GetKey(KeyCode.S)) input -= transform.forward;
        if (GetKey(KeyCode.D)) input += transform.right;
        if (GetKey(KeyCode.A)) input -= transform.right;
        if (GetKey(KeyCode.E) || GetKey(KeyCode.Space)) input += Vector3.up;
        if (GetKey(KeyCode.Q)) input -= Vector3.up;

        if (input.sqrMagnitude > 0.001f)
            transform.position += input.normalized * speed * Time.deltaTime;
    }

    void HandleInteraction()
    {
        if (computePlugin == null) return;

        Vector3 hitPoint = ComputeInteractionPoint();
        bool leftHeld = GetMouseButton(0);
        bool rightHeld = GetMouseButton(1);

        if (leftHeld)
        {
            computePlugin.interactionStrength = attractStrength;
            computePlugin.interactionPos = hitPoint;
            computePlugin.interactionRadius = interactionRadius;
        }
        else if (rightHeld)
        {
            computePlugin.interactionStrength = -repulseStrength;
            computePlugin.interactionPos = hitPoint;
            computePlugin.interactionRadius = interactionRadius;
        }
        else
        {
            computePlugin.interactionStrength = 0f;
        }
    }

    void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;
        bool active = GetMouseButton(0) || GetMouseButton(1);
        if (!active) return;

        Vector3 pt = ComputeInteractionPoint();
        Gizmos.color = GetMouseButton(0)
            ? new Color(0f, 1f, 0.5f, 0.3f)
            : new Color(1f, 0.2f, 0f, 0.3f);
        Gizmos.DrawWireSphere(pt, interactionRadius);
    }

    private Vector3 ComputeInteractionPoint()
    {
        Vector3 origin = transform.position;
        Vector3 dir = transform.forward; // unit-length

        // Default: fixed distance in front of the camera.
        Vector3 hitPoint = origin + dir * interactionDistance;

        if (!snapInteractionToSimBounds || computePlugin == null)
            return hitPoint;

        computePlugin.GetBoundsWS(out Vector3 bmin, out Vector3 bmax);

        if (TryRayAabb(origin, dir, bmin, bmax, out float tEnter, out float tExit))
        {
            // If we're outside the bounds, use the entry point.
            if (tEnter > 0.001f)
                return origin + dir * tEnter;

            // If we're inside the bounds, keep the fixed-distance behavior (more intuitive).
            // (tEnter is <= 0 in that case.)
        }

        return hitPoint;
    }

    private static bool TryRayAabb(Vector3 rayOrigin, Vector3 rayDir, Vector3 bmin, Vector3 bmax, out float tEnter, out float tExit)
    {
        float tmin = float.NegativeInfinity;
        float tmax = float.PositiveInfinity;

        if (!Slab(rayOrigin.x, rayDir.x, bmin.x, bmax.x, ref tmin, ref tmax) ||
            !Slab(rayOrigin.y, rayDir.y, bmin.y, bmax.y, ref tmin, ref tmax) ||
            !Slab(rayOrigin.z, rayDir.z, bmin.z, bmax.z, ref tmin, ref tmax))
        {
            tEnter = 0f;
            tExit = 0f;
            return false;
        }

        tEnter = tmin;
        tExit = tmax;
        return tmax >= Mathf.Max(tmin, 0f);
    }

    private static bool Slab(float o, float d, float min, float max, ref float tmin, ref float tmax)
    {
        const float eps = 1e-8f;
        if (Mathf.Abs(d) < eps)
        {
            // Ray parallel to slab: accept only if origin is within the slab.
            return o >= min && o <= max;
        }

        float invD = 1f / d;
        float t1 = (min - o) * invD;
        float t2 = (max - o) * invD;
        if (t1 > t2)
        {
            float tmp = t1;
            t1 = t2;
            t2 = tmp;
        }

        tmin = Mathf.Max(tmin, t1);
        tmax = Mathf.Min(tmax, t2);
        return tmax >= tmin;
    }

    // ------------------------------------------------------------------
    // Input wrappers (legacy Input Manager OR new Input System)

    private static bool GetKey(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var kb = Keyboard.current;
        if (kb == null) return false;
        return KeyCodeToKeyPressed(kb, key);
#else
        return Input.GetKey(key);
#endif
    }

    private static bool GetKeyDown(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var kb = Keyboard.current;
        if (kb == null) return false;
        return KeyCodeToKeyDown(kb, key);
#else
        return Input.GetKeyDown(key);
#endif
    }

    private static bool GetMouseButton(int button)
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var m = Mouse.current;
        if (m == null) return false;
        return button switch
        {
            0 => m.leftButton.isPressed,
            1 => m.rightButton.isPressed,
            2 => m.middleButton.isPressed,
            _ => false
        };
#else
        return Input.GetMouseButton(button);
#endif
    }

    private static float GetMouseAxisX()
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        // New Input System returns mouse delta in pixels. Scale down to feel similar
        // to legacy Input.GetAxis("Mouse X"). Adjust mouseSensitivity if needed.
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

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
    private static bool KeyCodeToKeyPressed(Keyboard kb, KeyCode keyCode)
    {
        return keyCode switch
        {
            KeyCode.W => kb.wKey.isPressed,
            KeyCode.A => kb.aKey.isPressed,
            KeyCode.S => kb.sKey.isPressed,
            KeyCode.D => kb.dKey.isPressed,
            KeyCode.Q => kb.qKey.isPressed,
            KeyCode.E => kb.eKey.isPressed,
            KeyCode.Space => kb.spaceKey.isPressed,
            KeyCode.LeftShift => kb.leftShiftKey.isPressed,
            KeyCode.RightShift => kb.rightShiftKey.isPressed,
            _ => false
        };
    }

    private static bool KeyCodeToKeyDown(Keyboard kb, KeyCode keyCode)
    {
        return keyCode switch
        {
            KeyCode.Escape => kb.escapeKey.wasPressedThisFrame,
            KeyCode.L => kb.lKey.wasPressedThisFrame,
            _ => false
        };
    }
#endif
}
