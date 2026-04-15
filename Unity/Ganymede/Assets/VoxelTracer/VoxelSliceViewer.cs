using UnityEngine;

/// <summary>
/// Full-screen slice viewer of the voxel fill volume.
/// Press a key to toggle the overlay on/off.
/// Scrub slices with scroll wheel while overlay is visible.
/// Press Tab to cycle display modes: Fill → SDF → HeatMap → Diffusivity → Fill…
/// In SDF mode, hover the mouse to see the SDF value at that voxel.
/// In HeatMap mode, hover to see the temperature value.
/// In Diffusivity mode, hover to see the thermal diffusivity value.
/// </summary>
[RequireComponent(typeof(Camera))]
[DefaultExecutionOrder(200)]
public class VoxelSliceViewer : MonoBehaviour
{
    [Header("References")]
    public VoxelTracerSystem voxelSystem;
    [Tooltip("Optional: assign to show live diffused temperature instead of initial stamp")]
    public ThermalReceiver thermalReceiver;

    [Header("Slice")]
    public SliceAxis axis = SliceAxis.Y;
    [Range(0f, 1f)]
    public float slicePosition = 0.5f;

    [Header("Colors")]
    public Color filledColor = Color.white;
    public Color emptyColor = new Color(0, 0, 0, 0.85f);
    public Color surfaceColor = new Color(0.2f, 0.8f, 0.2f, 1f);
    public bool highlightSurface = true;

    [Header("SDF Display")]
    [Tooltip("Maximum SDF distance (in world units) for color mapping")]
    public float sdfDisplayRange = 5f;

    [Header("HeatMap Display")]
    [Tooltip("Temperature mapped to cold end of gradient")]
    public float heatMapMin = 0f;
    [Tooltip("Temperature mapped to hot end of gradient")]
    public float heatMapMax = 100f;

    [Header("Diffusivity Display")]
    [Tooltip("Maximum diffusivity for color mapping")]
    public float diffusivityMax = 1f;

    [Header("Controls")]
    [Tooltip("Key to toggle the slice overlay on/off")]
    public KeyCode toggleKey = KeyCode.F2;

    public enum SliceAxis { X, Y, Z }
    public enum DisplayMode { Fill, SDF, HeatMap, Diffusivity }

    DisplayMode _displayMode = DisplayMode.Fill;

    bool _visible;
    Texture2D _sliceTex;
    float[] _fillData;
    float[] _sdfData;
    float[] _tempData;
    float[] _diffData;
    int _cachedNx, _cachedNy, _cachedNz;
    int _texW, _texH;
    float _lastRefresh = -999f;
    GUIStyle _labelStyle;
    GUIStyle _boxStyle;

    // Hover info for SDF mode
    float _imgX, _imgY, _imgW, _imgH;
    int _sliceIdx;

    void OnDisable()
    {
        if (_sliceTex != null) { Destroy(_sliceTex); _sliceTex = null; }
        _fillData = null;
        _sdfData = null;
        _tempData = null;
        _diffData = null;
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            _visible = !_visible;

        if (!_visible) return;
        if (voxelSystem == null || !voxelSystem.IsReady) return;

        // Auto-find ThermalReceiver if not assigned
        if (thermalReceiver == null)
            thermalReceiver = FindObjectOfType<ThermalReceiver>();

        // Scroll wheel scrubs slice position when overlay is visible
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.001f)
        {
            int maxSlice = GetSliceCount() - 1;
            if (maxSlice > 0)
            {
                float step = 1f / maxSlice;
                slicePosition = Mathf.Clamp01(slicePosition + scroll * step * 5f);
            }
        }

        // Axis switching: 1/2/3 keys while overlay is open
        if (Input.GetKeyDown(KeyCode.Alpha1)) axis = SliceAxis.X;
        if (Input.GetKeyDown(KeyCode.Alpha2)) axis = SliceAxis.Y;
        if (Input.GetKeyDown(KeyCode.Alpha3)) axis = SliceAxis.Z;

