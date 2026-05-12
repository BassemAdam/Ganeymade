using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Manages the full ingot lifecycle:
/// </summary>
public class IngotInteraction : MonoBehaviour
{
    // ----- References ----------------------------------------------

    [Header("Ingots")]
    public GameObject[] ingots;                  // Gold, Copper, Silver

    [Header("Pliers")]
    public GameObject pliers;

    [Header("Placement Slots (empty GameObjects marking where ingot sits)")]
    public Transform forgeSlot;                  
    public Transform anvilSlot;                  
    public Transform barrelSlot;                

    [Header("Hold Settings")]
    [Tooltip("Distance in front of camera when ingot+pliers are held.")]
    public float holdDistance = 0.55f;
    [Tooltip("How far below the camera centre the hold point sits.")]
    public float holdVerticalOffset = -0.18f;
    [Tooltip("How quickly the held object tracks the hold point.")]
    public float holdFollowSpeed = 18f;

    [Tooltip("Local-space offset applied to the ingot relative to the hold point, " +
             "so it sits at the pliers head rather than the arm.")]
    public Vector3 ingotGripOffset = new Vector3(-0.08f, 0f, 0.12f);

    [Header("Animation")]
    [Tooltip("Time in seconds for the ingot to travel to a slot.")]
    public float placeDuration = 0.6f;

    // ---- State ------------------------------------------------------

    public enum WorkflowStage
    {
        WaitingForPliers, WaitingForIngotPick, IngotHeld, AtForge, AtAnvil, AtBarrel, Returning
    }

    public WorkflowStage Stage { get; private set; } = WorkflowStage.WaitingForPliers;

    private GameObject _heldIngot;
    private int _heldIngotIndex = -1;
    private Vector3[] _ingotHomePositions;
    private Quaternion[] _ingotHomeRotations;
    private Vector3 _pliersHomePosition;
    private Quaternion _pliersHomeRotation;
    private Transform _holdPoint;          // auto-created child of Camera
    private Transform _ingotGripPoint;     // child of _holdPoint, offset to pliers head
    private Camera _cam;
    private bool _pliersPicked = false;
    private bool _nextPressed = false;
    public void NotifyNextPressed() => _nextPressed = true;

    // ----Events (Blacksmith minigame listens to these to know when to unlock Next) --

    public event System.Action OnIngotPickedUp;
    public event System.Action OnIngotPlacedInForge;
    public event System.Action OnIngotPlacedOnAnvil;
    public event System.Action OnIngotPlacedInBarrel;
    public event System.Action OnIngotReturned;

    // ----- Unity Lifecycle ----------------------------------------------

    private void Start()
    {
        _cam = Camera.main;
        if (_cam == null) 
            _cam = FindObjectOfType<Camera>();

        // Create the hold point as a child of the camera
        GameObject hp = new GameObject("IngotHoldPoint");
        hp.transform.SetParent(_cam.transform, false);
        hp.transform.localPosition = new Vector3(0f, holdVerticalOffset, holdDistance);
        hp.transform.localRotation = Quaternion.identity;
        _holdPoint = hp.transform;

        // Create the grip point as a child of the hold point.
        // The ingot parents to this so ingotGripOffset shifts it to the pliers head.
        GameObject gp = new GameObject("IngotGripPoint");
        gp.transform.SetParent(_holdPoint, false);
        gp.transform.localPosition = ingotGripOffset;
        gp.transform.localRotation = Quaternion.identity;
        _ingotGripPoint = gp.transform;

        // Cache home transforms for all ingots
        _ingotHomePositions = new Vector3[ingots.Length];
        _ingotHomeRotations = new Quaternion[ingots.Length];
        for (int i = 0; i < ingots.Length; i++)
        {
            _ingotHomePositions[i] = ingots[i].transform.position;
            _ingotHomeRotations[i] = ingots[i].transform.rotation;
        }

        // Cache pliers home so we can return them correctly after the cycle
        _pliersHomePosition = pliers.transform.position;
        _pliersHomeRotation = pliers.transform.rotation;
        Debug.Log($"[IngotInteraction] Pliers home cached at {_pliersHomePosition}");

        // Pliers start clickable while ingots are locked until pliers are picked up
        SetIngotCollidersEnabled(false);
        SetPliersColliderEnabled(true);
    }

