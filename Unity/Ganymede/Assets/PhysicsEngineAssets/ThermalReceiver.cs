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
    private static extern void SetSolidSimParams(float dt, uint gridWidth, uint gridHeight, uint gridDepth);

    [DllImport("RenderingPlugin")]
    private static extern void GetSolidComputeResult(float[] outData, int count);

    [DllImport("RenderingPlugin")]
    private static extern IntPtr GetRenderEventFunc();

    [DllImport("RenderingPlugin")]
    private static extern void SetDiffusivityData(float[] data, int count);

    [DllImport("RenderingPlugin")]
    private static extern void SetHeatSourceData(float[] pinTemperatures, int count);

    // ─── Configuration ──────────────────────────────────────────────────

    [Header("Temperature Visualization")]
    public float maxDisplayTemp = 300f;
    public float minDisplayTemp = 70f;
    public float defaultAmbientTemp = 70f;
    [Range(1, 10)] public int visualizationInterval = 2;

    [Header("Debug")]
    [Tooltip("Log diffusion stats every N frames. 0 = disabled.")]
    public int debugLogInterval = 60;

    [Header("Voxel Source")]
    public VoxelTracerSystem voxelTracer;

    [Header("Voxel Tracer")]
    public VoxelTracerCamera voxelTracerCamera;


    // ─── Private state ──────────────────────────────────────────────────

    private float[] gridData;
    private uint[] maskData;
    private float[] readbackData;
    private float[] diffusivityData;
    private float[] heatSourceData;
    private int frameCount = 0;
    private bool initialized = false;

    /// <summary>Live diffused temperature array (read-only). Null until initialised.</summary>
    public float[] LiveTemperatureData => initialized ? readbackData : null;
    public bool IsInitialized => initialized;

    private Texture3D _tempTexture;

    // For diffusion-change detection
    private float _prevMaxTemp = float.NegativeInfinity;
    private float _prevMinTemp = float.PositiveInfinity;
    private float _prevAvgTemp = float.NegativeInfinity;
    private bool _refreshingHeatSources = false;

    private int gridWidth, gridHeight, gridDepth;

    // Tracks the set of sources baked into heatSourceData so we can detect changes and re-upload only when necessary.
    private List<HeatSourceSnapshot> _trackedSourceSnapshots = new List<HeatSourceSnapshot>();

    // ─── Lifecycle ──────────────────────────────────────────────────────

    IEnumerator Start()
    {
        yield return new WaitUntil(() => voxelTracer != null && voxelTracer.IsReady);

        gridWidth = voxelTracer.Nx;
        gridHeight = voxelTracer.Ny;
        gridDepth = voxelTracer.Nz;

        int totalCells = gridWidth * gridHeight * gridDepth;
        gridData = new float[totalCells];
        maskData = new uint[totalCells];
        readbackData = new float[totalCells];
        diffusivityData = new float[totalCells];
        heatSourceData = new float[totalCells];

        Array.Fill(readbackData, defaultAmbientTemp);

        InitVisualization();

        yield return StartCoroutine(ReadTexturesAsync());
        DebugTextureReadback();

        SetSolidComputeData(gridData, totalCells);
        SetMaskData(maskData, totalCells);
        SetDiffusivityData(diffusivityData, totalCells);
        SetHeatSourceData(heatSourceData, totalCells);

        float dt = Time.deltaTime;

        SetSolidSimParams(dt, (uint)gridWidth, (uint)gridHeight, (uint)gridDepth);

        yield return new WaitForEndOfFrame();
        GL.IssuePluginEvent(GetRenderEventFunc(), 4);

        SnapshotCurrentSources();

        initialized = true;
    }

    void Update()
    {
        if (!initialized) return;

        // Check if the grid size changed 
        if (voxelTracer.Nx != gridWidth || voxelTracer.Ny != gridHeight || voxelTracer.Nz != gridDepth)
        {
            Debug.Log("[ThermalReceiver] Voxel grid changed — reinitializing diffusion.");
            StartCoroutine(Reinitialize());
            return;
        }
        // Re-read HeatSourceTexture whenever sources appear, disappear, move, or change temperature.  
        if (HeatSourcesChanged() && !_refreshingHeatSources)
        {
            Debug.Log("[ThermalReceiver] start heat source refresh");
            StartCoroutine(RefreshHeatSources());
        }

        float dt = Time.deltaTime;
        SetSolidSimParams(dt, (uint)gridWidth, (uint)gridHeight, (uint)gridDepth);
        GL.IssuePluginEvent(GetRenderEventFunc(), 4);
        frameCount++;

        if (frameCount % visualizationInterval == 0)
            UpdateVisualization();

        // ── Periodic diffusion health log ────────────────────────────────
        if (debugLogInterval > 0 && frameCount % debugLogInterval == 0)
            LogDiffusionStats(dt);
    }

    private IEnumerator ReadTexturesAsync()
    {
        int gx = voxelTracer.Nx;
        int gy = voxelTracer.Ny;
        int gz = voxelTracer.Nz;

        // Create a 2D RenderTexture to receive each slice copy
        RenderTexture tmp2D = new RenderTexture(gx, gy, 0, RenderTextureFormat.RFloat)
        {
            enableRandomWrite = false,
            filterMode = FilterMode.Point,
            dimension = UnityEngine.Rendering.TextureDimension.Tex2D
        };
        tmp2D.Create();

        // ── Fill texture → mask ──────────────────────────────────────────
        for (int z = 0; z < gz; z++)
        {
            Graphics.CopyTexture(voxelTracer.FillTexture, z, 0, tmp2D, 0, 0);

            var req = AsyncGPUReadback.Request(tmp2D);
            yield return new WaitUntil(() => req.done);

            if (req.hasError)
            {
                Debug.LogError($"[ThermalReceiver] FillTexture readback error at z={z}");
                continue;
            }

            var data = req.GetData<float>();
            for (int i = 0; i < data.Length; i++)
                maskData[z * gx * gy + i] = data[i] > 0.5f ? 1u : 0u;
        }

        // ── Temperature texture → gridData ───────────────────────────────
        for (int z = 0; z < gz; z++)
        {
            Graphics.CopyTexture(voxelTracer.TemperatureTexture, z, 0, tmp2D, 0, 0);

            var req = AsyncGPUReadback.Request(tmp2D);
            yield return new WaitUntil(() => req.done);

            if (req.hasError)
            {
                Debug.LogError($"[ThermalReceiver] TemperatureTexture readback error at z={z}");
                continue;
            }

            var data = req.GetData<float>();
            for (int i = 0; i < data.Length; i++)
                gridData[z * gx * gy + i] = data[i];
        }

        // ── Diffusivity texture → diffusivityData ────────────────────────
        for (int z = 0; z < gz; z++)
        {
            Graphics.CopyTexture(voxelTracer.DiffusivityTexture, z, 0, tmp2D, 0, 0);

            var req = AsyncGPUReadback.Request(tmp2D);
            yield return new WaitUntil(() => req.done);

            if (req.hasError)
            {
                Debug.LogError($"[ThermalReceiver] DiffusivityTexture readback error at z={z}");
                continue;
            }

            var data = req.GetData<float>();
            for (int i = 0; i < data.Length; i++)
                diffusivityData[z * gx * gy + i] = Mathf.Clamp(data[i], 0f, 30f);
        }

        // ── HeatSourceTexture → heatSourceData ───────────────────────────
        if (voxelTracer.HeatSourceTexture != null)
        {
            for (int z = 0; z < gz; z++)
            {
                Graphics.CopyTexture(voxelTracer.HeatSourceTexture, z, 0, tmp2D, 0, 0);
                var req = AsyncGPUReadback.Request(tmp2D);
                yield return new WaitUntil(() => req.done);

                if (req.hasError)
                {
                    Debug.LogError($"[ThermalReceiver] HeatSourceTexture readback error at z={z}");
                    continue;
                }

                var data = req.GetData<float>();
                for (int i = 0; i < data.Length; i++)
                    heatSourceData[z * gx * gy + i] = data[i];
            }
        }
        else
        {
            Debug.LogWarning("[ThermalReceiver] HeatSourceTexture is null — " +
                             "no heat sources will be pinned.  " +
                             "Apply the VoxelTracerSystem patch to generate this texture.");
            Array.Clear(heatSourceData, 0, heatSourceData.Length);
        }

        RenderTexture.active = null;
        tmp2D.Release();
        Destroy(tmp2D);
        Debug.Log("[ThermalReceiver] Texture readback complete. Uploading data to diffusion shader.");

        SetDiffusivityData(diffusivityData, gx * gy * gz);
    }
    //Re-reads only the HeatSourceTexture and re-uploads heatSourceData.
    private IEnumerator RefreshHeatSources()
    {
        if (_refreshingHeatSources) yield break;  // prevent concurrent refreshes if sources change mid-refresh
        _refreshingHeatSources = true;

        int gx = voxelTracer.Nx;
        int gy = voxelTracer.Ny;
        int gz = voxelTracer.Nz;

        if (voxelTracer.HeatSourceTexture == null) yield break;

        RenderTexture tmp2D = new RenderTexture(gx, gy, 0, RenderTextureFormat.RFloat)
        {
            enableRandomWrite = false,
            filterMode = FilterMode.Point,
            dimension = UnityEngine.Rendering.TextureDimension.Tex2D
        };
        tmp2D.Create();

        for (int z = 0; z < gz; z++)
        {
            Graphics.CopyTexture(voxelTracer.HeatSourceTexture, z, 0, tmp2D, 0, 0);
            var req = AsyncGPUReadback.Request(tmp2D);
            yield return new WaitUntil(() => req.done);

            if (req.hasError) continue;

            var data = req.GetData<float>();
            for (int i = 0; i < data.Length; i++)
                heatSourceData[z * gx * gy + i] = data[i];
        }

        tmp2D.Release();
        Destroy(tmp2D);

        SetHeatSourceData(heatSourceData, gx * gy * gz);

        SnapshotCurrentSources();
        _refreshingHeatSources = false;

        int pinnedCount = 0;
        foreach (float v in heatSourceData) if (v > 0f) pinnedCount++;
        Debug.Log($"[ThermalReceiver] Heat sources refreshed — {pinnedCount} pinned voxels.");
    }

    // ─── Change detection ────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the registered heat sources differ from the last snapshot.
    /// Checks: count, instance IDs, temperatures, active state, positions, radii.
    /// </summary>
    private bool HeatSourcesChanged()
    {
        var sources = VoxelTracerSystem.HeatSources;
        if (sources.Count != _trackedSourceSnapshots.Count) return true;

        // Build a lookup by instanceID instead of relying on HashSet iteration order
        var snapshotMap = new Dictionary<int, HeatSourceSnapshot>(_trackedSourceSnapshots.Count);
        foreach (var snap in _trackedSourceSnapshots)
            snapshotMap[snap.instanceID] = snap;

        foreach (var hs in sources)
        {
            if (hs == null) continue;
            if (!snapshotMap.TryGetValue(hs.GetInstanceID(), out var snap)) return true;
            if (snap.active != hs.active) return true;
            if (!Mathf.Approximately(snap.temperature, hs.temperature)) return true;
            if ((snap.position - hs.transform.position).sqrMagnitude > 1e-6f) return true;
            if (!Mathf.Approximately(snap.radius, hs.radius)) return true;
        }
        return false;
    }

    private void SnapshotCurrentSources()
    {
        _trackedSourceSnapshots.Clear();
        foreach (var hs in VoxelTracerSystem.HeatSources)
        {
            if (hs == null) continue;
            _trackedSourceSnapshots.Add(new HeatSourceSnapshot
            {
                instanceID = hs.GetInstanceID(),
                temperature = hs.temperature,
                active = hs.active,
                position = hs.transform.position,
                radius = hs.radius
            });
        }
    }

    private IEnumerator Reinitialize()
    {
        initialized = false;

        gridWidth = voxelTracer.Nx;
        gridHeight = voxelTracer.Ny;
        gridDepth = voxelTracer.Nz;

        int totalCells = gridWidth * gridHeight * gridDepth;
        gridData = new float[totalCells];
        maskData = new uint[totalCells];
        readbackData = new float[totalCells];
        diffusivityData = new float[totalCells];
        heatSourceData = new float[totalCells];

        yield return StartCoroutine(ReadTexturesAsync());
        DebugTextureReadback();

        SetSolidComputeData(gridData, totalCells);
        SetMaskData(maskData, totalCells);
        SetDiffusivityData(diffusivityData, totalCells);
        SetHeatSourceData(heatSourceData, totalCells);
        SetSolidSimParams(0.016f, (uint)gridWidth, (uint)gridHeight, (uint)gridDepth);

        yield return new WaitForEndOfFrame();

        //InitVisualization();
        frameCount = 0;
        initialized = true;
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
        GetSolidComputeResult(readbackData, readbackData.Length);

        float minT = float.MaxValue;
        float maxT = float.MinValue;
        double sumT = 0;
        int solidCount = 0;
        int pinnedCount = 0;

        for (int i = 0; i < readbackData.Length; i++)
        {
            if (maskData[i] == 0) continue;
            float t = readbackData[i];
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
            sumT += t;
            solidCount++;
            if (heatSourceData[i] > 0f) pinnedCount++;
        }

        float avgT = solidCount > 0 ? (float)(sumT / solidCount) : 0f;

        // Per-source stats — CPU-side overlap check mirroring WriteHeatSources kernel
        var sources = VoxelTracerSystem.HeatSources;
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"[ThermalReceiver] Frame {frameCount}  dt={dt:F4}  " +
                    $"min={minT:F2}  max={maxT:F2}  avg={avgT:F4}  " +
                    $"solidCells={solidCount}/{readbackData.Length}  pinnedVoxels={pinnedCount}");

        foreach (var hs in sources)
        {
            if (hs == null || !hs.isActiveAndEnabled || !hs.active) continue;

            // Reconstruct source bounds exactly as StampHeatSources does
            Vector3 srcPos;
            Vector3 srcExtents;
            bool isSphere;

            if (hs.radius > 0f)
            {
                srcPos = hs.transform.position;
                srcExtents = Vector3.one * hs.radius;
                isSphere = true;
            }
            else
            {
                var r = hs.GetComponent<Renderer>();
                if (r != null)
                {
                    srcPos = r.bounds.center;
                    srcExtents = r.bounds.extents + Vector3.one * (voxelTracer.ActiveVoxelSize * 0.5f);
                }
                else
                {
                    srcPos = hs.transform.position;
                    srcExtents = Vector3.one * 0.5f;
                }
                isSphere = false;
            }

            int pinnedBySource = 0;
            float srcMinT = float.MaxValue;
            float srcMaxT = float.MinValue;
            double srcSumT = 0;
            int srcSolidCount = 0;

            for (int z = 0; z < gridDepth; z++)
                for (int y = 0; y < gridHeight; y++)
                    for (int x = 0; x < gridWidth; x++)
                    {
                        int i = z * gridWidth * gridHeight + y * gridWidth + x;
                        if (maskData[i] == 0) continue;

                        // World position of voxel centre — mirrors the kernel's worldPos calculation
                        Vector3 worldPos = voxelTracer.ActiveGridMin +
                                        (new Vector3(x, y, z) + Vector3.one * 0.5f) * voxelTracer.ActiveVoxelSize;

                        bool inside;
                        if (isSphere)
                        {
                            Vector3 delta = worldPos - srcPos;
                            inside = delta.sqrMagnitude <= srcExtents.x * srcExtents.x;
                        }
                        else
                        {
                            Vector3 d = new Vector3(
                                Mathf.Abs(worldPos.x - srcPos.x) - srcExtents.x,
                                Mathf.Abs(worldPos.y - srcPos.y) - srcExtents.y,
                                Mathf.Abs(worldPos.z - srcPos.z) - srcExtents.z);
                            inside = d.x <= 0f && d.y <= 0f && d.z <= 0f;
                        }

                        if (!inside) continue;

                        pinnedBySource++;

                        // Neighbouring solid voxels that aren't pinned — these show diffusion effect
                        float t = readbackData[i];
                        srcSumT += t;
                        srcSolidCount++;
                        if (t < srcMinT) srcMinT = t;
                        if (t > srcMaxT) srcMaxT = t;
                    }

            // Measure diffusion spread: solid voxels adjacent to this source's pinned region
            // whose temperature is above ambient but not pinned (heat has diffused into them)
            int diffusedNeighbours = 0;
            float diffusedAvg = 0f;
            double diffusedSum = 0;
            float srcAvgT = srcSolidCount > 0 ? (float)(srcSumT / srcSolidCount) : 0f;


            for (int z = 0; z < gridDepth; z++)
                for (int y = 0; y < gridHeight; y++)
                    for (int x = 0; x < gridWidth; x++)
                    {
                        int i = z * gridWidth * gridHeight + y * gridWidth + x;
                        if (maskData[i] == 0) continue;
                        if (heatSourceData[i] > 0f) continue;  // skip pinned voxels

                        float t = readbackData[i];
                        if (pinnedBySource == 0)
                        {
                            sb.AppendLine($"  └ {hs.name} — WARNING: 0 pinned voxels found in heatSourceData. " +
                                        $"Source bounds may not overlap any solid voxel, or heatSourceData " +
                                        $"is stale. regionAvg={srcAvgT:F2}° (reading diffused temp, not pinned).");
                            continue;
                        }
                        // Check if any face-neighbour of this voxel is pinned by this source
                        bool adjacentToSource = false;
                        int[] dx = { -1, 1, 0, 0, 0, 0 };
                        int[] dy = { 0, 0, -1, 1, 0, 0 };
                        int[] dz = { 0, 0, 0, 0, -1, 1 };
                        for (int n = 0; n < 6; n++)
                        {
                            int nx = x + dx[n], ny = y + dy[n], nz = z + dz[n];
                            if (nx < 0 || nx >= gridWidth ||
                                ny < 0 || ny >= gridHeight ||
                                nz < 0 || nz >= gridDepth) continue;

                            int ni = nz * gridWidth * gridHeight + ny * gridWidth + nx;
                            if (heatSourceData[ni] <= 0f) continue;

                            // Check if that neighbour belongs to this source
                            Vector3 nWorldPos = voxelTracer.ActiveGridMin +
                                                (new Vector3(nx, ny, nz) + Vector3.one * 0.5f) * voxelTracer.ActiveVoxelSize;
                            bool nInside;
                            if (isSphere)
                            {
                                Vector3 delta = nWorldPos - srcPos;
                                nInside = delta.sqrMagnitude <= srcExtents.x * srcExtents.x;
                            }
                            else
                            {
                                Vector3 d = new Vector3(
                                    Mathf.Abs(nWorldPos.x - srcPos.x) - srcExtents.x,
                                    Mathf.Abs(nWorldPos.y - srcPos.y) - srcExtents.y,
                                    Mathf.Abs(nWorldPos.z - srcPos.z) - srcExtents.z);
                                nInside = d.x <= 0f && d.y <= 0f && d.z <= 0f;
                            }
                            if (nInside) { adjacentToSource = true; break; }
                        }

                        if (!adjacentToSource) continue;
                        diffusedNeighbours++;
                        diffusedSum += t;
                    }

            diffusedAvg = diffusedNeighbours > 0 ? (float)(diffusedSum / diffusedNeighbours) : 0f;
            sb.AppendLine($"  └ {hs.name} ({(isSphere ? "sphere" : "AABB")})  " +
                        $"pinTemp={hs.temperature:F1}°  pinnedVoxels={pinnedBySource}  " +
                        $"regionAvg={srcAvgT:F2}°  " +
                        $"diffusedNeighbours={diffusedNeighbours}  diffusedAvg={diffusedAvg:F2}°");
        }

        Debug.Log(sb.ToString());
        _prevMaxTemp = maxT;
        _prevAvgTemp = avgT;
    }

    private void DebugTextureReadback()
    {
        int totalCells = gridWidth * gridHeight * gridDepth;

        int filledVoxels = 0;
        int sourceVoxels = 0;
        int filledWithNonZeroDiff = 0;
        int filledWithZeroDiff = 0;
        int filledWithNonZeroTemp = 0;
        int filledWithZeroTemp = 0;

        float minDiff = float.MaxValue, maxDiff = float.MinValue, sumDiff = 0f;
        float minTempFilled = float.MaxValue, maxTempFilled = float.MinValue;
        float minDiffFilled = float.MaxValue, maxDiffFilled = float.MinValue;

        for (int i = 0; i < totalCells; i++)
        {
            float diff = diffusivityData[i];
            float temp = gridData[i];
            float pin = heatSourceData[i];

            if (diff < minDiff) minDiff = diff;
            if (diff > maxDiff) maxDiff = diff;
            sumDiff += diff;

            if (maskData[i] == 1u)
            {
                filledVoxels++;
                if (pin > 0f) sourceVoxels++;

                if (temp > 0f)
                {
                    filledWithNonZeroTemp++;
                    if (temp < minTempFilled) minTempFilled = temp;
                    if (temp > maxTempFilled) maxTempFilled = temp;
                }
                else { filledWithZeroTemp++; }

                if (diff > 0f)
                {
                    filledWithNonZeroDiff++;
                    if (diff < minDiffFilled) minDiffFilled = diff;
                    if (diff > maxDiffFilled) maxDiffFilled = diff;
                }
                else { filledWithZeroDiff++; }
            }
        }

        float avgDiff = sumDiff / totalCells;

        Debug.Log($"[ThermalReceiver] === Texture Readback Debug ===");
        Debug.Log($"[ThermalReceiver] Grid: {gridWidth}x{gridHeight}x{gridDepth} = {totalCells} total cells");
        Debug.Log($"[ThermalReceiver] Mask  — filled: {filledVoxels}/{totalCells} " +
                  $"({100f * filledVoxels / totalCells:F1}%)  " +
                  $"of which are pinned sources: {sourceVoxels}");
        Debug.Log($"[ThermalReceiver] Diff  — all cells:   min={minDiff:F4}  max={maxDiff:F4}  avg={avgDiff:F4}");

        if (filledVoxels > 0)
        {
            Debug.Log($"[ThermalReceiver] Diff  — filled only: " +
                      $"nonZero={filledWithNonZeroDiff}/{filledVoxels}, " +
                      $"zero={filledWithZeroDiff}/{filledVoxels}, " +
                      $"min={minDiffFilled:F4}  max={maxDiffFilled:F4}");
            Debug.Log($"[ThermalReceiver] Temp  — filled only: " +
                      $"nonZero={filledWithNonZeroTemp}/{filledVoxels}, " +
                      $"zero={filledWithZeroTemp}/{filledVoxels}, " +
                      $"min={minTempFilled:F4}  max={maxTempFilled:F4}");
        }
        else
        {
            Debug.LogWarning("[ThermalReceiver] ⚠ No filled voxels found — mask is all zero.");
        }

        if (sourceVoxels == 0)
            Debug.LogWarning("[ThermalReceiver] ⚠ No pinned source voxels found. " +
                             "Either no VoxelHeatSource components exist, or the " +
                             "HeatSourceTexture patch has not been applied.");

        if (maxDiff == 0f)
            Debug.LogWarning("[ThermalReceiver] ⚠ Diffusivity buffer is entirely zero.");
    }
    // ─── Visualization ───────────────────────────────────────────────────

    private void InitVisualization()
    {
        _tempTexture = new Texture3D(gridWidth, gridHeight, gridDepth, TextureFormat.RFloat, false);
        _tempTexture.wrapMode = TextureWrapMode.Clamp;
        _tempTexture.filterMode = FilterMode.Bilinear;

        _tempTexture.SetPixelData(readbackData, 0);
        _tempTexture.Apply();

        // Push to the ray marcher instead of the old cube material
        voxelTracerCamera.tempTexture = _tempTexture;
        voxelTracerCamera.minDisplayTemp = minDisplayTemp;
        voxelTracerCamera.maxDisplayTemp = maxDisplayTemp;
    }

    private void UpdateVisualization()
    {
        GetSolidComputeResult(readbackData, readbackData.Length);
        _tempTexture.SetPixelData(readbackData, 0);
        _tempTexture.Apply();

        // Keep min/max in sync every frame
        if (voxelTracerCamera != null)
        {
            voxelTracerCamera.minDisplayTemp = minDisplayTemp;
            voxelTracerCamera.maxDisplayTemp = _prevMaxTemp > 0f ? _prevMaxTemp : maxDisplayTemp;
        }
    }

    // ─── Structs ────────────────────────────────────────────────────────
    private struct HeatSourceSnapshot
    {
        public int instanceID;
        public float temperature;
        public bool active;
        public Vector3 position;
        public float radius;
    }
}