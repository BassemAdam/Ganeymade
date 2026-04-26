using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Manages fluid particle spawning from WaterSource components.
/// </summary>
[DefaultExecutionOrder(110)]
[RequireComponent(typeof(UseComputePlugin))]
public class SpawnManager : MonoBehaviour
{
#if (UNITY_IOS || UNITY_TVOS || UNITY_SWITCH) && !UNITY_EDITOR
    private const string PluginName = "__Internal";
#else
    private const string PluginName = "RenderingPlugin";
#endif

    // ── Native entry-points ──────────────────────────────────────────────

    [DllImport(PluginName)]
    private static extern void PatchParticles([In] int[] indices, [In] Particle[] data, int count);

    [DllImport(PluginName)]
    private static extern void GetComputeResult([Out] Particle[] data, int count);

    // ── Inspector ────────────────────────────────────────────────────────

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

    [Header("Dormant Particle")]
    [Tooltip("World-space position where explicitly dormant (phase == -1) particles " +
             "are parked. Must be outside the simulation bounds.")]
    public Vector3 dormantPosition = new Vector3(0f, -1000f, 0f);

    [Header("Debug")]
    public bool verbose = false;

    // ── Internal state ───────────────────────────────────────────────────

    private UseComputePlugin _sim;
    private WaterSource[] _sources;
    private int _scanCounter;

    // Per-source emission accumulator (fractional particles owed this frame)
    private float[] _emitAccumulators;

    // Mirrored CPU copy of the particle pool (updated each frame via readback)
    private Particle[] _cpuParticles;
    private int _particleCount;

    // Cached sim bounds — refreshed every frame (bounds can be dynamic)
    private Vector3 _boundsMin, _boundsMax;

    // Ring-buffer head for the slot search (avoids O(n) scan from 0 every frame)
    private int _searchHead;

    // Reusable patch arrays (re-allocated only when maxSpawnsPerFrame changes)
    private int[] _patchIndices;
    private Particle[] _patchData;
    private int _patchCount;
    // Tag tap slots permanently in a HashSet at Start()
    private HashSet<int> _tapReservedSlots = new HashSet<int>();

    // ── Unity lifecycle ──────────────────────────────────────────────────

    private void Start()
    {
        _sim = GetComponent<UseComputePlugin>();
        _particleCount = _sim.particleCount;
        _cpuParticles = new Particle[_particleCount];
        _patchIndices = new int[maxSpawnsPerFrame];
        _patchData = new Particle[maxSpawnsPerFrame];
        _sim.GetBoundsWS(out _boundsMin, out _boundsMax);
        _tapReservedSlots = _sim.TapReservedSlots;

        // mark all slots as live (position = centre of bounds) so none are reclaimed on frame 1.
        // The real positions arrive on the first Update's GetComputeResult call.
        Vector3 centre = (_boundsMin + _boundsMax) * 0.5f;
        for (int i = 0; i < _particleCount; i++)
        {
            _cpuParticles[i].position = centre;
            _cpuParticles[i].phase = 0;
        }

        ScanSources();

        if (verbose)
            Debug.Log($"[SpawnManager] Initialized. pool={_particleCount}, sources={(_sources?.Length ?? 0)}");
    }

    private void Update()
    {
        if (_sources == null) return;

        // Keep bounds current (supports moving/resizing containers)
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

            // Accumulate fractional particles owed this frame
            _emitAccumulators[s] += src.emissionRate * dt;

            int toSpawn = Mathf.FloorToInt(_emitAccumulators[s]);
            if (toSpawn <= 0) 
                continue;

            _emitAccumulators[s] -= toSpawn;
            toSpawn = Mathf.Min(toSpawn, maxSpawnsPerFrame - _patchCount);

            Vector3 dir = src.emissionDirection.sqrMagnitude > 0.0001f
                ? src.emissionDirection.normalized
                : Vector3.down;

            for (int i = 0; i < toSpawn; i++)
            {
                int slot = src.spawnMode == WaterSource.SpawnMode.Tap? FindDormantSlot() : FindReclaimableSlot(false);

                if (slot < 0)
                {
                    if (src.spawnMode == WaterSource.SpawnMode.Tap)
                    {
                        src.tapExhausted = true;
                        if (verbose)
                            Debug.Log($"[SpawnManager] Tap '{src.name}' pool exhausted.");
                    }
                    break;
                }

                Vector3 offset = UnityEngine.Random.insideUnitSphere * src.emissionRadius;
                Vector3 spawnPos = src.transform.position + offset;

                Particle p = default;
                p.position = spawnPos;

                // Tap mode: tighten the stream — use a narrow cone instead of a full sphere offset,
                // and add a small random radial spread perpendicular to the flow direction.
                if (src.spawnMode == WaterSource.SpawnMode.Tap)
                {
                    Vector3 right = Vector3.Cross(dir, Vector3.up);
                    if (right.sqrMagnitude < 0.001f) right = Vector3.Cross(dir, Vector3.forward);
                    right.Normalize();
                    Vector3 up2 = Vector3.Cross(dir, right).normalized;

                    float radial = UnityEngine.Random.Range(0f, src.emissionRadius * 0.15f); // very tight
                    float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    p.velocity = dir * src.emissionSpeed + right * (radial * Mathf.Cos(angle)) + up2 * (radial * Mathf.Sin(angle));
                }
                else
                {
                    p.velocity = dir * src.emissionSpeed;
                }

                p.mass = _sim.particleMass;
                p.temperature = src.initialTemperature;
                p.phase = 0;   // liquid
                p.density = _sim.restDensity;

                // Mark live in the CPU mirror immediately so this slot won't be
                // picked again before the next GPU readback arrives.
                _cpuParticles[slot] = p;

                _patchIndices[_patchCount] = slot;
                _patchData[_patchCount]= p;
                _patchCount++;
            }
        }

