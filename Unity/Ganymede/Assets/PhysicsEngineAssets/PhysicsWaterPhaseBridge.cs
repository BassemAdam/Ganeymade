using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Bridges the native Vulkan SPH simulation (RenderingPlugin) into the volumetric
/// WaterPhase raymarch shader by building a GPU density grid each frame.
///
/// Key goals:
/// - Avoid GPU→CPU→GPU readback (no ParticleRenderer instancing path).
/// - Keep physics particle data on GPU by having the native plugin copy its output
///   into a Unity ComputeBuffer (GPU→GPU copy).
/// - Voxelize/splat particles into a 3D density grid (RWStructuredBuffer<uint>)
///   using a Unity compute shader.
/// - Bind that density grid to Custom/WaterPhase, which samples it instead of noise.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(UseComputePlugin))]
public class PhysicsWaterPhaseBridge : MonoBehaviour
{
#if (UNITY_IOS || UNITY_TVOS || UNITY_SWITCH) && !UNITY_EDITOR
    private const string PluginName = "__Internal";
#else
    private const string PluginName = "RenderingPlugin";
#endif

    [DllImport(PluginName)]
    private static extern void SetUnityParticleOutputBuffer(IntPtr nativeBuffer);

    [Header("References")]
    [Tooltip("Compute shader that clears + splats particles into a density grid (ParticlesToDensityGrid.compute)")]
    public ComputeShader particlesToDensityCompute;

    [Tooltip("Renderer using the Custom/WaterPhase material (the volumetric water object)")]
    public Renderer waterRenderer;

    [Header("Density Grid")]
    public Vector3Int volumeDims = new Vector3Int(64, 64, 64);

    [Tooltip("Per-particle contribution before kernel weighting (unitless)")]
    [Min(0f)]
    public float particleContribution = 1.0f;

    [Tooltip("Fixed-point scale used for atomic uint accumulation")]
    [Min(1f)]
    public float fixedPointScale = 1024.0f;

    [Tooltip("Expected 'full' voxel occupancy in particles. Used to map the uint grid into a 0..1 density.")]
    [Min(0.001f)]
    public float maxParticlesPerVoxel = 12.0f;

    [Header("Splat Kernel")]
    [Tooltip("If < 0, uses UseComputePlugin.smoothingRadius")]
    public float smoothingRadiusWS = -1f;

    [Tooltip("0 = nearest voxel only, 1 = 27-voxel neighborhood, 2 = 125-voxel neighborhood")]
    [Range(0, 3)]
    public int kernelRadiusVoxels = 1;

    [Header("Runtime Controls")]
    [Tooltip("Forces the native plugin into perfTestMode (skips staging readback to CPU)")]
    public bool forcePerfTestMode = true;

    [Tooltip("If enabled, automatically disables ParticleRenderer on the same GameObject")]
    public bool disableParticleRenderer = true;

    [Tooltip("If enabled, fits the waterRenderer transform to the simulation bounds")]
    public bool autoFitWaterVolumeTransform = false;

    [Tooltip("If enabled, overrides WaterPhase _PhysicsBlend to 1 (fully physics-driven density)")]
    public bool forcePhysicsBlend = true;

    private UseComputePlugin computePlugin;

    // GPU buffers
    private ComputeBuffer particleOutputBuffer;
    private ComputeBuffer densityGrid;

    private IntPtr registeredParticleOutputNativePtr = IntPtr.Zero;

    // Compute kernels
    private int kClear;
    private int kSplat;

    // Per-renderer binding
    private MaterialPropertyBlock mpb;

    // Cached IDs
    private static readonly int ID_PhysicsDensityGrid = Shader.PropertyToID("_PhysicsDensityGrid");
    private static readonly int ID_PhysicsBoundsMinWS = Shader.PropertyToID("_PhysicsBoundsMinWS");
    private static readonly int ID_PhysicsBoundsMaxWS = Shader.PropertyToID("_PhysicsBoundsMaxWS");
    private static readonly int ID_PhysicsVolumeDims = Shader.PropertyToID("_PhysicsVolumeDims");
    private static readonly int ID_PhysicsBlend = Shader.PropertyToID("_PhysicsBlend");

