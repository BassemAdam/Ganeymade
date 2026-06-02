using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

// Manages fluid particle spawning from WaterSource components.

[DefaultExecutionOrder(110)]
[RequireComponent(typeof(UseComputePlugin))]
public class SpawnManager : MonoBehaviour
{
#if (UNITY_IOS || UNITY_TVOS || UNITY_SWITCH) && !UNITY_EDITOR
    private const string PluginName = "__Internal";
#else
    private const string PluginName = "RenderingPlugin";
#endif

    // Native entry-points 

    [DllImport(PluginName)]
    private static extern void PatchParticles([In] int[] indices, [In] Particle[] data, int count);

    [DllImport(PluginName)]
    private static extern void GetComputeResult([Out] Particle[] data, int count);
    [DllImport(PluginName)]
    private static extern void ResetAllParticles([In] Particle[] data, int count);

    // Inspector fields 

    [Header("Pool")]
    [Tooltip("Maximum particles that can be spawned in a single frame across all sources.")]
    [Min(1)]
    public int maxSpawnsPerFrame = 64;

    [Tooltip("Re-scan the scene for WaterSource components every N frames. " +
             "Set to 0 to scan only once at Start.")]
    [Min(0)]
    public int sourceScanInterval = 120;

    [Header("Slot Reclamation")]
    [Tooltip("A particle is eligible for reclamation (reuse as a new spawn) when it " +
             "is this many world-units outside the simulation bounds. Increase if you " +
             "see particles being recycled too early near the boundary.")]
    [Min(0f)]
    public float outOfBoundsMargin = 0.5f;

    [Header("Debug")]
    public bool verbose = false;

    //  Private variables
    private UseComputePlugin _sim;
    private WaterSource[] _sources;
    private int _scanCounter;

    // Per-source emission accumulator (fractional particles owed this frame)
    private float[] _emitAccumulators;

    // Mirrored CPU copy of the particle pool (updated each frame via readback)
    private Particle[] _cpuParticles;
    private int _particleCount;

    // Cached sim bounds, refreshed every frame (bounds can be dynamic)
    private Vector3 _boundsMin, _boundsMax;

    // Ring-buffer head for the slot search 
    private int _searchHead;
    private int _tapSearchHead = 0;

    // Reusable patch arrays (re-allocated only when maxSpawnsPerFrame changes)
    private int[] _patchIndices;
    private Particle[] _patchData;
    private int _patchCount;

    private HashSet<int> _tapReservedSlots = new HashSet<int>();
    private List<int> _tapSlotList = null;
    private Particle[] _readbackBuffer;
    private int _tapEmitCount = 0;
    private int _skipReadbackFrames = 3;
    private bool _bulkSpawnDone = false;

    // Unity lifecycle
    private void Start()
    {
        _sim = GetComponent<UseComputePlugin>();
        _particleCount = _sim.particleCount;
        _cpuParticles = new Particle[_particleCount];
        _readbackBuffer = new Particle[_particleCount];
        _patchIndices = new int[maxSpawnsPerFrame];
        _patchData = new Particle[maxSpawnsPerFrame];
        _sim.GetBoundsWS(out _boundsMin, out _boundsMax);
        _tapReservedSlots = _sim.TapReservedSlots;
        _tapSlotList = new List<int>(_tapReservedSlots);
        _tapEmitCount = 0;

        if (_sim.InitialParticleSnapshot != null)
        {
            Array.Copy(_sim.InitialParticleSnapshot, _cpuParticles, _particleCount);
            _sim.InitialParticleSnapshot = null; 
        }
        else
        {
            GetComputeResult(_cpuParticles, _particleCount);
        }
        ScanSources();

        // bulk-spawn: if all particles are dormant , activate them
        // in one shot rather than trickling through PatchParticles frame-by-frame.
        if (TryBulkSpawn())
        {
            ResetAllParticles(_cpuParticles, _particleCount);
            _bulkSpawnDone = true;
            if (verbose)
                Debug.Log($"SpawnManager: Bulk-spawned all dormant particles in one shot.");
        }
        else
        {
            ResetAllParticles(_cpuParticles, _particleCount);
        }

        if (verbose)
            Debug.Log($"SpawnManager: Initialized. pool={_particleCount}, sources={(_sources?.Length ?? 0)}");
    }

