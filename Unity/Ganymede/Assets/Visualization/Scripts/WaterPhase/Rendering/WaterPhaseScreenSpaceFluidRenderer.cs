// ============================================================
// Water Screen-Space Fluid — C# bridge between the physics
// simulation and WaterScreenSpaceFluidFeature.
//
// Subscribes to two draw events:
//   OnDrawDepth -> particle depth pass  (sphere impostor, writes eye-depth)
//   OnDrawThickness -> particle thickness pass (additive sphere chord)
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
    private Material      _boundMaterial;
    private ComputeBuffer _boundParticleBuffer;
    private int           _boundParticleCount = -1;
    private float         _boundParticleRadius = float.NaN;
    private Material      _passCacheMaterial;
    private int           _depthPass = -1;
    private int           _thicknessPass = -1;

    public WaterPhaseScreenSpaceFluidRenderer(MeshRenderer sourceMeshRenderer)
    {
        _sourceMeshRenderer = sourceMeshRenderer;
        WaterScreenSpaceFluidFeature.OnDrawDepth      += DrawParticlesDepth;
        WaterScreenSpaceFluidFeature.OnDrawThickness  += DrawParticlesThickness;
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
        PrepareMaterialForDraw(_material);

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
        _boundMaterial = null;
        _boundParticleBuffer = null;
        _boundParticleCount = -1;
        _boundParticleRadius = float.NaN;
        _passCacheMaterial = null;
        _depthPass = -1;
        _thicknessPass = -1;
        WaterScreenSpaceFluidFeature.IsActive       = false;
        WaterScreenSpaceFluidFeature.ActiveMaterial = null;
        WaterScreenSpaceFluidFeature.HasBounds      = false;
    }

    public void Release()
    {
        WaterScreenSpaceFluidFeature.OnDrawDepth      -= DrawParticlesDepth;
        WaterScreenSpaceFluidFeature.OnDrawThickness  -= DrawParticlesThickness;
        SetInactive();
    }

    // ---- Event handlers --------------------------------------------------

    private void DrawParticlesDepth(RasterCommandBuffer cmd, Material mat)
    {
        if (!_active || mat == null || _particleBuffer == null || _particleCount <= 0) return;

        PrepareMaterialForDraw(mat);
        DrawQuads(cmd, mat, _depthPass);
    }

    private void DrawParticlesThickness(RasterCommandBuffer cmd, Material mat)
    {
        if (!_active || mat == null || _particleBuffer == null || _particleCount <= 0) return;

        PrepareMaterialForDraw(mat);
        DrawQuads(cmd, mat, _thicknessPass);
    }

    // ---- Helpers ---------------------------------------------------------

    private void PrepareMaterialForDraw(Material mat)
    {
        CachePassIndices(mat);

        if (_boundMaterial == mat &&
            _boundParticleBuffer == _particleBuffer &&
            _boundParticleCount == _particleCount &&
            Mathf.Approximately(_boundParticleRadius, _particleRadius))
        {
            return;
        }

        mat.SetBuffer(ID_ParticleBuffer, _particleBuffer);
        mat.SetInt(ID_ParticleCount,  _particleCount);
        mat.SetFloat(ID_ParticleRadius, _particleRadius);
        mat.SetInt(ID_RenderPhase, 0); // 0 = liquid water

        _boundMaterial = mat;
        _boundParticleBuffer = _particleBuffer;
        _boundParticleCount = _particleCount;
        _boundParticleRadius = _particleRadius;
    }

    private void CachePassIndices(Material mat)
    {
        if (_passCacheMaterial == mat)
            return;

        _depthPass = mat.FindPass("ScreenSpaceFluidDepth");
        if (_depthPass < 0)
            _depthPass = 0;

        _thicknessPass = mat.FindPass("ScreenSpaceFluidThickness");
        if (_thicknessPass < 0)
            _thicknessPass = 1;

        _passCacheMaterial = mat;
        _boundMaterial = null;
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
