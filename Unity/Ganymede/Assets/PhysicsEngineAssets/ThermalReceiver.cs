using UnityEngine;
using System.Collections;
using System;
using System.Runtime.InteropServices;
using UnityEngine.Rendering;
using System.Collections.Generic;

public class ThermalReceiver : MonoBehaviour
{
    // ─── Native plugin imports ──────────────────────────────────────────

[DllImport("RenderingPlugin")]
private static extern void SetSolidComputeData(float[] data, int count);

[DllImport("RenderingPlugin")]
private static extern void SetMaskData(uint[] mask, int count);

[DllImport("RenderingPlugin")]
private static extern void SetSolidSimParams(float dt, float alpha,
    uint gridWidth, uint gridHeight, uint gridDepth);

[DllImport("RenderingPlugin")]
private static extern void GetSolidComputeResult(float[] outData, int count);

[DllImport("RenderingPlugin")]
private static extern IntPtr GetRenderEventFunc();

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

    [Header("Debug")]
    [Tooltip("Log diffusion stats every N frames. 0 = disabled.")]
    public int debugLogInterval = 60;

    // ─── Private state ──────────────────────────────────────────────────

    private float[]   gridData;
    private uint[]    maskData;
    private float[]   readbackData;
    private int       frameCount  = 0;
    private bool      initialized = false;

    private Texture3D _tempTexture;
    private Material  _cubeMaterial;

    // For diffusion-change detection
    private float _prevMaxTemp = float.NegativeInfinity;
    private float _prevAvgTemp = float.NegativeInfinity;

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

        float dt = Time.deltaTime;
        SetSolidSimParams(dt, alpha, (uint)gridWidth, (uint)gridHeight, (uint)gridDepth);
        Debug.Log($"[ThermalReceiver] SetSolidSimParams → dt={dt:F4}  alpha={alpha}  " +
                  $"grid={gridWidth}x{gridHeight}x{gridDepth}");
        yield return new WaitForEndOfFrame();

        SetSolidComputeData(gridData, totalCells);
        SetMaskData(maskData, totalCells);

        yield return new WaitForEndOfFrame();

        GL.IssuePluginEvent(GetRenderEventFunc(), 4);

        initialized = true;
        InitVisualization();

        // Scan the scene for heat sources immediately on start
        RefreshHeatSources();