        // Tab cycles display mode: Fill → SDF → HeatMap → Diffusivity → Fill…
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            _displayMode = _displayMode switch
            {
                DisplayMode.Fill => DisplayMode.SDF,
                DisplayMode.SDF => DisplayMode.HeatMap,
                DisplayMode.HeatMap => DisplayMode.Diffusivity,
                _ => DisplayMode.Fill
            };
            _lastRefresh = -999f; // force immediate rebuild
        }

        if (Time.time - _lastRefresh < 0.1f) return;
        _lastRefresh = Time.time;
        BuildSliceTexture();
    }

    int GetSliceCount()
    {
        if (voxelSystem == null) return 1;
        return axis switch
        {
            SliceAxis.X => voxelSystem.Nx,
            SliceAxis.Y => voxelSystem.Ny,
            SliceAxis.Z => voxelSystem.Nz,
            _ => 1
        };
    }

    void ReadFillData()
    {
        int nx = voxelSystem.Nx;
        int ny = voxelSystem.Ny;
        int nz = voxelSystem.Nz;
        int total = nx * ny * nz;

        var fillRT = voxelSystem.FillTexture;
        if (fillRT == null) return;

        if (_fillData == null || _fillData.Length != total ||
            _cachedNx != nx || _cachedNy != ny || _cachedNz != nz)
        {
            _fillData = new float[total];
            _cachedNx = nx;
            _cachedNy = ny;
            _cachedNz = nz;
        }

        Read3DTexture(fillRT, _fillData, nx, ny, nz);
    }

    void ReadSDFData()
    {
        int nx = voxelSystem.Nx;
        int ny = voxelSystem.Ny;
        int nz = voxelSystem.Nz;
        int total = nx * ny * nz;

        var sdfRT = voxelSystem.SDFTexture;
        if (sdfRT == null) return;

        if (_sdfData == null || _sdfData.Length != total)
            _sdfData = new float[total];

        Read3DTexture(sdfRT, _sdfData, nx, ny, nz);
    }

    void ReadTemperatureData()
    {
        int nx = voxelSystem.Nx;
        int ny = voxelSystem.Ny;
        int nz = voxelSystem.Nz;
        int total = nx * ny * nz;

        // Prefer live diffused temperature from ThermalReceiver when available
        if (thermalReceiver != null && thermalReceiver.IsInitialized)
        {
            var live = thermalReceiver.LiveTemperatureData;
            if (live != null && live.Length == total)
            {
                if (_tempData == null || _tempData.Length != total)
                    _tempData = new float[total];
                System.Array.Copy(live, _tempData, total);
                return;
            }
        }

        // Fallback: read the initial stamped temperature from the voxel system
        var tempRT = voxelSystem.TemperatureTexture;
        if (tempRT == null) return;

        if (_tempData == null || _tempData.Length != total)
            _tempData = new float[total];

        Read3DTexture(tempRT, _tempData, nx, ny, nz);
    }

    void ReadDiffusivityData()
    {
        int nx = voxelSystem.Nx;
        int ny = voxelSystem.Ny;
        int nz = voxelSystem.Nz;
        int total = nx * ny * nz;

        var diffRT = voxelSystem.DiffusivityTexture;
        if (diffRT == null) return;

        if (_diffData == null || _diffData.Length != total)
            _diffData = new float[total];

        Read3DTexture(diffRT, _diffData, nx, ny, nz);
    }

    void Read3DTexture(RenderTexture rt, float[] dest, int nx, int ny, int nz)
    {
        var tempRT = RenderTexture.GetTemporary(nx, ny, 0, RenderTextureFormat.RFloat);
        var tempTex = new Texture2D(nx, ny, TextureFormat.RFloat, false);

        for (int z = 0; z < nz; z++)
        {
            Graphics.CopyTexture(rt, z, 0, tempRT, 0, 0);
            var prev = RenderTexture.active;
            RenderTexture.active = tempRT;
            tempTex.ReadPixels(new Rect(0, 0, nx, ny), 0, 0, false);
            tempTex.Apply(false);
            RenderTexture.active = prev;

            var raw = tempTex.GetRawTextureData<float>();
            for (int i = 0; i < nx * ny; i++)
                dest[z * (nx * ny) + i] = raw[i];
        }

        RenderTexture.ReleaseTemporary(tempRT);
        Destroy(tempTex);
    }

    float GetFill(int x, int y, int z)
    {
        if (x < 0 || x >= _cachedNx || y < 0 || y >= _cachedNy || z < 0 || z >= _cachedNz) return 0;
        return _fillData[z * (_cachedNx * _cachedNy) + y * _cachedNx + x];
    }

    float GetSDF(int x, int y, int z)
    {
        if (_sdfData == null) return 0;
        if (x < 0 || x >= _cachedNx || y < 0 || y >= _cachedNy || z < 0 || z >= _cachedNz) return 0;
        return _sdfData[z * (_cachedNx * _cachedNy) + y * _cachedNx + x];
    }

    float GetTemperature(int x, int y, int z)
    {
        if (_tempData == null) return 0;
        if (x < 0 || x >= _cachedNx || y < 0 || y >= _cachedNy || z < 0 || z >= _cachedNz) return 0;
        return _tempData[z * (_cachedNx * _cachedNy) + y * _cachedNx + x];
    }

    float GetDiffusivity(int x, int y, int z)
    {
        if (_diffData == null) return 0;
        if (x < 0 || x >= _cachedNx || y < 0 || y >= _cachedNy || z < 0 || z >= _cachedNz) return 0;
        return _diffData[z * (_cachedNx * _cachedNy) + y * _cachedNx + x];
    }

    bool IsSurface(int x, int y, int z)
    {
        if (GetFill(x, y, z) < 0.5f) return false;
        return GetFill(x - 1, y, z) < 0.5f || GetFill(x + 1, y, z) < 0.5f ||
               GetFill(x, y - 1, z) < 0.5f || GetFill(x, y + 1, z) < 0.5f ||
               GetFill(x, y, z - 1) < 0.5f || GetFill(x, y, z + 1) < 0.5f;
    }

    void BuildSliceTexture()
    {
        ReadFillData();
        if (_fillData == null) return;

        if (_displayMode == DisplayMode.SDF)
            ReadSDFData();
        if (_displayMode == DisplayMode.HeatMap)
            ReadTemperatureData();
        if (_displayMode == DisplayMode.Diffusivity)
            ReadDiffusivityData();

        int nx = _cachedNx, ny = _cachedNy, nz = _cachedNz;

        // Determine slice dimensions based on axis
        int sliceW, sliceH;
        switch (axis)
        {
            case SliceAxis.X:
                sliceW = nz; sliceH = ny;
                _sliceIdx = Mathf.Clamp(Mathf.RoundToInt(slicePosition * (nx - 1)), 0, nx - 1);
                break;
            case SliceAxis.Y:
                sliceW = nx; sliceH = nz;
                _sliceIdx = Mathf.Clamp(Mathf.RoundToInt(slicePosition * (ny - 1)), 0, ny - 1);
                break;
            default: // Z
                sliceW = nx; sliceH = ny;
                _sliceIdx = Mathf.Clamp(Mathf.RoundToInt(slicePosition * (nz - 1)), 0, nz - 1);
                break;
        }

        if (sliceW <= 0 || sliceH <= 0) return;

        if (_sliceTex == null || _texW != sliceW || _texH != sliceH)
        {
            if (_sliceTex != null) Destroy(_sliceTex);
            _sliceTex = new Texture2D(sliceW, sliceH, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _texW = sliceW;
            _texH = sliceH;
        }

        var pixels = _sliceTex.GetPixels32();

        for (int v = 0; v < sliceH; v++)
        {
            for (int u = 0; u < sliceW; u++)
            {
                int x, y, z;
                switch (axis)
                {
                    case SliceAxis.X: x = _sliceIdx; y = v; z = u; break;
                    case SliceAxis.Y: x = u; y = _sliceIdx; z = v; break;
                    default: x = u; y = v; z = _sliceIdx; break;
                }

                Color c;
                if (_displayMode == DisplayMode.SDF && _sdfData != null)
                {
                    c = SDFToColor(GetSDF(x, y, z));
                }
                else if (_displayMode == DisplayMode.HeatMap && _tempData != null)
                {
                    float fill = GetFill(x, y, z);
                    if (fill > 0.5f)
                        c = TemperatureToColor(GetTemperature(x, y, z));
                    else
                        c = emptyColor;
                }
                else if (_displayMode == DisplayMode.Diffusivity && _diffData != null)
                {
                    float fill = GetFill(x, y, z);
                    if (fill > 0.5f)
                        c = DiffusivityToColor(GetDiffusivity(x, y, z));
                    else
                        c = emptyColor;
                }
                else
                {
                    float fill = GetFill(x, y, z);
                    if (fill > 0.5f)
                    {
                        if (highlightSurface && IsSurface(x, y, z))
                            c = surfaceColor;
                        else
                            c = filledColor;
                    }
                    else
                    {
                        c = emptyColor;
                    }
                }

                pixels[v * sliceW + u] = c;
            }
        }

        _sliceTex.SetPixels32(pixels);
        _sliceTex.Apply(false);
    }

    /// <summary>Map SDF value to a color: blue (inside/negative) → black (zero/surface) → red/yellow (outside/positive)</summary>
    Color SDFToColor(float sdf)
    {
        float range = Mathf.Max(sdfDisplayRange, 0.001f);
        float t = Mathf.Clamp(sdf / range, -1f, 1f); // -1 = deep inside, +1 = far outside

        if (t < 0f)
        {
            // Inside geometry: blue intensity (deep = bright blue, near surface = dark)
            float a = -t; // 0..1
            return new Color(0f, 0f, a, 1f);
        }
        else if (t < 0.01f)
        {
            // Near zero = surface boundary = green
            return new Color(0f, 1f, 0f, 1f);
        }
        else
        {
            // Outside geometry: red → yellow (far = bright yellow, near surface = dark red)
            float a = t; // 0..1
            return new Color(a, a * 0.5f, 0f, 1f);
        }
    }

    /// <summary>Map temperature to a cold-to-hot gradient: blue → cyan → green → yellow → red</summary>
    Color TemperatureToColor(float temp)
    {
        float range = Mathf.Max(heatMapMax - heatMapMin, 0.001f);
        float t = Mathf.Clamp01((temp - heatMapMin) / range);

        // 5-stop gradient: blue(0) → cyan(0.25) → green(0.5) → yellow(0.75) → red(1)
        if (t < 0.25f)
        {
            float s = t / 0.25f;
            return new Color(0f, s, 1f, 1f); // blue → cyan
        }
        else if (t < 0.5f)
        {
            float s = (t - 0.25f) / 0.25f;
            return new Color(0f, 1f, 1f - s, 1f); // cyan → green
        }
        else if (t < 0.75f)
        {
            float s = (t - 0.5f) / 0.25f;
            return new Color(s, 1f, 0f, 1f); // green → yellow
        }
        else
        {
            float s = (t - 0.75f) / 0.25f;
            return new Color(1f, 1f - s, 0f, 1f); // yellow → red
        }
    }

    /// <summary>Map diffusivity to a dark-to-bright purple gradient: black → indigo → violet → magenta → white</summary>
    Color DiffusivityToColor(float diff)
    {
        float range = Mathf.Max(diffusivityMax, 0.001f);
        float t = Mathf.Clamp01(diff / range);

        // 4-stop gradient: dark purple(0) → purple(0.33) → magenta(0.66) → white(1)
        if (t < 0.33f)
        {
            float s = t / 0.33f;
            return new Color(0.15f * s, 0f, 0.4f * s, 1f); // black → dark purple
        }
        else if (t < 0.66f)
        {
            float s = (t - 0.33f) / 0.33f;
            return new Color(0.15f + 0.85f * s, 0f, 0.4f + 0.2f * s, 1f); // dark purple → magenta
        }
        else
        {
            float s = (t - 0.66f) / 0.34f;
            return new Color(1f, s, 0.6f + 0.4f * s, 1f); // magenta → white
        }
    }

    void OnGUI()
    {
        if (!_visible || _sliceTex == null || voxelSystem == null || !voxelSystem.IsReady) return;

        // Lazy-init styles
        if (_labelStyle == null)
        {
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            _labelStyle.normal.textColor = Color.white;

            _boxStyle = new GUIStyle(GUI.skin.box);
        }

        float sw = Screen.width;
        float sh = Screen.height;

        // Dark semi-transparent background covering the whole screen
        GUI.color = new Color(0, 0, 0, 0.7f);
        GUI.DrawTexture(new Rect(0, 0, sw, sh), Texture2D.whiteTexture);
        GUI.color = Color.white;

        // Fit slice texture into screen with correct aspect ratio + padding
        float padding = 60f;
        float barH = 40f; // top info bar
        float availW = sw - padding * 2f;
        float availH = sh - padding * 2f - barH;
        float aspect = (float)_texW / Mathf.Max(_texH, 1);

        float imgW, imgH;
        if (availW / availH > aspect)
        {
            imgH = availH;
            imgW = imgH * aspect;
        }
        else
        {
            imgW = availW;
            imgH = imgW / Mathf.Max(aspect, 0.001f);
        }

        float imgX = (sw - imgW) * 0.5f;
        float imgY = barH + (availH - imgH) * 0.5f + padding;

        // Store image rect for hover detection
        _imgX = imgX; _imgY = imgY; _imgW = imgW; _imgH = imgH;

        // Draw the slice
        GUI.DrawTexture(new Rect(imgX, imgY, imgW, imgH), _sliceTex, ScaleMode.StretchToFill, true);

        // Thin border
        Color borderColor = _displayMode switch
        {
            DisplayMode.SDF => Color.cyan,
            DisplayMode.HeatMap => new Color(1f, 0.5f, 0f),
            DisplayMode.Diffusivity => new Color(0.5f, 0f, 1f),
            _ => Color.green
        };
        GUI.color = borderColor;
        float b = 2f;
        GUI.DrawTexture(new Rect(imgX - b, imgY - b, imgW + b * 2, b), Texture2D.whiteTexture); // top
        GUI.DrawTexture(new Rect(imgX - b, imgY + imgH, imgW + b * 2, b), Texture2D.whiteTexture); // bottom
        GUI.DrawTexture(new Rect(imgX - b, imgY, b, imgH), Texture2D.whiteTexture); // left
        GUI.DrawTexture(new Rect(imgX + imgW, imgY, b, imgH), Texture2D.whiteTexture); // right
        GUI.color = Color.white;

        // Info bar
        int maxIdx = GetSliceCount() - 1;
        string modeStr = _displayMode.ToString();

        string info = $"[{modeStr}] {axis} Slice {_sliceIdx}/{maxIdx}   |   [Scroll] slice   [1/2/3] axis   [Tab] mode   [{toggleKey}] close";
        GUI.Label(new Rect(0, 10, sw, barH), info, _labelStyle);

        // SDF hover tooltip & legend
        if (_displayMode == DisplayMode.SDF && _sdfData != null)
        {
            DrawSDFHoverInfo(imgX, imgY, imgW, imgH);
            DrawSDFLegend(imgX, imgY, imgH);
        }

        // HeatMap hover tooltip & legend
        if (_displayMode == DisplayMode.HeatMap && _tempData != null)
        {
            DrawHeatMapHoverInfo(imgX, imgY, imgW, imgH);
            DrawHeatMapLegend(imgX, imgY, imgH);
        }

        // Diffusivity hover tooltip & legend
        if (_displayMode == DisplayMode.Diffusivity && _diffData != null)
        {
            DrawDiffusivityHoverInfo(imgX, imgY, imgW, imgH);
            DrawDiffusivityLegend(imgX, imgY, imgH);
        }
    }

    void DrawSDFHoverInfo(float imgX, float imgY, float imgW, float imgH)
    {
        Vector2 mouse = Event.current.mousePosition;
        if (mouse.x < imgX || mouse.x > imgX + imgW ||
            mouse.y < imgY || mouse.y > imgY + imgH) return;

        // Map mouse to voxel coordinates
        float u01 = (mouse.x - imgX) / imgW;
        float v01 = 1f - (mouse.y - imgY) / imgH; // flip Y (GUI is top-down, texture is bottom-up)

        int u = Mathf.Clamp(Mathf.FloorToInt(u01 * _texW), 0, _texW - 1);
        int v = Mathf.Clamp(Mathf.FloorToInt(v01 * _texH), 0, _texH - 1);

        int x, y, z;
        switch (axis)
        {
            case SliceAxis.X: x = _sliceIdx; y = v; z = u; break;
            case SliceAxis.Y: x = u; y = _sliceIdx; z = v; break;
            default: x = u; y = v; z = _sliceIdx; break;
        }

        float sdf = GetSDF(x, y, z);
        float fill = GetFill(x, y, z);

        string label = $"Voxel [{x},{y},{z}]  SDF: {sdf:F3}  Fill: {(fill > 0.5f ? "solid" : "empty")}";

        // Draw tooltip near cursor
        var tooltipStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        tooltipStyle.normal.textColor = Color.white;

        float tw = tooltipStyle.CalcSize(new GUIContent(label)).x + 16f;
        float th = 28f;
        float tx = Mathf.Min(mouse.x + 20f, Screen.width - tw - 10f);
        float ty = mouse.y - th - 5f;

        GUI.color = new Color(0, 0, 0, 0.85f);
        GUI.DrawTexture(new Rect(tx - 4, ty - 2, tw + 8, th + 4), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(tx, ty, tw, th), label, tooltipStyle);
    }

    void DrawSDFLegend(float imgX, float imgY, float imgH)
    {
        // Color bar legend on the right side
        float legendW = 20f;
        float legendH = Mathf.Min(imgH * 0.6f, 300f);
        float legendX = imgX + _imgW + 15f;
        float legendY = imgY + (imgH - legendH) * 0.5f;

        if (legendX + legendW + 60f > Screen.width)
            legendX = imgX - legendW - 70f; // put on left if no room

        // Draw gradient bar
        int steps = (int)legendH;
        for (int i = 0; i < steps; i++)
        {
            float t = 1f - (float)i / (steps - 1); // top = +range, bottom = -range
            float sdf = Mathf.Lerp(-sdfDisplayRange, sdfDisplayRange, t);
            GUI.color = SDFToColor(sdf);
            GUI.DrawTexture(new Rect(legendX, legendY + i, legendW, 1), Texture2D.whiteTexture);
        }
        GUI.color = Color.white;

        // Labels
        var legendLabel = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft
        };
        legendLabel.normal.textColor = Color.white;

        GUI.Label(new Rect(legendX + legendW + 4, legendY - 8, 60, 20),
            $"+{sdfDisplayRange:F1}", legendLabel);
        GUI.Label(new Rect(legendX + legendW + 4, legendY + legendH * 0.5f - 8, 60, 20),
            "0 (surface)", legendLabel);
        GUI.Label(new Rect(legendX + legendW + 4, legendY + legendH - 12, 60, 20),
            $"-{sdfDisplayRange:F1}", legendLabel);
    }

    void DrawHeatMapHoverInfo(float imgX, float imgY, float imgW, float imgH)
    {
        Vector2 mouse = Event.current.mousePosition;
        if (mouse.x < imgX || mouse.x > imgX + imgW ||
            mouse.y < imgY || mouse.y > imgY + imgH) return;

        float u01 = (mouse.x - imgX) / imgW;
        float v01 = 1f - (mouse.y - imgY) / imgH;

        int u = Mathf.Clamp(Mathf.FloorToInt(u01 * _texW), 0, _texW - 1);
        int v = Mathf.Clamp(Mathf.FloorToInt(v01 * _texH), 0, _texH - 1);

        int x, y, z;
        switch (axis)
        {
            case SliceAxis.X: x = _sliceIdx; y = v; z = u; break;
            case SliceAxis.Y: x = u; y = _sliceIdx; z = v; break;
            default: x = u; y = v; z = _sliceIdx; break;
        }

        float temp = GetTemperature(x, y, z);
        float fill = GetFill(x, y, z);

        string label = $"Voxel [{x},{y},{z}]  Temp: {temp:F2}  Fill: {(fill > 0.5f ? "solid" : "empty")}";

        var tooltipStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        tooltipStyle.normal.textColor = Color.white;

        float tw = tooltipStyle.CalcSize(new GUIContent(label)).x + 16f;
        float th = 28f;
        float tx = Mathf.Min(mouse.x + 20f, Screen.width - tw - 10f);
        float ty = mouse.y - th - 5f;

        GUI.color = new Color(0, 0, 0, 0.85f);
        GUI.DrawTexture(new Rect(tx - 4, ty - 2, tw + 8, th + 4), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(tx, ty, tw, th), label, tooltipStyle);
    }

    void DrawHeatMapLegend(float imgX, float imgY, float imgH)
    {
        float legendW = 20f;
        float legendH = Mathf.Min(imgH * 0.6f, 300f);
        float legendX = imgX + _imgW + 15f;
        float legendY = imgY + (imgH - legendH) * 0.5f;

        if (legendX + legendW + 60f > Screen.width)
            legendX = imgX - legendW - 70f;

        int steps = (int)legendH;
        for (int i = 0; i < steps; i++)
        {
            float t01 = 1f - (float)i / (steps - 1); // top = hot, bottom = cold
            float temp = Mathf.Lerp(heatMapMin, heatMapMax, t01);
            GUI.color = TemperatureToColor(temp);
            GUI.DrawTexture(new Rect(legendX, legendY + i, legendW, 1), Texture2D.whiteTexture);
        }
        GUI.color = Color.white;

        var legendLabel = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft
        };
        legendLabel.normal.textColor = Color.white;

        GUI.Label(new Rect(legendX + legendW + 4, legendY - 8, 80, 20),
            $"{heatMapMax:F1} (hot)", legendLabel);
        GUI.Label(new Rect(legendX + legendW + 4, legendY + legendH * 0.5f - 8, 80, 20),
            $"{(heatMapMin + heatMapMax) * 0.5f:F1}", legendLabel);
        GUI.Label(new Rect(legendX + legendW + 4, legendY + legendH - 12, 80, 20),
            $"{heatMapMin:F1} (cold)", legendLabel);
    }

    void DrawDiffusivityHoverInfo(float imgX, float imgY, float imgW, float imgH)
    {
        Vector2 mouse = Event.current.mousePosition;
        if (mouse.x < imgX || mouse.x > imgX + imgW ||
            mouse.y < imgY || mouse.y > imgY + imgH) return;

        float u01 = (mouse.x - imgX) / imgW;
        float v01 = 1f - (mouse.y - imgY) / imgH;

        int u = Mathf.Clamp(Mathf.FloorToInt(u01 * _texW), 0, _texW - 1);
        int v = Mathf.Clamp(Mathf.FloorToInt(v01 * _texH), 0, _texH - 1);

        int x, y, z;
        switch (axis)
        {
            case SliceAxis.X: x = _sliceIdx; y = v; z = u; break;
            case SliceAxis.Y: x = u; y = _sliceIdx; z = v; break;
            default: x = u; y = v; z = _sliceIdx; break;
        }

        float diff = GetDiffusivity(x, y, z);
        float fill = GetFill(x, y, z);

        string label = $"Voxel [{x},{y},{z}]  Diffusivity: {diff:F4}  Fill: {(fill > 0.5f ? "solid" : "empty")}";

        var tooltipStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        tooltipStyle.normal.textColor = Color.white;

        float tw = tooltipStyle.CalcSize(new GUIContent(label)).x + 16f;
        float th = 28f;
        float tx = Mathf.Min(mouse.x + 20f, Screen.width - tw - 10f);
        float ty = mouse.y - th - 5f;

        GUI.color = new Color(0, 0, 0, 0.85f);
        GUI.DrawTexture(new Rect(tx - 4, ty - 2, tw + 8, th + 4), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(tx, ty, tw, th), label, tooltipStyle);
    }

    void DrawDiffusivityLegend(float imgX, float imgY, float imgH)
    {
        float legendW = 20f;
        float legendH = Mathf.Min(imgH * 0.6f, 300f);
        float legendX = imgX + _imgW + 15f;
        float legendY = imgY + (imgH - legendH) * 0.5f;

        if (legendX + legendW + 60f > Screen.width)
            legendX = imgX - legendW - 70f;

        int steps = (int)legendH;
        for (int i = 0; i < steps; i++)
        {
            float t01 = 1f - (float)i / (steps - 1); // top = high, bottom = low
            float diff = diffusivityMax * t01;
            GUI.color = DiffusivityToColor(diff);
            GUI.DrawTexture(new Rect(legendX, legendY + i, legendW, 1), Texture2D.whiteTexture);
        }
        GUI.color = Color.white;

        var legendLabel = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft
        };
        legendLabel.normal.textColor = Color.white;

        GUI.Label(new Rect(legendX + legendW + 4, legendY - 8, 80, 20),
            $"{diffusivityMax:F2} (high)", legendLabel);
        GUI.Label(new Rect(legendX + legendW + 4, legendY + legendH * 0.5f - 8, 80, 20),
            $"{diffusivityMax * 0.5f:F2}", legendLabel);
        GUI.Label(new Rect(legendX + legendW + 4, legendY + legendH - 12, 80, 20),
            "0.00 (low)", legendLabel);
    }
}