    private const string KW_PhysicsDensityGrid = "_PHYSICS_DENSITY_GRID";

    private static readonly int ID_VolumeDims = Shader.PropertyToID("_VolumeDims");
    private static readonly int ID_ParticleCount = Shader.PropertyToID("_ParticleCount");
    private static readonly int ID_BoundsMinWS = Shader.PropertyToID("_BoundsMinWS");
    private static readonly int ID_BoundsMaxWS = Shader.PropertyToID("_BoundsMaxWS");
    private static readonly int ID_ParticleContribution = Shader.PropertyToID("_ParticleContribution");
    private static readonly int ID_FixedPointScale = Shader.PropertyToID("_FixedPointScale");
    private static readonly int ID_SmoothingRadiusWS = Shader.PropertyToID("_SmoothingRadiusWS");
    private static readonly int ID_KernelRadiusVoxels = Shader.PropertyToID("_KernelRadiusVoxels");

    private void Awake()
    {
        computePlugin = GetComponent<UseComputePlugin>();
        mpb = new MaterialPropertyBlock();

        if (disableParticleRenderer)
        {
            var pr = GetComponent<ParticleRenderer>();
            if (pr != null)
                pr.enabled = false;
        }
    }

    private void Start()
    {
        if (particlesToDensityCompute == null)
        {
            Debug.LogError("[PhysicsWaterPhaseBridge] Missing particlesToDensityCompute reference.");
            enabled = false;
            return;
        }

        if (waterRenderer == null)
        {
            Debug.LogError("[PhysicsWaterPhaseBridge] Missing waterRenderer reference.");
            enabled = false;
            return;
        }

        kClear = particlesToDensityCompute.FindKernel("ClearGrid");
        kSplat = particlesToDensityCompute.FindKernel("SplatParticles");

        // Enable the physics density grid shader variant on this renderer only.
        // Using Renderer.material intentionally instantiates a per-renderer material.
        var instancedMat = waterRenderer.material;
        instancedMat.EnableKeyword(KW_PhysicsDensityGrid);

        EnsureBuffers();
        RegisterOutputBufferWithPlugin();
    }

