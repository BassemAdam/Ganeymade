using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Self-contained GPU marching cubes renderer.
/// Owns all MC-specific buffers, dispatches the compute kernel,
/// and draws the result procedurally via DrawProceduralIndirect.
///
/// Usage:
///   var mc = new MarchingCubesRenderer(computeShader, lutTextAsset, material);
///   mc.Render(densityTexture3D, gridSize, boundsMin, boundsMax, isoLevel, layer);
///   mc.Release();   // in OnDestroy
/// </summary>
public class MarchingCubesRenderer : IDisposable
{
    // ---- Compute shader + kernel ----
    private readonly ComputeShader _compute;
    private readonly int _kMarch;
    private readonly int _kClear;

    // ---- Lookup table buffers (created once, never resized) ----
    private readonly ComputeBuffer _lutBuffer;
    private readonly ComputeBuffer _offsetsBuffer;
    private readonly ComputeBuffer _lengthsBuffer;

    // ---- Per-frame output buffers ----
    private ComputeBuffer _vertexBuffer;   // AppendStructuredBuffer<Vertex> (32 bytes)
    private ComputeBuffer _drawArgsBuffer; // IndirectArguments: 4 uints

    // ---- Material ----
    private Material _matInstance;
    private readonly Material _sourceMaterial;
    private readonly MaterialPropertyBlock _drawProperties = new MaterialPropertyBlock();

    // ---- Constants ----
    private const int VertexStride = 32; // float4 position + float4 normal
    private const int MaxTrianglesPerVoxel = 5;
    private const int VerticesPerTriangle = 3;
    private const string KW_Procedural = "_MARCHING_CUBES_PROCEDURAL";

    // ---- Property IDs ----
    private static readonly int PID_Vertices       = Shader.PropertyToID("vertices");
    private static readonly int PID_DensityTexture3D = Shader.PropertyToID("DensityTexture3D");
    private static readonly int PID_Lut            = Shader.PropertyToID("lut");
    private static readonly int PID_Offsets         = Shader.PropertyToID("offsets");
    private static readonly int PID_Lengths         = Shader.PropertyToID("lengths");
    private static readonly int PID_GridSize       = Shader.PropertyToID("GridSize");
    private static readonly int PID_VoxelSize      = Shader.PropertyToID("VoxelSize");
    private static readonly int PID_BoundsMinWS    = Shader.PropertyToID("BoundsMinWS");
    private static readonly int PID_IsoLevel       = Shader.PropertyToID("isoLevel");
    private static readonly int PID_VertexBuffer    = Shader.PropertyToID("_MCVertices");
    private static readonly int PID_NormalTexture3D = Shader.PropertyToID("NormalTexture3D");

    // ---- Lookup data (moved out of shader to avoid Vulkan constant buffer limits) ----
    private static readonly int[] s_offsets = {0, 0, 3, 6, 12, 15, 21, 27, 36, 39, 45, 51, 60, 66, 75, 84, 90, 93, 99, 105, 114, 120, 129, 138, 150, 156, 165, 174, 186, 195, 207, 219, 228, 231, 237, 243, 252, 258, 267, 276, 288, 294, 303, 312, 324, 333, 345, 357, 366, 372, 381, 390, 396, 405, 417, 429, 438, 447, 459, 471, 480, 492, 507, 522, 528, 531, 537, 543, 552, 558, 567, 576, 588, 594, 603, 612, 624, 633, 645, 657, 666, 672, 681, 690, 702, 711, 723, 735, 750, 759, 771, 783, 798, 810, 825, 840, 852, 858, 867, 876, 888, 897, 909, 915, 924, 933, 945, 957, 972, 984, 999, 1008, 1014, 1023, 1035, 1047, 1056, 1068, 1083, 1092, 1098, 1110, 1125, 1140, 1152, 1167, 1173, 1185, 1188, 1191, 1197, 1203, 1212, 1218, 1227, 1236, 1248, 1254, 1263, 1272, 1284, 1293, 1305, 1317, 1326, 1332, 1341, 1350, 1362, 1371, 1383, 1395, 1410, 1419, 1425, 1437, 1446, 1458, 1467, 1482, 1488, 1494, 1503, 1512, 1524, 1533, 1545, 1557, 1572, 1581, 1593, 1605, 1620, 1632, 1647, 1662, 1674, 1683, 1695, 1707, 1716, 1728, 1743, 1758, 1770, 1782, 1791, 1806, 1812, 1827, 1839, 1845, 1848, 1854, 1863, 1872, 1884, 1893, 1905, 1917, 1932, 1941, 1953, 1965, 1980, 1986, 1995, 2004, 2010, 2019, 2031, 2043, 2058, 2070, 2085, 2100, 2106, 2118, 2127, 2142, 2154, 2163, 2169, 2181, 2184, 2193, 2205, 2217, 2232, 2244, 2259, 2268, 2280, 2292, 2307, 2322, 2328, 2337, 2349, 2355, 2358, 2364, 2373, 2382, 2388, 2397, 2409, 2415, 2418, 2427, 2433, 2445, 2448, 2454, 2457, 2460};

