using System;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class WaterPhaseResources : IDisposable
{
    private ComputeBuffer _particleOutputBuffer;
    private ComputeBuffer _densityGridBuffer;
    private RenderTexture _phaseDensityTexture;
    private RenderTexture _phaseDensityScratchTexture;
    private RenderTexture _surfaceNormalTexture;

    public ComputeBuffer ParticleOutputBuffer => _particleOutputBuffer;
    public ComputeBuffer DensityGridBuffer => _densityGridBuffer;
    public RenderTexture PhaseDensityTexture => _phaseDensityTexture;
    public RenderTexture PhaseDensityScratchTexture => _phaseDensityScratchTexture;
    public RenderTexture SurfaceNormalTexture => _surfaceNormalTexture;
    public Vector3Int VolumeDims { get; private set; }

    public void Ensure(Vector3Int requestedVolumeDims, int particleCount, int particleStride)
    {
        VolumeDims = new Vector3Int(
            Mathf.Max(1, requestedVolumeDims.x),
            Mathf.Max(1, requestedVolumeDims.y),
            Mathf.Max(1, requestedVolumeDims.z));

        int clampedParticleCount = Mathf.Max(1, particleCount);
        int gridCount = VolumeDims.x * VolumeDims.y * VolumeDims.z;

        EnsureParticleBuffer(clampedParticleCount, particleStride);
        EnsureDensityGrid(gridCount);
        _phaseDensityTexture = EnsureTexture(_phaseDensityTexture, VolumeDims, RenderTextureFormat.RGHalf);
        _phaseDensityScratchTexture = EnsureTexture(_phaseDensityScratchTexture, VolumeDims, RenderTextureFormat.RGHalf);
        _surfaceNormalTexture = EnsureTexture(_surfaceNormalTexture, VolumeDims, RenderTextureFormat.ARGBHalf);
    }

    public void Release()
    {
        ReleaseBuffer(ref _particleOutputBuffer);
        ReleaseBuffer(ref _densityGridBuffer);
        ReleaseTexture(ref _phaseDensityTexture);
        ReleaseTexture(ref _phaseDensityScratchTexture);
        ReleaseTexture(ref _surfaceNormalTexture);
    }

    public void Dispose()
    {
        Release();
    }

    private void EnsureParticleBuffer(int particleCount, int particleStride)
    {
        if (_particleOutputBuffer != null && _particleOutputBuffer.count == particleCount && _particleOutputBuffer.stride == particleStride)
            return;

        ReleaseBuffer(ref _particleOutputBuffer);
        _particleOutputBuffer = new ComputeBuffer(particleCount, particleStride, ComputeBufferType.Structured);
    }

    private void EnsureDensityGrid(int gridCount)
    {
        int totalCellCount = gridCount * 2;
        if (_densityGridBuffer != null && _densityGridBuffer.count == totalCellCount)
            return;

        ReleaseBuffer(ref _densityGridBuffer);
        _densityGridBuffer = new ComputeBuffer(totalCellCount, sizeof(uint), ComputeBufferType.Structured);
    }

    private static RenderTexture EnsureTexture(RenderTexture texture, Vector3Int dims, RenderTextureFormat format)
    {
        if (texture != null &&
            texture.width == dims.x &&
            texture.height == dims.y &&
            texture.volumeDepth == dims.z)
        {
            return texture;
        }

        if (texture != null)
        {
            texture.Release();
            UnityEngine.Object.Destroy(texture);
        }

        RenderTexture created = new RenderTexture(dims.x, dims.y, 0, format)
        {
            dimension = TextureDimension.Tex3D,
            volumeDepth = dims.z,
            enableRandomWrite = true,
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false
        };
        created.Create();
        return created;
    }

    private static void ReleaseBuffer(ref ComputeBuffer buffer)
    {
        if (buffer == null)
            return;

        buffer.Release();
        buffer = null;
    }

    private static void ReleaseTexture(ref RenderTexture texture)
    {
        if (texture == null)
            return;

        texture.Release();
        UnityEngine.Object.Destroy(texture);
        texture = null;
    }
}
