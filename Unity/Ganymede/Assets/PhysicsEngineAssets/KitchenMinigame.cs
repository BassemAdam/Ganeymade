using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.Universal;
using TMPro;

/// <summary>
/// Kitchen minigame controller. Fixed camera stations with hotkey navigation.
/// Builds a Canvas + TMP UI at runtime. No prefabs or manual setup needed for the UI.
///
/// Setup:
/// 1. Attach to any GameObject (e.g. an empty "GameManager").
/// 2. Assign the camera, tap WaterSource, and stove heat source references.
/// 3. Create 3 empty GameObjects as camera anchors (Overview, Tap, Stove)
///    and position/rotate them where you want the camera to look from.
/// 4. Assign them in the inspector.
/// </summary>
public class KitchenMinigame : MonoBehaviour
{
    public enum Station { Overview, Tap, Stove }

    [Header("Camera")]
    [Tooltip("The main camera to control. Auto-found if empty.")]
    public Camera mainCamera;

    [Tooltip("How fast the camera lerps to the target station.")]
    [Range(1f, 20f)] public float cameraLerpSpeed = 8f;

    [Header("Camera Anchors (position + rotation)")]
    public Transform overviewAnchor;
    public Transform tapAnchor;
    public Transform stoveAnchor;

    [Header("Interactables")]
    public WaterSource tapSource;
    public GameObject stoveObject;

    [Header("Temperature")]
    [Tooltip("Min stove temperature for the UI slider")]
    public float minStoveTemp = 0f;
    [Tooltip("Max stove temperature for the UI slider")]
    public float maxStoveTemp = 1000f;
    public KeyCode goToTap = KeyCode.Alpha1;
    public KeyCode goToStove = KeyCode.Alpha2;

    [Header("Hotkeys - Tap Station")]
    public KeyCode tapToggle = KeyCode.E;
    public KeyCode tapGoBack = KeyCode.Backspace;

    [Header("Hotkeys - Stove Station")]
    public KeyCode stoveToggle = KeyCode.E;
    public KeyCode stoveGoBack = KeyCode.Backspace;

    // ── State ────────────────────────────────────────────────────────────

    Station _currentStation = Station.Overview;
    Transform _targetAnchor;

    bool _tapOn;
    bool _stoveOn;

    FirstPersonCamera _fpsCamera;
    VoxelHeatSource _voxelHeat;
    HeatSourceObj _heatSourceObj;
    VoxelSolidMaterial _solidMat;

    float _stoveTemp;
    float _lastTempRead = -999f;

    // ── UI references (built at runtime) ────────────────────────────────

    GameObject _overviewPanel;
    GameObject _tapPanel;
    GameObject _stovePanel;

    TMP_Text _tapStatusText;
    TMP_Text _stoveStatusText;
    TMP_Text _stoveTempText;
    TMP_Text _tapToggleBtnText;
    TMP_Text _stoveToggleBtnText;
    TMP_Text _stoveTempSliderLabel;
    Slider _stoveTempSlider;

    // ── Lifecycle ────────────────────────────────────────────────────────