    private void Update()
    {
        if (_sources == null) return;

        // Keep bounds current  to supports moving or resizing containers
        _sim.GetBoundsWS(out _boundsMin, out _boundsMax);

        // Optionally re-scan for newly added / removed WaterSources
        if (sourceScanInterval > 0)
        {
            _scanCounter++;
            if (_scanCounter >= sourceScanInterval)
            {
                _scanCounter = 0;
                ScanSources();
            }
        }

        // Resize patch buffers if inspector value was changed at runtime
        if (_patchIndices == null || _patchIndices.Length != maxSpawnsPerFrame)
        {
            _patchIndices = new int[maxSpawnsPerFrame];
            _patchData = new Particle[maxSpawnsPerFrame];
        }

        _patchCount = 0;
        float dt = Time.deltaTime;

        for (int s = 0; s < _sources.Length && _patchCount < maxSpawnsPerFrame; s++)
        {
            WaterSource src = _sources[s];
            if (src == null || !src.isActive) 
                continue;
            
            // Tap mode: skip if this source has exhausted the pool
            if (src.spawnMode == WaterSource.SpawnMode.Tap && src.tapExhausted)
                continue;

            // Lattice mode is one-shot at Start via TryBulkSpawn; skip per-frame emission
            if (src.spawnMode == WaterSource.SpawnMode.Lattice)
                continue;

            // Accumulate fractional particles owed this frame
            _emitAccumulators[s] += src.emissionRate * dt;
            float maxAccum = src.emissionRate * Time.fixedDeltaTime * 2f;
            _emitAccumulators[s] = Mathf.Min(_emitAccumulators[s], maxAccum);
            int toSpawn = Mathf.FloorToInt(_emitAccumulators[s]);
            if (toSpawn <= 0) 
                continue;

            _emitAccumulators[s] -= toSpawn;
            toSpawn = Mathf.Min(toSpawn, maxSpawnsPerFrame - _patchCount);

            Vector3 dir = src.emissionDirection.sqrMagnitude > 0.0001f ? src.emissionDirection.normalized : Vector3.down;

            for (int i = 0; i < toSpawn; i++)
            {
                int slot = src.spawnMode == WaterSource.SpawnMode.Tap ? FindDormantSlot() : FindReclaimableSlot();

                if (slot < 0)
                {
                    if (src.spawnMode == WaterSource.SpawnMode.Tap)
                    {
                        src.tapExhausted = true;
                        if (verbose)
                            Debug.Log($"SpawnManager: Tap '{src.name}' pool exhausted.");
                    }
                    break;
                }

                Particle p = default;

                if (src.spawnMode == WaterSource.SpawnMode.Tap)
                {
                    Vector3 right = Vector3.Cross(dir, Vector3.up);
                    if (right.sqrMagnitude < 0.001f) 
                        right = Vector3.Cross(dir, Vector3.forward);
                    right.Normalize();
                    Vector3 up2 = Vector3.Cross(dir, right).normalized;

                    // Stagger consecutive particles along the stream axis by one smoothing radius so SPH pressure doesn't 
                    // blast them into a cone shape.
                    float streamSpacing = _sim.smoothingRadius * 1.1f;
                    int streamIndex = _tapEmitCount % Mathf.Max(1, Mathf.RoundToInt(src.emissionRadius / streamSpacing));

                    // Use emissionRadius for spawn spread so the user can tighten the stream.
                    // sqrt gives uniform-area disk distribution; for a coherent stream keep emissionRadius small.
                    float radial = Mathf.Sqrt(UnityEngine.Random.Range(0f, 1f)) * src.emissionRadius;
                    float angle  = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    p.position = src.transform.position + right * (radial * Mathf.Cos(angle)) + up2 * (radial * Mathf.Sin(angle)) + dir * (streamIndex * streamSpacing);

                    // Minimal jitter keeps the stream coherent; SPH pressure handles natural spreading.
                    float jitterAxial  = UnityEngine.Random.Range(-0.02f, 0.02f) * src.emissionSpeed;
                    float jitterRadial = UnityEngine.Random.Range(-0.005f, 0.005f) * src.emissionSpeed;
                    p.velocity = dir * (src.emissionSpeed + jitterAxial) + right * (jitterRadial * Mathf.Cos(angle)) + up2 * (jitterRadial * Mathf.Sin(angle));

                    _tapEmitCount++;
                }
                else
                {
                    Vector3 offset = UnityEngine.Random.insideUnitSphere * src.emissionRadius;
                    p.position = src.transform.position + offset;
                    p.velocity = dir * src.emissionSpeed;
                }

                p.mass = _sim.particleMass;
                p.temperature = src.initialTemperature;
                p.phase = 0;
                p.density = _sim.restDensity;
                p.fixedId = (int)slot;

                _cpuParticles[slot] = p;
                _patchIndices[_patchCount] = slot;
                _patchData[_patchCount] = p;
                _patchCount++;
            }
        }
        if (_patchCount > 0)
        {
            PatchParticles(_patchIndices, _patchData, _patchCount);
        }

        // Refresh the CPU mirror so FindReclaimableSlot stays accurate.
        if (_skipReadbackFrames > 0)
        {
            _skipReadbackFrames--;
        }
        else 
        {
            GetComputeResult(_readbackBuffer, _particleCount);
            for (int i = 0; i < _particleCount; i++)
            {
                int id = _readbackBuffer[i].fixedId;

                // Guard against zeroed readback data (g_OutputData not yet populated).
                // A valid particle always has fixedId == i after the reorder pass.
                // If fixedId is out of range or doesn't match, the data is stale — skip it.
                if (id < 0 || id >= _particleCount)
                    continue;

                // Never let GPU overwrite a tap slot's phase to -1 on the CPU mirror
                if (_tapReservedSlots.Contains(id) && _readbackBuffer[i].phase == -1)
                    continue;

                _cpuParticles[id] = _readbackBuffer[i];
            }
        }
    }

