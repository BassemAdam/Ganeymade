using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Dam minigame: press a key or click a UI button to break the dam.
/// On break, the intact dam is hidden and a pre-fractured "broken dam"
/// hierarchy is enabled, with each chunk getting a Rigidbody + MeshCollider
/// so it physically falls apart.
///
/// Setup:
/// 1. Attach to any GameObject (e.g. an empty "DamMinigame").
/// 2. Assign the intact dam reference and the broken dam reference
///    (drag broken_dam.fbx into the scene, parent its chunks under one
///    GameObject, and assign that root here).
/// </summary>
public class DamBreakMinigame : MonoBehaviour
{
    [Header("Camera")]
    [Tooltip("The main camera. Auto-found if empty.")]
    public Camera mainCamera;

    [Header("Dam")]
    [Tooltip("The intact dam GameObject to disable on break.")]
    public GameObject intactDam;

    [Tooltip("The pre-fractured broken dam hierarchy. Each child MeshRenderer is treated as a chunk.")]
    public GameObject brokenDam;

    [Header("Chunk Physics")]
    [Tooltip("Total mass distributed across all chunks (kg).")]
    [Min(0.1f)] public float totalMass = 500f;

    [Tooltip("Use convex MeshColliders (required for Rigidbody dynamic chunks).")]
    public bool convexColliders = true;

    [Tooltip("Physics material applied to chunk colliders (optional).")]
    public PhysicsMaterial chunkPhysicsMaterial;

    [Tooltip("Outward impulse applied to each chunk from the dam center on break.")]
    [Min(0f)] public float burstImpulse = 2f;

    [Tooltip("Random angular impulse magnitude applied to each chunk on break.")]
    [Min(0f)] public float burstTorque = 1f;

    [Tooltip("If a chunk has no MeshRenderer of its own but its children do, recurse.")]
    public bool recurseChildren = true;

    [Header("Hotkeys")]
    public KeyCode breakKey = KeyCode.E;
    public KeyCode resetKey = KeyCode.R;

    // State
    bool _isBroken;
    bool _chunksInitialised;

    struct ChunkState
    {
        public Transform tr;
        public Vector3 localPos;
        public Quaternion localRot;
        public Vector3 localScale;
        public Rigidbody rb;
    }
    readonly List<ChunkState> _chunks = new List<ChunkState>();

    // UI references
    TMP_Text _statusText;
    TMP_Text _breakBtnText;
    TMP_Text _resetBtnText;

    void Start()
    {
        if (mainCamera == null)
            mainCamera = Camera.main;

        // Hide broken dam at startup
        if (brokenDam != null)
            brokenDam.SetActive(false);

        BuildUI();
    }

    void Update()
    {
        if (Input.GetKeyDown(breakKey) && !_isBroken)
            BreakDam();
        if (Input.GetKeyDown(resetKey) && _isBroken)
            ResetDam();

        RefreshUI();
    }

    void BreakDam()
    {
        if (_isBroken) return;
        _isBroken = true;

        if (intactDam != null)
            intactDam.SetActive(false);

        if (brokenDam == null) return;

        if (!_chunksInitialised)
            InitChunks();

        brokenDam.SetActive(true);

        // Restore initial pose (in case Reset was never called between bursts)
        // and re-arm physics, then apply outward burst.
        Vector3 center = ComputeChunkCenter();
        for (int i = 0; i < _chunks.Count; i++)
        {
            var c = _chunks[i];
            if (c.tr == null) continue;
            c.tr.localPosition = c.localPos;
            c.tr.localRotation = c.localRot;
            c.tr.localScale = c.localScale;

            if (c.rb != null)
            {
                c.rb.isKinematic = false;
                c.rb.linearVelocity = Vector3.zero;
                c.rb.angularVelocity = Vector3.zero;

                if (burstImpulse > 0f)
                {
                    Vector3 dir = (c.tr.position - center);
                    if (dir.sqrMagnitude < 1e-6f) dir = Random.onUnitSphere;
                    else dir.Normalize();
                    c.rb.AddForce(dir * burstImpulse, ForceMode.Impulse);
                }
                if (burstTorque > 0f)
                    c.rb.AddTorque(Random.insideUnitSphere * burstTorque, ForceMode.Impulse);
            }
        }
    }

    void ResetDam()
    {
        if (!_isBroken) return;
        _isBroken = false;

        if (brokenDam != null)
        {
            // Freeze chunks and restore their original local transforms before hiding,
            // so the next break starts from a clean state.
            for (int i = 0; i < _chunks.Count; i++)
            {
                var c = _chunks[i];
                if (c.tr == null) continue;
                if (c.rb != null)
                {
                    c.rb.linearVelocity = Vector3.zero;
                    c.rb.angularVelocity = Vector3.zero;
                    c.rb.isKinematic = true;
                }
                c.tr.localPosition = c.localPos;
                c.tr.localRotation = c.localRot;
                c.tr.localScale = c.localScale;
            }
            brokenDam.SetActive(false);
        }

        if (intactDam != null)
            intactDam.SetActive(true);
    }

