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
    private static extern void SetThermalEnabled([MarshalAs(UnmanagedType.I1)] bool enabled);

    [DllImport(PluginName)]
    private static extern void SetSimParams(SimParams param);

    [DllImport(PluginName)]
    private static extern IntPtr GetRenderEventFunc();

    [DllImport(PluginName)]
    private static extern void SetFluidHeatSources(HeatSource[] sources, int count);

    [DllImport(PluginName)]
    private static extern void GetComputeResult([Out] Particle[] data, int count);

    [DllImport(PluginName)]
    private static extern void SetSDFData([In] float[] data, int count);

    [DllImport(PluginName)]
    private static extern void SetDrainZones([In] DrainZoneNative[] zones, int count);

    // --------------------------------------------------------------------
    // Public controls (read by other scripts)

    [Header("Particle Count")]
    [Tooltip("Number of particles simulated by the native plugin. Value is used as entered.")]
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
    [Tooltip("Enable/disable the per-frame temperature compute pass on the GPU")]
    public bool enableThermal = true;

    [Tooltip("Run temperature pass every N frames (1 = every frame, 3 = every 3rd). Higher = faster but less responsive heat")]
    [Range(1, 8)]
    public int thermalFrameInterval = 2;

    [Tooltip("How fast heat spreads between particles")]
    [Range(0.001f, 1)]
    public float thermalDiffusivity = 0.1f;

    [Tooltip("Temperature the fluid cools toward")]
    [Range(0f, 500f)]
    public float ambientTemperature = 20f;

    [Tooltip("how fast particles lose heat to the environment")]
    [Range(0f, 0.1f)]
    public float coolingRate = 0.01f;

    [Header("Phase Transition (Liquid ↔ Gas)")]
    [Tooltip("Temperature at which liquid transitions to gas")]
    public float boilingTemperature = 100f;

    [Tooltip("Energy required for full phase transition (higher = slower transition)")]
    [Min(0.01f)]
    public float latentHeat = 50f;

    [Tooltip("Rest density for gas particles (much lower than liquid restDensity)")]
    [Min(0.1f)]
    public float gasRestDensity = 5f;

    [Tooltip("Viscosity for gas particles (lower = more turbulent)")]
    [Min(0.01f)]
    public float gasViscosity = 1f;

    [Tooltip("Upward buoyancy acceleration for gas particles")]
    public float gasBuoyancy = 15f;

    [Tooltip("How much local SPH pressure raises the effective boiling point. " +
             "0 = pressure has no effect. ~0.1 = deep particles are harder to boil.")]
    [Range(0f, 1f)]
    public float pressureBoilingScale = 0.1f;

    [Tooltip("Cohesion (surface tension) strength between liquid particles. " +
             "Higher values keep the fluid together when disturbed. ~0.001–0.01.")]
    [Range(0f, 0.1f)]
    public float cohesionStrength = 0.005f;

    [Header("Init")]
    [Tooltip("Upload particles automatically on Start")]
    public bool autoInitialize = true;

    [Tooltip("Print debug logs")]
    public bool verbose = true;

    public enum SDFSource { Analytic, VoxelTracer }

    [Header("SDF Collision")]
    [Tooltip("Enable SDF-based collision")]
    public bool enableSDF = true;

    [Tooltip("Where to get SDF data from")]
    public SDFSource sdfSource = SDFSource.VoxelTracer;

    [Tooltip("Reference to the VoxelTracerSystem in the scene (auto-found if null)")]
    public VoxelTracerSystem voxelTracerRef;

    [Tooltip("SDF grid cell size in world units (used only for Analytic mode).")]
    [Range(0.05f, 1.0f)]
    public float sdfVoxelSize = 0.2f;

    [Tooltip("Regenerate SDF every N frames for dynamic boundaries (0 = static only)")]
    [Range(0, 60)]
    public int sdfDynamicUpdateInterval = 10;

    [Header("Drains")]
    private const int MAX_DRAIN_ZONES = 16;

    [Header("Heat Sources")]
    [Tooltip("Maximum number of heat sources supported")]
    private const int MAX_HEAT_SOURCES = 16;
    private HeatSource[] _heatSources = new HeatSource[MAX_HEAT_SOURCES];

    // Cached heat source references (avoid FindObjectsByType every frame)
    private HeatSourceObj[] _cachedSources;
    private Collider[] _cachedSourceColliders;
    private Vector3[] _lastSourcePositions;
    private float[] _lastSourceTemps;

    // Cached SDF boundary + drain references
    private FluidBoundary[] _cachedBoundaries;
    private WaterDrain[] _cachedDrains;
    private DrainZoneNative[] _drainNatives = new DrainZoneNative[MAX_DRAIN_ZONES];
    private float[] _sdfData;
    private int _sdfDimX, _sdfDimY, _sdfDimZ;
    private Vector3 _sdfOrigin; // world-space origin of the SDF grid
    private float _sdfCellSize; // actual cell size in use (may come from VoxelTracer)
    private int _sdfDynamicFrameCounter;
    private bool _sdfUploaded;


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
        int requestedCount = particleCount;
        if (requestedCount < 1)
        {
            requestedCount = 1;
            if (verbose)
                // Debug.LogWarning("[UseComputePlugin] particleCount must be >= 1. Auto-adjusted to 1.");
                ;
        }
        particleCount = requestedCount;

        int particleStride = Marshal.SizeOf<Particle>();
        int simParamsStride = Marshal.SizeOf<SimParams>();
        if (particleStride != 64)
            // Debug.LogError($"[UseComputePlugin] Particle stride mismatch. Expected 64, got {particleStride}. Check struct layout.");
            ;
        if (simParamsStride != 176)
            // Debug.LogError($"[UseComputePlugin] SimParams stride mismatch. Expected 156, got {simParamsStride}. Check struct layout.");
            ;

        // Cache function pointer once
        renderEventFunc = GetRenderEventFunc();

        // Create initial particle distribution (simple lattice in bounds)
        Particle[] particles = CreateInitialParticles(particleCount);
        readbackData = new Particle[particleCount];

        // Upload to native plugin once
        SetComputeData(particles, particles.Length);

        // TEMP DEBUG
        GetBoundsWS(out Vector3 dbgMin, out Vector3 dbgMax);
        /*
        Debug.Log($"[DEBUG Init] transform.position={transform.position}, boundsMin={boundsMin}, boundsMax={boundsMax}");
        Debug.Log($"[DEBUG Init] GetBoundsWS → min={dbgMin}, max={dbgMax}");
        Debug.Log($"[DEBUG Init] First 4 particles: " +
            $"p[0]={particles[0].position} " +
            $"p[1]={particles[1].position} " +
            $"p[2]={particles[2].position} " +
            $"p[3]={particles[3].position}");
        */

        // Configure plugin
        SetPerfTestMode(perfTestMode);
        SetSubStepCount(subStepCount);
        SetThermalEnabled(enableThermal);
        CacheHeatSources();

        // Auto-find VoxelTracerSystem if not assigned
        if (sdfSource == SDFSource.VoxelTracer && voxelTracerRef == null)
            voxelTracerRef = FindObjectOfType<VoxelTracerSystem>();

        // Default cell size until SDF is uploaded (VoxelTracer readback is async)
        _sdfCellSize = sdfVoxelSize;

        CacheBoundariesAndDrains();
        GenerateAndUploadSDF();
        UploadDrainZones();

        // Push params once immediately
        PushParams(Time.deltaTime);
        timeAccumulator = 0f;

        initialized = true;

        if (verbose)
        {
            // Debug.Log($"[UseComputePlugin] Initialized. count={particleCount}, ParticleStride={particleStride} bytes, renderEventFunc=0x{renderEventFunc.ToInt64():X}");
        }

        // Helpful hint: this plugin only runs when Unity is using Vulkan.
        // (On other graphics APIs it will early-out and appear frozen.)
        if (verbose && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
        {
            // Debug.LogWarning("[UseComputePlugin] Graphics API is not Vulkan. The native compute dispatch will no-op. " +
            //                 "Enable Vulkan in Project Settings > Player > Other Settings > Graphics APIs.");
        }
    }
    //
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

            // Adaptive throttle: when frame time exceeds budget, scale down sub-steps
            // to prevent the "spiral of death" (slow frame → more steps → slower frame → …).
            if (!perfTestMode && stepsToRun > 1 && Time.deltaTime > 1f / 55f)
            {
                float scale = (1f / 60f) / Time.deltaTime;
                stepsToRun = Mathf.Max(1, Mathf.RoundToInt(stepsToRun * scale));
            }

            timeAccumulator -= stepsToRun * simDt;
            // Prevent runaway accumulation when framerate tanks.
            timeAccumulator = Mathf.Min(timeAccumulator, simDt);
            dtForStep = simDt;
        }

        SetSubStepCount(stepsToRun);

        // Run temperature pass only every N frames to save GPU time
        bool thermalThisFrame = enableThermal && (frameCount % thermalFrameInterval == 0);
        SetThermalEnabled(thermalThisFrame);

        PushParams(dtForStep);
        if (thermalThisFrame)
            UpdateHeatSources();

        // Dynamic SDF regeneration
        if (enableSDF && sdfDynamicUpdateInterval > 0)
        {
            _sdfDynamicFrameCounter++;
            if (_sdfDynamicFrameCounter >= sdfDynamicUpdateInterval)
            {
                _sdfDynamicFrameCounter = 0;

                if (sdfSource == SDFSource.VoxelTracer)
                {
                    // VoxelTracer updates every frame anyway; re-read its SDF periodically
                    GenerateAndUploadSDF();
                }
                else if (_cachedBoundaries != null)
                {
                    bool hasDynamic = false;
                    for (int i = 0; i < _cachedBoundaries.Length; i++)
                    {
                        if (_cachedBoundaries[i] != null && _cachedBoundaries[i].isDynamic)
                        {
                            hasDynamic = true;
                            break;
                        }
                    }
                    if (hasDynamic)
                        GenerateAndUploadSDF();
                }
            }
        }

        // Update drain positions each frame (drains may move)
        if (_cachedDrains != null && _cachedDrains.Length > 0)
            UploadDrainZones();

        // Trigger native compute dispatch on the render thread.
        // With execution order attributes, this runs after camera interaction writes.
        if (renderEventFunc != IntPtr.Zero)
            GL.IssuePluginEvent(renderEventFunc, 3);
        frameCount++;

        if (verbose && frameCount % 300 == 0)
        {
#if UNITY_EDITOR
            GetComputeResult(readbackData, particleCount);
            // Debug.Log($"[ComputePlugin] Frame {frameCount} GPU state: {FormatParticles(readbackData, 4)}");

            // Use cached heat sources instead of FindObjectsByType
            if (_cachedSources != null && _cachedSources.Length > 0 && _cachedSources[0] != null)
            {
                Vector3 sourcePos = _cachedSources[0].transform.position;
                Collider col = _cachedSourceColliders != null && _cachedSourceColliders.Length > 0 ? _cachedSourceColliders[0] : null;
                float sourceRadius = col != null ? col.bounds.extents.magnitude
                                    : _cachedSources[0].transform.lossyScale.magnitude * 0.5f;
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

                int phaseLiquid = 0, phaseGas = 0, phaseDrained = 0;
                for (int i = 0; i < particleCount; i++)
                {
                    if (readbackData[i].phase == 0) phaseLiquid++;
                    else if (readbackData[i].phase == 1) phaseGas++;
                    else phaseDrained++;
                }
                Debug.Log($"[Frame {frameCount}] Phase: liquid={phaseLiquid} gas={phaseGas} drained={phaseDrained} | Avg temp: {avgTemp:F2}°");
            }
            else
            {
                // Debug.Log($"[Frame {frameCount}] GPU state: {FormatParticles(readbackData, 4)}");
            }
#endif
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
        p.sdfVoxelSize = enableSDF ? _sdfCellSize : 0f;
        p._pad1 = 0f;
        p.gravity = gravity;
        p.particleMass = particleMass;
        p.boundsMin = boundsMinWS;
        p.poly6Const = poly6Const;
        p.boundsMax = boundsMaxWS;
        p.spikyGradConst = spikyGradConst;
        p.sdfDims = enableSDF ? new UInt3((uint)_sdfDimX, (uint)_sdfDimY, (uint)_sdfDimZ) : new UInt3(0, 0, 0);
        p.viscLapConst = viscLapConst;
        p.gridOrigin = boundsMinWS;
        p.sdfEnabled = enableSDF ? 1u : 0u;

        p.damping = damping;
        p.bitonicK = 0;
        p.bitonicJ = 0;

        p.interactionStrength = interactionStrength;
        p.interactionPos = interactionPos;
        p.interactionRadius = interactionRadius;

        p.thermalDiffusivity = thermalDiffusivity;   
        p.ambientTemperature = ambientTemperature;
        p.coolingRate = coolingRate;

        p.boilingTemperature = boilingTemperature;
        p.latentHeat = latentHeat;
        p.gasRestDensity = gasRestDensity;
        p.gasViscosity = gasViscosity;
        p.gasBuoyancy = gasBuoyancy;
        p.pressureBoilingScale = pressureBoilingScale;
        p.cohesionStrength = cohesionStrength;

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
            // Debug.Log($"[UseComputePlugin] Initial particle grid: {nx} x {ny} x {nz} (= {nx * ny * nz}), spacing=({spacing.x:F4}, {spacing.y:F4}, {spacing.z:F4})");
        }

        return particles;
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
        particleCount = Mathf.Max(1, particleCount);
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

    void CacheHeatSources()
    {
        _cachedSources = FindObjectsByType<HeatSourceObj>(FindObjectsSortMode.None);
        int count = Mathf.Min(_cachedSources.Length, MAX_HEAT_SOURCES);
        _cachedSourceColliders = new Collider[count];
        _lastSourcePositions = new Vector3[count];
        _lastSourceTemps = new float[count];
        for (int i = 0; i < count; i++)
        {
            _cachedSourceColliders[i] = _cachedSources[i].GetComponent<Collider>();
            _lastSourcePositions[i] = Vector3.one * float.MaxValue; // force first upload
        }
    }

    void UpdateHeatSources()
    {
        // Re-cache if sources were destroyed or new ones added
        if (_cachedSources == null || _cachedSources.Length == 0 ||
            (_cachedSources.Length > 0 && _cachedSources[0] == null))
        {
            CacheHeatSources();
        }

        int count = Mathf.Min(_cachedSources.Length, MAX_HEAT_SOURCES);

        bool dirty = false;
        for (int i = 0; i < count; i++)
        {
            Vector3 pos = _cachedSources[i].transform.position;
            float temp = _cachedSources[i].GetTemperature();
            if (pos != _lastSourcePositions[i] || temp != _lastSourceTemps[i])
            {
                dirty = true;
                _lastSourcePositions[i] = pos;
                _lastSourceTemps[i] = temp;
            }
        }

        if (!dirty) return;

        for (int i = 0; i < MAX_HEAT_SOURCES; i++)
            _heatSources[i] = default;

        for (int i = 0; i < count; i++)
        {
            Vector3 pos = _lastSourcePositions[i];
            Collider col = _cachedSourceColliders[i];
            float radius = col != null ? col.bounds.extents.magnitude : _cachedSources[i].transform.lossyScale.magnitude * 0.5f;
            _heatSources[i].posX = pos.x;
            _heatSources[i].posY = pos.y;
            _heatSources[i].posZ = pos.z;
            _heatSources[i].radius = radius;
            _heatSources[i].temperature = _lastSourceTemps[i];
            _heatSources[i].active = 1u;
        }

        SetFluidHeatSources(_heatSources, MAX_HEAT_SOURCES);
    }

    void CacheBoundariesAndDrains()
    {
        _cachedBoundaries = FindObjectsByType<FluidBoundary>(FindObjectsSortMode.None);
        _cachedDrains = FindObjectsByType<WaterDrain>(FindObjectsSortMode.None);
        _sdfDynamicFrameCounter = 0;
        _sdfUploaded = false;

        if (verbose && _cachedBoundaries.Length > 0)
            // Debug.Log($"[UseComputePlugin] Found {_cachedBoundaries.Length} FluidBoundary objects, {_cachedDrains.Length} WaterDrain objects.");
            ;
    }

    void GenerateAndUploadSDF()
    {
        if (!enableSDF)
        {
            _sdfDimX = _sdfDimY = _sdfDimZ = 0;
            return;
        }

        if (sdfSource == SDFSource.VoxelTracer)
        {
            GenerateSDFFromVoxelTracer();
            return;
        }

        // Analytic mode: generate from FluidBoundary objects
        if (_cachedBoundaries == null || _cachedBoundaries.Length == 0)
        {
            _sdfDimX = _sdfDimY = _sdfDimZ = 0;
            return;
        }

        GetBoundsWS(out Vector3 bMin, out Vector3 bMax);
        FluidSDFGenerator.ComputeGridDims(bMin, bMax, sdfVoxelSize, out _sdfDimX, out _sdfDimY, out _sdfDimZ);

        int totalVoxels = _sdfDimX * _sdfDimY * _sdfDimZ;
        if (totalVoxels <= 0 || totalVoxels > 4 * 1024 * 1024)
        {
            if (verbose)
                // Debug.LogWarning($"[UseComputePlugin] SDF grid too large or invalid: {_sdfDimX}x{_sdfDimY}x{_sdfDimZ} = {totalVoxels} voxels. Skipping.");
                ;
            _sdfDimX = _sdfDimY = _sdfDimZ = 0;
            return;
        }

        _sdfOrigin = bMin;
        _sdfCellSize = sdfVoxelSize;

        float[] rawSdf = FluidSDFGenerator.Generate(_cachedBoundaries, bMin, sdfVoxelSize, _sdfDimX, _sdfDimY, _sdfDimZ);
        UploadSDFWithHeader(rawSdf);
    }

    void GenerateSDFFromVoxelTracer()
    {
        if (voxelTracerRef == null || !voxelTracerRef.IsReady)
        {
            if (verbose)
                // Debug.LogWarning("[UseComputePlugin] VoxelTracerSystem not ready or not assigned. SDF disabled.");
                ;
            _sdfDimX = _sdfDimY = _sdfDimZ = 0;
            return;
        }

        if (!voxelTracerRef.computeSDF)
        {
            if (verbose)
                // Debug.LogWarning("[UseComputePlugin] VoxelTracerSystem.computeSDF is OFF. Enable it to generate SDF.");
                ;
            _sdfDimX = _sdfDimY = _sdfDimZ = 0;
            return;
        }

        RenderTexture sdfTex = voxelTracerRef.SDFTexture;
        if (sdfTex == null)
        {
            if (verbose)
                // Debug.LogWarning("[UseComputePlugin] VoxelTracerSystem SDFTexture is null.");
                ;
            _sdfDimX = _sdfDimY = _sdfDimZ = 0;
            return;
        }

        int nx = voxelTracerRef.Nx;
        int ny = voxelTracerRef.Ny;
        int nz = voxelTracerRef.Nz;
        int totalVoxels = nx * ny * nz;

        if (totalVoxels <= 0 || totalVoxels > 4 * 1024 * 1024)
        {
            if (verbose)
                // Debug.LogWarning($"[UseComputePlugin] VoxelTracer SDF grid too large: {nx}x{ny}x{nz} = {totalVoxels}. Skipping.");
                ;
            _sdfDimX = _sdfDimY = _sdfDimZ = 0;
            return;
        }

        _sdfDimX = nx;
        _sdfDimY = ny;
        _sdfDimZ = nz;
        _sdfOrigin = voxelTracerRef.ActiveGridMin;
        _sdfCellSize = voxelTracerRef.ActiveVoxelSize;

        // Synchronous slice-by-slice readback of the 3D SDF texture
        float[] rawSdf = ReadBack3DTexture(sdfTex, nx, ny, nz);
        UploadSDFWithHeader(rawSdf);

        if (verbose)
        {
            // Debug.Log($"[UseComputePlugin] VoxelTracer SDF uploaded: {_sdfDimX}x{_sdfDimY}x{_sdfDimZ}, " +
            //           $"origin={_sdfOrigin}, voxelSize={_sdfCellSize:F3}");
        }
    }

    static float[] ReadBack3DTexture(RenderTexture rt, int nx, int ny, int nz)
    {
        float[] data = new float[nx * ny * nz];
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
                data[z * (nx * ny) + i] = raw[i];
        }

        RenderTexture.ReleaseTemporary(tempRT);
        Destroy(tempTex);
        return data;
    }

    void UploadSDFWithHeader(float[] rawSdf)
    {
        // Prepend 4-float header: [originX, originY, originZ, 0]
        const int HEADER = 4;
        _sdfData = new float[HEADER + rawSdf.Length];
        _sdfData[0] = _sdfOrigin.x;
        _sdfData[1] = _sdfOrigin.y;
        _sdfData[2] = _sdfOrigin.z;
        _sdfData[3] = 0f;
        Array.Copy(rawSdf, 0, _sdfData, HEADER, rawSdf.Length);

        SetSDFData(_sdfData, _sdfData.Length);
        _sdfUploaded = true;

        if (verbose && sdfSource == SDFSource.Analytic)
            // Debug.Log($"[UseComputePlugin] Analytic SDF uploaded: {_sdfDimX}x{_sdfDimY}x{_sdfDimZ} = {rawSdf.Length} voxels, voxelSize={_sdfCellSize}");
            ;
    }

    void UploadDrainZones()
    {
        if (_cachedDrains == null || _cachedDrains.Length == 0) return;

        for (int i = 0; i < MAX_DRAIN_ZONES; i++)
            _drainNatives[i] = default;

        int count = Mathf.Min(_cachedDrains.Length, MAX_DRAIN_ZONES);
        for (int i = 0; i < count; i++)
        {
            if (!_cachedDrains[i].isActive) continue;
            Vector3 pos = _cachedDrains[i].transform.position;
            _drainNatives[i].posX = pos.x;
            _drainNatives[i].posY = pos.y;
            _drainNatives[i].posZ = pos.z;
            _drainNatives[i].radius = _cachedDrains[i].drainRadius;
            _drainNatives[i].active = 1u;
        }

        SetDrainZones(_drainNatives, MAX_DRAIN_ZONES);
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
        public float sdfVoxelSize;
        public float _pad1;
        public Vector3 gravity;
        public float particleMass;
        public Vector3 boundsMin;
        public float poly6Const;
        public Vector3 boundsMax;
        public float spikyGradConst;
        public UInt3 sdfDims;
        public float viscLapConst;
        public Vector3 gridOrigin;
        public uint sdfEnabled;
        public float damping;
        public uint bitonicK;
        public uint bitonicJ;
        public float interactionStrength;
        public Vector3 interactionPos;
        public float interactionRadius;
        public float  thermalDiffusivity;
        public float  ambientTemperature;
        public float  coolingRate;
        public float  boilingTemperature;
        public float  latentHeat;
        public float  gasRestDensity;
        public float  gasViscosity;
        public float  gasBuoyancy;
        public float  pressureBoilingScale;
        public float  cohesionStrength;
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

    [StructLayout(LayoutKind.Sequential)]
    public struct DrainZoneNative
    {
        public float posX, posY, posZ;
        public float radius;
        public uint  active;
        public uint  _pad0;
        public uint  _pad1;
        public uint  _pad2;
    }
}
