using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Minimal C# wrapper for the native Vulkan compute plugin (RenderingPlugin.dll).
/// Responsibilities:
/// - Creates initial particle data and uploads it once via SetComputeData.
/// - Sends per-frame simulation parameters (push constants) via SetSimParams.
/// - Triggers the native dispatch each frame using GL.IssuePluginEvent.
///
/// This script is a dependency of ParticleRenderer.cs and FirstPersonCamera.cs.
/// </summary>
public class UseComputePlugin : MonoBehaviour
{
#if (UNITY_IOS || UNITY_TVOS || UNITY_SWITCH) && !UNITY_EDITOR
    private const string PluginName = "__Internal";
#else
    private const string PluginName = "RenderingPlugin";
#endif

    // Native entry points
    [DllImport(PluginName)]
    private static extern void SetComputeData([In] Particle[] data, int count);

    [DllImport(PluginName)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool IsComputeDone();

    [DllImport(PluginName)]
    private static extern void SetPerfTestMode([MarshalAs(UnmanagedType.I1)] bool enabled);

    [DllImport(PluginName)]
    private static extern void SetSubStepCount(int count);

    [DllImport(PluginName)]
    private static extern void SetSimParams(SimParams param);

    [DllImport(PluginName)]
    private static extern IntPtr GetRenderEventFunc();

    // --------------------------------------------------------------------
    // Public controls (read by other scripts)

    [Header("Particle Count")]
    [Tooltip("Number of particles simulated by the native plugin (max 262144)")]
    [Min(1)]
    public int particleCount = 32768;

    [Header("Simulation")]
    public Vector3 gravity = new Vector3(0f, -9.81f, 0f);

    [Tooltip("Target rest density (water ≈ 1000)")]
    public float restDensity = 1000f;

    [Tooltip("Gas stiffness (pressure constant)")]
    public float gasStiffness = 200f;

    [Tooltip("Viscosity coefficient")]
    public float viscosity = 0.1f;

    [Tooltip("Smoothing radius (kernel radius h)")]
    [Min(0.001f)]
    public float smoothingRadius = 0.2f;

    [Tooltip("Mass per particle")]
    [Min(0.0001f)]
    public float particleMass = 1f;

    [Tooltip("Per-frame velocity damping (e.g. 0.998)")]
    [Range(0.9f, 1.0f)]
    public float damping = 0.998f;

    [Header("Bounds")]
    public Vector3 boundsMin = new Vector3(-5f, -5f, -5f);
    public Vector3 boundsMax = new Vector3(5f, 5f, 5f);

    [Header("Performance")]
    [Tooltip("Skips GPU→CPU readback in the plugin (ParticleRenderer will show stale data)")]
    public bool perfTestMode = false;

    [Tooltip("How many SPH sub-steps to run per rendered frame")]
    [Range(1, 16)]
    public int subStepCount = 1;

    [Header("Interaction (written by FirstPersonCamera)")]
    public float interactionStrength;
    public Vector3 interactionPos;
    public float interactionRadius = 4f;

    [Header("Init")]
    [Tooltip("Upload particles automatically on Start")]
    public bool autoInitialize = true;

    [Tooltip("Print debug logs")]
    public bool verbose = true;

    // --------------------------------------------------------------------

    private bool initialized;
    private IntPtr renderEventFunc;

    private void Start()
    {
        if (autoInitialize)
            Initialize();
    }

    public void Initialize()
    {
        int clampedCount = Mathf.Clamp(particleCount, 1, 262144);
        particleCount = clampedCount;

        // Cache function pointer once
        renderEventFunc = GetRenderEventFunc();

        // Create initial particle distribution (simple lattice in bounds)
        Particle[] particles = CreateInitialParticles(particleCount);

        // Upload to native plugin once
        SetComputeData(particles, particles.Length);

        // Configure plugin
        SetPerfTestMode(perfTestMode);
        SetSubStepCount(subStepCount);

        // Push params once immediately
        PushParams(Time.deltaTime);

        initialized = true;

        if (verbose)
        {
            int particleStride = Marshal.SizeOf<Particle>();
            Debug.Log($"[UseComputePlugin] Initialized. count={particleCount}, ParticleStride={particleStride} bytes, renderEventFunc=0x{renderEventFunc.ToInt64():X}");
        }

        // Helpful hint: this plugin only runs when Unity is using Vulkan.
        // (On other graphics APIs it will early-out and appear frozen.)
        if (verbose && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
        {
            Debug.LogWarning("[UseComputePlugin] Graphics API is not Vulkan. The native compute dispatch will no-op. " +
                             "Enable Vulkan in Project Settings > Player > Other Settings > Graphics APIs.");
        }
    }

    private void Update()
    {
        if (!initialized)
            return;

        // Keep native-side toggles in sync (cheap calls)
        SetPerfTestMode(perfTestMode);
        SetSubStepCount(subStepCount);

        PushParams(Time.deltaTime);

        // Trigger native compute dispatch on the render thread
        if (renderEventFunc != IntPtr.Zero)
            GL.IssuePluginEvent(renderEventFunc, 3);
    }

    private void PushParams(float dt)
    {
        float h = Mathf.Max(0.001f, smoothingRadius);

        // SPH kernel constants
        // poly6Const = 315 / (64π h^9)
        // spikyGradConst = -45 / (π h^6)
        // viscLapConst = 45 / (π h^6)
        float pi = Mathf.PI;
        float h2 = h * h;
        float h3 = h2 * h;
        float h6 = h3 * h3;
        float h9 = h6 * h3;

        float poly6Const = 315f / (64f * pi * h9);
        float spikyGradConst = -45f / (pi * h6);
        float viscLapConst = 45f / (pi * h6);

        SimParams p = default;
        p.dt = dt;
        p.particleCount = (uint)particleCount;
        p.restDensity = restDensity;
        p.gasStiffness = gasStiffness;
        p.viscosity = viscosity;
        p.smoothingRadius = h;
        p._pad0 = 0f;
        p._pad1 = 0f;
        p.gravity = gravity;
        p.particleMass = particleMass;
        p.boundsMin = boundsMin;
        p.poly6Const = poly6Const;
        p.boundsMax = boundsMax;
        p.spikyGradConst = spikyGradConst;

        // Unused by the current shaders (hash uses particleCount as table size),
        // but kept for binary compatibility with the native SimParams struct.
        p.gridDims = new UInt3(0, 0, 0);
        p.viscLapConst = viscLapConst;
        p.gridOrigin = boundsMin;
        p.maxCells = 0;

        p.damping = damping;
        p.bitonicK = 0;
        p.bitonicJ = 0;

        p.interactionStrength = interactionStrength;
        p.interactionPos = interactionPos;
        p.interactionRadius = interactionRadius;

        SetSimParams(p);
    }

    private Particle[] CreateInitialParticles(int count)
    {
        Vector3 min = boundsMin;
        Vector3 max = boundsMax;
        Vector3 size = max - min;

        // Choose a grid resolution that can hold at least 'count' particles.
        int n = Mathf.CeilToInt(Mathf.Pow(count, 1f / 3f));
        n = Mathf.Max(1, n);

        // Spacing chosen to fit n^3 inside the bounds.
        float spacing = Mathf.Min(size.x, Mathf.Min(size.y, size.z)) / (n + 1);
        spacing = Mathf.Max(spacing, smoothingRadius * 0.5f);

        Vector3 start = min + Vector3.one * spacing;

        Particle[] particles = new Particle[count];
        int idx = 0;
        for (int z = 0; z < n && idx < count; z++)
        {
            for (int y = 0; y < n && idx < count; y++)
            {
                for (int x = 0; x < n && idx < count; x++)
                {
                    Vector3 pos = start + new Vector3(x * spacing, y * spacing, z * spacing);
                    pos.x = Mathf.Min(pos.x, max.x);
                    pos.y = Mathf.Min(pos.y, max.y);
                    pos.z = Mathf.Min(pos.z, max.z);

                    particles[idx++] = Particle.Create(pos, Vector3.zero, particleMass);
                }
            }
        }

        return particles;
    }

    // --------------------------------------------------------------------
    // Native structs (must match the plugin exactly)

    [StructLayout(LayoutKind.Sequential)]
    private struct UInt3
    {
        public uint x;
        public uint y;
        public uint z;

        public UInt3(uint x, uint y, uint z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SimParams
    {
        public float dt;
        public uint particleCount;
        public float restDensity;
        public float gasStiffness;
        public float viscosity;
        public float smoothingRadius;
        public float _pad0;
        public float _pad1;
        public Vector3 gravity;
        public float particleMass;
        public Vector3 boundsMin;
        public float poly6Const;
        public Vector3 boundsMax;
        public float spikyGradConst;
        public UInt3 gridDims;
        public float viscLapConst;
        public Vector3 gridOrigin;
        public uint maxCells;
        public float damping;
        public uint bitonicK;
        public uint bitonicJ;
        public float interactionStrength;
        public Vector3 interactionPos;
        public float interactionRadius;
    }
}
