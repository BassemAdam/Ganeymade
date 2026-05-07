using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Applies buoyancy and drag forces from SPH fluid to VoxelDynamic Rigidbodies.
/// Attach to the same GameObject as UseComputePlugin.
/// 
/// Supports two modes per-object (set on VoxelDynamic):
///   Analytical: estimates submerged fraction by sampling particles near the object bounds
///   GPUParticleSum: accumulates actual SPH forces from particles overlapping the object
/// </summary>
[DefaultExecutionOrder(120)] // after SpawnManager (110)
[RequireComponent(typeof(UseComputePlugin))]
public class FluidForceApplicator : MonoBehaviour
{
#if (UNITY_IOS || UNITY_TVOS || UNITY_SWITCH) && !UNITY_EDITOR
    private const string PluginName = "__Internal";
#else
    private const string PluginName = "RenderingPlugin";
#endif

    [System.Runtime.InteropServices.DllImport(PluginName)]
    private static extern void GetComputeResult([System.Runtime.InteropServices.Out] Particle[] data, int count);

    [Header("Settings")]
    [Tooltip("How often to sample particles for buoyancy (every N fixed frames). " +
             "Lower = more responsive but costs more.")]
    [Range(1, 10)]
    public int updateInterval = 2;

    [Tooltip("Fluid density for buoyancy calculation. Set to 0 to auto-use SPH restDensity.")]
    public float fluidDensityOverride = 0f;

    [Tooltip("Enable debug gizmos showing buoyancy forces.")]
    public bool debugDraw = false;

    // Internal
    private UseComputePlugin _sim;
    private Particle[] _readbackBuffer;
    private int _particleCount;
    private int _frameCounter;
    private List<VoxelDynamic> _activeObjects = new List<VoxelDynamic>();

    private float fluidDensity => fluidDensityOverride > 0f ? fluidDensityOverride : _sim.restDensity;

    void Start()
    {
        _sim = GetComponent<UseComputePlugin>();
        _particleCount = _sim.particleCount;
        _readbackBuffer = new Particle[_particleCount];
    }

    void FixedUpdate()
    {
        _frameCounter++;
        if (_frameCounter % updateInterval != 0) return;

        // Gather all VoxelDynamic objects that want fluid forces
        _activeObjects.Clear();
        foreach (var vd in VoxelTracerSystem.RegisteredDynamics)
        {
            if (vd == null || !vd.enableFluidForces || vd.rb == null) continue;
            _activeObjects.Add(vd);
        }
        if (_activeObjects.Count == 0) return;

        // Resize readback buffer if particle count changed
        int currentCount = _sim.FluidParticleCount > 0 ? _sim.FluidParticleCount : _sim.particleCount;
        if (_readbackBuffer == null || _readbackBuffer.Length < currentCount)
        {
            _particleCount = currentCount;
            _readbackBuffer = new Particle[_particleCount];
        }

        // Readback particle data from GPU
        GetComputeResult(_readbackBuffer, _particleCount);

        // Refresh bounds for all active objects
        foreach (var vd in _activeObjects)
            vd.RefreshBounds();

        // Apply forces based on mode
        foreach (var vd in _activeObjects)
        {
            if (vd.buoyancyMode == VoxelDynamic.BuoyancyMode.Analytical)
                ApplyAnalyticalBuoyancy(vd);
            else
                ApplyGPUParticleSumForces(vd);
        }
    }

