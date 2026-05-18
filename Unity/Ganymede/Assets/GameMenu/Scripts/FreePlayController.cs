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

    [Tooltip("Optional: HeatSourceObj on the same or different object. Auto-detected from solidMaterial if left empty.")]
    public HeatSourceObj heatSourceObj;

    [Tooltip("Optional: VoxelHeatSource on the same or different object. Auto-detected from solidMaterial if left empty.")]
    public VoxelHeatSource voxelHeatSource;

    [Header("Bounds Movement")]
    [Tooltip("How far left/right the bounds can move from the starting position.")]
    public float boundsRange = 10f;

    [Header("Simulation Speed")]
    [Tooltip("Minimum time scale (slow motion).")]
    public float minTimeScale = 0.01f;

    [Tooltip("Maximum time scale.")]
    public float maxTimeScale = 1f;

    [Header("Temperature")]
    [Tooltip("Minimum temperature on the slider.")]
    public float minTemperature = 0f;

    [Tooltip("Maximum temperature on the slider.")]
    public float maxTemperature = 1000f;

    [Header("Dynamic Object Spawning")]
    [Tooltip("Density for 'high density' spawned objects (should be > fluid restDensity to sink).")]
    public float highDensity = 200f;

    [Tooltip("Density for 'low density' spawned objects (should be < fluid restDensity to float).")]
    public float lowDensity = 95f;

    [Tooltip("Scale range for spawned primitives.")]
    public float spawnScale = 1f;

    // Internal state
    float _startX;
    float _startZ;
    float _currentOffsetX;
    float _currentOffsetZ;
    System.Collections.Generic.List<GameObject> _spawnedObjects = new System.Collections.Generic.List<GameObject>();

    // UI references
    Slider _boundsXSlider;
    Slider _boundsZSlider;
    Slider _tempSlider;
    Slider _simSpeedSlider;
    TMP_Text _boundsXValueText;
    TMP_Text _boundsZValueText;
    TMP_Text _tempValueText;
    TMP_Text _simSpeedValueText;
    Toggle _skyboxToggle;
    Material _cachedSkybox;
    GameObject _panelContent;
    TMP_Text _minimizeBtnText;
    bool _minimized;

    void Start()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (computePlugin != null)
        {
            _startX = computePlugin.transform.position.x;
            _startZ = computePlugin.transform.position.z;
        }

        // Auto-detect heat source components on the solid material's object
        if (solidMaterial != null)
        {
            if (heatSourceObj == null)
                heatSourceObj = solidMaterial.GetComponent<HeatSourceObj>();
            if (voxelHeatSource == null)
                voxelHeatSource = solidMaterial.GetComponent<VoxelHeatSource>();
        }

        BuildUI();
        SyncSlidersFromState();
    }

    void OnDestroy()
    {
        Time.timeScale = 1f;
        Time.fixedDeltaTime = 0.02f;
        if (_cachedSkybox != null)
            RenderSettings.skybox = _cachedSkybox;
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
        solidMaterial.isContinuousHeatSource = true;
        if (heatSourceObj != null) heatSourceObj.temperature = temp;
        if (voxelHeatSource != null) voxelHeatSource.temperature = temp;
    }

    void OnSimSpeedSliderChanged(float normalized)
    {
        float speed = Mathf.Lerp(minTimeScale, maxTimeScale, normalized);
        Time.timeScale = speed;
        Time.fixedDeltaTime = 0.02f * speed;
    }

    void OnSkyboxToggleChanged(bool isOn)
    {
        if (isOn)
        {
            RenderSettings.skybox = _cachedSkybox;
        }
        else
        {
            RenderSettings.skybox = null;
        }
        DynamicGI.UpdateEnvironment();
    }

    // ── Dynamic Object Spawning ─────────────────────────────────────────

    void SpawnDynamicObject(float density)
    {
        if (computePlugin == null) return;
        if (_spawnedObjects.Count >= 5) return;

        computePlugin.GetBoundsWS(out Vector3 bMin, out Vector3 bMax);

        // Spawn in upper-middle region (60%-85% height) to avoid ceiling
        float spawnYMin = Mathf.Lerp(bMin.y, bMax.y, 0.6f);
        float spawnYMax = Mathf.Lerp(bMin.y, bMax.y, 0.85f);
        Vector3 spawnPos = new Vector3(
            Random.Range(bMin.x + spawnScale, bMax.x - spawnScale),
            Random.Range(spawnYMin, spawnYMax),
            Random.Range(bMin.z + spawnScale, bMax.z - spawnScale));

        // Random primitive type
        PrimitiveType[] types = { PrimitiveType.Sphere, PrimitiveType.Capsule, PrimitiveType.Cube };
        PrimitiveType chosen = types[Random.Range(0, types.Length)];

        GameObject obj = GameObject.CreatePrimitive(chosen);
        obj.name = $"Spawned_{chosen}_{(density > lowDensity ? "Heavy" : "Light")}";
        obj.transform.position = spawnPos;
        obj.transform.localScale = Vector3.one * spawnScale;
        obj.transform.rotation = Random.rotation;

        // Assign URP-compatible material (CreatePrimitive uses Standard which is pink in URP builds)
        var renderer = obj.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = density > lowDensity
                ? new Color(0.6f, 0.25f, 0.2f) // reddish-brown for heavy
                : new Color(0.3f, 0.7f, 0.4f); // green for light
            renderer.sharedMaterial = mat;
        }

        // Set layer to SDFBoundary (layer 3)
        obj.layer = LayerMask.NameToLayer("SDFBoundary");

        // Add Rigidbody
        var rb = obj.AddComponent<Rigidbody>();
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        // Add VoxelDynamic
        var vd = obj.AddComponent<VoxelDynamic>();
        vd.enableFluidForces = true;
        vd.buoyancyMode = VoxelDynamic.BuoyancyMode.Analytical;
        vd.objectDensity = density;
        vd.dragCoefficient = 1f;
        vd.angularDragCoefficient = 0.5f;
        vd.sinkFactor = 0.5f;
        vd.stayUpright = false;
        vd.constrainToBounds = true;
        vd.boundsBounce = 0.3f;
        vd.simReference = computePlugin;
        vd.autoSetMass = true;

        _spawnedObjects.Add(obj);
    }

    void DestroyAllSpawned()
    {
        foreach (var obj in _spawnedObjects)
        {
            if (obj != null)
                Destroy(obj);
        }
        _spawnedObjects.Clear();
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

        if (_simSpeedSlider != null)
        {
            float norm = Mathf.InverseLerp(minTimeScale, maxTimeScale, Time.timeScale);
            _simSpeedSlider.SetValueWithoutNotify(norm);
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

        if (_simSpeedValueText != null)
        {
            float pct = Time.timeScale * 100f;
            _simSpeedValueText.text = $"Speed:  <color=#88DDAA><b>{pct:F0}%</b></color>";
        }
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

        // ── Title bar with minimize button ──────────────────────────────
        var titleBar = new GameObject("TitleBar");
        titleBar.layer = uiLayer;
        titleBar.transform.SetParent(panel.transform, false);
        titleBar.AddComponent<RectTransform>();

        var titleHlg = titleBar.AddComponent<HorizontalLayoutGroup>();
        titleHlg.spacing = 6;
        titleHlg.childAlignment = TextAnchor.MiddleCenter;
        titleHlg.childControlWidth = true;
        titleHlg.childControlHeight = true;
        titleHlg.childForceExpandWidth = true;
        titleHlg.childForceExpandHeight = false;

        var titleLE = titleBar.AddComponent<LayoutElement>();
        titleLE.preferredHeight = 36;

        // Title text
        var titleGO = new GameObject("Title");
        titleGO.layer = uiLayer;
        titleGO.transform.SetParent(titleBar.transform, false);
        titleGO.AddComponent<RectTransform>();
        titleGO.AddComponent<CanvasRenderer>();
        var titleTmp = titleGO.AddComponent<TextMeshProUGUI>();
        titleTmp.text = "FREE PLAY";
        titleTmp.fontSize = 26;
        titleTmp.fontStyle = FontStyles.Bold;
        titleTmp.color = new Color(0.92f, 0.95f, 1f);
        titleTmp.alignment = TextAlignmentOptions.Center;

        // Minimize button
        AddMinimizeButton(titleBar.transform, uiLayer);

        // ── Collapsible content container ───────────────────────────────
        _panelContent = new GameObject("Content");
        _panelContent.layer = uiLayer;
        _panelContent.transform.SetParent(panel.transform, false);
        _panelContent.AddComponent<RectTransform>();

        var contentVlg = _panelContent.AddComponent<VerticalLayoutGroup>();
        contentVlg.spacing = 6;
        contentVlg.childAlignment = TextAnchor.UpperLeft;
        contentVlg.childControlWidth = true;
        contentVlg.childControlHeight = true;
        contentVlg.childForceExpandWidth = true;
        contentVlg.childForceExpandHeight = false;

        var contentFitter = _panelContent.AddComponent<ContentSizeFitter>();
        contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var contentParent = _panelContent.transform;

        // ── Bounds X control ─────────────────────────────────────────────
        AddSectionLabel(contentParent, "AABB Bounds Position", uiLayer);
        _boundsXValueText = AddLabel(contentParent, "X Offset:  0.0", uiLayer);
        _boundsXSlider = AddSlider(contentParent, "BoundsXSlider", uiLayer, OnBoundsXSliderChanged);

        AddSpacer(contentParent, 4f, uiLayer);

        // ── Bounds Z control ─────────────────────────────────────────────
        _boundsZValueText = AddLabel(contentParent, "Z Offset:  0.0", uiLayer);
        _boundsZSlider = AddSlider(contentParent, "BoundsZSlider", uiLayer, OnBoundsZSliderChanged);

        AddSpacer(contentParent, 8f, uiLayer);

        // ── Temperature control ─────────────────────────────────────────
        AddSectionLabel(contentParent, "Solid Temperature", uiLayer);
        _tempValueText = AddLabel(contentParent, "Temp:  0 °", uiLayer);
        _tempSlider = AddSlider(contentParent, "TempSlider", uiLayer, OnTempSliderChanged);

        AddHintLabel(contentParent,
            $"{minTemperature:F0} ────── {maxTemperature:F0}", uiLayer);

        AddSpacer(contentParent, 8f, uiLayer);

        // ── Simulation Speed control ────────────────────────────────────
        AddSectionLabel(contentParent, "Simulation Speed", uiLayer);
        _simSpeedValueText = AddLabel(contentParent, "Speed:  100%", uiLayer);
        _simSpeedSlider = AddSlider(contentParent, "SimSpeedSlider", uiLayer, OnSimSpeedSliderChanged);

        AddHintLabel(contentParent,
            $"{minTimeScale * 100f:F0}% ────── {maxTimeScale * 100f:F0}%", uiLayer);

        AddSpacer(contentParent, 8f, uiLayer);

        // ── Skybox toggle ───────────────────────────────────────────────
        AddSectionLabel(contentParent, "Environment", uiLayer);
        _cachedSkybox = RenderSettings.skybox;
        _skyboxToggle = AddToggle(contentParent, "SkyboxToggle", "Skybox", uiLayer,
            RenderSettings.skybox != null, OnSkyboxToggleChanged);

        AddSpacer(contentParent, 8f, uiLayer);

        // ── Dynamic Object Spawning ────────────────────────────────────
        AddSectionLabel(contentParent, "Spawn Objects", uiLayer);
        AddButton(contentParent, "SpawnHeavy", "Spawn Heavy Object", uiLayer,
            new Color(0.7f, 0.3f, 0.3f, 1f), () => SpawnDynamicObject(highDensity));
        AddSpacer(contentParent, 4f, uiLayer);
        AddButton(contentParent, "SpawnLight", "Spawn Light Object", uiLayer,
            new Color(0.3f, 0.6f, 0.4f, 1f), () => SpawnDynamicObject(lowDensity));
        AddSpacer(contentParent, 4f, uiLayer);
        AddButton(contentParent, "DestroyAll", "Destroy All Spawned", uiLayer,
            new Color(0.5f, 0.2f, 0.2f, 1f), DestroyAllSpawned);

        // Start minimized
        _minimized = true;
        _panelContent.SetActive(false);
        if (_minimizeBtnText != null)
            _minimizeBtnText.text = "+";
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
        slider.navigation = new Navigation { mode = Navigation.Mode.None };

        slider.onValueChanged.AddListener(onChange);

        return slider;
    }

    Toggle AddToggle(Transform parent, string name, string label, int layer,
        bool initialValue, UnityEngine.Events.UnityAction<bool> onChange)
    {
        var go = new GameObject(name);
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 30;

        var hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = false;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;

        // Checkbox background
        var boxGO = new GameObject("Background");
        boxGO.layer = layer;
        boxGO.transform.SetParent(go.transform, false);
        var boxImg = boxGO.AddComponent<Image>();
        boxImg.color = new Color(0.15f, 0.15f, 0.22f, 1f);
        var boxLE = boxGO.AddComponent<LayoutElement>();
        boxLE.preferredWidth = 22;
        boxLE.preferredHeight = 22;

        // Checkmark
        var checkGO = new GameObject("Checkmark");
        checkGO.layer = layer;
        checkGO.transform.SetParent(boxGO.transform, false);
        var checkImg = checkGO.AddComponent<Image>();
        checkImg.color = new Color(0.3f, 0.55f, 0.85f, 1f);
        var checkRT = checkGO.GetComponent<RectTransform>();
        checkRT.anchorMin = new Vector2(0.15f, 0.15f);
        checkRT.anchorMax = new Vector2(0.85f, 0.85f);
        checkRT.offsetMin = Vector2.zero;
        checkRT.offsetMax = Vector2.zero;

        // Label
        var labelGO = new GameObject("Label");
        labelGO.layer = layer;
        labelGO.transform.SetParent(go.transform, false);
        labelGO.AddComponent<RectTransform>();
        labelGO.AddComponent<CanvasRenderer>();
        var tmp = labelGO.AddComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = 18;
        tmp.color = new Color(0.85f, 0.88f, 0.92f);
        var labelLE = labelGO.AddComponent<LayoutElement>();
        labelLE.preferredWidth = 200;

        var toggle = go.AddComponent<Toggle>();
        toggle.isOn = initialValue;
        toggle.targetGraphic = boxImg;
        toggle.graphic = checkImg;
        toggle.navigation = new Navigation { mode = Navigation.Mode.None };
        toggle.onValueChanged.AddListener(onChange);

        return toggle;
    }

    Button AddButton(Transform parent, string name, string label, int layer,
        Color btnColor, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(name);
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 34;

        var btnImg = go.AddComponent<Image>();
        btnImg.color = btnColor;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = btnImg;
        btn.navigation = new Navigation { mode = Navigation.Mode.None };

        var colors = btn.colors;
        colors.normalColor = btnColor;
        colors.highlightedColor = btnColor * 1.2f;
        colors.pressedColor = btnColor * 0.7f;
        btn.colors = colors;

        // Label text
        var labelGO = new GameObject("Label");
        labelGO.layer = layer;
        labelGO.transform.SetParent(go.transform, false);
        labelGO.AddComponent<CanvasRenderer>();
        var tmp = labelGO.AddComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = 17;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;

        var labelRT = labelGO.GetComponent<RectTransform>();
        labelRT.anchorMin = Vector2.zero;
        labelRT.anchorMax = Vector2.one;
        labelRT.offsetMin = Vector2.zero;
        labelRT.offsetMax = Vector2.zero;

        btn.onClick.AddListener(onClick);
        return btn;
    }

    void AddMinimizeButton(Transform parent, int layer)
    {
        var btnGO = new GameObject("MinimizeBtn");
        btnGO.layer = layer;
        btnGO.transform.SetParent(parent, false);

        var btnImg = btnGO.AddComponent<Image>();
        btnImg.color = new Color(0.2f, 0.2f, 0.3f, 0.9f);

        var btnLE = btnGO.AddComponent<LayoutElement>();
        btnLE.preferredWidth = 32;
        btnLE.preferredHeight = 28;
        btnLE.flexibleWidth = 0;

        var labelGO = new GameObject("Label");
        labelGO.layer = layer;
        labelGO.transform.SetParent(btnGO.transform, false);
        labelGO.AddComponent<CanvasRenderer>();
        _minimizeBtnText = labelGO.AddComponent<TextMeshProUGUI>();
        _minimizeBtnText.text = "—";
        _minimizeBtnText.fontSize = 22;
        _minimizeBtnText.fontStyle = FontStyles.Bold;
        _minimizeBtnText.color = new Color(0.85f, 0.9f, 1f);
        _minimizeBtnText.alignment = TextAlignmentOptions.Center;

        var labelRT = labelGO.GetComponent<RectTransform>();
        labelRT.anchorMin = Vector2.zero;
        labelRT.anchorMax = Vector2.one;
        labelRT.offsetMin = Vector2.zero;
        labelRT.offsetMax = Vector2.zero;

        var btn = btnGO.AddComponent<Button>();
        btn.targetGraphic = btnImg;
        btn.navigation = new Navigation { mode = Navigation.Mode.None };

        var normalColor = new Color(0.2f, 0.2f, 0.3f, 0.9f);
        var hoverColor = new Color(0.3f, 0.3f, 0.45f, 1f);
        var colors = btn.colors;
        colors.normalColor = normalColor;
        colors.highlightedColor = hoverColor;
        colors.pressedColor = new Color(0.15f, 0.15f, 0.25f, 1f);
        btn.colors = colors;

        btn.onClick.AddListener(OnMinimizeClicked);
    }

    void OnMinimizeClicked()
    {
        _minimized = !_minimized;
        _panelContent.SetActive(!_minimized);
        if (_minimizeBtnText != null)
            _minimizeBtnText.text = _minimized ? "+" : "—";
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