    private void Update()
    {
        // Sync grip offset every frame so you can tweak it live in the Inspector
        if (_ingotGripPoint != null)
            _ingotGripPoint.localPosition = ingotGripOffset;

        // Smoothly move held ingot toward the grip point every frame
        if (_heldIngot != null && Stage == WorkflowStage.IngotHeld)
            _heldIngot.transform.position = Vector3.Lerp(_heldIngot.transform.position, _ingotGripPoint.position,
                Time.deltaTime * holdFollowSpeed);

        HandleClicks();
    }

    // ------ click Handling --------------------------------------------------

    private void HandleClicks()
    {
        if (!Input.GetMouseButtonDown(0)) 
            return;

        // ---- Block click if it landed on a UI element (like the Next button) ----
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            Debug.Log("[IngotInteraction] Click blocked , the pointer is over UI.");
            return;
        }

        // ---- Diagnostic: draw the ray in the Scene view for 3 seconds -------
        Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
        Debug.DrawRay(ray.origin, ray.direction * 50f, Color.red, 3f);
        Debug.Log($"[IngotInteraction] Ray origin={ray.origin:F2}  dir={ray.direction:F2}  " +
                  $"mousePos={Input.mousePosition}  cursorLocked={Cursor.lockState}");

        // Cast against ALL layers so we can see what is in the way
        RaycastHit[] allHits = Physics.RaycastAll(ray, 50f);
        if (allHits.Length == 0)
        {
            Debug.LogWarning("[IngotInteraction] RaycastAll hit NOTHING in 50 units. " +
                             "Check that your objects have colliders enabled and are " +
                             "not on a layer set to ignore raycasts.");

            // List every collider in the scene so you can cross-reference
            var allCols = FindObjectsOfType<Collider>();
            foreach (var c in allCols)
                Debug.Log($"  Collider in scene: '{c.gameObject.name}'  " +
                          $"enabled={c.enabled}  layer={LayerMask.LayerToName(c.gameObject.layer)}  " +
                          $"pos={c.transform.position:F2}");
            return;
        }

        // Show everything the ray passed through (including triggers)
        foreach (var h in allHits)
            Debug.Log($"  RaycastAll hit: '{h.collider.gameObject.name}'  " +
                      $"dist={h.distance:F2}  layer={LayerMask.LayerToName(h.collider.gameObject.layer)}  " +
                      $"isTrigger={h.collider.isTrigger}");

        // Use the closest non-trigger hit for interaction
        System.Array.Sort(allHits, (a, b) => a.distance.CompareTo(b.distance));
        RaycastHit hit = default;
        bool foundHit = false;
        foreach (var h in allHits)
        {
            if (!h.collider.isTrigger) 
            { 
                hit = h; 
                foundHit = true; 
                break; 
            }
        }

        if (!foundHit)
        {
            Debug.LogWarning("[IngotInteraction] Ray only hit triggers — no solid collider found.");
            return;
        }

        Debug.Log($"[IngotInteraction] Using hit: '{hit.collider.gameObject.name}' | Stage: {Stage}");

