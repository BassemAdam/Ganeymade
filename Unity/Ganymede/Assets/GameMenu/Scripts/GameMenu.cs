using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

/// <summary>
/// Runtime main-menu controller.  Reads a GameMenuConfig asset and builds a
/// UI with one button per enabled scene.  Attach to a GameObject in your
/// dedicated "MainMenu" scene and assign the config in the Inspector.
/// </summary>
public class GameMenu : MonoBehaviour
{
    [Header("Configuration")]
    [Tooltip("Drag your GameMenuConfig asset here.")]
    public GameMenuConfig config;

    [Header("Style")]
    public Color backgroundColor = new Color(0.04f, 0.04f, 0.10f, 1f);
    public Color panelColor = new Color(0.06f, 0.06f, 0.14f, 0.92f);
    public Color buttonColor = new Color(0.18f, 0.22f, 0.32f, 1f);
    public Color buttonHoverColor = new Color(0.28f, 0.35f, 0.5f, 1f);
    public Color buttonPressColor = new Color(0.15f, 0.18f, 0.25f, 1f);
    public Color titleColor = new Color(0.70f, 0.85f, 1f);
    public Color textColor = new Color(0.90f, 0.92f, 0.96f);
    public Color descColor = new Color(0.55f, 0.58f, 0.65f);

    void Start()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (config == null)
        {
            Debug.LogError("[GameMenu] No GameMenuConfig assigned!");
            return;
        }

        BuildUI();
    }

    void BuildUI()
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0) uiLayer = 5;

        // ── Background fullscreen overlay ───────────────────────────────
        var bgGO = new GameObject("MenuBackground");
        bgGO.layer = uiLayer;
        var bgCanvas = bgGO.AddComponent<Canvas>();
        bgCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        bgCanvas.sortingOrder = 90;

        bgGO.AddComponent<CanvasScaler>();
        bgGO.AddComponent<GraphicRaycaster>();

        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = backgroundColor;
        bgImg.raycastTarget = false;

        // ── Main canvas ─────────────────────────────────────────────────
        var canvasGO = new GameObject("MenuCanvas");
        canvasGO.layer = uiLayer;
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        canvasGO.AddComponent<GraphicRaycaster>();

        // EventSystem
        if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var esGO = new GameObject("EventSystem");
            esGO.AddComponent<UnityEngine.EventSystems.EventSystem>();
            esGO.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        // ── Centre panel ────────────────────────────────────────────────
        var panel = CreateCentrePanel(canvasGO.transform, uiLayer);

        // Title
        AddTitle(panel.transform, config.gameTitle, uiLayer);

        // Separator
        AddSeparator(panel.transform, uiLayer);

        // Scene buttons
        var enabledScenes = config.GetEnabledScenes();
        if (enabledScenes.Count == 0)
        {
            AddLabel(panel.transform, "No scenes configured.", uiLayer);
        }
        else
        {
            foreach (var entry in enabledScenes)
            {
                AddSceneButton(panel.transform, entry, uiLayer);
            }
        }

        // Spacer + Quit
        AddSpacer(panel.transform, 20f, uiLayer);
        AddQuitButton(panel.transform, uiLayer);
    }

    // ── UI Construction Helpers ─────────────────────────────────────────

    GameObject CreateCentrePanel(Transform parent, int layer)
    {
        var go = new GameObject("MenuPanel");
        go.layer = layer;
        go.transform.SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = panelColor;

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(40, 40, 30, 30);
        vlg.spacing = 8;
        vlg.childAlignment = TextAnchor.UpperCenter;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(460, 0);

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
        tmp.fontSize = 42;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = titleColor;
        tmp.alignment = TextAlignmentOptions.Center;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 60;
    }

    void AddSeparator(Transform parent, int layer)
    {
        var go = new GameObject("Separator");
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();

        var img = go.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.1f);

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 2;
    }

    void AddLabel(Transform parent, string text, int layer)
    {
        var go = new GameObject("Label");
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        go.AddComponent<CanvasRenderer>();

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 20;
        tmp.color = descColor;
        tmp.alignment = TextAlignmentOptions.Center;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 36;
    }

    void AddSceneButton(Transform parent, SceneEntry entry, int layer)
    {
        // Button container
        var go = new GameObject("Btn_" + entry.displayName);
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();

        var img = go.AddComponent<Image>();
        img.color = buttonColor;

        var btn = go.AddComponent<Button>();
        var colors = btn.colors;
        colors.highlightedColor = buttonHoverColor;
        colors.pressedColor = buttonPressColor;
        btn.colors = colors;

        string sceneToLoad = entry.sceneName;
        btn.onClick.AddListener(() => LoadScene(sceneToLoad));

        // Inner vertical layout for name + description
        var innerVLG = go.AddComponent<VerticalLayoutGroup>();
        innerVLG.padding = new RectOffset(16, 16, 10, 10);
        innerVLG.spacing = 2;
        innerVLG.childControlWidth = true;
        innerVLG.childControlHeight = true;
        innerVLG.childForceExpandWidth = true;
        innerVLG.childForceExpandHeight = false;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = string.IsNullOrEmpty(entry.description) ? 48 : 68;

        // Scene name text
        var nameGO = new GameObject("Name");
        nameGO.layer = layer;
        nameGO.transform.SetParent(go.transform, false);
        nameGO.AddComponent<RectTransform>();
        nameGO.AddComponent<CanvasRenderer>();

        var nameTMP = nameGO.AddComponent<TextMeshProUGUI>();
        nameTMP.text = entry.displayName;
        nameTMP.fontSize = 22;
        nameTMP.fontStyle = FontStyles.Bold;
        nameTMP.color = textColor;
        nameTMP.alignment = TextAlignmentOptions.Left;
        nameTMP.raycastTarget = false;

        // Description text (if provided)
        if (!string.IsNullOrEmpty(entry.description))
        {
            var descGO = new GameObject("Desc");
            descGO.layer = layer;
            descGO.transform.SetParent(go.transform, false);
            descGO.AddComponent<RectTransform>();
            descGO.AddComponent<CanvasRenderer>();

            var descTMP = descGO.AddComponent<TextMeshProUGUI>();
            descTMP.text = entry.description;
            descTMP.fontSize = 14;
            descTMP.color = descColor;
            descTMP.alignment = TextAlignmentOptions.Left;
            descTMP.raycastTarget = false;
        }
    }

    void AddQuitButton(Transform parent, int layer)
    {
        var go = new GameObject("QuitButton");
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();

        var img = go.AddComponent<Image>();
        img.color = new Color(0.35f, 0.12f, 0.12f, 1f);

        var btn = go.AddComponent<Button>();
        var colors = btn.colors;
        colors.highlightedColor = new Color(0.5f, 0.18f, 0.18f);
        colors.pressedColor = new Color(0.25f, 0.08f, 0.08f);
        btn.colors = colors;
        btn.onClick.AddListener(QuitGame);

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 44;

        var textGO = new GameObject("Text");
        textGO.layer = layer;
        textGO.transform.SetParent(go.transform, false);
        textGO.AddComponent<RectTransform>();
        textGO.AddComponent<CanvasRenderer>();

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text = "QUIT";
        tmp.fontSize = 20;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = new Color(1f, 0.6f, 0.6f);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        var textRT = textGO.GetComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero;
        textRT.offsetMax = Vector2.zero;
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

    // ── Actions ─────────────────────────────────────────────────────────

    void LoadScene(string sceneName)
    {
        SceneManager.LoadScene(sceneName);
    }

    void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
