using System;
using System.Collections.Generic;
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

    [Header("Spawn Pool")]
    [Tooltip("How many particles to reserve as dormant (available for spawning). " +
             "These are placed at dormantParkPosition on init and recycled by SpawnManager.")]
    [Min(0)]
    public int spawnPoolReserve = 2048;

    [Tooltip("World-space position where dormant pool particles are parked. " +
             "Must be outside the simulation bounds. Keep in sync with SpawnManager.dormantPosition.")]
    public Vector3 dormantParkPosition = new Vector3(0f, -1000f, 0f);

    [Tooltip("Override where the initial spawn-pool sphere is placed. " +
             "When disabled the sphere is centred on each WaterSource (or the bounds centre if none exist). " +
             "Has no effect when spawnPoolReserve == 0.")]
    public bool overrideSpawnCenter = false;

    [Tooltip("World-space position used as the spawn-pool sphere centre when overrideSpawnCenter is enabled.")]
    public Vector3 spawnCenter = Vector3.zero;

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

    [Header("SDF Collision")]
    [Tooltip("Enable SDF-based collision")]
    public bool enableSDF = false;

    [Tooltip("Surface friction when particles slide along SDF surfaces. 0 = frictionless, 1 = heavy friction.")]
    [Range(0f, 1f)]
    public float sdfFriction = 0.1f;

    [Header("Boundary Particles")]
    [Tooltip("Enable boundary particle collision from voxelized meshes tagged with VoxelBoundaryCollider")]
    public bool enableBoundaryParticles = true;

    [Tooltip("Regenerate boundary particles every N frames (0 = static only, no refresh)")]
    [Min(0)]
    public int boundaryDynamicUpdateInterval = 0;

    [Tooltip("Reference to the VoxelTracerSystem in the scene (auto-found if null)")]
    public VoxelTracerSystem voxelTracerRef;

    [Tooltip("World-space distance particles are kept outside the SDF surface. " +
             "Increase if particles visually enter the boundary mesh.")]
    [Range(0.001f, 0.5f)]
    public float sdfSkinOffset = 0.05f;

    [Tooltip("Regenerate SDF every N frames (1 = every frame, higher = less frequent). " +
             "Set to 1 when moving or scaling boundaries at runtime.")]
    [Range(1, 60)]
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

    // Cached drain references
    private WaterDrain[] _cachedDrains;
    private DrainZoneNative[] _drainNatives = new DrainZoneNative[MAX_DRAIN_ZONES];
    private float[] _sdfData;
    private int _sdfDimX, _sdfDimY, _sdfDimZ;
    private Vector3 _sdfOrigin; // world-space origin of the SDF grid
    private float _sdfCellSize; // actual cell size in use (may come from VoxelTracer)
    private int _sdfDynamicFrameCounter;
    private bool _sdfUploaded;

    public HashSet<int> TapReservedSlots { get; } = new HashSet<int>();

    // Boundary particle state
    private int _boundaryStartIndex;
    private int _boundaryCount;
    private int _boundaryDynamicFrameCounter;
    private bool _boundaryInitialized;

    /// <summary>Number of fluid/gas particles (excluding boundary). SpawnManager should use this for its pool.</summary>
    public int FluidParticleCount => _boundaryStartIndex > 0 ? _boundaryStartIndex : particleCount;

    // --------------------------------------------------------------------

    private bool initialized;
    private IntPtr renderEventFunc;
    private float timeAccumulator;

    private Particle[] readbackData;  // for debug readback (keep reference to avoid GC)
    private int frameCount = 0;
    [NonSerialized] public Particle[] InitialParticleSnapshot = null;

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
        InitialParticleSnapshot = particles; 
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
        if (voxelTracerRef == null)
            voxelTracerRef = FindObjectOfType<VoxelTracerSystem>();

        // Default cell size until SDF is uploaded (VoxelTracer readback is async)
        _sdfCellSize = voxelTracerRef != null ? voxelTracerRef.voxelSize : 0.25f;

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
                GenerateAndUploadSDF();
            }
        }

        // Boundary particle deferred init + dynamic refresh
        if (enableBoundaryParticles)
        {
            if (!_boundaryInitialized)
            {
                // Wait at least 2 frames so GPU has dispatched and readback is valid
                if (frameCount >= 2)
                    GenerateAndUploadBoundaryParticles();
            }
            else if (boundaryDynamicUpdateInterval > 0)
            {
                _boundaryDynamicFrameCounter++;
                if (_boundaryDynamicFrameCounter >= boundaryDynamicUpdateInterval)
                {
                    _boundaryDynamicFrameCounter = 0;
                    RefreshBoundaryParticlePositions();
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
        p.sdfFriction = sdfFriction;
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

        // How many particles go into the spawn-pool sphere vs the background lattice
        int reservedForSpawn = Mathf.Clamp(spawnPoolReserve, 0, count);
        int latticeCount = count - reservedForSpawn;

        Particle[] particles = new Particle[count];
        int idx = 0;

        // ── Spawn-pool sphere(s) ──────────────────────────────────────────
        if (reservedForSpawn > 0)
        {
            WaterSource[] sources = FindObjectsByType<WaterSource>(FindObjectsSortMode.None);
            if (overrideSpawnCenter || sources == null || sources.Length == 0)
            {
                float restSpacing = smoothingRadius * 0.5f;
                float minRadius = restSpacing * Mathf.Pow(reservedForSpawn, 1f / 3f) * 0.6f;
                float sphereRadius = Mathf.Max(smoothingRadius * 3f, minRadius);
                Vector3 centre = overrideSpawnCenter ? spawnCenter : (min + max) * 0.5f;    
                SpawnSphereAt(particles, ref idx, count, centre, sphereRadius, reservedForSpawn, particleMass);
            }
            else
            {
                int perSource = reservedForSpawn / sources.Length;
                int remainder = reservedForSpawn - perSource * sources.Length;
                float restSpacing = smoothingRadius * 0.5f;

                for (int s = 0; s < sources.Length; s++)
                {
                    int thisCount = perSource + (s == sources.Length - 1 ? remainder : 0);
                    if (thisCount <= 0) 
                        continue;

                    // ── TAP MODE ─────────────────────────────────────────────
                    if (sources[s].spawnMode == WaterSource.SpawnMode.Tap)
                    {
                        Vector3 dir = sources[s].emissionDirection.normalized;
                        float speed = sources[s].emissionSpeed;
                        float radius = sources[s].emissionRadius;
                        Vector3 origin = sources[s].transform.position;

                        Vector3 right = Vector3.Cross(dir, Vector3.up);
                        if (right.sqrMagnitude < 0.001f) right = Vector3.Cross(dir, Vector3.forward);
                        right.Normalize();
                        Vector3 up2 = Vector3.Cross(dir, right).normalized;
                        Vector3 gravity = new Vector3(0f, Physics.gravity.y, 0f);

                        int tapStartIdx = idx; // remember where tap slots begin

                        for (int k = 0; k < thisCount && idx < count; k++)
                        {
                            Particle p = Particle.Create(dormantParkPosition, Vector3.zero, particleMass);
                            p.fixedId = idx; 
                            p.phase = -1;
                            particles[idx++] = p;
                        }
                        // Register these indices so SpawnManager never recycles them
                        for (int k = tapStartIdx; k < idx; k++)
                            TapReservedSlots.Add(k);
                    }
                    // ── SPHERE MODE ───────────────────────────────────────────
                    else
                    {
                        Vector3 centre = sources[s].transform.position;
                        float emitRadius = sources[s].emissionRadius;
                        float minRadius = restSpacing * Mathf.Pow(thisCount, 1f / 3f) * 0.6f;
                        float sphereRadius = Mathf.Max(emitRadius, minRadius);
                        SpawnSphereAt(particles, ref idx, count, centre, sphereRadius, thisCount, particleMass);
                    }
                }
            }
        }

        // ── Background lattice for any remaining slots ────────────────────
        // If the user sets spawnPoolReserve < particleCount, the leftover slots are filled with the usual bounds-filling lattice.
        if (latticeCount > 0)
        {
            Vector3 size    = max - min;
            FindBestFactorGrid(latticeCount, size, out int nx, out int ny, out int nz);

            Vector3 safeSize = new Vector3(Mathf.Max(size.x, 0.001f),Mathf.Max(size.y, 0.001f),Mathf.Max(size.z, 0.001f));
            Vector3 spacing = new Vector3(safeSize.x / Mathf.Max(nx, 1),safeSize.y / Mathf.Max(ny, 1),safeSize.z / Mathf.Max(nz, 1));
            Vector3 start = min + spacing * 0.5f;

            for (int z = 0; z < nz && idx < count; z++)
                for (int y = 0; y < ny && idx < count; y++)
                    for (int x = 0; x < nx && idx < count; x++)
                    {
                        Vector3 pos = start + new Vector3(x * spacing.x, y * spacing.y, z * spacing.z);
                        particles[idx++] = Particle.Create(pos, Vector3.zero, particleMass);
                    }
        }

        // Any slots still unfilled (rounding) get parked dormant.
        while (idx < count)
        {
            Particle p = Particle.Create(dormantParkPosition, Vector3.zero, particleMass);
            p.fixedId = idx; 
            p.phase = -1;
            particles[idx++] = p;
        }

        if (verbose)
        {
            // Debug.Log($"[UseComputePlugin] Init: {reservedForSpawn} sphere + {latticeCount} lattice particles.");
        }

        return particles;
    }
    private static void SpawnSphereAt(Particle[] particles, ref int idx, int totalCount,Vector3 centre, float sphereRadius,int spawnCount, float mass)
    {
        for (int i = 0; i < spawnCount && idx < totalCount; i++)
        {
            float t = (float)i / Mathf.Max(1, spawnCount - 1);
            float r = sphereRadius * Mathf.Pow(t, 1f / 3f);
            float inclination = Mathf.Acos(1f - 2f * ((i + 0.5f) / spawnCount));
            float azimuth = Mathf.PI * (1f + Mathf.Sqrt(5f)) * i;
            Vector3 offset = new Vector3(r * Mathf.Sin(inclination) * Mathf.Cos(azimuth), r * Mathf.Cos(inclination), r * Mathf.Sin(inclination) * Mathf.Sin(azimuth));
 
            particles[idx++] = Particle.Create(centre + offset, Vector3.zero, mass);
        }
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
        _cachedDrains = FindObjectsByType<WaterDrain>(FindObjectsSortMode.None);
        _sdfDynamicFrameCounter = 0;
        _sdfUploaded = false;
    }

    void GenerateAndUploadSDF()
    {
        if (!enableSDF)
        {
            _sdfDimX = _sdfDimY = _sdfDimZ = 0;
            return;
        }

        GenerateSDFFromVoxelTracer();
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

        // Bake skin offset: subtract from all SDF values so the zero-isosurface
        // moves outward, keeping particles away from the visual mesh surface.
        for (int i = 0; i < rawSdf.Length; i++)
            _sdfData[HEADER + i] = rawSdf[i] - sdfSkinOffset;

        SetSDFData(_sdfData, _sdfData.Length);
        _sdfUploaded = true;
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
        public float sdfFriction;
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

    // ================================================================
    // Boundary Particles
    // ================================================================

    void GenerateAndUploadBoundaryParticles()
    {
        if (voxelTracerRef == null || !voxelTracerRef.IsReady)
            return; // Retry next frame

        var colliders = VoxelTracerSystem.BoundaryColliders;
        if (colliders == null || colliders.Count == 0)
            return; // Retry next frame until VoxelBoundaryCollider registers

        // Collect bounds and normal filter from tagged objects
        var boundsList = new List<Bounds>(colliders.Count);
        bool useNormalFilter = false;
        Vector3 filterDirection = Vector3.up;
        float filterThreshold = 0f;

        foreach (var bc in colliders)
        {
            if (bc == null) continue;
            var rend = bc.GetComponent<Renderer>();
            if (rend != null)
            {
                var b = rend.bounds;
                b.Expand(voxelTracerRef.voxelSize); // slight expansion to catch edge voxels
                boundsList.Add(b);
            }
            if (bc.useNormalFilter && !useNormalFilter)
            {
                useNormalFilter = true;
                filterDirection = bc.filterDirection;
                filterThreshold = bc.filterThreshold;
            }
        }

        if (boundsList.Count == 0) return;

        List<Vector3> surfacePositions = voxelTracerRef.GetSurfaceVoxelPositions(
            smoothingRadius, boundsList.ToArray(), useNormalFilter, filterDirection, filterThreshold);

        if (surfacePositions.Count == 0) return; // Voxelizer may not have run yet

        const int MAX_ELEMENTS = 262144;
        int maxBoundary = MAX_ELEMENTS - particleCount;
        int boundarySlots = Mathf.Min(surfacePositions.Count, maxBoundary);
        if (boundarySlots <= 0)
        {
            Debug.LogWarning("[Boundary] Cannot fit boundary particles — buffer at max capacity.");
            _boundaryInitialized = true;
            return;
        }

        int newTotal = particleCount + boundarySlots;
        _boundaryStartIndex = particleCount;
        _boundaryCount = boundarySlots;

        // Build the boundary particles
        int[] indices = new int[_boundaryCount];
        Particle[] boundaryData = new Particle[_boundaryCount];
        for (int i = 0; i < _boundaryCount; i++)
        {
            int idx = _boundaryStartIndex + i;
            indices[i] = idx;
            boundaryData[i] = new Particle
            {
                position = surfacePositions[i],
                density = restDensity,
                velocity = Vector3.zero,
                pressure = 0f,
                acceleration = Vector3.zero,
                mass = particleMass,
                temperature = 0f,
                phase = 2,
                latentHeatAccum = 0f,
                fixedId = idx,
            };
        }

        // Expand the native buffer: read current state, append boundary, re-upload
        Particle[] allParticles = new Particle[newTotal];
        // Copy current fluid particles from the snapshot (still valid at this point)
        if (InitialParticleSnapshot != null && InitialParticleSnapshot.Length >= particleCount)
            Array.Copy(InitialParticleSnapshot, allParticles, particleCount);
        else
            GetComputeResult(allParticles, particleCount);

        // Write boundary particles into the tail
        for (int i = 0; i < _boundaryCount; i++)
            allParticles[_boundaryStartIndex + i] = boundaryData[i];

        // Re-upload expanded buffer
        particleCount = newTotal;
        readbackData = new Particle[newTotal];
        SetComputeData(allParticles, newTotal);
        SetComputeData(allParticles, newTotal);
        _boundaryInitialized = true;

        if (verbose)
            Debug.Log($"[Boundary] Generated {_boundaryCount} boundary particles from {surfacePositions.Count} surface voxels. Total: {particleCount}");
    }

    void RefreshBoundaryParticlePositions()
    {
        if (voxelTracerRef == null || !voxelTracerRef.IsReady || _boundaryCount == 0)
            return;

        var colliders = VoxelTracerSystem.BoundaryColliders;
        if (colliders == null || colliders.Count == 0) return;

        var boundsList = new List<Bounds>(colliders.Count);
        bool useNormalFilter = false;
        Vector3 filterDirection = Vector3.up;
        float filterThreshold = 0f;

        foreach (var bc in colliders)
        {
            if (bc == null) continue;
            var rend = bc.GetComponent<Renderer>();
            if (rend != null)
            {
                var b = rend.bounds;
                b.Expand(voxelTracerRef.voxelSize);
                boundsList.Add(b);
            }
            if (bc.useNormalFilter && !useNormalFilter)
            {
                useNormalFilter = true;
                filterDirection = bc.filterDirection;
                filterThreshold = bc.filterThreshold;
            }
        }

        if (boundsList.Count == 0) return;

        List<Vector3> surfacePositions = voxelTracerRef.GetSurfaceVoxelPositions(
            smoothingRadius, boundsList.ToArray(), useNormalFilter, filterDirection, filterThreshold);

        int updateCount = Mathf.Min(surfacePositions.Count, _boundaryCount);
        int[] indices = new int[updateCount];
        Particle[] patchData = new Particle[updateCount];

        for (int i = 0; i < updateCount; i++)
        {
            int idx = _boundaryStartIndex + i;
            indices[i] = idx;
            patchData[i] = new Particle
            {
                position = surfacePositions[i],
                density = restDensity,
                velocity = Vector3.zero,
                pressure = 0f,
                acceleration = Vector3.zero,
                mass = particleMass,
                temperature = 0f,
                phase = 2,
                latentHeatAccum = 0f,
                fixedId = idx,
            };
        }

        PatchParticles(indices, patchData, updateCount);
    }

    [DllImport(PluginName)]
    private static extern void PatchParticles([In] int[] indices, [In] Particle[] data, int count);
}
