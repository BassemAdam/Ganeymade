using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Manages the full ingot lifecycle:
///   Table (pick pliers → pick ingot) → Forge → Anvil → Barrel → Table (return)
///
/// SETUP:
/// 1. Attach to a manager GameObject (or the Camera).
/// 2. Assign the 3 ingot GameObjects, the pliers, and the 4 placement
///    transforms (ForgeSlot, AnvilSlot, BarrelSlot) in the Inspector.
/// 3. The StationNavigator on the Camera drives GoToForge / GoToAnvil etc.
///    via the public methods below — wire those up in the Inspector or
///    subscribe to OnStationReached.
/// </summary>
public class IngotInteraction : MonoBehaviour
{
    // ── References ─────────────────────────────────────────────────────────────

    [Header("Ingots (assign all 3)")]
    public GameObject[] ingots;                  // Gold, Copper, Silver

    [Header("Pliers")]
    public GameObject pliers;

    [Header("Placement Slots (empty GameObjects marking where ingot sits)")]
    public Transform forgeSlot;                  // where ingot rests inside forge
    public Transform anvilSlot;                  // where ingot rests on anvil
    public Transform barrelSlot;                 // where ingot sinks into barrel

    [Header("Hold Settings")]
    [Tooltip("Distance in front of camera when ingot+pliers are held.")]
    public float holdDistance = 0.55f;
    [Tooltip("How far below the camera centre the hold point sits.")]
    public float holdVerticalOffset = -0.18f;
    [Tooltip("How quickly the held object tracks the hold point.")]
    public float holdFollowSpeed = 18f;

    [Header("Forge Reference")]
    [Tooltip("The VoxelSolidMaterial on the forge. isContinuousHeatSource is " +
             "enabled/disabled when the ingot enters/leaves.")]
    public VoxelSolidMaterial forgeHeatSource;

    [Header("Animation")]
    [Tooltip("Time in seconds for the ingot to travel to a slot.")]
    public float placeDuration = 0.6f;

    // ── State ──────────────────────────────────────────────────────────────────

    public enum WorkflowStage
    {
        WaitingForPliers,
        WaitingForIngotPick,
        IngotHeld,
        AtForge,
        AtAnvil,
        AtBarrel,
        Returning
    }

    public WorkflowStage Stage { get; private set; } = WorkflowStage.WaitingForPliers;

    private GameObject    _heldIngot;
    private int           _heldIngotIndex   = -1;
    private Vector3[]     _ingotHomePositions;
    private Quaternion[]  _ingotHomeRotations;
    private Transform     _holdPoint;          // auto-created child of Camera
    private Camera        _cam;
    private bool          _pliersPicked       = false;

    // ── Events (StationNavigator listens to these to know when to unlock Next) ─

    public event System.Action OnIngotPickedUp;
    public event System.Action OnIngotPlacedInForge;
    public event System.Action OnIngotPlacedOnAnvil;
    public event System.Action OnIngotPlacedInBarrel;
    public event System.Action OnIngotReturned;

    // ── Unity Lifecycle ────────────────────────────────────────────────────────

    private void Start()
    {
        _cam = Camera.main;
        if (_cam == null) _cam = FindObjectOfType<Camera>();

        // Create the hold point as a child of the camera
        GameObject hp = new GameObject("IngotHoldPoint");
        hp.transform.SetParent(_cam.transform, false);
        hp.transform.localPosition = new Vector3(0f, holdVerticalOffset, holdDistance);
        hp.transform.localRotation = Quaternion.identity;
        _holdPoint = hp.transform;

        // Cache home transforms for all ingots
        _ingotHomePositions = new Vector3[ingots.Length];
        _ingotHomeRotations = new Quaternion[ingots.Length];
        for (int i = 0; i < ingots.Length; i++)
        {
            _ingotHomePositions[i] = ingots[i].transform.position;
            _ingotHomeRotations[i] = ingots[i].transform.rotation;
        }

        // Pliers start clickable; ingots are locked until pliers are picked up
        SetIngotCollidersEnabled(false);
        SetPliersColliderEnabled(true);

        if (forgeHeatSource != null)
            forgeHeatSource.isContinuousHeatSource = false;
    }

    private void Update()
    {
        // Smoothly move held object toward the hold point every frame
        if (_heldIngot != null && Stage == WorkflowStage.IngotHeld)
            _heldIngot.transform.position = Vector3.Lerp(
                _heldIngot.transform.position,
                _holdPoint.position,
                Time.deltaTime * holdFollowSpeed);

        HandleClicks();
    }

    // ── Click Handling ─────────────────────────────────────────────────────────

    private void HandleClicks()
    {
        if (!Input.GetMouseButtonDown(0)) return;

        // ── Block click if it landed on a UI element (e.g. the Next button) ───
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            Debug.Log("[IngotInteraction] Click blocked — pointer is over UI.");
            return;
        }

        // ── Diagnostic: draw the ray in the Scene view for 3 seconds ──────────
        Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
        Debug.DrawRay(ray.origin, ray.direction * 50f, Color.red, 3f);
        Debug.Log($"[IngotInteraction] Ray origin={ray.origin:F2}  dir={ray.direction:F2}  " +
                  $"mousePos={Input.mousePosition}  cursorLocked={Cursor.lockState}");

