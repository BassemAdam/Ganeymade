using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;


//Builds normal-line geometry on filled surface voxels.
//attach to the same Camera that has VoxelTracerCamera.
//Rendering is handled by <see cref="VoxelNormalGizmosFeature"/>.

[RequireComponent(typeof(Camera))]
[DefaultExecutionOrder(100)] // run after VoxelTracerCamera
public class VoxelNormalGizmos : MonoBehaviour
{
    [Header("References")]
    public VoxelTracerSystem voxelSystem;

    [Header("Display")]
    [Tooltip("Length of normal lines in world units")]
    public float normalLength = 0.5f;

    [Tooltip("Color of normal lines")]
    public Color normalColor = Color.green;

    [Tooltip("Only show normals for surface voxels (those with at least one empty neighbor)")]
    public bool surfaceOnly = true;

    [Tooltip("Max voxels to draw normals for (performance limit)")]
    [Range(100, 50000)]
    public int maxLines = 10000;

    [Tooltip("How often to refresh the voxel data (seconds). 0 = every frame.")]
    [Min(0)] public float refreshInterval = 0.2f;

    [Header("Layer Filter")]
    [Tooltip("Only show normals for voxels overlapping objects on these layers. " +
             "Set to 'Everything' to show all surface normals.")]
    public LayerMask normalLayers = ~0;

    Material _lineMat;
    Mesh _lineMesh;
    int _lineCount;
    float _lastRefresh = -999f;

    // Cached readback data
    float[] _fillData;
    int _cachedNx, _cachedNy, _cachedNz;

    // Temp buffers for building mesh
    Vector3[] _lineStarts;
    Vector3[] _lineEnds;

    // Public API for VoxelNormalGizmosFeature
    public bool HasLines => enabled && _lineMesh != null && _lineCount > 0 && _lineMat != null;


    // Draw the normal lines into the given CommandBuffer.
    // Called by VoxelNormalGizmosFeature inside a render graph raster pass.
    public void DrawLines(RasterCommandBuffer cmd, Matrix4x4 view, Matrix4x4 proj)
    {
        if (!HasLines) return;
        cmd.SetViewProjectionMatrices(view, proj);
        _lineMat.SetColor("_Color", normalColor);
        cmd.DrawMesh(_lineMesh, Matrix4x4.identity, _lineMat, 0, 0);
    }

    // Lifecycle