    private static readonly int[] s_lengths = {0, 3, 3, 6, 3, 6, 6, 9, 3, 6, 6, 9, 6, 9, 9, 6, 3, 6, 6, 9, 6, 9, 9, 12, 6, 9, 9, 12, 9, 12, 12, 9, 3, 6, 6, 9, 6, 9, 9, 12, 6, 9, 9, 12, 9, 12, 12, 9, 6, 9, 9, 6, 9, 12, 12, 9, 9, 12, 12, 9, 12, 15, 15, 6, 3, 6, 6, 9, 6, 9, 9, 12, 6, 9, 9, 12, 9, 12, 12, 9, 6, 9, 9, 12, 9, 12, 12, 15, 9, 12, 12, 15, 12, 15, 15, 12, 6, 9, 9, 12, 9, 12, 6, 9, 9, 12, 12, 15, 12, 15, 9, 6, 9, 12, 12, 9, 12, 15, 9, 6, 12, 15, 15, 12, 15, 6, 12, 3, 3, 6, 6, 9, 6, 9, 9, 12, 6, 9, 9, 12, 9, 12, 12, 9, 6, 9, 9, 12, 9, 12, 12, 15, 9, 6, 12, 9, 12, 9, 15, 6, 6, 9, 9, 12, 9, 12, 12, 15, 9, 12, 12, 15, 12, 15, 15, 12, 9, 12, 12, 9, 12, 15, 15, 12, 12, 9, 15, 6, 15, 12, 6, 3, 6, 9, 9, 12, 9, 12, 12, 15, 9, 12, 12, 15, 6, 9, 9, 6, 9, 12, 12, 15, 12, 15, 15, 6, 12, 9, 15, 12, 9, 6, 12, 3, 9, 12, 12, 15, 12, 15, 9, 12, 12, 15, 15, 6, 9, 12, 6, 3, 6, 9, 9, 6, 9, 12, 6, 3, 9, 6, 12, 3, 6, 3, 3, 0};

    // ---- State ----
    private int _lastVertexCount;
    private bool _disposed;
    private bool _staticKernelBindingsApplied;
    private readonly int _waterLayer;  // cached once — LayerMask.NameToLayer is not free
    private ComputeBuffer _boundClearArgsBuffer;
    private ComputeBuffer _boundMarchArgsBuffer;
    private ComputeBuffer _boundComputeVertexBuffer;
    private ComputeBuffer _boundDrawVertexBuffer;
    private ComputeBuffer _boundThicknessVertexBuffer;
    private RenderTexture _boundDensityTexture;
    private RenderTexture _boundNormalTexture;
    private Material _boundThicknessMaterial;
    private int _boundThicknessPassIndex = -1;

