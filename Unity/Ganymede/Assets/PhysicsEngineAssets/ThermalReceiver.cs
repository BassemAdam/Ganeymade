using UnityEngine;
using System.Collections;
using System;
using System.Runtime.InteropServices;
using UnityEngine.Rendering;
using System.Collections.Generic;

public class ThermalReceiver : MonoBehaviour
{
    // ─── Native plugin imports ──────────────────────────────────────────

#if (PLATFORM_IOS || PLATFORM_TVOS || PLATFORM_BRATWURST || PLATFORM_SWITCH) && !UNITY_EDITOR
    [DllImport("__Internal")]
#else
    [DllImport("RenderingPlugin")]
#endif
    private static extern void SetComputeData(float[] data, int count);

#if (PLATFORM_IOS || PLATFORM_TVOS || PLATFORM_BRATWURST || PLATFORM_SWITCH) && !UNITY_EDITOR
    [DllImport("__Internal")]
#else
    [DllImport("RenderingPlugin")]
#endif
    private static extern void GetComputeResult(float[] outData, int count);

#if (PLATFORM_IOS || PLATFORM_TVOS || PLATFORM_BRATWURST || PLATFORM_SWITCH) && !UNITY_EDITOR
    [DllImport("__Internal")]
#else
    [DllImport("RenderingPlugin")]
#endif
    private static extern bool IsComputeDone();

#if (PLATFORM_IOS || PLATFORM_TVOS || PLATFORM_BRATWURST || PLATFORM_SWITCH) && !UNITY_EDITOR
    [DllImport("__Internal")]
#else
    [DllImport("RenderingPlugin")]
#endif
    private static extern IntPtr GetRenderEventFunc();

#if (PLATFORM_IOS || PLATFORM_TVOS || PLATFORM_BRATWURST || PLATFORM_SWITCH) && !UNITY_EDITOR
    [DllImport("__Internal")]
#else
    [DllImport("RenderingPlugin")]
#endif
    private static extern IntPtr GetComputeOutputBuffer();

#if (PLATFORM_IOS || PLATFORM_TVOS || PLATFORM_BRATWURST || PLATFORM_SWITCH) && !UNITY_EDITOR
    [DllImport("__Internal")]
#else
    [DllImport("RenderingPlugin")]
#endif
    private static extern void SetSimParams(float dt, float alpha, uint gridWidth, uint gridHeight, uint gridDepth);
#if (PLATFORM_IOS || PLATFORM_TVOS || PLATFORM_BRATWURST || PLATFORM_SWITCH) && !UNITY_EDITOR
    [DllImport("__Internal")]
#else
    [DllImport("RenderingPlugin")]
#endif
    private static extern void SetMaskData(uint[] mask, int count);

    // ─── Configuration ──────────────────────────────────────────────────

    [Header("Grid Settings")]
    [Range(4, 128)] public int gridWidth  = 32;
    [Range(4, 128)] public int gridHeight = 32;
    [Range(4, 128)] public int gridDepth  = 32;

    [Header("Simulation Parameters")]
    [Range(0.01f, 10.0f)] public float alpha = 1.0f;

    [Header("Temperature Visualization")]
    public float maxDisplayTemp = 100f;
    [Range(1, 10)] public int visualizationInterval = 2;

    [Tooltip("Multiplier on cell size for adjacency detection. Increase if boundary cells are missed.")]
    public float adjacencyMultiplier = 0.6f;

    // ─── Private state ──────────────────────────────────────────────────

    private float[]   gridData;
    private uint[]    maskData;
    private float[]   readbackData;
    private int       frameCount  = 0;
    private bool      initialized = false;

    private Texture3D _tempTexture;
    private Color[]   _tempColors;
    private Material  _cubeMaterial;

    // Tracks which sources we've already baked into the mask
    // so we only recompute when something changes
    private List<HeatSourceObj> _trackedSources = new List<HeatSourceObj>();

    // ─── Lifecycle ──────────────────────────────────────────────────────

    IEnumerator Start()
    {
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
        {
            Debug.LogError("[ThermalReceiver] Requires Vulkan.");
            yield break;
        }

        int totalCells = gridWidth * gridHeight * gridDepth;
        gridData     = new float[totalCells];
        maskData     = new uint[totalCells];
        readbackData = new float[totalCells];

        // Upload empty grid first so the GPU buffers are initialized
        SetComputeData(gridData, totalCells);
        SetMaskData(maskData, totalCells);

        yield return new WaitForEndOfFrame();

        SetSimParams(Time.deltaTime, alpha,(uint)gridWidth, (uint)gridHeight, (uint)gridDepth);
        GL.IssuePluginEvent(GetRenderEventFunc(), 3);

        initialized = true;
        InitVisualization();

        // Scan the scene for heat sources immediately on start
        RefreshHeatSources();

        Debug.Log($"[ThermalReceiver] Started {gridWidth}x{gridHeight}x{gridDepth} grid.");
    }

