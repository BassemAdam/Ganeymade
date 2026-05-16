using UnityEngine;

/// <summary>
/// Wraps a terrain (or any flat-ish area) with a large inverted cylinder using
/// the Custom/TerrainEdgeFog shader so the player can never see past the edge.
///
/// Usage:
/// 1. Add this component to any GameObject in the scene.
/// 2. Assign 'targetTerrain' (or just leave it null and set 'centerOverride').
/// 3. Press Play (or use the context-menu "Rebuild Fog Ring" while editing).
/// </summary>
[ExecuteAlways]
public class TerrainEdgeFogRing : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("Terrain to wrap. If null, uses 'centerOverride' + 'radiusOverride'.")]
    public Terrain targetTerrain;

    [Tooltip("World-space center used when no terrain is assigned.")]
    public Vector3 centerOverride = Vector3.zero;

    [Tooltip("Radius (XZ) used when no terrain is assigned.")]
    public float radiusOverride = 200f;

    [Header("Ring")]
    [Tooltip("Multiplier on the terrain half-extent for the inner clear radius.")]
    [Min(0.1f)] public float innerScale = 0.55f;

    [Tooltip("Multiplier on the terrain half-extent for the outer fully-fogged radius.")]
    [Min(0.1f)] public float outerScale = 0.95f;

    [Tooltip("Vertical extent of the ring (world units).")]
    [Min(1f)] public float ringHeight = 200f;

    [Tooltip("Optional vertical fade so the dome doesn't tint sky pixels above this distance from the ring center. 0 = disabled.")]
    [Min(0f)] public float heightFalloff = 0f;

    [Header("Look")]
    public Color fogColor = new Color(0.62f, 0.70f, 0.78f, 1f);
    [Range(0f, 1f)] public float maxAlpha = 1f;
    [Tooltip("Atmospheric density per 100 m. Higher = thicker fog. Try 0.5–3.")]
    [Range(0f, 5f)] public float density = 1.5f;

    [Header("Mesh")]
    [Range(8, 128)] public int segments = 48;

    GameObject _ring;
    Material _mat;
    MeshFilter _mf;

    static readonly int _ID_FogColor = Shader.PropertyToID("_FogColor");
    static readonly int _ID_FogCenter = Shader.PropertyToID("_FogCenter");
    static readonly int _ID_InnerRadius = Shader.PropertyToID("_InnerRadius");
    static readonly int _ID_OuterRadius = Shader.PropertyToID("_OuterRadius");
    static readonly int _ID_Density = Shader.PropertyToID("_Density");
    static readonly int _ID_HeightFalloff = Shader.PropertyToID("_HeightFalloff");
    static readonly int _ID_HeightCenter = Shader.PropertyToID("_HeightCenter");
    static readonly int _ID_MaxAlpha = Shader.PropertyToID("_MaxAlpha");

    void OnEnable() => Rebuild();
    void OnValidate() { if (isActiveAndEnabled) Rebuild(); }

    [ContextMenu("Rebuild Fog Ring")]
    public void Rebuild()
    {
        EnsureRing();
        ApplyParameters();
    }

    void EnsureRing()
    {
        if (_ring == null)
        {
            _ring = new GameObject("~TerrainEdgeFogRing");
            _ring.hideFlags = HideFlags.DontSave;
            _ring.transform.SetParent(transform, false);

            _mf = _ring.AddComponent<MeshFilter>();
            var mr = _ring.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            var shader = Shader.Find("Custom/TerrainEdgeFog");
            if (shader == null)
            {
                Debug.LogError("[TerrainEdgeFogRing] Custom/TerrainEdgeFog shader not found.");
                return;
            }
            _mat = new Material(shader) { hideFlags = HideFlags.DontSave };
            mr.sharedMaterial = _mat;
        }

        if (_mf.sharedMesh == null || _mf.sharedMesh.vertexCount != segments * 2)
            _mf.sharedMesh = BuildCylinder(segments);
    }

    void ApplyParameters()
    {
        if (_ring == null || _mat == null) return;

        Vector3 center;
        float halfExtent;

        if (targetTerrain != null && targetTerrain.terrainData != null)
        {
            var size = targetTerrain.terrainData.size;
            var pos = targetTerrain.transform.position;
            center = new Vector3(pos.x + size.x * 0.5f, pos.y, pos.z + size.z * 0.5f);
            halfExtent = Mathf.Max(size.x, size.z) * 0.5f;
        }
        else
        {
            center = centerOverride;
            halfExtent = radiusOverride;
        }

        float inner = halfExtent * innerScale;
        float outer = halfExtent * outerScale;
        if (outer <= inner) outer = inner + 1f;

        // Place / scale the ring mesh — base radius is 1, so XZ scale = outer.
        _ring.transform.position = new Vector3(center.x, center.y, center.z);
        _ring.transform.rotation = Quaternion.identity;
        _ring.transform.localScale = new Vector3(outer, ringHeight, outer);

        _mat.SetColor(_ID_FogColor, fogColor);
        _mat.SetVector(_ID_FogCenter, center);
        _mat.SetFloat(_ID_InnerRadius, inner);
        _mat.SetFloat(_ID_OuterRadius, outer);
        _mat.SetFloat(_ID_Density, density);
        _mat.SetFloat(_ID_HeightFalloff, heightFalloff);
        _mat.SetFloat(_ID_HeightCenter, center.y + ringHeight * 0.5f);
        _mat.SetFloat(_ID_MaxAlpha, maxAlpha);
    }

    /// <summary>Open cylinder of radius 1 and height 1, centered on Y=0.5 (so it sits on the pivot).</summary>
    static Mesh BuildCylinder(int segments)
    {
        var verts = new Vector3[segments * 2];
        var indices = new int[segments * 6];

        for (int i = 0; i < segments; i++)
        {
            float a = (i / (float)segments) * Mathf.PI * 2f;
            float x = Mathf.Cos(a);
            float z = Mathf.Sin(a);
            verts[i * 2 + 0] = new Vector3(x, 0f, z);
            verts[i * 2 + 1] = new Vector3(x, 1f, z);
        }

        for (int i = 0; i < segments; i++)
        {
            int i0 = i * 2;
            int i1 = i * 2 + 1;
            int i2 = ((i + 1) % segments) * 2;
            int i3 = ((i + 1) % segments) * 2 + 1;

            int o = i * 6;
            // Two triangles per quad. Winding chosen so face normals point outward;
            // the shader uses Cull Front so we render the interior.
            indices[o + 0] = i0; indices[o + 1] = i1; indices[o + 2] = i2;
            indices[o + 3] = i2; indices[o + 4] = i1; indices[o + 5] = i3;
        }

        var m = new Mesh { name = "TerrainEdgeFogCylinder" };
        m.SetVertices(verts);
        m.SetTriangles(indices, 0);
        // A bounds large enough that the renderer never frustum-culls it from inside.
        m.bounds = new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(1e6f, 1e6f, 1e6f));
        return m;
    }

    void OnDisable()
    {
        if (_ring != null)
        {
            if (Application.isPlaying) Destroy(_ring); else DestroyImmediate(_ring);
            _ring = null;
        }
        if (_mat != null)
        {
            if (Application.isPlaying) Destroy(_mat); else DestroyImmediate(_mat);
            _mat = null;
        }
    }
}
