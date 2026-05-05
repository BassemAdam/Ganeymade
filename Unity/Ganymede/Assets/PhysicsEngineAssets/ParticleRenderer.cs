using UnityEngine;
using System.Runtime.InteropServices;
using UnityEngine.Serialization;

/// <summary>
/// Renders particles using DrawMeshInstancedIndirect.
/// Reads particle data via non-blocking readback (1-frame latency) and uploads to a ComputeBuffer.
/// Attach to the same GameObject as UseComputePlugin.
/// </summary>
public class ParticleRenderer : MonoBehaviour
{
    private const float TemperatureMinDefault = 0f;
    private const float TemperatureMaxDefault = 150f;
    private const float PressureMinDefault = 10000f;
    private const float PressureMaxDefault = 20000f;
    private const float DensityMinDefault = 0f;
    private const float DensityMaxDefault = 300f;
    private const float SpeedMinDefault = 0f;
    private const float SpeedMaxDefault = 5f;

    public enum VisualizedField
    {
        Temperature = 0,
        Pressure = 1,
        Density = 2,
        Speed = 3,
    }

#if (PLATFORM_IOS || PLATFORM_TVOS || PLATFORM_BRATWURST || PLATFORM_SWITCH) && !UNITY_EDITOR
    [DllImport("__Internal")]
#else
    [DllImport("RenderingPlugin")]
#endif
    private static extern void GetComputeResult(Particle[] outData, int count);

    [Header("Rendering")]
    [Tooltip("Material using Custom/ParticleInstanced shader")]
    public Material particleMaterial;

    [Tooltip("Mesh to draw per particle (assign a sphere)")]
    public Mesh particleMesh;

    [Tooltip("Particle render size")]
    public float particleSize = 0.05f;

    [Header("Visualization")]
    [Tooltip("Which particle value is mapped to the gradient")]
    public VisualizedField visualizedField = VisualizedField.Temperature;

    [FormerlySerializedAs("temperatureGradient")]
    [Tooltip("Color gradient mapped to the selected particle value (left = low, right = high)")]
    public Gradient valueGradient;

    [FormerlySerializedAs("minTemp")]
    [Tooltip("Minimum selected value in the gradient (mapped to left edge)")]
    public float minValue = 20f;

    [FormerlySerializedAs("maxTemp")]
    [Tooltip("Maximum selected value in the gradient (mapped to right edge)")]
    public float maxValue = 150f;

    public UseComputePlugin computePlugin;

    private Texture2D gradientTexture;
    private ComputeBuffer particleBuffer;
    private ComputeBuffer argsBuffer;
    private Particle[] readbackData;
    private uint[] args = new uint[5];
    private Bounds renderBounds;
    private MaterialPropertyBlock mpb;

    [SerializeField, HideInInspector]
    private VisualizedField lastValidatedField = VisualizedField.Temperature;

