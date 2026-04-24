using UnityEngine;
using System.Runtime.InteropServices;

/// <summary>
/// Renders particles using DrawMeshInstancedIndirect.
/// Reads particle data via non-blocking readback (1-frame latency) and uploads to a ComputeBuffer.
/// Attach to the same GameObject as UseComputePlugin.
/// </summary>
public class ParticleRenderer : MonoBehaviour
{
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

    [Header("Temperature Gradient")]
    [Tooltip("Color gradient mapped to particle temperature (left = cold, right = hot)")]
    public Gradient temperatureGradient;

    [Tooltip("Minimum temperature in the gradient (mapped to left edge)")]
    [Range(0f, 100f)]
    public float minTemp = 20f;

    [Tooltip("Maximum temperature in the gradient (mapped to right edge)")]
    [Range(20f, 500f)]
    public float maxTemp = 150f;

    public UseComputePlugin computePlugin;

    private Texture2D gradientTexture;
    private ComputeBuffer particleBuffer;
    private ComputeBuffer argsBuffer;
    private Particle[] readbackData;
    private Particle[] uploadData;
    private uint[] args = new uint[5];
    private Bounds renderBounds;
    private MaterialPropertyBlock mpb;

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

        mpb = new MaterialPropertyBlock();

        // DrawMeshInstancedIndirect requires instancing enabled on the material.
        if (particleMaterial != null)
            particleMaterial.enableInstancing = true;

        // Set up default gradient if none was configured in the Inspector
        // Temperature gradient: Blue (cold) → Red (hot)
        if (temperatureGradient == null || temperatureGradient.colorKeys.Length < 2)
        {
            temperatureGradient = new Gradient();
            temperatureGradient.SetKeys(
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
        uploadData = new Particle[count];
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

        // Read from plugin (non-blocking, 1-frame latency)
        GetComputeResult(readbackData, readbackData.Length);

        // Pack only live particles (phase >= 0) into the upload buffer so the
        // GPU never has to deal with dormant slots parked at (0,-1000,0).
        int liveCount = 0;
        for (int i = 0; i < readbackData.Length; i++)
        {
            if (readbackData[i].phase >= 0)
                uploadData[liveCount++] = readbackData[i];
        }

        if (liveCount == 0)
            return;

        particleBuffer.SetData(uploadData, 0, 0, liveCount);

        // Update instance count in the indirect args so the draw call matches.
        args[1] = (uint)liveCount;
        argsBuffer.SetData(args);

        // Rebake gradient only when it has changed
        BakeGradientTexture();

        // Bind resources per-draw to avoid Vulkan "missing binding" warnings.
        // (Also prevents modifying the shared material asset at runtime.)
        mpb.Clear();
        mpb.SetBuffer("_ParticleBuffer", particleBuffer);
        mpb.SetFloat("_Size", particleSize);
        mpb.SetFloat("_MinTemperature", minTemp);
        mpb.SetFloat("_MaxTemperature", maxTemp);
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
        gradientDirty = true;
    }

    private bool gradientDirty = true;

    /// <summary>Call when the gradient is changed at runtime (e.g. from Inspector).</summary>
    public void MarkGradientDirty() => gradientDirty = true;

    private void BakeGradientTexture()
    {
        if (!gradientDirty) return;
        for (int i = 0; i < 256; i++)
            gradientTexture.SetPixel(i, 0, temperatureGradient.Evaluate(i / 255f));
        gradientTexture.Apply();
        gradientDirty = false;
    }
}