    private void EnsureBuffers()
    {
        int particleCount = Mathf.Max(1, computePlugin != null ? computePlugin.particleCount : 1);

        volumeDims.x = Mathf.Max(1, volumeDims.x);
        volumeDims.y = Mathf.Max(1, volumeDims.y);
        volumeDims.z = Mathf.Max(1, volumeDims.z);

        int gridCount = volumeDims.x * volumeDims.y * volumeDims.z;

        int particleStride = Marshal.SizeOf<Particle>();

        if (particleOutputBuffer == null || particleOutputBuffer.count != particleCount || particleOutputBuffer.stride != particleStride)
        {
            particleOutputBuffer?.Release();
            particleOutputBuffer = new ComputeBuffer(particleCount, particleStride, ComputeBufferType.Structured);
            // Force re-register with the native plugin (buffer pointer changed).
            registeredParticleOutputNativePtr = IntPtr.Zero;
        }

        if (densityGrid == null || densityGrid.count != gridCount)
        {
            densityGrid?.Release();
            densityGrid = new ComputeBuffer(gridCount, sizeof(uint), ComputeBufferType.Structured);
        }

        // Bind buffers once (Unity will rebind internally as needed).
        particlesToDensityCompute.SetBuffer(kClear, "_DensityGrid", densityGrid);
        particlesToDensityCompute.SetBuffer(kSplat, "_DensityGrid", densityGrid);
        particlesToDensityCompute.SetBuffer(kSplat, "_ParticleBuffer", particleOutputBuffer);

        // If we recreated the particle output buffer at runtime, re-register with the native plugin.
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan && particleOutputBuffer != null)
        {
            IntPtr nativePtr = particleOutputBuffer.GetNativeBufferPtr();
            if (nativePtr != IntPtr.Zero && nativePtr != registeredParticleOutputNativePtr)
            {
                SetUnityParticleOutputBuffer(nativePtr);
                registeredParticleOutputNativePtr = nativePtr;
            }
        }
    }

    private void RegisterOutputBufferWithPlugin()
    {
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
        {
            Debug.LogWarning("[PhysicsWaterPhaseBridge] Graphics API is not Vulkan. Native plugin output copy will not run.");
            return;
        }

        if (particleOutputBuffer == null)
            return;

        // Let the native plugin copy its latest output into our Unity-owned buffer each dispatch.
        registeredParticleOutputNativePtr = particleOutputBuffer.GetNativeBufferPtr();
        SetUnityParticleOutputBuffer(registeredParticleOutputNativePtr);
    }

    private void LateUpdate()
    {
        if (computePlugin == null || !isActiveAndEnabled)
            return;

        if (forcePerfTestMode)
            computePlugin.perfTestMode = true;

        // Handle live edits of particleCount / volumeDims.
        EnsureBuffers();

        computePlugin.GetBoundsWS(out Vector3 boundsMin, out Vector3 boundsMax);

        if (autoFitWaterVolumeTransform && waterRenderer != null)
        {
            Vector3 center = (boundsMin + boundsMax) * 0.5f;
            Vector3 size = (boundsMax - boundsMin);
            waterRenderer.transform.position = center;
            waterRenderer.transform.localScale = size;
        }

        // Compute params
        particlesToDensityCompute.SetInts(ID_VolumeDims, volumeDims.x, volumeDims.y, volumeDims.z);
        particlesToDensityCompute.SetInt(ID_ParticleCount, computePlugin.particleCount);
        particlesToDensityCompute.SetVector(ID_BoundsMinWS, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0f));
        particlesToDensityCompute.SetVector(ID_BoundsMaxWS, new Vector4(boundsMax.x, boundsMax.y, boundsMax.z, 0f));
        particlesToDensityCompute.SetFloat(ID_ParticleContribution, particleContribution);
        particlesToDensityCompute.SetFloat(ID_FixedPointScale, fixedPointScale);

        float h = smoothingRadiusWS >= 0f ? smoothingRadiusWS : computePlugin.smoothingRadius;
        particlesToDensityCompute.SetFloat(ID_SmoothingRadiusWS, Mathf.Max(0f, h));
        particlesToDensityCompute.SetInt(ID_KernelRadiusVoxels, kernelRadiusVoxels);

        // Dispatch clear
        int gx = Mathf.CeilToInt(volumeDims.x / 8.0f);
        int gy = Mathf.CeilToInt(volumeDims.y / 8.0f);
        int gz = Mathf.CeilToInt(volumeDims.z / 8.0f);
        particlesToDensityCompute.Dispatch(kClear, gx, gy, gz);

        // Dispatch splat
        int groups = Mathf.CeilToInt(computePlugin.particleCount / 256.0f);
        particlesToDensityCompute.Dispatch(kSplat, Mathf.Max(1, groups), 1, 1);

        // Bind to WaterPhase shader.
        float invScale = 1.0f / Mathf.Max(
            1e-5f,
            fixedPointScale * Mathf.Max(1e-5f, particleContribution) * Mathf.Max(1e-5f, maxParticlesPerVoxel)
        );

        mpb.Clear();
        mpb.SetBuffer(ID_PhysicsDensityGrid, densityGrid);
        mpb.SetVector(ID_PhysicsBoundsMinWS, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0f));
        mpb.SetVector(ID_PhysicsBoundsMaxWS, new Vector4(boundsMax.x, boundsMax.y, boundsMax.z, 0f));
        mpb.SetVector(ID_PhysicsVolumeDims, new Vector4(volumeDims.x, volumeDims.y, volumeDims.z, invScale));
        if (forcePhysicsBlend)
            mpb.SetFloat(ID_PhysicsBlend, 1.0f);

        waterRenderer.SetPropertyBlock(mpb);
    }

    private void OnDestroy()
    {
        try
        {
            // Prevent the plugin from holding onto a destroyed Unity buffer pointer.
            SetUnityParticleOutputBuffer(IntPtr.Zero);
            registeredParticleOutputNativePtr = IntPtr.Zero;
        }
        catch (Exception)
        {
            // Ignore shutdown order issues (domain reload, plugin not loaded, etc.)
        }

        particleOutputBuffer?.Release();
        densityGrid?.Release();
    }
}