    void Start()
    {
        if (computePlugin == null)
        {
            Debug.LogError("[ParticleRenderer] computePlugin is not assigned! " +
                           "Drag your simulation GameObject's UseComputePlugin into this field.");
            enabled = false;
            return;
        }        
        int count = computePlugin.particleCount;

        if (maxValue <= minValue)
            ApplyFieldDefaultRange();

        lastValidatedField = visualizedField;

        mpb = new MaterialPropertyBlock();

        // DrawMeshInstancedIndirect requires instancing enabled on the material.
        if (particleMaterial != null)
            particleMaterial.enableInstancing = true;

        // Set up default gradient if none was configured in the Inspector
        // Temperature gradient: Blue (cold) → Red (hot)
        bool allWhite = true;
        foreach (var key in valueGradient.colorKeys)
            if (key.color != Color.white) 
            { 
                allWhite = false; 
                break; 
            }

        if (allWhite)
        {
            valueGradient = new Gradient();
            valueGradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0f, 0f, 1f), 0f),        // Blue (cold)
                    new GradientColorKey(new Color(0f, 1f, 1f), 0.25f),     // Cyan
                    new GradientColorKey(new Color(1f, 1f, 0f), 0.5f),      // Yellow
                    new GradientColorKey(new Color(1f, 0.5f, 0f), 0.75f),   // Orange
                    new GradientColorKey(new Color(1f, 0f, 0f), 1f)         // Red (hot)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f)
                }
            );
        }

        gradientTexture = new Texture2D(256, 1, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        BakeGradientTexture();

        readbackData = new Particle[count];
        particleBuffer = new ComputeBuffer(count, Marshal.SizeOf<Particle>());

        // Indirect args: (indexCount, instanceCount, startIndex, baseVertex, startInstance)
        argsBuffer = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
        if (particleMesh != null)
        {
            args[0] = particleMesh.GetIndexCount(0);
            args[1] = (uint)count;
            args[2] = 0;
            args[3] = 0;
            args[4] = 0;
        }
        argsBuffer.SetData(args);

        // Large bounds so particles are never culled
        renderBounds = new Bounds(Vector3.zero, Vector3.one * 500f);

        Debug.Log($"[ParticleRenderer] Initialized with {count} particles");
    }

    void LateUpdate()
    {
        if (particleMaterial == null || particleMesh == null || particleBuffer == null)
            return;
        // Skip all readback and rendering in perf-test mode
        if (computePlugin != null && computePlugin.perfTestMode)
            return;

        // Auto-resize buffers if particle count changed (e.g. boundary particles appended)
        int currentCount = computePlugin.particleCount;
        if (currentCount != readbackData.Length)
        {
            particleBuffer.Release();
            readbackData = new Particle[currentCount];
            particleBuffer = new ComputeBuffer(currentCount, Marshal.SizeOf<Particle>());
            args[1] = (uint)currentCount;
            argsBuffer.SetData(args);
        }

        // Read the full particle buffer — the shader handles dormant particles
        // (phase < 0) by collapsing them to w=0 (point at infinity, no rasterization).
        GetComputeResult(readbackData, readbackData.Length);
        particleBuffer.SetData(readbackData);

        // Rebake gradient only when it has changed
        BakeGradientTexture();

        // Bind resources per-draw to avoid Vulkan "missing binding" warnings.
        // (Also prevents modifying the shared material asset at runtime.)
        mpb.Clear();
        mpb.SetBuffer("_ParticleBuffer", particleBuffer);
        mpb.SetFloat("_Size", particleSize);
        mpb.SetInt("_VisualizedField", (int)visualizedField);
        mpb.SetFloat("_MinValue", minValue);
        mpb.SetFloat("_MaxValue", maxValue);
        mpb.SetTexture("_GradientTex", gradientTexture);

        Graphics.DrawMeshInstancedIndirect(particleMesh, 0, particleMaterial, renderBounds, argsBuffer, 0, mpb);
    }

    void OnDestroy()
    {
        particleBuffer?.Release();
        argsBuffer?.Release();
        if (gradientTexture != null) Destroy(gradientTexture);
    }

    void OnValidate()
    {
        if (visualizedField != lastValidatedField)
        {
            ApplyFieldDefaultRange();
            lastValidatedField = visualizedField;
        }

        if (maxValue <= minValue)
            maxValue = minValue + 0.01f;

        gradientDirty = true;
    }

    private bool gradientDirty = true;

    /// <summary>Call when the gradient is changed at runtime (e.g. from Inspector).</summary>
    public void MarkGradientDirty() => gradientDirty = true;

    [ContextMenu("Apply Field Default Range")]
    public void ApplyFieldDefaultRange()
    {
        switch (visualizedField)
        {
            case VisualizedField.Pressure:
                minValue = PressureMinDefault;
                maxValue = PressureMaxDefault;
                break;

            case VisualizedField.Density:
                minValue = DensityMinDefault;
                maxValue = DensityMaxDefault;
                break;

            case VisualizedField.Speed:
                minValue = SpeedMinDefault;
                maxValue = SpeedMaxDefault;
                break;

            case VisualizedField.Temperature:
            default:
                minValue = TemperatureMinDefault;
                maxValue = TemperatureMaxDefault;
                break;
        }
    }

    private void BakeGradientTexture()
    {
        if (!gradientDirty) return;
        for (int i = 0; i < 256; i++)
            gradientTexture.SetPixel(i, 0, valueGradient.Evaluate(i / 255f));
        gradientTexture.Apply();
        gradientDirty = false;
    }
}
