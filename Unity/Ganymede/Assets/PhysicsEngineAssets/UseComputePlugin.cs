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
[DefaultExecutionOrder(100)]
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

    [DllImport(PluginName)]
    private static extern void SetFluidHeatSources(HeatSource[] sources, int count);

    [DllImport(PluginName)]
    private static extern void GetComputeResult([Out] Particle[] data, int count);

    // --------------------------------------------------------------------
    // Public controls (read by other scripts)

    [Header("Particle Count")]
    [Tooltip("Number of particles simulated by the native plugin. Must be power-of-two for bitonic sort stability (auto-corrected on init).")]
    [Min(1)]
    public int particleCount = 32768;

    [Header("Simulation")]
    public Vector3 gravity = new Vector3(0f, -9.81f, 0f);

    [Tooltip("Target rest density in normalized solver units (typical range: 20..80)")]
    public float restDensity = 30f;

    [Tooltip("Gas stiffness (pressure constant). Higher = less compressible")]
    public float gasStiffness = 10f;

    [Tooltip("Viscosity coefficient. Higher = thicker fluid")]
    public float viscosity = 10f;

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
    [Tooltip("If enabled, boundsMin/boundsMax are treated as local offsets from this GameObject's position (ignores rotation).")]
    public bool boundsAreLocalToTransform = true;

    [Tooltip("Container min corner (local if boundsAreLocalToTransform, otherwise world)")]
    public Vector3 boundsMin = new Vector3(-5f, -5f, -5f);

    [Tooltip("Container max corner (local if boundsAreLocalToTransform, otherwise world)")]
    public Vector3 boundsMax = new Vector3(5f, 5f, 5f);

    [Header("Performance")]
    [Tooltip("Skips GPU→CPU readback in the plugin (ParticleRenderer will show stale data)")]
    public bool perfTestMode = false;

    [Tooltip("How many SPH sub-steps to run per rendered frame")]
    [Range(1, 16)]
    public int subStepCount = 1;

    [Header("Time Stepping")]
    [Tooltip("Use fixed simulation timestep with accumulator (more stable and responsive under variable FPS)")]
    public bool useFixedTimeStep = true;

    [Tooltip("Fixed simulation dt when fixed stepping is enabled")]
    [Range(0.0005f, 0.02f)]
    public float fixedTimeStep = 0.002f;

    [Tooltip("Maximum fixed sub-steps to run per frame")]
    [Range(1, 32)]
    public int maxSubSteps = 10;

    [Header("Interaction (written by FirstPersonCamera)")]
    public float interactionStrength;
    public Vector3 interactionPos;
    public float interactionRadius = 3f;

    [Header("Thermal properties")]
    [Tooltip("How fast heat spreads between particles")]
    [Range(0.001f, 1)]
    public float thermalDiffusivity = 0.1f;

    [Tooltip("Temperature the fluid cools toward")]
    [Range(0f, 500f)]
    public float ambientTemperature = 20f;

    [Tooltip("how fast particles lose heat to the environment")]
    [Range(0f, 0.1f)]
    public float coolingRate = 0.01f;

    [Header("Init")]
    [Tooltip("Upload particles automatically on Start")]
    public bool autoInitialize = true;

    [Tooltip("Print debug logs")]
    public bool verbose = true;

    [Header("Heat Sources")]
    [Tooltip("Maximum number of heat sources supported")]
    private const int MAX_HEAT_SOURCES = 16;
    private HeatSource[] _heatSources = new HeatSource[MAX_HEAT_SOURCES];


    // --------------------------------------------------------------------

    private bool initialized;
    private IntPtr renderEventFunc;
    private float timeAccumulator;

    private Particle[] readbackData;  // for debug readback (keep reference to avoid GC)
    private int frameCount = 0;

    private void Start()
    {
        if (autoInitialize)
            Initialize();
    }

    public void Initialize()
    {
        int requestedCount = Mathf.Clamp(particleCount, 1, 262144);
        int sanitizedCount = NormalizeParticleCount(requestedCount);
        if (sanitizedCount != requestedCount && verbose)
        {
            Debug.LogWarning($"[UseComputePlugin] particleCount={requestedCount} is not power-of-two. Auto-adjusted to {sanitizedCount} for stable bitonic sort.");
        }
        particleCount = sanitizedCount;

        int particleStride = Marshal.SizeOf<Particle>();
        int simParamsStride = Marshal.SizeOf<SimParams>();
        if (particleStride != 64)
            Debug.LogError($"[UseComputePlugin] Particle stride mismatch. Expected 64, got {particleStride}. Check struct layout.");
        if (simParamsStride != 156)
            Debug.LogError($"[UseComputePlugin] SimParams stride mismatch. Expected 156, got {simParamsStride}. Check struct layout.");

        // Cache function pointer once
        renderEventFunc = GetRenderEventFunc();

        // Create initial particle distribution (simple lattice in bounds)
        Particle[] particles = CreateInitialParticles(particleCount);
        readbackData = new Particle[particleCount];

        // Upload to native plugin once
        SetComputeData(particles, particles.Length);

        // TEMP DEBUG
        GetBoundsWS(out Vector3 dbgMin, out Vector3 dbgMax);
        Debug.Log($"[DEBUG Init] transform.position={transform.position}, boundsMin={boundsMin}, boundsMax={boundsMax}");
        Debug.Log($"[DEBUG Init] GetBoundsWS → min={dbgMin}, max={dbgMax}");
        Debug.Log($"[DEBUG Init] First 4 particles: " +
            $"p[0]={particles[0].position} " +
            $"p[1]={particles[1].position} " +
            $"p[2]={particles[2].position} " +
            $"p[3]={particles[3].position}");

        // Configure plugin
        SetPerfTestMode(perfTestMode);
        SetSubStepCount(subStepCount);

        // Push params once immediately
        PushParams(Time.deltaTime);
        timeAccumulator = 0f;

        initialized = true;

        if (verbose)
        {
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
        //SetPerfTestMode(perfTestMode);

        int stepsToRun = Mathf.Max(1, subStepCount);
        float dtForStep = Mathf.Max(Time.deltaTime, 0.0001f);

        if (useFixedTimeStep)
        {
            float simDt = Mathf.Max(0.0001f, fixedTimeStep);
            timeAccumulator += Time.deltaTime;

            stepsToRun = Mathf.FloorToInt(timeAccumulator / simDt);
            if (stepsToRun > maxSubSteps)
                stepsToRun = maxSubSteps;

            if (stepsToRun <= 0)
                return;

            timeAccumulator -= stepsToRun * simDt;
            // Prevent runaway accumulation when framerate tanks.
            timeAccumulator = Mathf.Min(timeAccumulator, simDt);
            dtForStep = simDt;
        }

        SetSubStepCount(stepsToRun);
        PushParams(dtForStep);
        UpdateHeatSources();

        // Trigger native compute dispatch on the render thread.
        // With execution order attributes, this runs after camera interaction writes.
        if (renderEventFunc != IntPtr.Zero)
            GL.IssuePluginEvent(renderEventFunc, 3);
        frameCount++;

        if (verbose && frameCount % 60 == 0)
        {
            GetComputeResult(readbackData, particleCount);
            // After N iterations of multiply-by-2: values = initial * 2^N
            Debug.Log($"[ComputePlugin] Frame {frameCount} GPU state: {FormatParticles(readbackData, 4)}");

            // Find the first active heat source position
            HeatSourceObj[] sources = FindObjectsByType<HeatSourceObj>(FindObjectsSortMode.None);
            if (sources.Length > 0)
            {
                Vector3 sourcePos = sources[0].transform.position;
                float sourceRadius = sources[0].GetComponent<Collider>()?.bounds.extents.magnitude
                                    ?? sources[0].transform.lossyScale.magnitude * 0.5f;
                float maxTemp = 0f;
                float avgTemp = 0f;
                int nearCount = 0;
                int hotCount = 0;   // particles actually receiving heat
                for (int i = 0; i < particleCount; i++)
                {
                    avgTemp += readbackData[i].temperature;
                    if (readbackData[i].temperature > ambientTemperature + 1f)
                        hotCount++;
                    float dist = Vector3.Distance(readbackData[i].position, sourcePos);
                    if (dist < sourceRadius * 2f)  // check twice the radius for spreading
                    {
                        nearCount++;
                        maxTemp = Mathf.Max(maxTemp, readbackData[i].temperature);
                    }
                }
                avgTemp /= particleCount;
                Debug.Log($"[Frame {frameCount}] " +
                        $"Particles near source: {nearCount} | " +
                        $"Max temp near source: {maxTemp:F1}° | " +
                        $"Global avg temp: {avgTemp:F2}° | " +
                        $"Hot particles (>{ambientTemperature+1f:F0}°): {hotCount}");
            }
            else
            {
                Debug.Log($"[Frame {frameCount}] GPU state: {FormatParticles(readbackData, 4)}");
            }
        }
    }

    /// <summary>
    /// Returns the simulation container bounds in world-space.
    /// </summary>
    public void GetBoundsWS(out Vector3 boundsMinWS, out Vector3 boundsMaxWS)
    {
        Vector3 a = boundsMin;
        Vector3 b = boundsMax;

        if (boundsAreLocalToTransform)
        {
            Vector3 origin = transform.position;
            a += origin;
            b += origin;
        }

        boundsMinWS = Vector3.Min(a, b);
        boundsMaxWS = Vector3.Max(a, b);
    }

    private void PushParams(float dt)
    {
        float h = Mathf.Max(0.001f, smoothingRadius);

        GetBoundsWS(out Vector3 boundsMinWS, out Vector3 boundsMaxWS);

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
        p.boundsMin = boundsMinWS;
        p.poly6Const = poly6Const;
        p.boundsMax = boundsMaxWS;
        p.spikyGradConst = spikyGradConst;
        // Unused by the current shaders (hash uses particleCount as table size),
        // but kept for binary compatibility with the native SimParams struct.
        p.gridDims = new UInt3(0, 0, 0);
        p.viscLapConst = viscLapConst;
        p.gridOrigin = boundsMinWS;
        p.maxCells = 0;

        p.damping = damping;
        p.bitonicK = 0;
        p.bitonicJ = 0;

        p.interactionStrength = interactionStrength;
        p.interactionPos = interactionPos;
        p.interactionRadius = interactionRadius;

        p.thermalDiffusivity = thermalDiffusivity;   
        p.ambientTemperature = ambientTemperature;
        p.coolingRate = coolingRate;

        SetSimParams(p);
    }

    private Particle[] CreateInitialParticles(int count)
    {
        GetBoundsWS(out Vector3 min, out Vector3 max);
        Vector3 size = max - min;

        int nx, ny, nz;
        FindBestFactorGrid(count, size, out nx, out ny, out nz);

        Vector3 safeSize = new Vector3(
            Mathf.Max(size.x, 0.001f),
            Mathf.Max(size.y, 0.001f),
            Mathf.Max(size.z, 0.001f)
        );

        Vector3 spacing = new Vector3(
            safeSize.x / Mathf.Max(nx, 1),
            safeSize.y / Mathf.Max(ny, 1),
            safeSize.z / Mathf.Max(nz, 1)
        );

        Vector3 start = min + spacing * 0.5f;

        Particle[] particles = new Particle[count];
        int idx = 0;
        for (int z = 0; z < nz && idx < count; z++)
        {
            for (int y = 0; y < ny && idx < count; y++)
            {
                for (int x = 0; x < nx && idx < count; x++)
                {
                    Vector3 pos = start + new Vector3(x * spacing.x, y * spacing.y, z * spacing.z);

                    particles[idx++] = Particle.Create(pos, Vector3.zero, particleMass);
                }
            }
        }

        if (verbose)
        {
            Debug.Log($"[UseComputePlugin] Initial particle grid: {nx} x {ny} x {nz} (= {nx * ny * nz}), spacing=({spacing.x:F4}, {spacing.y:F4}, {spacing.z:F4})");
        }

        return particles;
    }

    private static int NormalizeParticleCount(int requested)
    {
        int clamped = Mathf.Clamp(requested, 1, 262144);

        // Bitonic sort requires power-of-two length for fully correct ordering.
        int pow2 = Mathf.ClosestPowerOfTwo(clamped);
        return Mathf.Clamp(pow2, 1, 262144);
    }

    private static void FindBestFactorGrid(int count, Vector3 boundsSize, out int nx, out int ny, out int nz)
    {
        nx = 1;
        ny = 1;
        nz = count;

        float sx = Mathf.Max(boundsSize.x, 0.0001f);
        float sy = Mathf.Max(boundsSize.y, 0.0001f);
        float sz = Mathf.Max(boundsSize.z, 0.0001f);

        float sGeo = Mathf.Pow(sx * sy * sz, 1f / 3f);
        Vector3 sNorm = new Vector3(sx / sGeo, sy / sGeo, sz / sGeo);

        float bestError = float.MaxValue;

        int[] p = new int[3];
        int[] candidate = new int[3];

        int aMax = Mathf.FloorToInt(Mathf.Pow(count, 1f / 3f));
        for (int a = 1; a <= Mathf.Max(1, aMax); a++)
        {
            if ((count % a) != 0)
                continue;

            int rem = count / a;
            int bMax = Mathf.FloorToInt(Mathf.Sqrt(rem));
            for (int b = 1; b <= Mathf.Max(1, bMax); b++)
            {
                if ((rem % b) != 0)
                    continue;

                int c = rem / b;

                p[0] = a;
                p[1] = b;
                p[2] = c;

                // Evaluate all permutations of (a,b,c) against bounds aspect ratio.
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        if (j == i) continue;
                        int k = 3 - i - j;

                        candidate[0] = p[i];
                        candidate[1] = p[j];
                        candidate[2] = p[k];

                        float dGeo = Mathf.Pow(candidate[0] * candidate[1] * candidate[2], 1f / 3f);
                        Vector3 dNorm = new Vector3(candidate[0] / dGeo, candidate[1] / dGeo, candidate[2] / dGeo);

                        float error = (dNorm - sNorm).sqrMagnitude;
                        if (error < bestError)
                        {
                            bestError = error;
                            nx = candidate[0];
                            ny = candidate[1];
                            nz = candidate[2];
                        }
                    }
                }
            }
        }
    }

    private void OnValidate()
    {
        particleCount = NormalizeParticleCount(particleCount);
        maxSubSteps = Mathf.Max(1, maxSubSteps);
        fixedTimeStep = Mathf.Max(0.0001f, fixedTimeStep);
    }

    private void OnDrawGizmosSelected()
    {
        GetBoundsWS(out Vector3 min, out Vector3 max);

        Gizmos.color = new Color(0f, 1f, 1f, 0.65f);
        Gizmos.DrawWireCube((min + max) * 0.5f, (max - min));

        if (!Application.isPlaying)
            return;

        if (Mathf.Abs(interactionStrength) > 0.0001f)
        {
            Gizmos.color = interactionStrength > 0f
                ? new Color(0f, 1f, 0.5f, 0.65f)
                : new Color(1f, 0.2f, 0f, 0.65f);
            Gizmos.DrawWireSphere(interactionPos, interactionRadius);
        }
    }

    void UpdateHeatSources()
    {
        HeatSourceObj[] sources = FindObjectsByType<HeatSourceObj>(FindObjectsSortMode.None);

        // Clear all slots first
        for (int i = 0; i < MAX_HEAT_SOURCES; i++)
            _heatSources[i] = default;

        int count = Mathf.Min(sources.Length, MAX_HEAT_SOURCES);
        for (int i = 0; i < count; i++)
        {
            Vector3 pos = sources[i].transform.position;
            Collider col = sources[i].GetComponent<Collider>();
            float radius = col != null? col.bounds.extents.magnitude : sources[i].transform.lossyScale.magnitude * 0.5f;
            _heatSources[i].posX = pos.x;
            _heatSources[i].posY = pos.y;
            _heatSources[i].posZ = pos.z;
            _heatSources[i].radius = radius;
            _heatSources[i].temperature = sources[i].GetTemperature();
            _heatSources[i].active= 1u;

            if (frameCount % 60 == 0)
                Debug.Log($"[HeatSource {i}] pos=({pos.x:F2},{pos.y:F2},{pos.z:F2}) " + $"radius={radius:F2} temp={sources[i].GetTemperature():F1}");
        }
        if (count == 0 && frameCount % 60 == 0)
            Debug.LogWarning("[HeatSources] No HeatSource components found in scene.");

        SetFluidHeatSources(_heatSources, MAX_HEAT_SOURCES);
    }

    private static string FormatParticles(Particle[] arr, int maxShow)
    {
        int show = Mathf.Min(arr.Length, maxShow);
        string result = "[";
        for (int i = 0; i < show; i++)
        {
            if (i > 0) result += ", ";
            result += $"pos={arr[i].position} temp={arr[i].temperature:F2}";
        }
        if (arr.Length > maxShow)
            result += $", ... ({arr.Length - maxShow} more)";
        result += "]";
        return result;
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
        public float  thermalDiffusivity;
        public float  ambientTemperature;
        public float  coolingRate;
    }

    
    [StructLayout(LayoutKind.Sequential)]
    public struct HeatSource
    {
        public float posX, posY, posZ;  // world-space position
        public float radius;            // radius of influence
        public float temperature;       // target temperature
        public uint  active;            // 1 = active, 0 = inactive slot
        public uint  _pad0;
        public uint  _pad1;
    }
}