    // ── Chunk discovery & physics setup ─────────────────────────────────

    void InitChunks()
    {
        _chunks.Clear();
        if (brokenDam == null) { _chunksInitialised = true; return; }

        // Make sure the hierarchy is enabled so GetComponentsInChildren finds them.
        bool wasActive = brokenDam.activeSelf;
        brokenDam.SetActive(true);

        var renderers = recurseChildren
            ? brokenDam.GetComponentsInChildren<MeshRenderer>(true)
            : GetDirectMeshRenderers(brokenDam.transform);

        // Distribute total mass evenly across chunks.
        float perChunkMass = renderers.Length > 0 ? Mathf.Max(0.01f, totalMass / renderers.Length) : 1f;

        foreach (var mr in renderers)
        {
            var go = mr.gameObject;
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            // MeshCollider — convex is required for non-kinematic Rigidbodies
            var mc = go.GetComponent<MeshCollider>();
            if (mc == null) mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.sharedMesh;
            mc.convex = convexColliders;
            if (chunkPhysicsMaterial != null) mc.sharedMaterial = chunkPhysicsMaterial;

            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.mass = perChunkMass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.isKinematic = true; // start frozen, BreakDam unfreezes

            _chunks.Add(new ChunkState
            {
                tr = go.transform,
                localPos = go.transform.localPosition,
                localRot = go.transform.localRotation,
                localScale = go.transform.localScale,
                rb = rb
            });
        }

        if (!wasActive) brokenDam.SetActive(false);
        _chunksInitialised = true;
    }

    static MeshRenderer[] GetDirectMeshRenderers(Transform root)
    {
        var list = new List<MeshRenderer>();
        for (int i = 0; i < root.childCount; i++)
        {
            var mr = root.GetChild(i).GetComponent<MeshRenderer>();
            if (mr != null) list.Add(mr);
        }
        return list.ToArray();
    }

    Vector3 ComputeChunkCenter()
    {
        if (_chunks.Count == 0) return brokenDam != null ? brokenDam.transform.position : transform.position;
        Vector3 sum = Vector3.zero;
        int n = 0;
        for (int i = 0; i < _chunks.Count; i++)
        {
            if (_chunks[i].tr == null) continue;
            sum += _chunks[i].tr.position;
            n++;
        }
        return n > 0 ? sum / n : brokenDam.transform.position;
    }

    void RefreshUI()
    {
        if (_statusText != null)
        {
            string col = _isBroken ? "#FF4444" : "#33FF66";
            string label = _isBroken ? "BROKEN" : "INTACT";
            _statusText.text = $"Dam:  <color={col}><b>{label}</b></color>";
        }
        if (_breakBtnText != null)
            _breakBtnText.text = $"[{KeyName(breakKey)}]  Break Dam";
        if (_resetBtnText != null)
            _resetBtnText.text = $"[{KeyName(resetKey)}]  Reset Dam";
    }

    // ── Build UI at runtime ─────────────────────────────────────────────

    void BuildUI()
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0) uiLayer = 5;

        // Use ScreenSpaceOverlay so no extra camera is needed — avoids
        // duplicating render features (fluid/voxel) on a second camera.
        var canvasGO = new GameObject("DamUI_Canvas");
        canvasGO.layer = uiLayer;
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        canvasGO.AddComponent<GraphicRaycaster>();

        // EventSystem
        if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var esGO = new GameObject("EventSystem");
            esGO.AddComponent<UnityEngine.EventSystems.EventSystem>();
            esGO.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        // Panel
        var panel = CreatePanel(canvasGO.transform, "DamPanel", uiLayer);
        AddTitle(panel.transform, "DAM", uiLayer);
        _statusText = AddLabel(panel.transform, "Dam: INTACT", uiLayer);
        _breakBtnText = AddButton(panel.transform, $"[{KeyName(breakKey)}]  Break Dam", () => BreakDam(), uiLayer);
        _resetBtnText = AddButton(panel.transform, $"[{KeyName(resetKey)}]  Reset Dam", () => ResetDam(), uiLayer);
    }

    // ── UI helpers (same style as KitchenMinigame) ──────────────────────

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

        var textGO = new GameObject("Text");
        textGO.layer = layer;
        textGO.AddComponent<RectTransform>();
        textGO.AddComponent<CanvasRenderer>();
        textGO.transform.SetParent(go.transform, false);

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = 18;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.richText = true;

        var textRT = textGO.GetComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero;
        textRT.offsetMax = Vector2.zero;

        return tmp;
    }

    string KeyName(KeyCode k) => k switch
    {
        KeyCode.Alpha1 => "1",
        KeyCode.Alpha2 => "2",
        KeyCode.Alpha3 => "3",
        _ => k.ToString()
    };

    public bool IsBroken => _isBroken;
}