        Debug.Log($"[ThermalReceiver] Started {gridWidth}x{gridHeight}x{gridDepth} grid " +
                  $"({totalCells} cells).");
    }

    void Update()
    {
        if (!initialized) return;

        // Check if any heat sources have appeared, disappeared, or moved
        CheckForSourceChanges();

        float dt = Time.deltaTime;
        SetSolidSimParams(dt, alpha, (uint)gridWidth, (uint)gridHeight, (uint)gridDepth);
        GL.IssuePluginEvent(GetRenderEventFunc(), 4);
        frameCount++;

        if (frameCount % visualizationInterval == 0)
            UpdateVisualization();

        // ── Periodic diffusion health log ────────────────────────────────
        if (debugLogInterval > 0 && frameCount % debugLogInterval == 0)
            LogDiffusionStats(dt);
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
        Vector3 scale     = transform.lossyScale;
        float   cellSizeX = scale.x / gridWidth;
        float   cellSizeY = scale.y / gridHeight;
        float   cellSizeZ = scale.z / gridDepth;
        float   cellSize  = Mathf.Min(cellSizeX, cellSizeY, cellSizeZ);

        int totalPinnedCells = 0;

        foreach (HeatSourceObj source in sources)
        {
            float temp        = source.GetTemperature();
            int   pinnedByThis = 0;

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
                    pinnedByThis++;
                }
            }

            // Log per-source summary so we can catch zero-cell sources
            // (which usually means adjacencyMultiplier is too small or world transform is wrong)
            Debug.Log($"[ThermalReceiver] Source '{source.name}'  temp={temp:F1}  " +
                      $"pinned cells={pinnedByThis}  " +
                      $"(cellSize={cellSize:F4}  threshold={cellSize * adjacencyMultiplier:F4})");

            if (pinnedByThis == 0)
                Debug.LogWarning($"[ThermalReceiver] ⚠ Source '{source.name}' pinned 0 cells — " +
                                 $"it won't drive any diffusion. " +
                                 $"Try increasing adjacencyMultiplier (currently {adjacencyMultiplier}).");

            totalPinnedCells += pinnedByThis;
        }

        _trackedSources = new List<HeatSourceObj>(sources);

        SetSolidComputeData(gridData, totalCells);
        SetMaskData(maskData, totalCells);

        Debug.Log($"[ThermalReceiver] Mask uploaded — {sources.Length} source(s), " +
                  $"{totalPinnedCells}/{totalCells} cells pinned.");
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

        if (current.Length != _trackedSources.Count)
        {
            Debug.Log($"[ThermalReceiver] Source count changed ({_trackedSources.Count} → {current.Length}), refreshing mask.");
            RefreshHeatSources();
            // Reset hasChanged for all sources after rebuild
            foreach (HeatSourceObj src in current)
                src.transform.hasChanged = false;
            return;
        }

        bool needsRefresh = false;

        foreach (HeatSourceObj src in current)
        {
            if (!_trackedSources.Contains(src))
            {
                Debug.Log($"[ThermalReceiver] New source detected: '{src.name}', refreshing mask.");
                needsRefresh = true;
                break;
            }

            if (src.transform.hasChanged)
            {
                Debug.Log($"[ThermalReceiver] Source '{src.name}' moved, refreshing mask.");
                needsRefresh = true;
                // Don't break — keep iterating to reset all hasChanged flags below
            }
        }

        if (needsRefresh)
        {
            RefreshHeatSources();
            // Reset ALL sources, not just the one that triggered
            foreach (HeatSourceObj src in current)
                src.transform.hasChanged = false;
        }
    }
    private float GetMinCellSize()
    {
        Vector3 s = transform.lossyScale;
        return Mathf.Min(s.x / gridWidth, s.y / gridHeight, s.z / gridDepth);
    }

    // ─── Diffusion health logging ────────────────────────────────────────

    /// <summary>
    /// Reads back the current temperature buffer and logs min / max / avg
    /// so you can confirm heat is spreading frame-over-frame.
    /// Also warns if the buffer is all-zero (upload never reached the GPU)
    /// or completely static (diffusion is stalled / shader not running).
    /// </summary>
    private void LogDiffusionStats(float dt)
    {
        // Pull fresh data from the GPU
        GetSolidComputeResult(readbackData, readbackData.Length);

        float minT = float.MaxValue;
        float maxT = float.MinValue;
        double sumT = 0;
        int nonZero = 0;

        foreach (float t in readbackData)
        {
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
            sumT += t;
            if (t != 0f) nonZero++;
        }

        float avgT = (float)(sumT / readbackData.Length);

        Debug.Log($"[ThermalReceiver] Frame {frameCount}  dt={dt:F4}  " +
                  $"min={minT:F2}  max={maxT:F2}  avg={avgT:F4}  " +
                  $"nonZeroCells={nonZero}/{readbackData.Length}");

        // ── Sanity warnings ──────────────────────────────────────────────

        if (maxT == 0f && nonZero == 0)
            Debug.LogWarning("[ThermalReceiver] ⚠ Entire temperature buffer is zero — " +
                             "either no heat sources are pinned or GPU readback is broken.");

        if (Mathf.Approximately(maxT, _prevMaxTemp) && Mathf.Approximately(avgT, _prevAvgTemp)
            && maxT != 0f && frameCount > debugLogInterval)
            Debug.LogWarning($"[ThermalReceiver] ⚠ Temperature is completely static " +
                             $"(max={maxT:F2}, avg={avgT:F4} unchanged for {debugLogInterval} frames). " +
                             $"The shader may not be dispatching — check numthreads vs numGroups.");

        if (float.IsNaN(maxT) || float.IsInfinity(maxT))
            Debug.LogError($"[ThermalReceiver] ✖ NaN/Inf detected in temperature buffer! " +
                           $"alpha*dt={alpha * dt:F4} — simulation is numerically unstable. " +
                           $"Reduce alpha or dt (stability requires alpha*dt < 1/6 ≈ 0.1667).");

        _prevMaxTemp = maxT;
        _prevAvgTemp = avgT;
    }

    // ─── Visualization ───────────────────────────────────────────────────

    private void InitVisualization()
    {
        _tempTexture = new Texture3D(gridWidth, gridHeight, gridDepth,
                                    TextureFormat.RFloat, false);
        _tempTexture.wrapMode   = TextureWrapMode.Clamp;
        _tempTexture.filterMode = FilterMode.Bilinear;

        _cubeMaterial = GetComponent<Renderer>().material;
        _cubeMaterial.SetTexture("_TempTex", _tempTexture);
        _cubeMaterial.SetFloat("_MaxTemp", maxDisplayTemp);

        Debug.Log($"[ThermalReceiver] Visualization texture created " +
                  $"({gridWidth}x{gridHeight}x{gridDepth}, RFloat).");
    }

    private void UpdateVisualization()
    {
        GetSolidComputeResult(readbackData, readbackData.Length);

        _tempTexture.SetPixelData(readbackData, 0);
        _tempTexture.Apply();
    }
}