    void Start()
    {
        if (mainCamera == null)
            mainCamera = Camera.main;

        if (mainCamera != null)
        {
            _fpsCamera = mainCamera.GetComponent<FirstPersonCamera>();
            if (_fpsCamera != null)
                _fpsCamera.enabled = false;
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (stoveObject != null)
        {
            _voxelHeat = stoveObject.GetComponent<VoxelHeatSource>();
            _heatSourceObj = stoveObject.GetComponent<HeatSourceObj>();
            _solidMat = stoveObject.GetComponent<VoxelSolidMaterial>();
        }

        if (tapSource != null)
            _tapOn = tapSource.isActive;

        _stoveOn = false;
        SetStoveState(false);
        _stoveTemp = GetStoveTemperature();

        // Build UI after state is read so slider starts at the correct value
        BuildUI();
        ShowStation(_currentStation);

        _targetAnchor = overviewAnchor;
        if (_targetAnchor != null && mainCamera != null)
        {
            mainCamera.transform.position = _targetAnchor.position;
            mainCamera.transform.rotation = _targetAnchor.rotation;
        }
    }

    void Update()
    {
        HandleInput();
        LerpCamera();
        ReadTemperatures();
        RefreshUI();
    }

    // ── Input ────────────────────────────────────────────────────────────

    void HandleInput()
    {
        switch (_currentStation)
        {
            case Station.Overview:
                if (Input.GetKeyDown(goToTap)) GoTo(Station.Tap);
                if (Input.GetKeyDown(goToStove)) GoTo(Station.Stove);
                break;
            case Station.Tap:
                if (Input.GetKeyDown(tapToggle)) ToggleTap();
                if (Input.GetKeyDown(tapGoBack)) GoTo(Station.Overview);
                break;
            case Station.Stove:
                if (Input.GetKeyDown(stoveToggle)) ToggleStove();
                if (Input.GetKeyDown(stoveGoBack)) GoTo(Station.Overview);
                break;
        }
    }

    void GoTo(Station station)
    {
        _currentStation = station;
        _targetAnchor = station switch
        {
            Station.Tap => tapAnchor,
            Station.Stove => stoveAnchor,
            _ => overviewAnchor
        };
        ShowStation(station);
    }

    void LerpCamera()
    {
        if (_targetAnchor == null || mainCamera == null) return;
        float t = 1f - Mathf.Exp(-cameraLerpSpeed * Time.deltaTime);
        mainCamera.transform.position = Vector3.Lerp(mainCamera.transform.position, _targetAnchor.position, t);
        mainCamera.transform.rotation = Quaternion.Slerp(mainCamera.transform.rotation, _targetAnchor.rotation, t);
    }

    // ── Controls ─────────────────────────────────────────────────────────

    void ToggleTap()
    {
        if (tapSource == null) return;
        _tapOn = !_tapOn;
        tapSource.isActive = _tapOn;
        if (_tapOn && tapSource.tapExhausted)
            tapSource.tapExhausted = false;
    }

    void ToggleStove()
    {
        _stoveOn = !_stoveOn;
        SetStoveState(_stoveOn);
    }

    bool GetStoveState()
    {
        if (_voxelHeat != null) return _voxelHeat.enabled;
        if (_heatSourceObj != null) return _heatSourceObj.enabled;
        if (_solidMat != null) return _solidMat.enabled;
        return false;
    }

    void SetStoveState(bool on)
    {
        if (_voxelHeat != null) _voxelHeat.enabled = on;
        if (_heatSourceObj != null) _heatSourceObj.enabled = on;
        if (_solidMat != null) _solidMat.enabled = on;
    }

    float GetStoveTemperature()
    {
        if (_voxelHeat != null) return _voxelHeat.temperature;
        if (_heatSourceObj != null) return _heatSourceObj.temperature;
        if (_solidMat != null) return _solidMat.temperature;
        return 0f;
    }

    void SetStoveTemperature(float temp)
    {
        if (_voxelHeat != null) _voxelHeat.temperature = temp;
        if (_heatSourceObj != null) _heatSourceObj.temperature = temp;
        if (_solidMat != null) _solidMat.temperature = temp;
        _stoveTemp = temp;
    }

    void ReadTemperatures()
    {
        if (Time.time - _lastTempRead < 0.5f) return;
        _lastTempRead = Time.time;
        _stoveTemp = GetStoveTemperature();
    }

    // ── Show / Hide panels ──────────────────────────────────────────────

    void ShowStation(Station s)
    {
        if (_overviewPanel != null) _overviewPanel.SetActive(s == Station.Overview);
        if (_tapPanel != null) _tapPanel.SetActive(s == Station.Tap);
        if (_stovePanel != null) _stovePanel.SetActive(s == Station.Stove);
    }

    void RefreshUI()
    {
        if (_tapStatusText != null)
        {
            string col = _tapOn ? "#33FF66" : "#FF4444";
            _tapStatusText.text = $"Status:  <color={col}><b>{(_tapOn ? "ON" : "OFF")}</b></color>";
        }
        if (_tapToggleBtnText != null)
            _tapToggleBtnText.text = _tapOn ? $"[{KeyName(tapToggle)}]  Turn OFF" : $"[{KeyName(tapToggle)}]  Turn ON";

        if (_stoveStatusText != null)
        {
            string col = _stoveOn ? "#33FF66" : "#FF4444";
            _stoveStatusText.text = $"Heat:  <color={col}><b>{(_stoveOn ? "ON" : "OFF")}</b></color>";
        }
        if (_stoveTempText != null)
            _stoveTempText.text = $"Temp:  <color=#FFD633><b>{_stoveTemp:F1} C</b></color>";
        if (_stoveTempSliderLabel != null)
            _stoveTempSliderLabel.text = $"{_stoveTemp:F0} C";
        if (_stoveToggleBtnText != null)
            _stoveToggleBtnText.text = _stoveOn ? $"[{KeyName(stoveToggle)}]  Turn OFF" : $"[{KeyName(stoveToggle)}]  Turn ON";
    }

    // ── Build UI at runtime ─────────────────────────────────────────────

    void BuildUI()
    {
        Debug.Log("[KitchenMinigame] Building UI...");

        // Create a dedicated UI layer (use layer 5 = "UI" which Unity reserves)
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0) uiLayer = 5;

        // Create an overlay camera that ONLY renders UI.
        // URP camera stacking ensures it renders after ALL render features.
        var uiCamGO = new GameObject("KitchenUI_OverlayCamera");
        var uiCam = uiCamGO.AddComponent<Camera>();
        uiCam.clearFlags = CameraClearFlags.Nothing;
        uiCam.cullingMask = 1 << uiLayer;
        uiCam.depth = 100;
        uiCam.orthographic = true;

        // In URP, set this camera as Overlay and add to main camera stack
        var uiCamData = uiCam.GetUniversalAdditionalCameraData();
        uiCamData.renderType = CameraRenderType.Overlay;

        if (mainCamera == null) mainCamera = Camera.main;
        if (mainCamera != null)
        {
            var baseCamData = mainCamera.GetUniversalAdditionalCameraData();
            baseCamData.cameraStack.Add(uiCam);
            Debug.Log("[KitchenMinigame] Added overlay camera to stack of: " + mainCamera.name);
        }

        // Canvas in ScreenSpaceCamera mode, rendered by the overlay camera
        var canvasGO = new GameObject("KitchenUI_Canvas");
        canvasGO.layer = uiLayer;
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = uiCam;
        canvas.sortingOrder = 100;
        canvas.planeDistance = 1f;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        canvasGO.AddComponent<GraphicRaycaster>();

        Debug.Log("[KitchenMinigame] Canvas created with overlay camera");

        // EventSystem
        if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var esGO = new GameObject("EventSystem");
            esGO.AddComponent<UnityEngine.EventSystems.EventSystem>();
            esGO.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        // ── Overview panel ──
        _overviewPanel = CreatePanel(canvasGO.transform, "OverviewPanel", uiLayer);
        AddTitle(_overviewPanel.transform, "KITCHEN", uiLayer);
        AddButton(_overviewPanel.transform, $"[{KeyName(goToTap)}]  Go to Tap", () => GoTo(Station.Tap), uiLayer);
        AddButton(_overviewPanel.transform, $"[{KeyName(goToStove)}]  Go to Stove", () => GoTo(Station.Stove), uiLayer);

        // ── Tap panel ──
        _tapPanel = CreatePanel(canvasGO.transform, "TapPanel", uiLayer);
        AddTitle(_tapPanel.transform, "TAP", uiLayer);
        _tapStatusText = AddLabel(_tapPanel.transform, "Status: OFF", uiLayer);
        _tapToggleBtnText = AddButton(_tapPanel.transform, $"[{KeyName(tapToggle)}]  Turn ON", () => ToggleTap(), uiLayer);
        AddButton(_tapPanel.transform, $"[{KeyName(tapGoBack)}]  Back", () => GoTo(Station.Overview), uiLayer);

        // ── Stove panel ──
        _stovePanel = CreatePanel(canvasGO.transform, "StovePanel", uiLayer);
        AddTitle(_stovePanel.transform, "STOVE", uiLayer);
        _stoveStatusText = AddLabel(_stovePanel.transform, "Heat: OFF", uiLayer);
        _stoveTempText = AddLabel(_stovePanel.transform, "Temp: 0.0 C", uiLayer);
        AddSlider(_stovePanel.transform, uiLayer, minStoveTemp, maxStoveTemp, _stoveTemp,
            out _stoveTempSlider, out _stoveTempSliderLabel, (val) => SetStoveTemperature(val));
        _stoveToggleBtnText = AddButton(_stovePanel.transform, $"[{KeyName(stoveToggle)}]  Turn ON", () => ToggleStove(), uiLayer);
        AddButton(_stovePanel.transform, $"[{KeyName(stoveGoBack)}]  Back", () => GoTo(Station.Overview), uiLayer);

        Debug.Log("[KitchenMinigame] UI built successfully.");
    }

