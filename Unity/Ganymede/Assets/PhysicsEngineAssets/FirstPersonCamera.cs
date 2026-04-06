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

        Vector3 hitPoint = transform.position + transform.forward * interactionDistance;
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

        Vector3 pt = transform.position + transform.forward * interactionDistance;
        Gizmos.color = GetMouseButton(0)
            ? new Color(0f, 1f, 0.5f, 0.3f)
            : new Color(1f, 0.2f, 0f, 0.3f);
        Gizmos.DrawWireSphere(pt, interactionRadius);
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
