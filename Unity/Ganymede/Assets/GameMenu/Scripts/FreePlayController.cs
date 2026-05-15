using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Free-play controller: lets the user slide the simulation AABB bounds
/// on X and Z axes and adjust VoxelSolidMaterial temperature via UI sliders.
/// Builds a runtime Canvas + TMP UI (same style as the other minigames).
///
/// Setup:
/// 1. Attach to any GameObject in the Free Play scene.
/// 2. Assign the UseComputePlugin reference (the object whose position moves the AABB).
/// 3. Assign the VoxelSolidMaterial whose temperature you want to control.
/// </summary>
public class FreePlayController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The UseComputePlugin whose transform controls the AABB position.")]
    public UseComputePlugin computePlugin;

    [Tooltip("The VoxelSolidMaterial whose temperature to control.")]
    public VoxelSolidMaterial solidMaterial;

    [Header("Bounds Movement")]
    [Tooltip("How far left/right the bounds can move from the starting position.")]
    public float boundsRange = 10f;

    [Header("Temperature")]
    [Tooltip("Minimum temperature on the slider.")]
    public float minTemperature = 0f;

    [Tooltip("Maximum temperature on the slider.")]
    public float maxTemperature = 1000f;

    // Internal state
    float _startX;
    float _startZ;
    float _currentOffsetX;
    float _currentOffsetZ;

    // UI references
    Slider _boundsXSlider;
    Slider _boundsZSlider;
    Slider _tempSlider;
    TMP_Text _boundsXValueText;
    TMP_Text _boundsZValueText;
    TMP_Text _tempValueText;

    void Start()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (computePlugin != null)
        {
            _startX = computePlugin.transform.position.x;
            _startZ = computePlugin.transform.position.z;
        }

        BuildUI();
        SyncSlidersFromState();
    }

    void Update()
    {
        RefreshUI();
    }

    void ApplyBoundsPosition()
    {
        if (computePlugin == null) return;
        var pos = computePlugin.transform.position;
        pos.x = _startX + _currentOffsetX;
        pos.z = _startZ + _currentOffsetZ;
        computePlugin.transform.position = pos;
    }

    void OnBoundsXSliderChanged(float normalized)
    {
        _currentOffsetX = Mathf.Lerp(-boundsRange, boundsRange, normalized);
        ApplyBoundsPosition();
    }

    void OnBoundsZSliderChanged(float normalized)
    {
        _currentOffsetZ = Mathf.Lerp(-boundsRange, boundsRange, normalized);
        ApplyBoundsPosition();
    }

    void OnTempSliderChanged(float normalized)
    {
        if (solidMaterial == null) return;
        float temp = Mathf.Lerp(minTemperature, maxTemperature, normalized);
        solidMaterial.temperature = temp;
    }

    void SyncSlidersFromState()
    {
        if (_boundsXSlider != null)
            _boundsXSlider.SetValueWithoutNotify((_currentOffsetX + boundsRange) / (2f * boundsRange));

        if (_boundsZSlider != null)
            _boundsZSlider.SetValueWithoutNotify((_currentOffsetZ + boundsRange) / (2f * boundsRange));

        if (_tempSlider != null && solidMaterial != null)
        {
            float norm = Mathf.InverseLerp(minTemperature, maxTemperature, solidMaterial.temperature);
            _tempSlider.SetValueWithoutNotify(norm);
        }
    }

    void RefreshUI()
    {
        if (_boundsXValueText != null)
            _boundsXValueText.text = $"X Offset:  <color=#55BBFF><b>{_currentOffsetX:F1}</b></color>";

        if (_boundsZValueText != null)
            _boundsZValueText.text = $"Z Offset:  <color=#55BBFF><b>{_currentOffsetZ:F1}</b></color>";

        if (_tempValueText != null && solidMaterial != null)
            _tempValueText.text = $"Temp:  <color=#FFD633><b>{solidMaterial.temperature:F0} °</b></color>";
    }

    // ── Build UI ────────────────────────────────────────────────────────

    void BuildUI()
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0) uiLayer = 5;

        var canvasGO = new GameObject("FreePlayUI_Canvas");
        canvasGO.layer = uiLayer;
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        canvasGO.AddComponent<GraphicRaycaster>();

        if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var esGO = new GameObject("EventSystem");
            esGO.AddComponent<UnityEngine.EventSystems.EventSystem>();
            esGO.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        var panel = CreatePanel(canvasGO.transform, "FreePlayPanel", uiLayer);

        AddTitle(panel.transform, "FREE PLAY", uiLayer);

        // ── Bounds X control ─────────────────────────────────────────────
        AddSectionLabel(panel.transform, "AABB Bounds Position", uiLayer);
        _boundsXValueText = AddLabel(panel.transform, "X Offset:  0.0", uiLayer);
        _boundsXSlider = AddSlider(panel.transform, "BoundsXSlider", uiLayer, OnBoundsXSliderChanged);

        AddSpacer(panel.transform, 4f, uiLayer);

        // ── Bounds Z control ─────────────────────────────────────────────
        _boundsZValueText = AddLabel(panel.transform, "Z Offset:  0.0", uiLayer);
        _boundsZSlider = AddSlider(panel.transform, "BoundsZSlider", uiLayer, OnBoundsZSliderChanged);

        AddSpacer(panel.transform, 8f, uiLayer);

        // ── Temperature control ─────────────────────────────────────────
        AddSectionLabel(panel.transform, "Solid Temperature", uiLayer);
        _tempValueText = AddLabel(panel.transform, "Temp:  0 °", uiLayer);
        _tempSlider = AddSlider(panel.transform, "TempSlider", uiLayer, OnTempSliderChanged);

        AddHintLabel(panel.transform,
            $"{minTemperature:F0} ────── {maxTemperature:F0}", uiLayer);
    }

    // ── UI helpers (matching existing minigame style) ────────────────────

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
        rt.sizeDelta = new Vector2(340, 0);

        return go;
    }

    void AddTitle(Transform parent, string text, int layer)
    {
        var go = new GameObject("Title");
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        go.AddComponent<CanvasRenderer>();

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 26;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = new Color(0.92f, 0.95f, 1f);
        tmp.alignment = TextAlignmentOptions.Center;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 36;
    }

    void AddSectionLabel(Transform parent, string text, int layer)
    {
        var go = new GameObject("Section");
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        go.AddComponent<CanvasRenderer>();

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 16;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = new Color(0.6f, 0.7f, 0.85f);

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 24;
    }

    TMP_Text AddLabel(Transform parent, string text, int layer)
    {
        var go = new GameObject("Label");
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        go.AddComponent<CanvasRenderer>();

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 20;
        tmp.color = new Color(0.85f, 0.88f, 0.92f);
        tmp.richText = true;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 28;

        return tmp;
    }

    void AddHintLabel(Transform parent, string text, int layer)
    {
        var go = new GameObject("Hint");
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        go.AddComponent<CanvasRenderer>();

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 13;
        tmp.color = new Color(0.45f, 0.48f, 0.55f);
        tmp.alignment = TextAlignmentOptions.Center;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 20;
    }

    Slider AddSlider(Transform parent, string name, int layer, UnityEngine.Events.UnityAction<float> onChange)
    {
        var go = new GameObject(name);
        go.layer = layer;
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 28;

        var slider = go.AddComponent<Slider>();
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.wholeNumbers = false;

        // Background
        var bgGO = new GameObject("Background");
        bgGO.layer = layer;
        bgGO.transform.SetParent(go.transform, false);
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0.15f, 0.15f, 0.22f, 1f);
        var bgRT = bgGO.GetComponent<RectTransform>();
        bgRT.anchorMin = new Vector2(0, 0.35f);
        bgRT.anchorMax = new Vector2(1, 0.65f);
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;

        // Fill area
        var fillAreaGO = new GameObject("Fill Area");
        fillAreaGO.layer = layer;
        fillAreaGO.transform.SetParent(go.transform, false);
        var fillAreaRT = fillAreaGO.AddComponent<RectTransform>();
        fillAreaRT.anchorMin = new Vector2(0, 0.35f);
        fillAreaRT.anchorMax = new Vector2(1, 0.65f);
        fillAreaRT.offsetMin = Vector2.zero;
        fillAreaRT.offsetMax = Vector2.zero;

        var fillGO = new GameObject("Fill");
        fillGO.layer = layer;
        fillGO.transform.SetParent(fillAreaGO.transform, false);
        var fillImg = fillGO.AddComponent<Image>();
        fillImg.color = new Color(0.3f, 0.55f, 0.85f, 1f);
        var fillRT = fillGO.GetComponent<RectTransform>();
        fillRT.anchorMin = Vector2.zero;
        fillRT.anchorMax = Vector2.one;
        fillRT.offsetMin = Vector2.zero;
        fillRT.offsetMax = Vector2.zero;

        slider.fillRect = fillRT;

        // Handle slide area
        var handleAreaGO = new GameObject("Handle Slide Area");
        handleAreaGO.layer = layer;
        handleAreaGO.transform.SetParent(go.transform, false);
        var handleAreaRT = handleAreaGO.AddComponent<RectTransform>();
        handleAreaRT.anchorMin = Vector2.zero;
        handleAreaRT.anchorMax = Vector2.one;
        handleAreaRT.offsetMin = Vector2.zero;
        handleAreaRT.offsetMax = Vector2.zero;

        var handleGO = new GameObject("Handle");
        handleGO.layer = layer;
        handleGO.transform.SetParent(handleAreaGO.transform, false);
        var handleImg = handleGO.AddComponent<Image>();
        handleImg.color = new Color(0.85f, 0.9f, 1f, 1f);
        var handleRT = handleGO.GetComponent<RectTransform>();
        handleRT.sizeDelta = new Vector2(14, 0);
        handleRT.anchorMin = new Vector2(0, 0);
        handleRT.anchorMax = new Vector2(0, 1);

        slider.handleRect = handleRT;
        slider.targetGraphic = handleImg;

        slider.onValueChanged.AddListener(onChange);

        return slider;
    }

    void AddSpacer(Transform parent, float height, int layer)
    {
        var go = new GameObject("Spacer");
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = height;
    }
}
