// ============================================================
// Water Screen-Space Fluid — C# bridge between the physics
// simulation and WaterScreenSpaceFluidFeature.
//
// Subscribes to two draw events:
//   OnDrawDepth    -> particle depth pass  (sphere impostor, writes eye-depth)
//   OnDrawThickness -> particle thickness pass (additive sphere chord)
// ============================================================
using UnityEngine;
using UnityEngine.Rendering;

public sealed class WaterPhaseScreenSpaceFluidRenderer
{
    // Shader property IDs cached at class load time. Using IDs instead of strings avoids
    // repeated hash lookups every frame when setting material properties.
    private static readonly int ID_ParticleBuffer = Shader.PropertyToID("_ParticleBuffer");
    private static readonly int ID_ParticleCount  = Shader.PropertyToID("_ParticleCount");
    private static readonly int ID_ParticleRadius = Shader.PropertyToID("_ParticleRadius");
    private static readonly int ID_RenderPhase    = Shader.PropertyToID("_RenderPhase");

    // Current frame state set by Render, read by the draw callbacks when the pipeline fires them.
    private ComputeBuffer _particleBuffer;
    private Material      _material;
    private int           _particleCount;
    private float         _particleRadius;

    // Guards the draw callbacks. When false, DrawParticlesDepth and DrawParticlesThickness return immediately
    // so the render pipeline does not try to draw particles that haven't been set up this frame.
    private bool _active;

    // The "bound" fields track what was last uploaded to the material so we only call SetBuffer/SetInt/SetFloat
    // when something actually changed. These are expensive calls and the material is shared, so we minimize them.
    private Material      _boundMaterial;
    private ComputeBuffer _boundParticleBuffer;
    private int           _boundParticleCount = -1;
    private float         _boundParticleRadius = float.NaN;  // NaN ensures the first frame always uploads

    // CachePassIndices looks up pass indices by name which involves a string search.
    // We cache the result and only redo it when the material reference changes.
    private Material _passCacheMaterial;
    private int      _depthPass     = -1;
    private int      _thicknessPass = -1;

    public WaterPhaseScreenSpaceFluidRenderer()
    {
        // Subscribe to the URP draw events immediately at construction.
        // These callbacks are what connect this renderer to the render pass.
        // The render pass fires them at the correct moment in the pipeline when the right render target is bound.
        WaterScreenSpaceFluidFeature.OnDrawDepth      += DrawParticlesDepth;
        WaterScreenSpaceFluidFeature.OnDrawThickness  += DrawParticlesThickness;
    }

    // Called every frame by PhysicsWaterPhaseBridge when SSF mode is active.
    // This does not issue draw calls directly. It just updates state so the draw callbacks
    // have valid data when the URP render pass fires them later in the same frame.
    public void Render(
        WaterPhaseBridgeSettings settings,
        UseComputePlugin         computePlugin,
        WaterPhaseResources      resources,
        Vector3                  boundsMin,
        Vector3                  boundsMax,
        int                      layer)
    {
        if (settings == null || computePlugin == null || resources == null) return;

        _material       = settings.Rendering.screenSpaceFluidMaterial;
        _particleBuffer = resources.ParticleOutputBuffer;
        // Clamp to 0 to guard against a negative count from the compute plugin before it has run.
        _particleCount  = Mathf.Max(0, computePlugin.FluidParticleCount);

        // If any critical resource is missing or there are no particles this frame, hide the effect.
        // This handles cases like: material removed in the inspector, buffer not yet allocated,
        // or the simulation was paused and produced zero particles.
        if (_material == null || _particleBuffer == null || _particleCount <= 0)
        {
            SetInactive();
            return;
        }

        // Read the particle radius from the material so it stays consistent with whatever
        // the artist has set in the inspector, rather than maintaining a separate setting.
        float configuredParticleRadius = _material.GetFloat(ID_ParticleRadius);
        _particleRadius = configuredParticleRadius;

        // Upload the particle buffer, count, and radius to the material now if they changed.
        // This needs to happen in Render (CPU side) before the draw callbacks fire (GPU side).
        PrepareMaterialForDraw(_material);

        // Mark as active so the draw callbacks know they are allowed to issue draw calls.
        _active = true;

        // Push the per-frame state to the feature so the URP render pass can pick up this
        // frame's material and bounds. The render pass reads these static fields when it runs.
        WaterScreenSpaceFluidFeature.ActiveMaterial = _material;
        WaterScreenSpaceFluidFeature.IsActive       = true;
        WaterScreenSpaceFluidFeature.BoundsMin      = boundsMin;
        WaterScreenSpaceFluidFeature.BoundsMax      = boundsMax;
        WaterScreenSpaceFluidFeature.HasBounds      = true;
    }

    // Clears all per-frame state and tells the feature to stop rendering.
    // Called when the particle count drops to zero, a required asset goes missing,
    // or the mode switches away from screen-space fluid.
    public void SetInactive()
    {
        _active = false;

        // Clear all references so the draw callbacks see a clean null state and skip early.
        _particleBuffer      = null;
        _particleCount       = 0;
        _material            = null;

        // Reset the "bound" cache so the next Render call re-uploads everything from scratch.
        // This matters because SetInactive can happen mid-session and the material state is stale afterward.
        _boundMaterial       = null;
        _boundParticleBuffer = null;
        _boundParticleCount  = -1;
        _boundParticleRadius = float.NaN;
        _passCacheMaterial   = null;
        _depthPass           = -1;
        _thicknessPass       = -1;

        // Tell the feature to stop adding the render pass to the pipeline.
        WaterScreenSpaceFluidFeature.IsActive       = false;
        WaterScreenSpaceFluidFeature.ActiveMaterial = null;
        WaterScreenSpaceFluidFeature.HasBounds      = false;
    }