    // Slot reclamation 

    // Finds the next reclaimable particle slot using a ring-buffer search.
    // a slot is reclaimable when phase == -1 or the particle has left the simulation bounds by outOfBoundsMargin,

    private int FindReclaimableSlot()
    {
        for (int attempts = 0; attempts < _particleCount; attempts++)
        {
            int idx = _searchHead;
            _searchHead = (_searchHead + 1) % _particleCount;

            if (IsReclaimable(ref _cpuParticles[idx], idx))
                return idx;
        }
        return -1;
    }

    private bool IsReclaimable(ref Particle p, int index)
    {
        if (_tapReservedSlots.Contains(index)) 
            return false; // never recycle tap slots

        if (p.phase == -1) 
            return true;

        float m = outOfBoundsMargin;
        float reclaimMargin = m + 0.1f;
        if (p.position.x < _boundsMin.x - reclaimMargin || p.position.x > _boundsMax.x + reclaimMargin) 
            return true;
        if (p.position.y < _boundsMin.y - reclaimMargin || p.position.y > _boundsMax.y + reclaimMargin) 
            return true;
        if (p.position.z < _boundsMin.z - reclaimMargin || p.position.z > _boundsMax.z + reclaimMargin) 
            return true;

        return false;
    }

    // Finds the next slot that is explicitly dormant (phase == -1).
    // Used by tap sources , never reclaims escaped/out-of-bounds particles.
    private int FindDormantSlot()
    {
        int count = _tapSlotList.Count;
        if (count == 0) 
            return -1;

        for (int attempts = 0; attempts < count; attempts++)
        {
            int idx = _tapSlotList[_tapSearchHead % count];
            _tapSearchHead = (_tapSearchHead + 1) % count;

            if (_cpuParticles[idx].phase == -1)
                return idx;
        }
        return -1;
    }

    // Scene scanning 

    private void ScanSources()
    {
        _sources = FindObjectsByType<WaterSource>(FindObjectsSortMode.None);

        float[] prev = _emitAccumulators;
        _emitAccumulators = new float[_sources.Length];
        if (prev != null)
            Array.Copy(prev, _emitAccumulators, Mathf.Min(prev.Length, _emitAccumulators.Length));

        if (verbose)
            Debug.Log($"SpawnManager: Found {_sources.Length} WaterSource(s).");
    }