        if (_patchCount > 0)
        {
            PatchParticles(_patchIndices, _patchData, _patchCount);

            if (verbose)
                Debug.Log($"[SpawnManager] Spawned {_patchCount} particles this frame.");
        }

        // Refresh the CPU mirror so FindReclaimableSlot stays accurate.
        // GetComputeResult has 1-frame GPU latency — slots we just patched are
        // already marked live in _cpuParticles above so there's no double-reclaim.
        GetComputeResult(_cpuParticles, _particleCount);
    }

    // ── Slot reclamation ─────────────────────────────────────────────────

    /// <summary>
    /// Finds the next reclaimable particle slot using a ring-buffer search.
    ///
    /// A slot is reclaimable when:
    ///   (a) phase == -1  — explicitly marked dormant, OR
    ///   (b) the particle has left the simulation bounds by outOfBoundsMargin, it has "escaped" and can safely be teleported back to the source.
    /// Returns -1 if no reclaimable slot is available.
    /// </summary>
    private int FindReclaimableSlot(bool tapMode)
    {
        for (int attempts = 0; attempts < _particleCount; attempts++)
        {
            int idx = _searchHead;
            _searchHead = (_searchHead + 1) % _particleCount;

            if (tapMode)
            {
                // Tap: only take genuinely dormant slots, never recycle live/escaped ones
                if (_cpuParticles[idx].phase == -1)
                    return idx;
            }
            else
            {
                if (IsReclaimable(ref _cpuParticles[idx], idx))
                    return idx;
            }
        }
        return -1;
    }

    private bool IsReclaimable(ref Particle p, int index)
    {
        if (_tapReservedSlots.Contains(index)) return false; // never recycle tap slots

        if (p.phase == -1) return true;

        float m = outOfBoundsMargin;
        if (p.position.x < _boundsMin.x - m || p.position.x > _boundsMax.x + m) return true;
        if (p.position.y < _boundsMin.y - m || p.position.y > _boundsMax.y + m) return true;
        if (p.position.z < _boundsMin.z - m || p.position.z > _boundsMax.z + m) return true;

        return false;
    }
    /// Finds the next slot that is explicitly dormant (phase == -1).
    /// Used by tap sources — never reclaims escaped/out-of-bounds particles.
    private int FindDormantSlot()
    {
        float threshold = 1f; // distance from dormantPosition to count as "parked"
        for (int attempts = 0; attempts < _particleCount; attempts++)
        {
            int idx = _searchHead;
            _searchHead = (_searchHead + 1) % _particleCount;

            Particle p = _cpuParticles[idx];

            // Identity check: is this particle parked at the dormant position?
            if (Vector3.Distance(p.position, _sim.dormantParkPosition) < threshold)
                return idx;
        }
        return -1;
    }
    // ── Scene scanning ───────────────────────────────────────────────────

    private void ScanSources()
    {
        _sources = FindObjectsByType<WaterSource>(FindObjectsSortMode.None);

        float[] prev = _emitAccumulators;
        _emitAccumulators = new float[_sources.Length];
        if (prev != null)
            Array.Copy(prev, _emitAccumulators, Mathf.Min(prev.Length, _emitAccumulators.Length));

        if (verbose)
            Debug.Log($"[SpawnManager] Found {_sources.Length} WaterSource(s).");
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

    // ── Particle mirror struct ─────────

    [StructLayout(LayoutKind.Sequential)]
    private struct Particle
    {
        public Vector3 position;        // 12 bytes
        public float density;         //  4 bytes
        public Vector3 velocity;        // 12 bytes
        public float pressure;        //  4 bytes
        public Vector3 acceleration;    // 12 bytes
        public float mass;            //  4 bytes
        public float temperature;     //  4 bytes
        public int phase;           //  4 bytes
        public float latentHeatAccum; //  4 bytes
        public float  _pad1;           //  4 bytes
        // total: 64 bytes
    }
}