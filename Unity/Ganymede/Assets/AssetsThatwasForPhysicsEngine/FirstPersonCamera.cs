using UnityEngine;

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

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // Right-click to re-lock cursor (left-click is reserved for attract)
        if (Cursor.lockState == CursorLockMode.None && Input.GetKeyDown(KeyCode.L))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void HandleMouseLook()
    {
        if (Cursor.lockState != CursorLockMode.Locked) return;

        float mx = Input.GetAxis("Mouse X") * mouseSensitivity;
        float my = Input.GetAxis("Mouse Y") * mouseSensitivity;

        yaw += mx;
        pitch -= my;
        pitch = Mathf.Clamp(pitch, -maxPitch, maxPitch);

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    void HandleMovement()
    {
        float speed = moveSpeed;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            speed *= sprintMultiplier;

        Vector3 input = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) input += transform.forward;
        if (Input.GetKey(KeyCode.S)) input -= transform.forward;
        if (Input.GetKey(KeyCode.D)) input += transform.right;
        if (Input.GetKey(KeyCode.A)) input -= transform.right;
        if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.Space)) input += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) input -= Vector3.up;

        if (input.sqrMagnitude > 0.001f)
            transform.position += input.normalized * speed * Time.deltaTime;
    }

    void HandleInteraction()
    {
        if (computePlugin == null) return;

        Vector3 hitPoint = transform.position + transform.forward * interactionDistance;
        bool leftHeld = Input.GetMouseButton(0);
        bool rightHeld = Input.GetMouseButton(1);

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
        bool active = Input.GetMouseButton(0) || Input.GetMouseButton(1);
        if (!active) return;

        Vector3 pt = transform.position + transform.forward * interactionDistance;
        Gizmos.color = Input.GetMouseButton(0)
            ? new Color(0f, 1f, 0.5f, 0.3f)
            : new Color(1f, 0.2f, 0f, 0.3f);
        Gizmos.DrawWireSphere(pt, interactionRadius);
    }
}
