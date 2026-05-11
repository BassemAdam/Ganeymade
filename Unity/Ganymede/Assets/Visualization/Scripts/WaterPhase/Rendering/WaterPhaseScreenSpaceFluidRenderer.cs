// ============================================================
// Water Screen-Space Fluid — C# bridge between the physics
// simulation and WaterScreenSpaceFluidFeature.
//
// Subscribes to two draw events:
//   OnDrawDepth     → particle depth pass  (sphere impostor, writes eye-depth)
//   OnDrawThickness → particle thickness pass (additive sphere chord)
// ============================================================
using UnityEngine;
using UnityEngine.Rendering;

public sealed class WaterPhaseScreenSpaceFluidRenderer
{
    private static readonly int ID_ParticleBuffer = Shader.PropertyToID("_ParticleBuffer");
    private static readonly int ID_ParticleCount  = Shader.PropertyToID("_ParticleCount");
    private static readonly int ID_ParticleRadius = Shader.PropertyToID("_ParticleRadius");
    private static readonly int ID_RenderPhase    = Shader.PropertyToID("_RenderPhase");

    private readonly MeshRenderer _sourceMeshRenderer;

    private ComputeBuffer _particleBuffer;
    private Material      _material;
    private int           _particleCount;
    private float         _particleRadius;
    private bool          _active;

    public WaterPhaseScreenSpaceFluidRenderer(MeshRenderer sourceMeshRenderer)
    {
        _sourceMeshRenderer = sourceMeshRenderer;
        WaterScreenSpaceFluidFeature.OnDrawDepth      += DrawParticlesDepth;
        WaterScreenSpaceFluidFeature.OnDrawThickness  += DrawParticlesThickness;
        WaterScreenSpaceFluidFeature.OnDrawLightDepth += DrawParticlesLightDepth;
    }

    // Called every frame by PhysicsWaterPhaseBridge when SSF mode is active.
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
        _particleCount  = Mathf.Max(0, computePlugin.FluidParticleCount);

        if (_material == null || _particleBuffer == null || _particleCount <= 0)
        {
            SetInactive();
            return;
        }

        if (_sourceMeshRenderer != null)
            _sourceMeshRenderer.enabled = false;

        float configuredParticleRadius = _material.GetFloat(ID_ParticleRadius);
        _particleRadius = configuredParticleRadius;

        _active = true;

        // Push state to the feature
        WaterScreenSpaceFluidFeature.ActiveMaterial = _material;
        WaterScreenSpaceFluidFeature.IsActive       = true;
        WaterScreenSpaceFluidFeature.BoundsMin      = boundsMin;
        WaterScreenSpaceFluidFeature.BoundsMax      = boundsMax;
        WaterScreenSpaceFluidFeature.HasBounds      = true;
    }

    public void SetInactive()
    {
        _active         = false;
        _particleBuffer = null;
        _particleCount  = 0;
        _material       = null;
        WaterScreenSpaceFluidFeature.IsActive       = false;
        WaterScreenSpaceFluidFeature.ActiveMaterial = null;
        WaterScreenSpaceFluidFeature.HasBounds      = false;
    }

    public void Release()
    {
        WaterScreenSpaceFluidFeature.OnDrawDepth      -= DrawParticlesDepth;
        WaterScreenSpaceFluidFeature.OnDrawThickness  -= DrawParticlesThickness;
        WaterScreenSpaceFluidFeature.OnDrawLightDepth -= DrawParticlesLightDepth;
        SetInactive();
    }

    // ---- Event handlers --------------------------------------------------

    private void DrawParticlesDepth(RasterCommandBuffer cmd, Material mat)
    {
        if (!_active || mat == null || _particleBuffer == null || _particleCount <= 0) return;

        BindParticleState(mat);
        int pass = mat.FindPass("ScreenSpaceFluidDepth");
        if (pass < 0) pass = 0;
        DrawQuads(cmd, mat, pass);
    }

    private void DrawParticlesThickness(RasterCommandBuffer cmd, Material mat)
    {
        if (!_active || mat == null || _particleBuffer == null || _particleCount <= 0) return;

        BindParticleState(mat);
        int pass = mat.FindPass("ScreenSpaceFluidThickness");
        if (pass < 0) pass = 1;
        DrawQuads(cmd, mat, pass);
    }

    private void DrawParticlesLightDepth(RasterCommandBuffer cmd, Material mat)
    {
        if (!_active || mat == null || _particleBuffer == null || _particleCount <= 0) return;

        BindParticleState(mat);
        int pass = mat.FindPass("ScreenSpaceFluidLightDepth");
        if (pass < 0) pass = 2;
        DrawQuads(cmd, mat, pass);
    }

    // ---- Helpers ---------------------------------------------------------

    private void BindParticleState(Material mat)
    {
        mat.SetBuffer(ID_ParticleBuffer, _particleBuffer);
        mat.SetInt(ID_ParticleCount,  _particleCount);
        mat.SetFloat(ID_ParticleRadius, _particleRadius);
        mat.SetInt(ID_RenderPhase, 0); // 0 = liquid water
    }

    private void DrawQuads(RasterCommandBuffer cmd, Material mat, int pass)
    {
        // 6 vertices per particle (two triangles forming the billboard quad)
        cmd.DrawProcedural(
            Matrix4x4.identity,
            mat,
            pass,
            MeshTopology.Triangles,
            6,
            _particleCount);
    }
}
