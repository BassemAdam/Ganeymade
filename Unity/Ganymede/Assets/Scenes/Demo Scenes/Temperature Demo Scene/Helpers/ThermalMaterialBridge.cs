using UnityEngine;

/// <summary>
/// Feeds the live Texture3D and voxel grid bounds from ThermalReceiver into
/// a material using Custom/TempTextureShader_URP.
///
/// Usage:
///   1. Add this to any GameObject that has a Renderer with the thermal material.
///   2. Assign ThermalReceiver.
///   3. Press Play.
/// </summary>
[RequireComponent(typeof(Renderer))]
public class ThermalMaterialBridge : MonoBehaviour
{
    [Header("Source")]
    public ThermalReceiver thermalReceiver;

    [Header("Optional")]
    [Tooltip("Leave empty to use this GameObject's own Renderer.")]
    public Renderer targetRenderer;

    [Tooltip("Sync min/max temperature range from ThermalReceiver every frame.")]
    public bool syncTempRange = true;

    // Cached shader property IDs
    private static readonly int TempTexID = Shader.PropertyToID("_TempTex");
    private static readonly int MinTempID = Shader.PropertyToID("_MinTemp");
    private static readonly int MaxTempID = Shader.PropertyToID("_MaxTemp");
    private static readonly int GridMinID = Shader.PropertyToID("_GridMin");
    private static readonly int GridMaxID = Shader.PropertyToID("_GridMax");

    private Material _material;
    private Texture3D _lastAssignedTex;

    private void Awake()
    {
        if (targetRenderer == null)
            targetRenderer = GetComponent<Renderer>();

        // Use an instance so we don't modify the shared material asset.
        _material = targetRenderer.material;
    }

    private void Update()
    {
        if (thermalReceiver == null || !thermalReceiver.IsInitialized) return;

        var vt = thermalReceiver.voxelTracer;
        if (vt == null) return;

        // ── Texture ───────────────────────────────────────────────────────
        var tex = thermalReceiver.tempTexture;
        if (tex != null && tex != _lastAssignedTex)
        {
            _material.SetTexture(TempTexID, tex);
            _lastAssignedTex = tex;
        }

        // ── Grid bounds (world space) ─────────────────────────────────────
        // ActiveGridMin is the world-space corner of voxel (0,0,0).
        // The far corner is min + (cellCount * voxelSize) per axis.
        Vector3 gridMin = vt.ActiveGridMin;
        Vector3 gridMax = gridMin + new Vector3(vt.Nx, vt.Ny, vt.Nz) * vt.ActiveVoxelSize;

        _material.SetVector(GridMinID, gridMin);
        _material.SetVector(GridMaxID, gridMax);

        // ── Temperature range ─────────────────────────────────────────────
        if (syncTempRange)
        {
            _material.SetFloat(MinTempID, thermalReceiver.minDisplayTemp);
            _material.SetFloat(MaxTempID, thermalReceiver.maxDisplayTemp);
        }
    }
}