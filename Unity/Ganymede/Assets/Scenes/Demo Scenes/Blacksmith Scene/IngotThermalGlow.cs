using UnityEngine;

public class IngotThermalGlow : MonoBehaviour
{
    [Header("References")]
    public ThermalReceiver thermalReceiver;
    public Renderer ingotRenderer;

    [Header("Sampling")]
    public Renderer boundsOverride;

    [Range(0f, 0.5f)]
    public float glowVisibilityThreshold = 0.05f;

    [Header("Glow Colours")]
    [ColorUsage(true, true)] public Color coldGlow = new Color(0f, 0f, 0f, 1f);
    [ColorUsage(true, true)] public Color redGlow = new Color(2f, 0f, 0f, 1f);
    [ColorUsage(true, true)] public Color orangeGlow = new Color(4f, 1f, 0f, 1f);
    [ColorUsage(true, true)] public Color whiteGlow = new Color(8f, 7f, 5f, 1f);

    Material[] _instanceMaterials;
    static readonly int EmissionID = Shader.PropertyToID("_EmissionColor");

    void Awake()
    {
        if (ingotRenderer == null)
            ingotRenderer = GetComponent<Renderer>();
    }

    void OnEnable()
    {
        if (ingotRenderer != null)
            _instanceMaterials = ingotRenderer.materials;
    }

    void OnDisable()
    {
        if (_instanceMaterials != null)
            foreach (var m in _instanceMaterials)
                if (m != null) 
                    m.SetColor(EmissionID, Color.black);
    }

    void Update()
    {
        if (thermalReceiver == null || !thermalReceiver.IsInitialized) 
            return;

        Texture3D tex = thermalReceiver.tempTexture;
        if (tex == null) 
            return;

        VoxelTracerSystem vt = thermalReceiver.voxelTracer;
        if (vt == null || !vt.IsReady) 
            return;

        float[] liveData = thermalReceiver.LiveTemperatureData;
        if (liveData == null) 
            return;

        if (_instanceMaterials == null ||_instanceMaterials.Length != ingotRenderer.sharedMaterials.Length)
            _instanceMaterials = ingotRenderer.materials;

        float minTemp = thermalReceiver.minDisplayTemp;
        float maxTemp = thermalReceiver.maxDisplayTemp;

        var pixels = tex.GetPixelData<float>(0);

        int nx = vt.Nx, ny = vt.Ny, nz = vt.Nz;
        float vs = vt.ActiveVoxelSize;
        Vector3 gridMin = vt.ActiveGridMin;
        Bounds bounds = (boundsOverride != null ? boundsOverride : ingotRenderer).bounds;

        int x0 = Mathf.Max(0, Mathf.FloorToInt((bounds.min.x - gridMin.x) / vs));
        int y0 = Mathf.Max(0, Mathf.FloorToInt((bounds.min.y - gridMin.y) / vs));
        int z0 = Mathf.Max(0, Mathf.FloorToInt((bounds.min.z - gridMin.z) / vs));
        int x1 = Mathf.Min(nx-1, Mathf.CeilToInt ((bounds.max.x - gridMin.x) / vs));
        int y1 = Mathf.Min(ny-1, Mathf.CeilToInt ((bounds.max.y - gridMin.y) / vs));
        int z1 = Mathf.Min(nz-1, Mathf.CeilToInt ((bounds.max.z - gridMin.z) / vs));

        double sum = 0;
        int count = 0;

        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int idx = z * nx * ny + y * nx + x;
            if (liveData[idx] <= 0f) 
                continue;
            sum += pixels[idx];
            count++;
        }

        float avgTemp = count > 0 ? (float)(sum / count) : minTemp;
        float t = Mathf.InverseLerp(minTemp, maxTemp, avgTemp);
        Color emission = t < glowVisibilityThreshold ? coldGlow : TempToEmission(t);

        foreach (var m in _instanceMaterials)
            if (m != null) 
                m.SetColor(EmissionID, emission);
    }

    Color TempToEmission(float t)
    {
        if (t <= 0.33f) 
            return Color.Lerp(coldGlow, redGlow, t / 0.33f);
        if (t <= 0.66f) 
            return Color.Lerp(redGlow, orangeGlow, (t - 0.33f) / 0.33f);
        return Color.Lerp(orangeGlow, whiteGlow, (t - 0.66f) / 0.34f);
    }
}