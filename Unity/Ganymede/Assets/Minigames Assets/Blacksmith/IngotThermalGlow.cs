using UnityEngine;


// Drives the ingot's emission colour from the same Texture3D temperature
// volume that VoxelTracerCamera uses for its voxel visualisation.

public class IngotThermalGlow : MonoBehaviour
{
    [Header("References")]
    public ThermalReceiver thermalReceiver;
    public Renderer ingotRenderer;

    [Header("Sampling")]
    [Tooltip("Only voxels whose temperature is above this value are included "
           + "in the average. Matches defaultAmbientTemp so cold/air voxels "
           + "are excluded from the average, just like the voxel visualiser.")]
    public float ambientThreshold = 71f;

    [Tooltip("Override the bounds used to select voxels. "
           + "Leave None to use ingotRenderer.bounds automatically.")]
    public Renderer boundsOverride;

    [Header("Glow Temperature Range")]
    [Tooltip("Below this temperature the ingot shows no glow")]
    public float glowMinTemp = 100f;
    [Tooltip("At and above this temperature the ingot is white-hot. "
           + "Set to the maximum temperature your ingot material can reach")]
    public float glowMaxTemp = 1200f;

    [Header("Glow Colours")]
    [ColorUsage(true, true)] public Color coldGlow = new Color(0f, 0f, 0f, 1f); // no emission
    [ColorUsage(true, true)] public Color redGlow = new Color(2f, 0f, 0f, 1f); // dull red
    [ColorUsage(true, true)] public Color orangeGlow = new Color(4f, 1f, 0f, 1f); // orange
    [ColorUsage(true, true)] public Color whiteGlow = new Color(8f, 7f, 5f, 1f); // white-hot

    // --- Private variables  ----------
    MaterialPropertyBlock _mpb;
    static readonly int EmissionID = Shader.PropertyToID("_EmissionColor");

    void Awake()
    {
        _mpb = new MaterialPropertyBlock();
        if (ingotRenderer == null)
            ingotRenderer = GetComponent<Renderer>();
    }

    void Update()
    {
        // Wait until ThermalReceiver has finished its Start() coroutine and VoxelTracerCamera has a live tempTexture.
        if (thermalReceiver == null || !thermalReceiver.IsInitialized)
            return;

        VoxelTracerCamera vtCam = thermalReceiver.voxelTracerCamera;
        if (vtCam == null || vtCam.tempTexture == null)
            return;

        VoxelTracerSystem vt = thermalReceiver.voxelTracer;
        if (vt == null || !vt.IsReady)
            return;

        // Use the dynamic max temp from the receiver to keep colors consistent with the voxels
        float currentMax = thermalReceiver.maxDisplayTemp; 
        
        // Sample the temperature
        float avgTemp = SampleAverageTemperature(vtCam.tempTexture, vt);

        // Map to emission
        // Use the same scale as the voxel visualizer for consistency
        Color emission = TempToEmission(avgTemp, glowMinTemp, glowMaxTemp);

        ingotRenderer.GetPropertyBlock(_mpb);
        _mpb.SetColor(EmissionID, emission);
        ingotRenderer.SetPropertyBlock(_mpb);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    // Reads the CPU-side pixel data from the Texture3D and averages the
    // temperature of voxels that fall inside the ingot's world bounds and are above ambientThreshold.

    float SampleAverageTemperature(Texture3D tex, VoxelTracerSystem vt)
    {
        // Texture3D stores RFloat , one float per voxel
        var pixels = tex.GetPixelData<float>(0);
        if (pixels.Length == 0) return thermalReceiver.defaultAmbientTemp;
    
        int nx = vt.Nx, ny = vt.Ny, nz = vt.Nz;
        float vs = vt.ActiveVoxelSize;
        Vector3 gridMin = vt.ActiveGridMin;
        Bounds bounds = (boundsOverride != null ? boundsOverride : ingotRenderer).bounds;

        // Convert world-space AABB to grid-index range (clamped)
        int x0 = Mathf.Max(0, Mathf.FloorToInt((bounds.min.x - gridMin.x) / vs));
        int y0 = Mathf.Max(0, Mathf.FloorToInt((bounds.min.y - gridMin.y) / vs));
        int z0 = Mathf.Max(0, Mathf.FloorToInt((bounds.min.z - gridMin.z) / vs));
        int x1 = Mathf.Min(nx-1, Mathf.CeilToInt ((bounds.max.x - gridMin.x) / vs));
        int y1 = Mathf.Min(ny-1, Mathf.CeilToInt ((bounds.max.y - gridMin.y) / vs));
        int z1 = Mathf.Min(nz-1, Mathf.CeilToInt ((bounds.max.z - gridMin.z) / vs));

        double sum  = 0;
        int count = 0;

        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float t = pixels[z * nx * ny + y * nx + x];

            // Exclude ambient/air voxels
            sum += t;
            count++;
        }

        return count > 0 ? (float)(sum / count) : thermalReceiver.defaultAmbientTemp;
    }

    // Maps temperature to emission colour 
    Color TempToEmission(float temp, float minTemp, float maxTemp)
    {
        float t = Mathf.InverseLerp(minTemp, maxTemp, temp);   

        if (t <= 0f)    
            return coldGlow;
        if (t <= 0.33f) 
            return Color.Lerp(coldGlow, redGlow, t / 0.33f);
        if (t <= 0.66f) 
            return Color.Lerp(redGlow, orangeGlow, (t - 0.33f) / 0.33f);
        return Color.Lerp(orangeGlow, whiteGlow, (t - 0.66f) / 0.34f);
    }
}