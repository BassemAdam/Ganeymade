using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

/// <summary>
/// Drop this on any GameObject in a game scene to get a small
/// "Back to Menu" button (or press Escape).
/// </summary>
public class ReturnToMenu : MonoBehaviour
{
    [Tooltip("Scene name of the main menu (must be in Build Settings).")]
    public string menuSceneName = "MainMenu";

    [Tooltip("Key to return to menu.")]
    public KeyCode returnKey = KeyCode.Escape;

    void Start()
    {
        BuildBackButton();
    }

    void Update()
    {
        if (Input.GetKeyDown(returnKey))
            GoToMenu();
    }

    void GoToMenu()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        SceneManager.LoadScene(menuSceneName);
    }

    void BuildBackButton()
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0) uiLayer = 5;

        // Check if a canvas with "ReturnUI" already exists
        var existing = GameObject.Find("ReturnUI_Canvas");
        if (existing != null) return;

        var canvasGO = new GameObject("ReturnUI_Canvas");
        canvasGO.layer = uiLayer;
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        canvasGO.AddComponent<GraphicRaycaster>();

        // EventSystem
        if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var esGO = new GameObject("EventSystem");
            esGO.AddComponent<UnityEngine.EventSystems.EventSystem>();
            esGO.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        // Button in top-right corner
        var btnGO = new GameObject("BackButton");
        btnGO.layer = uiLayer;
        btnGO.transform.SetParent(canvasGO.transform, false);

        var btnImg = btnGO.AddComponent<Image>();
        btnImg.color = new Color(0.15f, 0.15f, 0.2f, 0.85f);

        var btn = btnGO.AddComponent<Button>();
        var colors = btn.colors;
        colors.highlightedColor = new Color(0.3f, 0.3f, 0.4f);
        colors.pressedColor = new Color(0.1f, 0.1f, 0.15f);
        btn.colors = colors;
        btn.onClick.AddListener(GoToMenu);

        var btnRT = btnGO.GetComponent<RectTransform>();
        btnRT.anchorMin = new Vector2(1, 1);
        btnRT.anchorMax = new Vector2(1, 1);
        btnRT.pivot = new Vector2(1, 1);
        btnRT.anchoredPosition = new Vector2(-20, -20);
        btnRT.sizeDelta = new Vector2(160, 40);

        // Button text
        var textGO = new GameObject("Text");
        textGO.layer = uiLayer;
        textGO.transform.SetParent(btnGO.transform, false);
        textGO.AddComponent<RectTransform>();
        textGO.AddComponent<CanvasRenderer>();

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text = $"[{returnKey}]  Menu";
        tmp.fontSize = 16;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        var textRT = textGO.GetComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero;
        textRT.offsetMax = Vector2.zero;
    }
}