    void Update()
    {
        if (!initialized) return;

        // Check if any heat sources have appeared, disappeared, or moved
        CheckForSourceChanges();

        SetSimParams(Time.deltaTime, alpha,(uint)gridWidth, (uint)gridHeight, (uint)gridDepth);
        GL.IssuePluginEvent(GetRenderEventFunc(), 3);
        frameCount++;

        if (frameCount % visualizationInterval == 0)
            UpdateVisualization();
    }

    // ─── Heat source detection ───────────────────────────────────────────

    /// <summary>
    /// Finds all HeatSources in the scene and rebuilds the mask
    /// from scratch based on which grid cells they overlap.
    /// </summary>
    public void RefreshHeatSources()
    {
        HeatSourceObj[] sources = FindObjectsByType<HeatSourceObj>(FindObjectsSortMode.None);

        int totalCells = gridWidth * gridHeight * gridDepth;
        System.Array.Clear(gridData, 0, totalCells);
        System.Array.Clear(maskData, 0, totalCells);

        int sliceSize = gridWidth * gridHeight;

        // Compute the world-space size of one grid cell
        Vector3 scale    = transform.lossyScale;
        float   cellSizeX = scale.x / gridWidth;
        float   cellSizeY = scale.y / gridHeight;
        float   cellSizeZ = scale.z / gridDepth;
        float   cellSize  = Mathf.Min(cellSizeX, cellSizeY, cellSizeZ);

        foreach (HeatSourceObj source in sources)
        {
            float temp = source.GetTemperature();

            for (int z = 0; z < gridDepth; z++)
            for (int y = 0; y < gridHeight; y++)
            for (int x = 0; x < gridWidth; x++)
            {
                Vector3 worldPos = GridToWorld(x, y, z);

                if (source.IsAdjacentToSource(worldPos, cellSize * adjacencyMultiplier))
                {
                    int idx = z * sliceSize + y * gridWidth + x;
                    gridData[idx] = temp;
                    maskData[idx] = 1u;
                }
            }
        }

        _trackedSources = new List<HeatSourceObj>(sources);

        SetComputeData(gridData, totalCells);
        SetMaskData(maskData, totalCells);

        Debug.Log($"[ThermalReceiver] Refreshed {sources.Length} heat source(s).");
    }

    /// <summary>
    /// Converts a grid cell (x,y,z) to its world-space center position.
    /// This is the inverse of the click-to-grid conversion.
    /// </summary>
    private Vector3 GridToWorld(int x, int y, int z)
    {
        // Cell center in local space [-0.5, 0.5]
        float lx = (x + 0.5f) / gridWidth  - 0.5f;
        float ly = (y + 0.5f) / gridHeight - 0.5f;
        float lz = (z + 0.5f) / gridDepth  - 0.5f;

        return transform.TransformPoint(new Vector3(lx, ly, lz));
    }

    /// <summary>
    /// Called every frame — detects if sources have been added, removed,
    /// or moved since last refresh, and triggers a rebuild if so.
    /// </summary>
    private void CheckForSourceChanges()
    {
        HeatSourceObj[] current = FindObjectsByType<HeatSourceObj>(FindObjectsSortMode.None);

        // Check count change
        if (current.Length != _trackedSources.Count)
        {
            RefreshHeatSources();
            return;
        }

        // Check if any source has moved significantly
        foreach (HeatSourceObj src in current)
        {
            if (!_trackedSources.Contains(src))
            {
                RefreshHeatSources();
                return;
            }

            // Rebuild if source moved more than half a cell size
            float cellSize = GetMinCellSize();
            if (src.transform.hasChanged)
            {
                RefreshHeatSources();
                src.transform.hasChanged = false;
                return;
            }
        }
    }

    private float GetMinCellSize()
    {
        Vector3 s = transform.lossyScale;
        return Mathf.Min(s.x / gridWidth, s.y / gridHeight, s.z / gridDepth);
    }

    // ─── Visualization ───────────────────────────────────────────────────

    private void InitVisualization()
    {
        int totalCells = gridWidth * gridHeight * gridDepth;

        _tempTexture = new Texture3D(gridWidth, gridHeight, gridDepth,
                                      TextureFormat.RFloat, false);
        _tempTexture.wrapMode   = TextureWrapMode.Clamp;
        _tempTexture.filterMode = FilterMode.Bilinear;

        _tempColors = new Color[totalCells];

        _cubeMaterial = GetComponent<Renderer>().material;
        _cubeMaterial.SetTexture("_TempTex", _tempTexture);
        _cubeMaterial.SetFloat("_MaxTemp", maxDisplayTemp);
    }

    private void UpdateVisualization()
    {
        GetComputeResult(readbackData, readbackData.Length);

        int total = gridWidth * gridHeight * gridDepth;
        for (int i = 0; i < total; i++)
            _tempColors[i] = new Color(readbackData[i], 0, 0, 1);

        _tempTexture.SetPixels(_tempColors);
        _tempTexture.Apply();
    }
}