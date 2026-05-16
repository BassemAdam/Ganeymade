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
    private ComputeBuffer _velocityGridBuffer;
    private RenderTexture _vapourVelocityTexture;
    private RenderTexture _vapourNoiseTex_A;
    private RenderTexture _vapourNoiseTex_B;
    private int _noisePingPong = 0;

    public ComputeBuffer ParticleOutputBuffer => _particleOutputBuffer;
    public ComputeBuffer DensityGridBuffer => _densityGridBuffer;
    public RenderTexture PhaseDensityTexture => _phaseDensityTexture;
    public RenderTexture PhaseDensityScratchTexture => _phaseDensityScratchTexture;
    public RenderTexture SurfaceNormalTexture => _surfaceNormalTexture;
    public ComputeBuffer VelocityGridBuffer => _velocityGridBuffer;
    public RenderTexture VapourVelocityTexture => _vapourVelocityTexture;
    // Ping-pong noise textures for semi-Lagrangian vapour advection.
    // Src is the last-written texture; Dst is the one to write into this frame.
    public RenderTexture VapourNoiseSrcTex => _noisePingPong == 0 ? _vapourNoiseTex_A : _vapourNoiseTex_B;
    public RenderTexture VapourNoiseDstTex => _noisePingPong == 0 ? _vapourNoiseTex_B : _vapourNoiseTex_A;
    public void SwapVapourNoisePingPong() => _noisePingPong ^= 1;
    public Vector3Int VolumeDims { get; private set; }
    public int Version { get; private set; }

    public bool Ensure(Vector3Int requestedVolumeDims, int particleCount, int particleStride)
    {
        Vector3Int requestedDims = new Vector3Int(
            Mathf.Max(1, requestedVolumeDims.x),
            Mathf.Max(1, requestedVolumeDims.y),
            Mathf.Max(1, requestedVolumeDims.z));

        bool changed = requestedDims != VolumeDims;
        VolumeDims = requestedDims;

        int clampedParticleCount = Mathf.Max(1, particleCount);
        int gridCount = VolumeDims.x * VolumeDims.y * VolumeDims.z;

        changed |= EnsureParticleBuffer(clampedParticleCount, particleStride);
        changed |= EnsureDensityGrid(gridCount);
        changed |= EnsureTexture(ref _phaseDensityTexture, VolumeDims, RenderTextureFormat.RGHalf);
        changed |= EnsureTexture(ref _phaseDensityScratchTexture, VolumeDims, RenderTextureFormat.RGHalf);
        changed |= EnsureTexture(ref _surfaceNormalTexture, VolumeDims, RenderTextureFormat.ARGBHalf);
        changed |= EnsureVelocityGrid(gridCount);
        changed |= EnsureTexture(ref _vapourVelocityTexture, VolumeDims, RenderTextureFormat.ARGBHalf);
        changed |= EnsureTexture(ref _vapourNoiseTex_A, VolumeDims, RenderTextureFormat.RHalf);
        changed |= EnsureTexture(ref _vapourNoiseTex_B, VolumeDims, RenderTextureFormat.RHalf);

        if (changed)
            Version++;

        return changed;
    }

    public void Release()
    {
        ReleaseBuffer(ref _particleOutputBuffer);
        ReleaseBuffer(ref _densityGridBuffer);
        ReleaseTexture(ref _phaseDensityTexture);
        ReleaseTexture(ref _phaseDensityScratchTexture);
        ReleaseTexture(ref _surfaceNormalTexture);
        ReleaseBuffer(ref _velocityGridBuffer);
        ReleaseTexture(ref _vapourVelocityTexture);
        ReleaseTexture(ref _vapourNoiseTex_A);
        ReleaseTexture(ref _vapourNoiseTex_B);
    }

    public void Dispose()
    {
        Release();
    }

    private bool EnsureParticleBuffer(int particleCount, int particleStride)
    {
        if (_particleOutputBuffer != null && _particleOutputBuffer.count == particleCount && _particleOutputBuffer.stride == particleStride)
            return false;

        ReleaseBuffer(ref _particleOutputBuffer);
        _particleOutputBuffer = new ComputeBuffer(particleCount, particleStride, ComputeBufferType.Structured);
        return true;
    }

    private bool EnsureDensityGrid(int gridCount)
    {
        int totalCellCount = gridCount * 2;
        if (_densityGridBuffer != null && _densityGridBuffer.count == totalCellCount)
            return false;

        ReleaseBuffer(ref _densityGridBuffer);
        _densityGridBuffer = new ComputeBuffer(totalCellCount, sizeof(uint), ComputeBufferType.Structured);
        return true;
    }

    private bool EnsureVelocityGrid(int gridCount)
    {
        // 4 slabs per voxel: vx_biased, vy_biased, vz_biased, weight
        int totalCellCount = gridCount * 4;
        if (_velocityGridBuffer != null && _velocityGridBuffer.count == totalCellCount)
            return false;

        ReleaseBuffer(ref _velocityGridBuffer);
        _velocityGridBuffer = new ComputeBuffer(totalCellCount, sizeof(uint), ComputeBufferType.Structured);
        return true;
    }

    private static bool EnsureTexture(ref RenderTexture texture, Vector3Int dims, RenderTextureFormat format)
    {
        if (texture != null &&
            texture.width == dims.x &&
            texture.height == dims.y &&
            texture.volumeDepth == dims.z)
        {
            return false;
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
        texture = created;
        return true;
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