    // Full teardown. Called when the owning MonoBehaviour is destroyed.
    // Must unsubscribe from the events or the static event delegate holds a reference
    // to this dead object and either leaks memory or crashes when the pipeline fires later.
    public void Release()
    {
        WaterScreenSpaceFluidFeature.OnDrawDepth      -= DrawParticlesDepth;
        WaterScreenSpaceFluidFeature.OnDrawThickness  -= DrawParticlesThickness;
        SetInactive();
    }

    // ---- Draw callbacks (fired by the URP render pass, not called directly) ----

    // Invoked by WaterSSFRenderPass when the depth render target is bound.
    // At this point all draw calls land into the custom depth texture, not the main color buffer.
    private void DrawParticlesDepth(RasterCommandBuffer cmd, Material mat)
    {
        // _active is false if Render wasn't called this frame or SetInactive was called.
        // All other guards are safety nets for race conditions or external tampering.
        if (!_active || mat == null || _particleBuffer == null || _particleCount <= 0) return;

        // Re-run PrepareMaterialForDraw here in case the material reference the render pass
        // provides (mat) differs from what Render uploaded to. In practice they should be the same,
        // but this keeps the draw callbacks self-contained and safe.
        PrepareMaterialForDraw(mat);
        DrawQuads(cmd, mat, _depthPass);
    }

    // Invoked by WaterSSFRenderPass when the thickness render target is bound.
    // Thickness uses additive blending (no depth buffer) so all particles accumulate.
    private void DrawParticlesThickness(RasterCommandBuffer cmd, Material mat)
    {
        if (!_active || mat == null || _particleBuffer == null || _particleCount <= 0) return;

        PrepareMaterialForDraw(mat);
        DrawQuads(cmd, mat, _thicknessPass);
    }

    // ---- Helpers -------------------------------------------------------

    // Uploads particle data to the material only when something has actually changed.
    // SetBuffer and SetInt are not free, and since this material is shared between
    // the depth and thickness passes, redundant uploads double the cost for no benefit.
    private void PrepareMaterialForDraw(Material mat)
    {
        // Make sure pass indices are resolved for this material before we use them.
        CachePassIndices(mat);

        // Compare all four values that the shader cares about. If all match, the material
        // already has the correct state from a previous call this frame and we can skip the upload.
        if (_boundMaterial == mat &&
            _boundParticleBuffer == _particleBuffer &&
            _boundParticleCount == _particleCount &&
            Mathf.Approximately(_boundParticleRadius, _particleRadius))
        {
            return;
        }

        mat.SetBuffer(ID_ParticleBuffer, _particleBuffer);
        mat.SetInt   (ID_ParticleCount,  _particleCount);
        mat.SetFloat (ID_ParticleRadius, _particleRadius);
        // 0 = liquid water phase. The same material can handle multiple phases (e.g. vapour = 1).
        // Keeping this as an explicit set here means the shader always knows which phase we're in,
        // even if another pass left a different value.
        mat.SetInt   (ID_RenderPhase, 0);

        // Record what we just uploaded so the next call can skip if nothing changed.
        _boundMaterial       = mat;
        _boundParticleBuffer = _particleBuffer;
        _boundParticleCount  = _particleCount;
        _boundParticleRadius = _particleRadius;
    }

    // Resolves shader pass indices by name and caches them so we don't search every frame.
    // FindPass does a linear string search through the shader's pass list, so calling it
    // every frame for every particle draw would be measurably slow.
    private void CachePassIndices(Material mat)
    {
        // Only redo the lookup when the material reference changes. The indices are
        // stable for the lifetime of a given material so there's no need to re-check otherwise.
        if (_passCacheMaterial == mat)
            return;

        _depthPass = mat.FindPass("ScreenSpaceFluidDepth");
        // Fall back to pass 0 if the named pass doesn't exist. Matches the PASS_DEPTH constant
        // in WaterSSFRenderPass so the draw call still uses the right sub-shader.
        if (_depthPass < 0)
            _depthPass = 0;

        _thicknessPass = mat.FindPass("ScreenSpaceFluidThickness");
        if (_thicknessPass < 0)
            _thicknessPass = 1;

        _passCacheMaterial = mat;

        // Invalidate the bound cache whenever the material changes because the new material
        // may have different buffer bindings or none at all, so we must re-upload.
        _boundMaterial = null;
    }

    // Issues a GPU draw call for all particles without a vertex buffer.
    // The vertex shader generates billboard quad geometry procedurally using the vertex ID,
    // so we only need to tell the GPU how many vertices to produce per particle (6 = 2 triangles).
    private void DrawQuads(RasterCommandBuffer cmd, Material mat, int pass)
    {
        // 6 vertices per particle (two triangles forming the billboard quad).
        // The vertex shader uses gl_VertexID / SV_VertexID to figure out which corner it is.
        cmd.DrawProcedural(
            Matrix4x4.identity,
            mat,
            pass,
            MeshTopology.Triangles,
            6,              // vertex count per instance
            _particleCount  // instance count (one billboard per particle)
        );
    }
}