    /// <summary>
    /// Creates a new MarchingCubesRenderer.
    /// </summary>
    /// <param name="compute">MarchingCubesCompute.compute asset</param>
    /// <param name="lutAsset">MarchingCubesLUT.txt asset</param>
    /// <param name="material">Material using Custom/WaterLiquid shader</param>
    public MarchingCubesRenderer(ComputeShader compute, TextAsset lutAsset, Material material)
    {
        if (compute == null) throw new ArgumentNullException(nameof(compute));
        if (lutAsset == null) throw new ArgumentNullException(nameof(lutAsset));
        if (material == null) throw new ArgumentNullException(nameof(material));

        _compute = compute;
        _sourceMaterial = material;
        _kMarch = compute.FindKernel("MarchDensity");
        _kClear = compute.FindKernel("ClearArgs");

        // Parse flat LUT from text file
        int[] lutVals = lutAsset.text.Trim().Split(',').Select(x => int.Parse(x.Trim())).ToArray();

        // Create LUT buffers (read-only StructuredBuffers on the GPU)
        _lutBuffer = new ComputeBuffer(lutVals.Length, sizeof(int));
        _lutBuffer.SetData(lutVals);

        _offsetsBuffer = new ComputeBuffer(s_offsets.Length, sizeof(int));
        _offsetsBuffer.SetData(s_offsets);

        _lengthsBuffer = new ComputeBuffer(s_lengths.Length, sizeof(int));
        _lengthsBuffer.SetData(s_lengths);

        ApplyStaticKernelBindings();

        _waterLayer = LayerMask.NameToLayer("Water");

        // Register to draw into the custom water depth rendering pass
        WaterThicknessFeature.OnDrawWaterProcedural += DrawProceduralThickness;
    }

    /// <summary>
    /// Dispatches marching cubes on the density grid and draws the result.
    /// Call this every frame from LateUpdate.
    /// </summary>
    public void Render(
        RenderTexture densityTexture3D,
        RenderTexture normalTexture3D,
        Vector3Int gridSize,
        Vector3 boundsMin,
        Vector3 boundsMax,
        float isoLevel,
        int layer)
    {
        if (_disposed || densityTexture3D == null || normalTexture3D == null) return;

        Vector3Int dims = new Vector3Int(
            Mathf.Max(2, gridSize.x),
            Mathf.Max(2, gridSize.y),
            Mathf.Max(2, gridSize.z));

        EnsureBuffers(dims);

        Vector3 sizeWS = boundsMax - boundsMin;
        Vector3 voxelSize = new Vector3(
            sizeWS.x / Mathf.Max(1, dims.x - 1),
            sizeWS.y / Mathf.Max(1, dims.y - 1),
            sizeWS.z / Mathf.Max(1, dims.z - 1));

        BindDispatchResources(densityTexture3D, normalTexture3D);

        // Reset draw args
        _compute.Dispatch(_kClear, 1, 1, 1);

        // Bind per-frame data
        _compute.SetInts(PID_GridSize, dims.x, dims.y, dims.z);
        _compute.SetVector(PID_VoxelSize, new Vector4(voxelSize.x, voxelSize.y, voxelSize.z, 0f));
        _compute.SetVector(PID_BoundsMinWS, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0f));
        _compute.SetFloat(PID_IsoLevel, Mathf.Clamp01(isoLevel));

        // Dispatch
        int gx = Mathf.CeilToInt(dims.x / 8f);
        int gy = Mathf.CeilToInt(dims.y / 8f);
        int gz = Mathf.CeilToInt(dims.z / 8f);
        _compute.Dispatch(_kMarch, gx, gy, gz);

        EnsureMaterialInstance();

        Bounds drawBounds = new Bounds((boundsMin + boundsMax) * 0.5f, sizeWS);

        // Force this procedural mesh to render on the specific "Water" layer 
        // to catch the custom Render Passes. If it doesn't exist, fall back to given layer.
        int finalLayer = _waterLayer != -1 ? _waterLayer : layer;

