using UnityEngine;
using UnityEngine.Rendering;


/// Attach to a Camera to visualise the voxel volume produced by
/// <see cref="VoxelTracerSystem"/> via DDA ray marching.
/// Composites voxels on top of the normal scene rendering.
/// Normals are derived from DDA face-crossing direction.
/// Requires <see cref="VoxelCompositeFeature"/> added to the active URP Renderer.
[RequireComponent(typeof(Camera))]
public sealed class VoxelTracerCamera : MonoBehaviour
{
    // Inspector

    [Header("References")]
    public VoxelTracerSystem voxelSystem;
    public ComputeShader rayMarchCS;

    [Header("Visualisation")]
    public VisMode visMode = VisMode.Lit;
    public Color surfaceColor = new Color(0.85f, 0.85f, 0.85f);

    [Header("Lighting")]
    public Vector3 lightDirection = new Vector3(0.5f, 1f, 0.3f);
    public Color lightColor = Color.white;
    public Color ambientColor = new Color(0.12f, 0.12f, 0.18f);

    [Header("Quality")]
    [Range(256, 4096)] public int maxSteps = 1024;

    [Header("Temperature Visualisation")]
    public Texture3D tempTexture;
    public float minDisplayTemp = 0f;
    public float maxDisplayTemp = 100f;

    public enum VisMode { Lit = 0, Normals = 1 }

    // Private state

    Camera _cam;
    int _kernel;
    RenderTexture _colorRT;
    Material _compositeMat;

    static readonly int _VoxTex = Shader.PropertyToID("_VoxTex");

    // Public accessors (used by VoxelCompositeFeature)

    public bool IsReadyToRender => enabled && voxelSystem != null && voxelSystem.IsReady
                                   && rayMarchCS != null && _compositeMat != null
                                   && tempTexture != null;
    public Material CompositeMaterial => _compositeMat;
    public RenderTexture ColorRT => _colorRT;

    // Lifecycle

    void OnEnable()
    {
        _cam = GetComponent<Camera>();

        if (rayMarchCS == null)
        {
            Debug.LogError("VoxelTracerCamera: assign rayMarchCS.");
            enabled = false;
            return;
        }

        _kernel = rayMarchCS.FindKernel("RayMarch");

        _compositeMat = new Material(Shader.Find("Hidden/VoxelComposite"));
        if (_compositeMat == null || _compositeMat.shader == null || !_compositeMat.shader.isSupported)
        {
            Debug.LogError("VoxelTracerCamera: could not find Hidden/VoxelComposite shader.");
        }
    }

    void OnDisable()
    {
        ReleaseRTs();
        if (_compositeMat != null) { Destroy(_compositeMat); _compositeMat = null; }
    }

    // Public API for VoxelCompositeFeature


    //Ensure the colour render target exists at the given resolution.
    //Called by VoxelCompositeFeature before recording render graph passes.

    public void EnsureColorRT(int w, int h)
    {
        if (_colorRT != null && _colorRT.width == w && _colorRT.height == h)
            return;

        ReleaseRTs();

        _colorRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point,
            useMipMap = false
        };
        _colorRT.Create();
    }

    /// Record compute-shader dispatch commands into the given CommandBuffer.
    /// Called by VoxelCompositeFeature inside a render graph unsafe pass.
    public void DispatchRayMarch(CommandBuffer cmd, int w, int h)
    {
        if (!IsReadyToRender || _colorRT == null) return;

        // Camera
        cmd.SetComputeMatrixParam(rayMarchCS, "_CamToWorld", _cam.cameraToWorldMatrix);
        cmd.SetComputeMatrixParam(rayMarchCS, "_InvProj", _cam.projectionMatrix.inverse);
        cmd.SetComputeVectorParam(rayMarchCS, "_CamPos", _cam.transform.position);
        cmd.SetComputeVectorParam(rayMarchCS, "_ScreenSize", new Vector4(w, h, 0, 0));

        // Volume
        cmd.SetComputeIntParam(rayMarchCS, "_Width", voxelSystem.Nx);
        cmd.SetComputeIntParam(rayMarchCS, "_Height", voxelSystem.Ny);
        cmd.SetComputeIntParam(rayMarchCS, "_Depth", voxelSystem.Nz);
        cmd.SetComputeVectorParam(rayMarchCS, "_Start", voxelSystem.ActiveGridMin);
        cmd.SetComputeFloatParam(rayMarchCS, "_Unit", voxelSystem.ActiveVoxelSize);

        // Fill texture
        cmd.SetComputeTextureParam(rayMarchCS, _kernel, "_FillTex", voxelSystem.FillTexture);

        // Shading
        cmd.SetComputeIntParam(rayMarchCS, "_VisMode", (int)visMode);
        cmd.SetComputeVectorParam(rayMarchCS, "_SurfaceColor", (Vector4)surfaceColor);
        cmd.SetComputeVectorParam(rayMarchCS, "_BackgroundColor", new Vector4(0, 0, 0, 0));

        Vector3 ld = lightDirection.normalized;
        cmd.SetComputeVectorParam(rayMarchCS, "_LightDir", new Vector4(ld.x, ld.y, ld.z, 0));
        cmd.SetComputeVectorParam(rayMarchCS, "_LightColor", (Vector4)lightColor);
        cmd.SetComputeVectorParam(rayMarchCS, "_AmbientColor", (Vector4)ambientColor);

        cmd.SetComputeIntParam(rayMarchCS, "_MaxSteps", maxSteps);

        // Output
        cmd.SetComputeTextureParam(rayMarchCS, _kernel, "_ColorOut", _colorRT);

        if (tempTexture != null)
            cmd.SetComputeTextureParam(rayMarchCS, _kernel, "_TempTex", tempTexture);
        cmd.SetComputeFloatParam(rayMarchCS, "_MinTemp", minDisplayTemp);
        cmd.SetComputeFloatParam(rayMarchCS, "_MaxTemp", maxDisplayTemp);

        // Dispatch
        cmd.DispatchCompute(rayMarchCS, _kernel, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
    }

    // RT management

    void ReleaseRTs()
    {
        if (_colorRT != null) { _colorRT.Release(); Destroy(_colorRT); _colorRT = null; }
    }
}