    void OnEnable()
    {
        var shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null) return;
        _lineMat = new Material(shader);
        _lineMat.hideFlags = HideFlags.HideAndDontSave;
        _lineMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        _lineMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        _lineMat.SetInt("_Cull", (int)CullMode.Off);
        _lineMat.SetInt("_ZWrite", 0);
        _lineMat.SetInt("_ZTest", (int)CompareFunction.Always);
    }

    void OnDisable()
    {
        if (_lineMat != null) { DestroyImmediate(_lineMat); _lineMat = null; }
        if (_lineMesh != null) { DestroyImmediate(_lineMesh); _lineMesh = null; }
        _lineStarts = null;
        _lineEnds = null;
        _fillData = null;
        _lineCount = 0;
    }

    void Update()
    {
        if (voxelSystem == null || !voxelSystem.IsReady) return;
        if (Time.time - _lastRefresh < refreshInterval) return;

        _lastRefresh = Time.time;
        RebuildLines();
    }

    void RebuildLines()
    {
        int nx = voxelSystem.Nx;
        int ny = voxelSystem.Ny;
        int nz = voxelSystem.Nz;
        int total = nx * ny * nz;

        //GPU readback of fill texture into CPU array
        var fillRT = voxelSystem.FillTexture;
        if (fillRT == null) return;

        //Create a temporary Texture3D readback via compute buffer copy
        //Since direct Texture3D readback is complex, use AsyncGPUReadback
        //For simplicity, use a RenderTexture.active trick per-slice
        if (_fillData == null || _fillData.Length != total ||
            _cachedNx != nx || _cachedNy != ny || _cachedNz != nz)
        {
            _fillData = new float[total];
            _cachedNx = nx;
            _cachedNy = ny;
            _cachedNz = nz;
        }

        //Read each Z slice
        var tempRT = RenderTexture.GetTemporary(nx, ny, 0, RenderTextureFormat.RFloat);
        var tempTex = new Texture2D(nx, ny, TextureFormat.RFloat, false);

        for (int z = 0; z < nz; z++)
        {
            Graphics.CopyTexture(fillRT, z, 0, tempRT, 0, 0);
            var prev = RenderTexture.active;
            RenderTexture.active = tempRT;
            tempTex.ReadPixels(new Rect(0, 0, nx, ny), 0, 0, false);
            tempTex.Apply(false);
            RenderTexture.active = prev;

            var raw = tempTex.GetRawTextureData<float>();
            for (int i = 0; i < nx * ny; i++)
                _fillData[z * (nx * ny) + i] = raw[i];
        }

        RenderTexture.ReleaseTemporary(tempRT);
        Destroy(tempTex);

        //Collect AABB bounds of renderers on the target layers for filtering.
        //When normalLayers == Everything (~0), skip the per-voxel check entirely.
        bool filterByLayer = normalLayers.value != ~0;
        List<Bounds> layerBounds = null;
        if (filterByLayer)
        {
            layerBounds = new List<Bounds>();
            foreach (var mr in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                if ((normalLayers.value & (1 << mr.gameObject.layer)) != 0)
                    layerBounds.Add(mr.bounds);
            }
            foreach (var smr in FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
            {
                if ((normalLayers.value & (1 << smr.gameObject.layer)) != 0)
                    layerBounds.Add(smr.bounds);
            }
        }

        //Build normal lines from surface voxels
        float unit = voxelSystem.ActiveVoxelSize;
        float halfUnit = unit * 0.5f;
        Vector3 start = voxelSystem.ActiveGridMin;

        if (_lineStarts == null || _lineStarts.Length != maxLines)
        {
            _lineStarts = new Vector3[maxLines];
            _lineEnds = new Vector3[maxLines];
        }

        _lineCount = 0;

        for (int z = 0; z < nz && _lineCount < maxLines; z++)
            for (int y = 0; y < ny && _lineCount < maxLines; y++)
                for (int x = 0; x < nx && _lineCount < maxLines; x++)
                {
                    int idx = z * (nx * ny) + y * nx + x;
                    if (_fillData[idx] < 0.5f) continue;

                    if (surfaceOnly)
                    {
                        //Check if it has at least one empty neighbor (6-connected)
                        bool isSurface = false;
                        if (x == 0 || GetFill(x - 1, y, z, nx, ny, nz) < 0.5f) isSurface = true;
                        else if (x == nx - 1 || GetFill(x + 1, y, z, nx, ny, nz) < 0.5f) isSurface = true;
                        else if (y == 0 || GetFill(x, y - 1, z, nx, ny, nz) < 0.5f) isSurface = true;
                        else if (y == ny - 1 || GetFill(x, y + 1, z, nx, ny, nz) < 0.5f) isSurface = true;
                        else if (z == 0 || GetFill(x, y, z - 1, nx, ny, nz) < 0.5f) isSurface = true;
                        else if (z == nz - 1 || GetFill(x, y, z + 1, nx, ny, nz) < 0.5f) isSurface = true;

                        if (!isSurface) continue;
                    }

                    //Compute gradient normal from fill field (central differences)
                    float gx = GetFill(Mathf.Min(x + 1, nx - 1), y, z, nx, ny, nz)
                              - GetFill(Mathf.Max(x - 1, 0), y, z, nx, ny, nz);
                    float gy = GetFill(x, Mathf.Min(y + 1, ny - 1), z, nx, ny, nz)
                              - GetFill(x, Mathf.Max(y - 1, 0), z, nx, ny, nz);
                    float gz = GetFill(x, y, Mathf.Min(z + 1, nz - 1), nx, ny, nz)
                              - GetFill(x, y, Mathf.Max(z - 1, 0), nx, ny, nz);

                    Vector3 grad = new Vector3(gx, gy, gz);
                    float len2 = grad.sqrMagnitude;
                    if (len2 < 1e-8f) continue;

                    //Outward normal = -gradient (gradient points from filled to empty)
                    Vector3 normal = -grad / Mathf.Sqrt(len2);

                    Vector3 center = new Vector3(
                        start.x + unit * x + halfUnit,
                        start.y + unit * y + halfUnit,
                        start.z + unit * z + halfUnit
                    );

                    //Skip voxels not overlapping any renderer on the target layers
                    if (filterByLayer && !IsInsideAnyBounds(center, layerBounds))
                        continue;

                    _lineStarts[_lineCount] = center;
                    _lineEnds[_lineCount] = center + normal * normalLength;
                    _lineCount++;
                }

        //Build mesh from line data
        BuildLineMesh();
    }

    void BuildLineMesh()
    {
        if (_lineCount == 0)
        {
            if (_lineMesh != null) _lineMesh.Clear();
            return;
        }

        if (_lineMesh == null)
        {
            _lineMesh = new Mesh();
            _lineMesh.hideFlags = HideFlags.HideAndDontSave;
        }

        int vertCount = _lineCount * 2;
        var verts = new Vector3[vertCount];
        var colors = new Color[vertCount];
        var indices = new int[vertCount];

        for (int i = 0; i < _lineCount; i++)
        {
            verts[i * 2] = _lineStarts[i];
            verts[i * 2 + 1] = _lineEnds[i];
            colors[i * 2] = normalColor;
            colors[i * 2 + 1] = normalColor;
            indices[i * 2] = i * 2;
            indices[i * 2 + 1] = i * 2 + 1;
        }

        _lineMesh.Clear();
        _lineMesh.vertices = verts;
        _lineMesh.colors = colors;
        _lineMesh.SetIndices(indices, MeshTopology.Lines, 0);
    }

    static bool IsInsideAnyBounds(Vector3 point, List<Bounds> boundsList)
    {
        for (int i = 0; i < boundsList.Count; i++)
        {
            if (boundsList[i].Contains(point))
                return true;
        }
        return false;
    }

    float GetFill(int x, int y, int z, int nx, int ny, int nz)
    {
        if (x < 0 || x >= nx || y < 0 || y >= ny || z < 0 || z >= nz) return 0;
        return _fillData[z * (nx * ny) + y * nx + x];
    }
}