        switch (Stage)
        {
            case WorkflowStage.WaitingForPliers:
                if (pliers != null && hit.collider.gameObject == pliers)
                    PickUpPliers();
                break;

            case WorkflowStage.WaitingForIngotPick:
                for (int i = 0; i < ingots.Length; i++)
                {
                    if (hit.collider.gameObject == ingots[i])
                    {
                        PickUpIngot(i);
                        break;
                    }
                }
                break;

            case WorkflowStage.IngotHeld:
                // Allow swapping ingot while still at the table (before pressing Next)
                for (int i = 0; i < ingots.Length; i++)
                {
                    if (i != _heldIngotIndex && hit.collider.gameObject == ingots[i])
                    {
                        SwapIngot(i);
                        break;
                    }
                }
                break;
        }
    }

    // ---- pick up pliers ---------------------------

    private void PickUpPliers()
    {
        _pliersPicked = true;

        // disable the plier collider so it doesn't interfere
        SetPliersColliderEnabled(false);

        // Parent pliers to the hold point to preserve their current world transform
        pliers.transform.SetParent(_holdPoint, true);

        // animate position to hold point 
        StartCoroutine(MoveToLocal(pliers.transform,Vector3.zero,pliers.transform.localRotation,placeDuration));

        // allow clicking an ingot
        SetIngotCollidersEnabled(true);
        Stage = WorkflowStage.WaitingForIngotPick;

        Debug.Log("[IngotInteraction] Pliers picked up. Click an ingot.");
    }

    // --- pick up ingot ----------------------------

    private void PickUpIngot(int index)
    {
        _heldIngotIndex = index;
        _heldIngot = ingots[index];

        // Disable only the picked ingot's collider (others stay enabled for swapping)
        foreach (var col in _heldIngot.GetComponentsInChildren<Collider>())
            col.enabled = false;

        // Parent ingot to the grip point so it sits at the pliers head
        _heldIngot.transform.SetParent(_ingotGripPoint, true);

        Stage = WorkflowStage.IngotHeld;
        OnIngotPickedUp?.Invoke();

        Debug.Log($"[IngotInteraction] Picked up ingot [{index}] — {_heldIngot.name}. Press Next.");
    }

    // ---- swap ingot while still at table ----------------------------

    private void SwapIngot(int newIndex)
    {
        // Return the current ingot to its home instantly
        _heldIngot.transform.SetParent(null, true);
        _heldIngot.transform.position = _ingotHomePositions[_heldIngotIndex];
        _heldIngot.transform.rotation = _ingotHomeRotations[_heldIngotIndex];

        Debug.Log($"[IngotInteraction] Swapped from [{_heldIngotIndex}] to [{newIndex}] — {ingots[newIndex].name}.");

        // Re-enable colliders on all ingots so the unchosen ones remain clickable
        SetIngotCollidersEnabled(true);

        // Pick up the new one
        PickUpIngot(newIndex);
    }

    // --------- place ingot in forge ---------------------

    public void PlaceInForge()
    {
        if (_heldIngot == null) return;
        Stage = WorkflowStage.AtForge;

        StartCoroutine(PlaceAtSlot(forgeSlot, () =>
        {
            StartCoroutine(SnapToGripAfterClick());
            OnIngotPlacedInForge?.Invoke();
            Debug.Log("[IngotInteraction] Ingot placed in forge. Forge heating ON.");
        }));
    }

    // --------- place ingot on anvil -----------------------

    public void PlaceOnAnvil()
    {
        if (_heldIngot == null) return;
        Stage = WorkflowStage.AtAnvil;

        // Detach from grip point so it animates freely to the anvil slot
        _heldIngot.transform.SetParent(null, true);

        StartCoroutine(PlaceAtSlot(anvilSlot, () =>
    {
            StartCoroutine(SnapToGripAfterClick());
            OnIngotPlacedOnAnvil?.Invoke();
            Debug.Log("[IngotInteraction] Ingot placed on anvil.");
        }));
    }

    // --------- drop ingot into barrel -----------------------

    public void PlaceInBarrel()
    {
        if (_heldIngot == null) return;
        Stage = WorkflowStage.AtBarrel;

        // Detach from grip point so it animates freely into the barrel
        _heldIngot.transform.SetParent(null, true);

        StartCoroutine(PlaceAtSlot(barrelSlot, () =>
        {
            StartCoroutine(SnapToGripAfterClick());
            OnIngotPlacedInBarrel?.Invoke();
            Debug.Log("[IngotInteraction] Ingot quenched in barrel.");
        }));
    }

    // --------- return ingot to table -----------------------------

    public void ReturnIngotToTable()
    {
        if (_heldIngot == null) return;
        Stage = WorkflowStage.Returning;

        // Return pliers to their own cached home position/rotation
        if (pliers != null)
        {
            pliers.transform.SetParent(null, true);
            StartCoroutine(MoveToWorld(pliers.transform,_pliersHomePosition,_pliersHomeRotation,placeDuration));
        }

        // Use cached index before clearing state
        int returnIndex = _heldIngotIndex;
        _heldIngot.transform.SetParent(null, true); 

        StartCoroutine(PlaceAtHome(returnIndex, () =>
        {
            _heldIngot = null;
            _heldIngotIndex = -1;
            _pliersPicked = false;

            // Re-enable pliers collider for next round
            SetPliersColliderEnabled(true);

            Stage = WorkflowStage.WaitingForPliers;
            OnIngotReturned?.Invoke();
            Debug.Log("[IngotInteraction] Ingot returned. Ready for next round.");
        }));
    }

    // -------- helpers ----------------------------

    // Move an object (already unparented) to a world-space slot transform.
    private IEnumerator PlaceAtSlot(Transform slot, System.Action onDone)
    {
        yield return StartCoroutine(MoveToWorld(_heldIngot.transform,slot.position, slot.rotation, placeDuration));
        onDone?.Invoke();
    }

    // Move an object back to its original home position on the table.
    private IEnumerator PlaceAtHome(int index, System.Action onDone)
    {
        // Capture reference now , _heldIngot will be cleared in the callback
        Transform t = _heldIngot.transform;

        yield return StartCoroutine(MoveToWorld(t, _ingotHomePositions[index],_ingotHomeRotations[index], placeDuration));
        onDone?.Invoke();
    }

    private IEnumerator SnapToGripAfterClick()
    {
        _nextPressed = false;
        yield return new WaitUntil(() => _nextPressed);
        _heldIngot.transform.SetParent(_ingotGripPoint, true);
        _heldIngot.transform.localPosition = Vector3.zero;
        _heldIngot.transform.localRotation = Quaternion.identity;
        _nextPressed = false;
    }

    // move a transform to a world position/rotation over duration smoothly
    private IEnumerator MoveToWorld(Transform t, Vector3 targetPos, Quaternion targetRot, float duration)
    {
        Vector3 startPos = t.position;
        Quaternion startRot = t.rotation;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, elapsed / duration);
            t.position = Vector3.Lerp(startPos, targetPos, k);
            t.rotation = Quaternion.Slerp(startRot, targetRot, k);
            yield return null;
        }

        t.position = targetPos;
        t.rotation = targetRot;
    }

    //move a transform to a local position/rotation over duration smoothly
    private IEnumerator MoveToLocal(Transform t, Vector3 localPos, Quaternion localRot, float duration)
    {
        Vector3 startPos = t.localPosition;
        Quaternion startRot = t.localRotation;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, elapsed / duration);
            t.localPosition = Vector3.Lerp(startPos, localPos, k);
            t.localRotation = Quaternion.Slerp(startRot, localRot, k);
            yield return null;
        }

        t.localPosition = localPos;
        t.localRotation = localRot;
    }

    private void SetIngotCollidersEnabled(bool enabled)
    {
        foreach (var ingot in ingots)
        {
            // Enable all colliders on the ingot, including children
            foreach (var col in ingot.GetComponentsInChildren<Collider>())
                col.enabled = enabled;
        }
    }

    private void SetPliersColliderEnabled(bool enabled)
    {
        if (pliers == null) return;
        foreach (var col in pliers.GetComponentsInChildren<Collider>())
            col.enabled = enabled;
    }
}