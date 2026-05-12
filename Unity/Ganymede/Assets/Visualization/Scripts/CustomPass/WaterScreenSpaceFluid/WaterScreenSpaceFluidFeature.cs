// ============================================================
// Water Screen-Space Fluid — URP Renderer Feature (orchestrator).
//
// Owns the full Simon Green pipeline:
//   0  Depth         (sphere impostor, eye-depth + HW Z)
//   1  Thickness     (Gaussian splats, additive)
//   2  LightDepth    (sphere impostor from light POV — optional)
//   3  Blur X/Y      (separable bilateral on eye-depth)
//   4  Normals       (edge-aware finite differences)
//   5  Composite     (Fresnel + Beer + refr + refl + shadow)
//   6  ThicknessBlur (separable Gaussian on thickness, X then Y)
//   7  NormalsBlur   (separable Gaussian on normals,   X then Y)
//
// State + draw events are static so a single MonoBehaviour bridge
// (WaterPhaseScreenSpaceFluidRenderer) can drive every active camera.
// ============================================================
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public sealed class WaterScreenSpaceFluidFeature : ScriptableRendererFeature
{
    // -------- Static API consumed by WaterPhaseScreenSpaceFluidRenderer --------
    public static event Action<RasterCommandBuffer, Material> OnDrawDepth;
    public static event Action<RasterCommandBuffer, Material> OnDrawThickness;
    public static event Action<RasterCommandBuffer, Material> OnDrawLightDepth;

    public static bool     IsActive;
    public static Material ActiveMaterial;

    // Optional simulation bounds (world space) — used to fit the light view.
    public static Vector3 BoundsMin;
    public static Vector3 BoundsMax;
    public static bool    HasBounds;

    // -------- Inspector knobs --------
    [SerializeField] private RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingTransparents;

    [Header("Light-View Shadow")]
    [SerializeField] private bool       enableLightShadow      = false;
    [SerializeField] private int        lightShadowResolution  = 1024;
    [SerializeField] private float      lightShadowStrength    = 0.85f;
    [SerializeField] private float      lightShadowBias        = 0.05f;
    [SerializeField] private float      lightShadowExtraExtent = 1.0f;

    private WaterSSFRenderPass _pass;

    public override void Create()
    {
        _pass = new WaterSSFRenderPass
        {
            renderPassEvent = injectionPoint
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null || !IsActive || ActiveMaterial == null) return;

        var camType = renderingData.cameraData.cameraType;
        if (camType != CameraType.Game && camType != CameraType.SceneView) return;

        _pass.renderPassEvent      = injectionPoint;
        _pass.OnDrawDepth          = OnDrawDepth;
        _pass.OnDrawThickness      = OnDrawThickness;
        _pass.OnDrawLightDepth     = OnDrawLightDepth;
        _pass.Material             = ActiveMaterial;
        _pass.EnableLightShadow    = enableLightShadow;
        _pass.LightShadowResolution = Mathf.Clamp(lightShadowResolution, 256, 4096);
        _pass.LightShadowStrength  = Mathf.Clamp01(lightShadowStrength);
        _pass.LightShadowBias      = Mathf.Max(0f, lightShadowBias);
        _pass.LightShadowExtra     = Mathf.Max(0f, lightShadowExtraExtent);
        _pass.HasBounds            = HasBounds;
        _pass.BoundsMin            = BoundsMin;
        _pass.BoundsMax            = BoundsMax;

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing) { }
}