        Graphics.DrawProceduralIndirect(
            _matInstance,
            drawBounds,
            MeshTopology.Triangles,
            _drawArgsBuffer,
            0,
            null,
            _drawProperties,
            ShadowCastingMode.Off,
            true,
            finalLayer);
    }

    private void ApplyStaticKernelBindings()
    {
        _compute.SetBuffer(_kMarch, PID_Lut, _lutBuffer);
        _compute.SetBuffer(_kMarch, PID_Offsets, _offsetsBuffer);
        _compute.SetBuffer(_kMarch, PID_Lengths, _lengthsBuffer);
        _staticKernelBindingsApplied = true;
    }

    private void BindDispatchResources(RenderTexture densityTexture3D, RenderTexture normalTexture3D)
    {
        if (!_staticKernelBindingsApplied)
            ApplyStaticKernelBindings();

        if (_boundClearArgsBuffer != _drawArgsBuffer)
        {
            _compute.SetBuffer(_kClear, "drawArgs", _drawArgsBuffer);
            _boundClearArgsBuffer = _drawArgsBuffer;
        }

        if (_boundMarchArgsBuffer != _drawArgsBuffer)
        {
            _compute.SetBuffer(_kMarch, "drawArgs", _drawArgsBuffer);
            _boundMarchArgsBuffer = _drawArgsBuffer;
        }

        if (_boundComputeVertexBuffer != _vertexBuffer)
        {
            _compute.SetBuffer(_kMarch, PID_Vertices, _vertexBuffer);
            _boundComputeVertexBuffer = _vertexBuffer;
        }

        if (_boundDrawVertexBuffer != _vertexBuffer)
        {
            _drawProperties.SetBuffer(PID_VertexBuffer, _vertexBuffer);
            _boundDrawVertexBuffer = _vertexBuffer;
        }

        if (_boundDensityTexture != densityTexture3D)
        {
            _compute.SetTexture(_kMarch, PID_DensityTexture3D, densityTexture3D);
            _boundDensityTexture = densityTexture3D;
        }

        if (_boundNormalTexture != normalTexture3D)
        {
            _compute.SetTexture(_kMarch, PID_NormalTexture3D, normalTexture3D);
            _boundNormalTexture = normalTexture3D;
        }
    }

    private void EnsureMaterialInstance()
    {
        if (_matInstance != null)
            return;

        _matInstance = new Material(_sourceMaterial);
        _matInstance.EnableKeyword(KW_Procedural);
    }

    private void EnsureBuffers(Vector3Int dims)
    {
        long maxVerts = (long)(dims.x - 1) * (dims.y - 1) * (dims.z - 1)
                        * MaxTrianglesPerVoxel * VerticesPerTriangle;
        int vertCount = (int)Mathf.Min(maxVerts, 6_000_000);

        if (_vertexBuffer == null || _vertexBuffer.count != vertCount)
        {
            _vertexBuffer?.Release();
            _vertexBuffer = new ComputeBuffer(vertCount, VertexStride, ComputeBufferType.Default);
            _lastVertexCount = vertCount;
        }

        if (_drawArgsBuffer == null)
        {
            _drawArgsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            _drawArgsBuffer.SetData(new uint[] { 0, 1, 0, 0 });
        }
    }

    /// <summary>
    /// Releases all GPU resources. Call from OnDestroy.
    /// </summary>
    public void Release()
    {
        if (_disposed) return;
        _disposed = true;

        WaterThicknessFeature.OnDrawWaterProcedural -= DrawProceduralThickness;

        _lutBuffer?.Release();
        _offsetsBuffer?.Release();
        _lengthsBuffer?.Release();
        _vertexBuffer?.Release();
        _drawArgsBuffer?.Release();

        if (_matInstance != null)
            UnityEngine.Object.Destroy(_matInstance);
    }

    private void DrawProceduralThickness(RasterCommandBuffer cmd, Material thicknessMat)
    {
        if (_disposed || _drawArgsBuffer == null || _vertexBuffer == null || thicknessMat == null) return;

        if (_boundThicknessMaterial != thicknessMat)
        {
            thicknessMat.EnableKeyword(KW_Procedural);
            _boundThicknessMaterial = thicknessMat;
            _boundThicknessPassIndex = thicknessMat.FindPass("WaterThicknessGen");
            if (_boundThicknessPassIndex < 0)
                _boundThicknessPassIndex = 0;
            _boundThicknessVertexBuffer = null;
        }

        if (_boundThicknessVertexBuffer != _vertexBuffer)
        {
            thicknessMat.SetBuffer(PID_VertexBuffer, _vertexBuffer);
            _boundThicknessVertexBuffer = _vertexBuffer;
        }
        
        // Also tell the command buffer explicitly
        cmd.EnableShaderKeyword(KW_Procedural);
        cmd.SetGlobalBuffer(PID_VertexBuffer, _vertexBuffer);

        cmd.DrawProceduralIndirect(
            Matrix4x4.identity, 
            thicknessMat, 
            _boundThicknessPassIndex, 
            MeshTopology.Triangles, 
            _drawArgsBuffer, 
            0);
            
        cmd.DisableShaderKeyword(KW_Procedural);
    }

    public void Dispose() => Release();
}