    /// <summary>
    /// Analytical buoyancy: count particles inside/near the object bounds to estimate
    /// submerged fraction, then apply Archimedes' force.
    /// </summary>
    private void ApplyAnalyticalBuoyancy(VoxelDynamic vd)
    {
        Bounds b = vd.worldBounds;
        float h = _sim.smoothingRadius;

        // Expand bounds by smoothing radius to catch nearby particles
        Bounds expanded = b;
        expanded.Expand(h);

        int insideCount = 0;
        int nearbyCount = 0;
        Vector3 avgFluidVelocity = Vector3.zero;

        for (int i = 0; i < _particleCount; i++)
        {
            Particle p = _readbackBuffer[i];
            if (p.phase != 0) continue; // only liquid

            Vector3 pos = p.position;
            if (!expanded.Contains(pos)) continue;

            nearbyCount++;
            avgFluidVelocity += p.velocity;

            if (b.Contains(pos))
                insideCount++;
        }

        if (nearbyCount > 0)
            avgFluidVelocity /= nearbyCount;

        // Estimate submerged fraction from particle density in the object volume
        float objectVolume = vd.ApproximateVolume();
        float particleVolume = _sim.particleMass / fluidDensity;
        float expectedParticlesInVolume = objectVolume / Mathf.Max(particleVolume, 1e-6f);
        float submergedFraction = Mathf.Clamp01(insideCount / Mathf.Max(expectedParticlesInVolume, 1f));
        vd.submergedFraction = submergedFraction;

        if (submergedFraction < 0.001f)
        {
            vd.lastBuoyancyForce = Vector3.zero;
            vd.lastDragForce = Vector3.zero;
            return;
        }

        // Archimedes: buoyancy = fluidDensity * g * submergedVolume
        float submergedVolume = objectVolume * submergedFraction;
        float buoyancyMagnitude = fluidDensity * Mathf.Abs(Physics.gravity.y) * submergedVolume;
        Vector3 buoyancyForce = Vector3.up * buoyancyMagnitude;

        // The Rigidbody already has gravity, so we just add buoyancy
        vd.lastBuoyancyForce = buoyancyForce;
        vd.rb.AddForce(buoyancyForce, ForceMode.Force);

        // Waterline damping: strongly damp vertical velocity when partially submerged
        // to prevent endless bobbing oscillation. Maximum at 50% submerged (waterline).
        float waterlineFactor = 1.0f - Mathf.Abs(submergedFraction - 0.5f) * 2.0f; // peaks at 0.5
        float verticalVel = vd.rb.linearVelocity.y;
        float waterlineDamping = waterlineFactor * vd.dragCoefficient * 2.0f * vd.rb.mass;
        vd.rb.AddForce(Vector3.down * verticalVel * waterlineDamping, ForceMode.Force);

        // Drag: resist motion relative to fluid
        Vector3 relativeVelocity = vd.rb.linearVelocity - avgFluidVelocity;
        float dragMag = 0.5f * fluidDensity * vd.dragCoefficient * submergedFraction *
                        relativeVelocity.sqrMagnitude;
        Vector3 dragForce = -relativeVelocity.normalized * dragMag;

        // Clamp drag to prevent instability
        float maxDrag = vd.rb.mass * relativeVelocity.magnitude / Time.fixedDeltaTime;
        if (dragForce.magnitude > maxDrag)
            dragForce = dragForce.normalized * maxDrag;

        vd.lastDragForce = dragForce;
        vd.rb.AddForce(dragForce, ForceMode.Force);

        // Angular drag
        Vector3 angVel = vd.rb.angularVelocity;
        float angDrag = vd.angularDragCoefficient * submergedFraction * fluidDensity * 0.01f;
        vd.rb.AddTorque(-angVel * angDrag, ForceMode.Force);
    }

