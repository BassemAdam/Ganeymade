using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Moves a plain Camera through a sequence of waypoint stations using a quadratic Bezier curve for smooth animated transitions.
// Creates its own Next/Previous UI buttons at runtime 


public class BlacksmithMinigame : MonoBehaviour
{
    // Station Definition 

    [System.Serializable]
    public class Station
    {
        [Tooltip("The waypoint GameObject. Camera will match its position and rotation.")]
        public Transform waypoint;

        [Tooltip("Displayed in the station label (e.g. Table, Forge, Anvil, Barrel).")]
        public string label;

        [Tooltip("Bezier control point offset from the midpoint between stations. " +
                 "Raise Y for an arc, shift X/Z for a lateral sweep.")]
        public Vector3 controlPointOffset = new Vector3(0f, 1.5f, 0f);
    }

    // Inspector Fields 

    [Header("Stations (fill in order)")]
    public Station[] stations;

    [Header("Movement Settings")]
    [Tooltip("Duration of each camera move in seconds.")]
    public float travelDuration = 1.8f;

    [Tooltip("Speed curve along the path. Ease-in/out gives a natural feel.")]
    public AnimationCurve travelCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("UI Settings")]
    [Tooltip("Show a Previous button as well as Next.")]
    public bool showPreviousButton = false;

    [Tooltip("Font size for the buttons and label.")]
    public int uiFontSize = 18;

    [Tooltip("Tint color of the Next/Previous buttons.")]
    public Color buttonColor = new Color(0.15f, 0.15f, 0.15f, 0.85f);

    [Tooltip("Color of the button label text.")]
    public Color buttonTextColor = Color.white;

    [Tooltip("Color of the station label text.")]
    public Color stationLabelColor = Color.white;

    [Header("Ingot Interaction")]
    [Tooltip("Assign the IngotInteraction component. The navigator calls placement " +
             "methods on arrival and locks the Next button until each stage is ready.")]
    public IngotInteraction ingotInteraction;

    // Runtime State 

    [Header("State (read-only in Play)")]
    [SerializeField] private int  currentStationIndex = 0;
    [SerializeField] private bool isMoving  = false;

    //  Events 

    public event System.Action<int, Station> OnStationReached;
    public event System.Action<int, int> OnTravelStarted;

    // Private variables

    private Canvas _canvas;
    private Button _nextButton;
    private Button _prevButton;
    private TMP_Text _stationLabel;   // falls back to Text if TMP unavailable

    // Unity Lifecycle 

    private void Start()
    {
        if (stations == null || stations.Length == 0)
        {
            Debug.LogError("StationNavigator: No stations assigned in the Inspector.");
            return;
        }

        BuildUI();
        SnapToStation(currentStationIndex);

        // Re-evaluate the Next button whenever the ingot workflow advances
        if (ingotInteraction != null)
        {
            ingotInteraction.OnPliersPickedUp += () => RefreshUI();
            ingotInteraction.OnIngotPickedUp += ()=> RefreshUI();
            ingotInteraction.OnIngotPlacedInForge += ()=> RefreshUI();
            ingotInteraction.OnIngotReturned += () =>
            {
                currentStationIndex = 0;  
                RefreshUI();
            };
        }

        RefreshUI();
    }

    private void Update()
    {
        // Space or Enter = Next station 
        if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return))
            GoToNextStation();

        // Backspace = Previous station 
        if (showPreviousButton && Input.GetKeyDown(KeyCode.Backspace))
            GoToPreviousStation();
    }

    //  Public functions 

    public void GoToNextStation()
    {
        if (isMoving) 
            return;
        if (ingotInteraction != null) 
            ingotInteraction.NotifyNextPressed();
        int next = (currentStationIndex + 1) % stations.Length;
        TravelToStation(next);
    }

    public void GoToPreviousStation()
    {
        if (isMoving) 
            return;
        int prev = (currentStationIndex - 1 + stations.Length) % stations.Length;
        TravelToStation(prev);
    }

    public void GoToStation(int index)
    {
        if (isMoving || index < 0 || index >= stations.Length) 
            return;
        TravelToStation(index);
    }

    public bool IsMoving => isMoving;
    public int CurrentStationIndex => currentStationIndex;
    public string CurrentStationLabel => stations[currentStationIndex].label;

    //  UI building 

    private void BuildUI()
    {
        // create even system
        if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            GameObject esGO = new GameObject("EventSystem");
            esGO.AddComponent<UnityEngine.EventSystems.EventSystem>();
            esGO.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        // Create a Screen Space which is an overlay canvas owned by this script
        GameObject canvasGO = new GameObject("StationNavigator_Canvas");
        _canvas = canvasGO.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 100;

        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        canvasGO.AddComponent<GraphicRaycaster>();

        // Station label (top-center) 
        _stationLabel = CreateLabel(canvasGO,"StationLabel","", new Vector2(0.5f, 1f), new Vector2(0f, -40f),new Vector2(400f, 50f),
            uiFontSize + 2, stationLabelColor);

        // Next button (bottom-right) 
        _nextButton = CreateButton(canvasGO,"NextButton", "Next ->",new Vector2(1f, 0f),new Vector2(-160f, 60f),new Vector2(140f, 50f));
        _nextButton.onClick.AddListener(GoToNextStation);

        // Previous button (bottom-left)
        if (showPreviousButton)
        {
            _prevButton = CreateButton(canvasGO,"PrevButton","<-  Back",new Vector2(0f, 0f),new Vector2(160f, 60f), new Vector2(140f, 50f));
            _prevButton.onClick.AddListener(GoToPreviousStation);
        }
    }

    // Create a TextMeshPro label 
    private TMP_Text CreateLabel(GameObject parent, string name, string text,Vector2 anchor, Vector2 anchoredPos,
        Vector2 size, int fontSize, Color color)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        TMP_Text tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;

        return tmp;
    }

    // Create a styled button with a background image and label.
    private Button CreateButton(GameObject parent, string name, string label, Vector2 anchor, Vector2 anchoredPos, Vector2 size)
    {
        // Container
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        // Background
        Image img = go.AddComponent<Image>();
        img.color = buttonColor;

        // Rounded feel via sprite (falls back to plain rect if no sprite available)
        img.type = Image.Type.Sliced;

        // Button component
        Button btn = go.AddComponent<Button>();

        ColorBlock cb = btn.colors;
        cb.normalColor = buttonColor;
        cb.highlightedColor= buttonColor * 1.25f;
        cb.pressedColor = buttonColor * 0.75f;
        cb.selectedColor = buttonColor;
        cb.fadeDuration = 0.1f;
        btn.colors = cb;

        // Text child
        GameObject textGO = new GameObject("Label");
        textGO.transform.SetParent(go.transform, false);

        RectTransform trt = textGO.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;

        TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = uiFontSize;
        tmp.color = buttonTextColor;
        tmp.alignment = TextAlignmentOptions.Center;

        return btn;
    }

    //  UI Refresh 
    private void RefreshUI()
    {
        if (_stationLabel != null)
            _stationLabel.text = GetInstructionText();

        bool nextAllowed = !isMoving && IsNextAllowedByIngotStage();

        if (_nextButton != null)
            _nextButton.interactable = nextAllowed;

        if (_prevButton != null)
            _prevButton.interactable = !isMoving;
    }

    private string GetInstructionText()
    {
        if (ingotInteraction == null)
            return stations[currentStationIndex].label;

        switch (currentStationIndex)
        {
            case 0:
                switch (ingotInteraction.Stage)
                {
                    case IngotInteraction.WorkflowStage.WaitingForPliers:
                        return "Pick up the pliers to start";
                    case IngotInteraction.WorkflowStage.WaitingForIngotPick:
                        return "Pick silver, gold, copper, platinum, steel or bronze ingot";
                    case IngotInteraction.WorkflowStage.IngotHeld:
                        IngotData heldData = ingotInteraction.GetHeldIngotData();
                        string matInfo = heldData != null
                            ? $"{heldData.materialName} | Diffusivity: {heldData.DiffusivityFormatted()}. " : "";
                        return $"{matInfo}Press Next to head to the forge";
                    default:
                        return stations[currentStationIndex].label;
                }
            case 1:
                return "The ingot is heating in the forge, press Next to continue";
            case 2:
                return "The ingot has been returned. Press Next to start again";
            default:
                return stations[currentStationIndex].label;
        }
    }

    // Returns true when the ingot workflow has completed whatever is needed at the current station and the user may press Next.
    // 0 = Table : locked until user picks an ingot 
    // 1 = Forge : always unlocked 
    // 2 = Table : locked while ReturnIngotToTable animation plays (Returning stage)
    private bool IsNextAllowedByIngotStage()
    {
        if (ingotInteraction == null) 
            return true;   

        switch (currentStationIndex)
        {
            case 0:   // must have picked up an ingot before moving to forge
                return ingotInteraction.Stage == IngotInteraction.WorkflowStage.IngotHeld;
            case 2:   // locked while the return animation is playing
                return ingotInteraction.Stage == IngotInteraction.WorkflowStage.WaitingForPliers;
            default:  
                return true;
        }
    }

    //  Movement 

    private void TravelToStation(int targetIndex)
    {
        if (targetIndex == currentStationIndex) 
            return;
        OnTravelStarted?.Invoke(currentStationIndex, targetIndex);
        StartCoroutine(MoveAlongBezier(currentStationIndex, targetIndex));
        currentStationIndex = targetIndex;
    }

    private IEnumerator MoveAlongBezier(int fromIndex, int toIndex)
    {
        isMoving = true;
        RefreshUI();

        Vector3 startPos = transform.position;
        Vector3 endPos = stations[toIndex].waypoint.position;
        Vector3 control = (startPos + endPos) * 0.5f + stations[toIndex].controlPointOffset;
        Quaternion startRot = transform.rotation;
        Quaternion endRot = stations[toIndex].waypoint.rotation;

        float elapsed = 0f;
        while (elapsed < travelDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / travelDuration);
            float curved = travelCurve.Evaluate(t);
            float om = 1f - curved;

            // Quadratic Bezier position
            transform.position = (om * om) * startPos + (2f * om * curved) * control + (curved * curved) * endPos;

            // Independent rotation slerp
            transform.rotation = Quaternion.Slerp(startRot, endRot, curved);

            yield return null;
        }

        SnapToStation(toIndex);
        isMoving = false;

        HandleIngotPlacement(toIndex);
        RefreshUI();
        OnStationReached?.Invoke(toIndex, stations[toIndex]);
    }

    // Called immediately after the camera arrives at a station.
    private void HandleIngotPlacement(int stationIndex)
    {
        if (ingotInteraction == null) return;

        switch (stationIndex)
        {
            case 1:   //at Forge
                ingotInteraction.PlaceInForge();
                break;
            case 2:   //back at Table
                ingotInteraction.ReturnIngotToTable();
                break;
        }
    }

    private void SnapToStation(int index)
    {
        if (stations[index].waypoint == null)
        {
            Debug.LogWarning($"StationNavigator: Waypoint for '{stations[index].label}' is null.");
            return;
        }
        transform.position = stations[index].waypoint.position;
        transform.rotation = stations[index].waypoint.rotation;
    }

    //  Scene Gizmos

    private void OnDrawGizmos()
    {
        if (stations == null || stations.Length < 2) 
            return;

        for (int i = 0; i < stations.Length; i++)
        {
            int next = (i + 1) % stations.Length;
            if (stations[i].waypoint == null || stations[next].waypoint == null) 
                continue;

            Vector3 s = stations[i].waypoint.position;
            Vector3 e = stations[next].waypoint.position;
            Vector3 cp = (s + e) * 0.5f + stations[next].controlPointOffset;

            // Bezier path
            Gizmos.color = Color.yellow;
            Vector3 prev = s;
            for (int step = 1; step <= 24; step++)
            {
                float t  = step / 24f;
                float om = 1f - t;
                Vector3 pt = (om * om) * s + (2f * om * t) * cp + (t * t) * e;
                Gizmos.DrawLine(prev, pt);
                prev = pt;
            }

            // Waypoint spheres
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(stations[i].waypoint.position, 0.08f);

            // Control point
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.7f);
            Gizmos.DrawWireSphere(cp, 0.05f);
        }
    }
}