    GameObject CreatePanel(Transform parent, string name, int layer)
    {
        var go = new GameObject(name);
        go.layer = layer;
        go.AddComponent<RectTransform>();
        go.AddComponent<CanvasRenderer>();
        go.transform.SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.05f, 0.05f, 0.12f, 0.88f);

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(16, 16, 14, 14);
        vlg.spacing = 6;
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(20, -20);
        rt.sizeDelta = new Vector2(320, 0);

        return go;
    }

    void AddTitle(Transform parent, string text, int layer)
    {
        var go = new GameObject("Title");
        go.layer = layer;
        go.AddComponent<RectTransform>();
        go.AddComponent<CanvasRenderer>();
        go.transform.SetParent(parent, false);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 26;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = new Color(0.92f, 0.95f, 1f);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.richText = true;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 36;
    }

    TMP_Text AddLabel(Transform parent, string text, int layer)
    {
        var go = new GameObject("Label");
        go.layer = layer;
        go.AddComponent<RectTransform>();
        go.AddComponent<CanvasRenderer>();
        go.transform.SetParent(parent, false);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 20;
        tmp.color = new Color(0.85f, 0.88f, 0.92f);
        tmp.richText = true;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 30;

        return tmp;
    }

    TMP_Text AddButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick, int layer)
    {
        var go = new GameObject("Button");
        go.layer = layer;
        go.AddComponent<RectTransform>();
        go.AddComponent<CanvasRenderer>();
        go.transform.SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.18f, 0.22f, 0.32f, 1f);

        var btn = go.AddComponent<Button>();
        var colors = btn.colors;
        colors.highlightedColor = new Color(0.28f, 0.35f, 0.5f);
        colors.pressedColor = new Color(0.15f, 0.18f, 0.25f);
        btn.colors = colors;
        btn.onClick.AddListener(onClick);

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 36;

        // Text child
        var textGO = new GameObject("Text");
        textGO.layer = layer;
        textGO.AddComponent<RectTransform>();
        textGO.AddComponent<CanvasRenderer>();
        textGO.transform.SetParent(go.transform, false);

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = 18;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;

        var textRT = textGO.GetComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero;
        textRT.offsetMax = Vector2.zero;

        return tmp;
    }

    void AddSlider(Transform parent, int layer, float min, float max, float initial,
        out Slider sliderOut, out TMP_Text labelOut, UnityEngine.Events.UnityAction<float> onChanged)
    {
        // Row container
        var row = new GameObject("SliderRow");
        row.layer = layer;
        row.AddComponent<RectTransform>();
        row.transform.SetParent(parent, false);

        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = true;

        var rowLE = row.AddComponent<LayoutElement>();
        rowLE.preferredHeight = 30;

        // "Set:" label
        var setLabel = new GameObject("SetLabel");
        setLabel.layer = layer;
        setLabel.AddComponent<RectTransform>();
        setLabel.AddComponent<CanvasRenderer>();
        setLabel.transform.SetParent(row.transform, false);
        var setTmp = setLabel.AddComponent<TextMeshProUGUI>();
        setTmp.text = "Set:";
        setTmp.fontSize = 16;
        setTmp.color = new Color(0.85f, 0.88f, 0.92f);
        setTmp.alignment = TextAlignmentOptions.MidlineLeft;
        var setLE = setLabel.AddComponent<LayoutElement>();
        setLE.preferredWidth = 35;

        // Slider
        var sliderGO = new GameObject("Slider");
        sliderGO.layer = layer;
        var sliderRT = sliderGO.AddComponent<RectTransform>();
        sliderGO.transform.SetParent(row.transform, false);
        var sliderLE = sliderGO.AddComponent<LayoutElement>();
        sliderLE.flexibleWidth = 1;

        // Background
        var bgGO = new GameObject("Background");
        bgGO.layer = layer;
        bgGO.AddComponent<RectTransform>();
        bgGO.AddComponent<CanvasRenderer>();
        bgGO.transform.SetParent(sliderGO.transform, false);
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0.15f, 0.15f, 0.2f, 1f);
        var bgRT = bgGO.GetComponent<RectTransform>();
        bgRT.anchorMin = new Vector2(0, 0.35f);
        bgRT.anchorMax = new Vector2(1, 0.65f);
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;

        // Fill area
        var fillArea = new GameObject("Fill Area");
        fillArea.layer = layer;
        fillArea.AddComponent<RectTransform>();
        fillArea.transform.SetParent(sliderGO.transform, false);
        var fillAreaRT = fillArea.GetComponent<RectTransform>();
        fillAreaRT.anchorMin = new Vector2(0, 0.35f);
        fillAreaRT.anchorMax = new Vector2(1, 0.65f);
        fillAreaRT.offsetMin = Vector2.zero;
        fillAreaRT.offsetMax = Vector2.zero;

        var fillGO = new GameObject("Fill");
        fillGO.layer = layer;
        fillGO.AddComponent<RectTransform>();
        fillGO.AddComponent<CanvasRenderer>();
        fillGO.transform.SetParent(fillArea.transform, false);
        var fillImg = fillGO.AddComponent<Image>();
        fillImg.color = new Color(1f, 0.55f, 0.1f, 1f);
        var fillRT = fillGO.GetComponent<RectTransform>();
        fillRT.anchorMin = Vector2.zero;
        fillRT.anchorMax = Vector2.one;
        fillRT.offsetMin = Vector2.zero;
        fillRT.offsetMax = Vector2.zero;

        // Handle slide area
        var handleArea = new GameObject("Handle Slide Area");
        handleArea.layer = layer;
        handleArea.AddComponent<RectTransform>();
        handleArea.transform.SetParent(sliderGO.transform, false);
        var handleAreaRT = handleArea.GetComponent<RectTransform>();
        handleAreaRT.anchorMin = Vector2.zero;
        handleAreaRT.anchorMax = Vector2.one;
        handleAreaRT.offsetMin = Vector2.zero;
        handleAreaRT.offsetMax = Vector2.zero;

        var handleGO = new GameObject("Handle");
        handleGO.layer = layer;
        handleGO.AddComponent<RectTransform>();
        handleGO.AddComponent<CanvasRenderer>();
        handleGO.transform.SetParent(handleArea.transform, false);
        var handleImg = handleGO.AddComponent<Image>();
        handleImg.color = Color.white;
        var handleRT = handleGO.GetComponent<RectTransform>();
        handleRT.sizeDelta = new Vector2(14, 0);

        // Slider component
        var slider = sliderGO.AddComponent<Slider>();
        slider.fillRect = fillRT;
        slider.handleRect = handleRT;
        slider.targetGraphic = handleImg;
        slider.minValue = min;
        slider.maxValue = max;
        slider.wholeNumbers = true;
        slider.value = initial;
        slider.onValueChanged.AddListener(onChanged);

        // Value label
        var valGO = new GameObject("ValueLabel");
        valGO.layer = layer;
        valGO.AddComponent<RectTransform>();
        valGO.AddComponent<CanvasRenderer>();
        valGO.transform.SetParent(row.transform, false);
        var valTmp = valGO.AddComponent<TextMeshProUGUI>();
        valTmp.text = $"{initial:F0} C";
        valTmp.fontSize = 16;
        valTmp.fontStyle = FontStyles.Bold;
        valTmp.color = new Color(1f, 0.85f, 0.3f);
        valTmp.alignment = TextAlignmentOptions.MidlineRight;
        var valLE = valGO.AddComponent<LayoutElement>();
        valLE.preferredWidth = 60;

        sliderOut = slider;
        labelOut = valTmp;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    static string KeyName(KeyCode k)
    {
        return k switch
        {
            KeyCode.Alpha1 => "1",
            KeyCode.Alpha2 => "2",
            KeyCode.Alpha3 => "3",
            KeyCode.Backspace => "Bksp",
            _ => k.ToString()
        };
    }
}