    /// <summary>
    /// GPU particle sum: accumulate actual pressure and velocity from all particles
    /// overlapping the object bounds. More accurate for complex shapes.
    /// </summary>
    private void ApplyGPUParticleSumForces(VoxelDynamic vd)
    {
        Bounds b = vd.worldBounds;
        float h = _sim.smoothingRadius;

        Vector3 forceSum = Vector3.zero;
        Vector3 avgFluidVelocity = Vector3.zero;
        int insideCount = 0;
        float totalPressure = 0f;

        for (int i = 0; i < _particleCount; i++)
        {
            Particle p = _readbackBuffer[i];
            if (p.phase != 0) continue;

            Vector3 pos = p.position;
            Vector3 closest = b.ClosestPoint(pos);
            float dist = Vector3.Distance(pos, closest);

            if (dist > h) continue;

            // Particle is within smoothing radius of the object surface
            if (b.Contains(pos))
            {
                insideCount++;
                avgFluidVelocity += p.velocity;

                // Force from pressure: pushes outward from particle toward object center
                Vector3 dirToCenter = (b.center - pos).normalized;
                float weight = 1.0f - dist / h;
                forceSum += dirToCenter * p.pressure * _sim.particleMass / Mathf.Max(p.density, 0.01f) * weight;
                totalPressure += p.pressure;
            }
            else
            {
                // Near-surface particle: contributes pressure force inward
                float weight = 1.0f - dist / h;
                Vector3 dirInward = (pos - closest).normalized;
                forceSum += dirInward * p.pressure * _sim.particleMass / Mathf.Max(p.density, 0.01f) * weight * weight;
                totalPressure += p.pressure * weight;
                insideCount++;
                avgFluidVelocity += p.velocity * weight;
            }
        }

        if (insideCount == 0)
        {
            vd.submergedFraction = 0;
            vd.lastBuoyancyForce = Vector3.zero;
            vd.lastDragForce = Vector3.zero;
            return;
        }

        avgFluidVelocity /= insideCount;

        // Estimate submerged fraction
        float objectVolume = vd.ApproximateVolume();
        float particleVolume = _sim.particleMass / fluidDensity;
        float expectedParticles = objectVolume / Mathf.Max(particleVolume, 1e-6f);
        vd.submergedFraction = Mathf.Clamp01(insideCount / Mathf.Max(expectedParticles, 1f));

        // Apply accumulated SPH force (buoyancy emerges from pressure gradient)
        vd.lastBuoyancyForce = forceSum;
        vd.rb.AddForce(forceSum, ForceMode.Force);

        // Additional Archimedes correction (SPH pressure alone may underestimate buoyancy)
        float archimedesBoost = fluidDensity * Mathf.Abs(Physics.gravity.y) * objectVolume * vd.submergedFraction;
        Vector3 archForce = Vector3.up * archimedesBoost * 0.5f; // 50% blend with SPH forces
        vd.rb.AddForce(archForce, ForceMode.Force);
        vd.lastBuoyancyForce += archForce;

        // Drag
        Vector3 relVel = vd.rb.linearVelocity - avgFluidVelocity;
        float dragMag = 0.5f * fluidDensity * vd.dragCoefficient * vd.submergedFraction *
                        relVel.sqrMagnitude;
        Vector3 dragForce = -relVel.normalized * dragMag;
        float maxDrag = vd.rb.mass * relVel.magnitude / Time.fixedDeltaTime;
        if (dragForce.magnitude > maxDrag)
            dragForce = dragForce.normalized * maxDrag;

        vd.lastDragForce = dragForce;
        vd.rb.AddForce(dragForce, ForceMode.Force);

        // Angular drag
        Vector3 angVel = vd.rb.angularVelocity;
        float angDrag = vd.angularDragCoefficient * vd.submergedFraction * fluidDensity * 0.01f;
        vd.rb.AddTorque(-angVel * angDrag, ForceMode.Force);
    }

    void OnDrawGizmosSelected()
    {
        if (!debugDraw || _activeObjects == null) return;

        foreach (var vd in _activeObjects)
        {
            if (vd == null) continue;
            // Draw buoyancy force
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(vd.worldBounds.center, vd.lastBuoyancyForce * 0.001f);
            // Draw drag force
            Gizmos.color = Color.red;
            Gizmos.DrawRay(vd.worldBounds.center, vd.lastDragForce * 0.001f);
            // Submerged fraction label
            Gizmos.color = new Color(0, 1, 1, vd.submergedFraction);
            Gizmos.DrawWireCube(vd.worldBounds.center, vd.worldBounds.size);
        }
    }
}