        // Cast against ALL layers so we can see what (if anything) is in the way
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
            if (!h.collider.isTrigger) { hit = h; foundHit = true; break; }
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
        }
    }

    // ── Step 0 : Pick up pliers ────────────────────────────────────────────────

    private void PickUpPliers()
    {
        _pliersPicked = true;

        // Pliers are now held — disable their collider so it doesn't interfere
        SetPliersColliderEnabled(false);

        // Parent pliers to the hold point so they ride with the camera
        pliers.transform.SetParent(_holdPoint, true);
        StartCoroutine(MoveToLocal(pliers.transform, Vector3.zero, Quaternion.identity, placeDuration));

        // Now let the user click an ingot
        SetIngotCollidersEnabled(true);
        Stage = WorkflowStage.WaitingForIngotPick;

        Debug.Log("[IngotInteraction] Pliers picked up. Click an ingot.");
    }

    // ── Step 1 : Pick up ingot ─────────────────────────────────────────────────

    private void PickUpIngot(int index)
    {
        _heldIngotIndex = index;
        _heldIngot      = ingots[index];

        // Disable other ingots so only the chosen one matters going forward
        for (int i = 0; i < ingots.Length; i++)
            if (i != index) ingots[i].SetActive(false);

        SetIngotCollidersEnabled(false);

        // Smoothly float ingot up to hold point (slightly behind pliers)
        _heldIngot.transform.SetParent(_holdPoint, true);

        Stage = WorkflowStage.IngotHeld;
        OnIngotPickedUp?.Invoke();

        Debug.Log($"[IngotInteraction] Picked up ingot [{index}] — {_heldIngot.name}. Press Next.");
    }

    // ── Step 2 : Place ingot in forge (called by StationNavigator on arrival) ──

    public void PlaceInForge()
    {
        if (_heldIngot == null) return;
        Stage = WorkflowStage.AtForge;

        StartCoroutine(PlaceAtSlot(forgeSlot, () =>
        {
            // Activate forge heat source
            if (forgeHeatSource != null)
                forgeHeatSource.isContinuousHeatSource = true;

            OnIngotPlacedInForge?.Invoke();
            Debug.Log("[IngotInteraction] Ingot placed in forge. Forge heating ON.");
        }));
    }

    // ── Step 3 : Place ingot on anvil ──────────────────────────────────────────

    public void PlaceOnAnvil()
    {
        if (_heldIngot == null) return;
        Stage = WorkflowStage.AtAnvil;

        // Detach from hold point so it rests on the anvil independently
        _heldIngot.transform.SetParent(null, true);

        // Forge no longer heating
        if (forgeHeatSource != null)
            forgeHeatSource.isContinuousHeatSource = false;

        StartCoroutine(PlaceAtSlot(anvilSlot, () =>
        {
            OnIngotPlacedOnAnvil?.Invoke();
            Debug.Log("[IngotInteraction] Ingot placed on anvil.");
        }));
    }

    // ── Step 4 : Drop ingot into barrel ───────────────────────────────────────

    public void PlaceInBarrel()
    {
        if (_heldIngot == null) return;
        Stage = WorkflowStage.AtBarrel;

        _heldIngot.transform.SetParent(null, true);

        StartCoroutine(PlaceAtSlot(barrelSlot, () =>
        {
            OnIngotPlacedInBarrel?.Invoke();
            Debug.Log("[IngotInteraction] Ingot quenched in barrel.");
        }));
    }

    // ── Step 5 : Return ingot to table ────────────────────────────────────────

    public void ReturnIngotToTable()
    {
        if (_heldIngot == null) return;
        Stage = WorkflowStage.Returning;

        _heldIngot.transform.SetParent(null, true);

        // Drop pliers back on table too
        if (pliers != null)
        {
            pliers.transform.SetParent(null, true);
            StartCoroutine(MoveToWorld(pliers.transform,
                _ingotHomePositions[_heldIngotIndex] + Vector3.right * 0.15f,
                _ingotHomeRotations[_heldIngotIndex],
                placeDuration));
        }

        StartCoroutine(PlaceAtHome(_heldIngotIndex, () =>
        {
            // Restore hidden ingots
            for (int i = 0; i < ingots.Length; i++)
                ingots[i].SetActive(true);

            _heldIngot      = null;
            _heldIngotIndex = -1;
            _pliersPicked   = false;

            Stage = WorkflowStage.WaitingForPliers;
            OnIngotReturned?.Invoke();
            Debug.Log("[IngotInteraction] Ingot returned. Ready for next round.");
        }));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// Moves an object (already unparented) to a world-space slot transform.
    private IEnumerator PlaceAtSlot(Transform slot, System.Action onDone)
    {
        yield return StartCoroutine(MoveToWorld(
            _heldIngot.transform,
            slot.position,
            slot.rotation,
            placeDuration));

        onDone?.Invoke();
    }

    /// Moves an object back to its original home position on the table.
    private IEnumerator PlaceAtHome(int index, System.Action onDone)
    {
        yield return StartCoroutine(MoveToWorld(
            _heldIngot.transform,
            _ingotHomePositions[index],
            _ingotHomeRotations[index],
            placeDuration));

        onDone?.Invoke();
    }

    /// Smoothly moves a transform to a world position/rotation over duration.
    private IEnumerator MoveToWorld(Transform t, Vector3 targetPos, Quaternion targetRot, float duration)
    {
        Vector3    startPos = t.position;
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

    /// Smoothly moves a transform to a local position/rotation over duration.
    private IEnumerator MoveToLocal(Transform t, Vector3 localPos, Quaternion localRot, float duration)
    {
        Vector3    startPos = t.localPosition;
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