    // Bulk spawn 

    // If all non-tap particles are dormant (full spawn pool), activate them all
    // at their source positions in one shot. Returns true if bulk spawn was performed.

    private bool TryBulkSpawn()
    {
        if (_sources == null || _sources.Length == 0) 
            return false;

        // Count dormant non-tap slots
        int dormantCount = 0;
        for (int i = 0; i < _particleCount; i++)
        {
            if (_tapReservedSlots.Contains(i)) 
                continue;
            if (_cpuParticles[i].phase == -1) 
                dormantCount++;
        }

        // Only bulk-spawn if majority of particles are dormant (spawn pool is full)
        if (dormantCount < _particleCount / 2) return false;

        // Collect lattice sources and sphere sources separately
        var latticeSources = new List<WaterSource>();
        var sphereSources = new List<WaterSource>();
        foreach (var src in _sources)
        {
            if (src == null || !src.isActive) 
                continue;
            if (src.spawnMode == WaterSource.SpawnMode.Lattice)
                latticeSources.Add(src);
            else if (src.spawnMode == WaterSource.SpawnMode.Sphere)
                sphereSources.Add(src);
        }
        if (sphereSources.Count == 0 && latticeSources.Count == 0) 
            return false;

        // Build a queue of dormant slot indices
        var dormantSlots = new List<int>(dormantCount);
        for (int i = 0; i < _particleCount; i++)
        {
            if (_tapReservedSlots.Contains(i)) 
                continue;
            if (_cpuParticles[i].phase == -1) 
                dormantSlots.Add(i);
        }
        int slotHead = 0;

        // Lattice sources: place particles at exact grid positions 
        var latticePositions = new List<Vector3>();
        foreach (var src in latticeSources)
        {
            src.GetLatticePositions(latticePositions, _sim.smoothingRadius);
            Vector3 dir = src.emissionDirection.sqrMagnitude > 0.0001f
                ? src.emissionDirection.normalized : Vector3.down;

            for (int j = 0; j < latticePositions.Count && slotHead < dormantSlots.Count; j++)
            {
                int slot = dormantSlots[slotHead++];
                Particle p = default;
                p.position = latticePositions[j];
                p.velocity = Vector3.zero;
                p.mass = _sim.particleMass;
                p.temperature = src.initialTemperature;
                p.phase = 0;
                p.density = _sim.restDensity;
                p.fixedId = slot;
                _cpuParticles[slot] = p;
            }

            if (verbose)
                Debug.Log($"SpawnManager : Lattice '{src.name}': placed {Mathf.Min(latticePositions.Count, slotHead)} particles.");
        }

        // Sphere sources: distribute remaining dormant slots 
        if (sphereSources.Count > 0)
        {
            int remaining = dormantSlots.Count - slotHead;
            int perSource = remaining / sphereSources.Count;
            int sourceIdx = 0;
            int assigned = 0;

            while (slotHead < dormantSlots.Count && sourceIdx < sphereSources.Count)
            {
                int slot = dormantSlots[slotHead++];
                var src = sphereSources[sourceIdx];
                Vector3 dir = src.emissionDirection.sqrMagnitude > 0.0001f
                    ? src.emissionDirection.normalized : Vector3.down;

                Vector3 offset = UnityEngine.Random.insideUnitSphere * src.emissionRadius;
                Particle p = default;
                p.position = src.transform.position + offset;
                p.velocity = dir * src.emissionSpeed;
                p.mass = _sim.particleMass;
                p.temperature = src.initialTemperature;
                p.phase = 0;
                p.density = _sim.restDensity;
                p.fixedId = slot;
                _cpuParticles[slot] = p;
                assigned++;

                if (assigned >= perSource && sourceIdx < sphereSources.Count - 1)
                {
                    assigned = 0;
                    sourceIdx++;
                }
            }
        }

        return true;
    }

    private void OnDrawGizmosSelected()
    {
        if (_sources == null) 
            return;
        foreach (var src in _sources)
        {
            if (src == null || !src.isActive) 
                continue;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.4f);
            Gizmos.DrawWireSphere(src.transform.position, src.emissionRadius);
        }
    }
}