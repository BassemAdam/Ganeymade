using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

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
/// - Normalize the fixed-point uint grid into a RWTexture3D<float> for hardware
///   trilinear sampling in marching cubes and water shading.
/// - Bind that density grid to Custom/WaterPhase, which samples it instead of noise.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(UseComputePlugin))]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
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

    [Tooltip("Compute shader that runs marching cubes on the density grid (MarchingCubesCompute.compute)")]
    public ComputeShader marchingCubesCompute;

    [Tooltip("Flat LUT text file for marching cubes triangle table (MarchingCubesLUT.txt)")]
    public TextAsset marchingCubesLUT;

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
    public float smoothingRadiusWS = 1f;

    [Tooltip("0 = nearest voxel only, 1 = 27-voxel neighborhood, 2 = 125-voxel neighborhood")]
    [Range(0, 3)]
    public int kernelRadiusVoxels = 2;

    [HideInInspector]
    public Transform visualProxyTransform;

    [Header("Materials")]
    [Tooltip("Material used for Ray Marching")]
    public Material rayMarchingMaterial;
    
    [Tooltip("Material used for Marching Cubes")]
    public Material marchingCubesMaterial;

    [Tooltip("If enabled, uses the Marching Cubes material. Otherwise uses the Ray Marching material.")]
    public bool useMarchingCubes = false;

    [Header("Marching Cubes Settings")]
    [Tooltip("Iso threshold applied to normalized density (0..1-ish). Higher values make the surface shrink.")]
    [Range(0f, 1f)]
    public float marchingCubesIsoLevel = 0.2f;

    private bool _lastUseMarchingCubes;

    private UseComputePlugin computePlugin;
    private Renderer waterRenderer;
    private MeshRenderer rootMeshRenderer;
    private MeshFilter rootMeshFilter;

    // GPU buffers
    private ComputeBuffer particleOutputBuffer;
    private ComputeBuffer densityGrid; // uint accumulation buffer (atomics)
    private RenderTexture densityTexture3D; // normalized float field

    // Marching cubes (delegated to MarchingCubesRenderer)
    private MarchingCubesRenderer _mcRenderer;

    private IntPtr registeredParticleOutputNativePtr = IntPtr.Zero;

    private int kClear;
    private int kSplat;
    private int kNormalize;

    // Per-renderer binding
    private MaterialPropertyBlock mpb;

    // Cached stride (Marshal.SizeOf is relatively expensive; also helps avoid per-frame reflection)
    private int _particleStride;

    // Init guard (Start might not have executed yet if the component is enabled mid-frame)
    private bool _kernelsInitialized;

    private const string DefaultProxyObjectName = "WaterVisualProxy";



    // Cached IDs
    private static readonly int ID_PhysicsDensityGrid = Shader.PropertyToID("_PhysicsDensityGrid");
    private static readonly int ID_PhysicsBoundsMinWS = Shader.PropertyToID("_PhysicsBoundsMinWS");
    private static readonly int ID_PhysicsBoundsMaxWS = Shader.PropertyToID("_PhysicsBoundsMaxWS");
    private static readonly int ID_PhysicsVolumeDims = Shader.PropertyToID("_PhysicsVolumeDims");

    private static readonly int ID_VolumeDims = Shader.PropertyToID("_VolumeDims");
    private static readonly int ID_ParticleCount = Shader.PropertyToID("_ParticleCount");
    private static readonly int ID_BoundsMinWS = Shader.PropertyToID("_BoundsMinWS");
    private static readonly int ID_BoundsMaxWS = Shader.PropertyToID("_BoundsMaxWS");
    private static readonly int ID_ParticleContribution = Shader.PropertyToID("_ParticleContribution");
    private static readonly int ID_FixedPointScale = Shader.PropertyToID("_FixedPointScale");
    private static readonly int ID_SmoothingRadiusWS = Shader.PropertyToID("_SmoothingRadiusWS");
    private static readonly int ID_KernelRadiusVoxels = Shader.PropertyToID("_KernelRadiusVoxels");
    private static readonly int ID_InvDensityScale = Shader.PropertyToID("_InvDensityScale");

    private void Awake()
    {
        computePlugin = GetComponent<UseComputePlugin>();
        rootMeshRenderer = GetComponent<MeshRenderer>();
        rootMeshFilter = GetComponent<MeshFilter>();
        mpb = new MaterialPropertyBlock();
        _particleStride = Marshal.SizeOf<Particle>();

        if (rootMeshFilter.sharedMesh == null)
        {
            GameObject tempCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rootMeshFilter.sharedMesh = tempCube.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tempCube);
        }

        EnsureVisualRenderer();
    }

    private void OnEnable()
    {
        // Non-serialized fields (like MaterialPropertyBlock) can be null in some Editor/play-mode
        // configurations or after enable/disable cycles. Make sure we're safe before LateUpdate.
        if (mpb == null)
            mpb = new MaterialPropertyBlock();
        if (_particleStride <= 0)
            _particleStride = Marshal.SizeOf<Particle>();

        EnsureVisualRenderer();
    }

    private void EnsureVisualRenderer()
    {
        if (rootMeshRenderer == null)
            rootMeshRenderer = GetComponent<MeshRenderer>();
        if (rootMeshFilter == null)
            rootMeshFilter = GetComponent<MeshFilter>();

        if (visualProxyTransform == null)
        {
            Transform existing = transform.Find(DefaultProxyObjectName);
            if (existing != null)
            {
                visualProxyTransform = existing;
            }
            else
            {
                GameObject proxy = new GameObject(DefaultProxyObjectName);
                visualProxyTransform = proxy.transform;
                visualProxyTransform.SetParent(transform, false);
            }
        }

        MeshFilter proxyFilter = visualProxyTransform.GetComponent<MeshFilter>();
        if (proxyFilter == null)
            proxyFilter = visualProxyTransform.gameObject.AddComponent<MeshFilter>();

        MeshRenderer proxyRenderer = visualProxyTransform.GetComponent<MeshRenderer>();
        if (proxyRenderer == null)
            proxyRenderer = visualProxyTransform.gameObject.AddComponent<MeshRenderer>();

        if (rootMeshFilter != null && proxyFilter.sharedMesh == null)
            proxyFilter.sharedMesh = rootMeshFilter.sharedMesh;

        if (rootMeshRenderer != null)
        {
            Material[] rootMats = rootMeshRenderer.sharedMaterials;
            if ((proxyRenderer.sharedMaterials == null || proxyRenderer.sharedMaterials.Length == 0) &&
                rootMats != null && rootMats.Length > 0)
            {
                proxyRenderer.sharedMaterials = rootMats;
            }

            if (proxyRenderer != rootMeshRenderer)
                rootMeshRenderer.enabled = false;
        }

        waterRenderer = proxyRenderer;
    }

    private void Start()
    {
        if (!TryInitializeKernelsAndMaterials())
        {
            enabled = false;
            return;
        }

        EnsureBuffers();
        RegisterOutputBufferWithPlugin();
    }

    private bool TryInitializeKernelsAndMaterials()
    {
        if (particlesToDensityCompute == null)
        {
            Debug.LogError("[PhysicsWaterPhaseBridge] Missing particlesToDensityCompute reference.");
            return false;
        }

        if (rayMarchingMaterial == null || marchingCubesMaterial == null)
        {
            Debug.LogError("[PhysicsWaterPhaseBridge] Please assign both Ray Marching and Marching Cubes materials in the Inspector.");
            return false;
        }

        if (!_kernelsInitialized)
        {
            kClear = particlesToDensityCompute.FindKernel("ClearGrid");
            kSplat = particlesToDensityCompute.FindKernel("SplatParticles");
            kNormalize = particlesToDensityCompute.FindKernel("NormalizeToTexture");
            _kernelsInitialized = true;
        }

        _lastUseMarchingCubes = useMarchingCubes;
        UpdateMaterial();
        return true;
    }

    private void UpdateMaterial()
    {
        EnsureVisualRenderer();
        if (waterRenderer == null)
            return;

        if (!useMarchingCubes)
        {
            // Ray-marching path: use shared material directly so inspector changes apply at runtime.
            waterRenderer.enabled = true;
            waterRenderer.sharedMaterials = new Material[] { rayMarchingMaterial };
        }
        else
        {
            // Marching-cubes path: do NOT render the proxy cube; we render procedurally.
            waterRenderer.enabled = false;
        }
    }


    private void EnsureBuffers()
    {
        int particleCount = Mathf.Max(1, computePlugin != null ? computePlugin.particleCount : 1);

        volumeDims.x = Mathf.Max(1, volumeDims.x);
        volumeDims.y = Mathf.Max(1, volumeDims.y);
        volumeDims.z = Mathf.Max(1, volumeDims.z);

        int gridCount = volumeDims.x * volumeDims.y * volumeDims.z;

        int particleStride = _particleStride > 0 ? _particleStride : Marshal.SizeOf<Particle>();

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

        if (densityTexture3D == null ||
            densityTexture3D.width != volumeDims.x ||
            densityTexture3D.height != volumeDims.y ||
            densityTexture3D.volumeDepth != volumeDims.z)
        {
            if (densityTexture3D != null)
            {
                densityTexture3D.Release();
                Destroy(densityTexture3D);
            }

            densityTexture3D = new RenderTexture(volumeDims.x, volumeDims.y, 0, RenderTextureFormat.RFloat)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = volumeDims.z,
                enableRandomWrite = true,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            densityTexture3D.Create();
        }

        // Bind buffers once (Unity will rebind internally as needed).
        particlesToDensityCompute.SetBuffer(kClear, "_DensityGrid", densityGrid);
        particlesToDensityCompute.SetBuffer(kClear, "_ParticleBuffer", particleOutputBuffer); // bound for safety
        particlesToDensityCompute.SetBuffer(kSplat, "_DensityGrid", densityGrid);
        particlesToDensityCompute.SetBuffer(kSplat, "_ParticleBuffer", particleOutputBuffer);
        particlesToDensityCompute.SetBuffer(kNormalize, "_DensityGrid", densityGrid);
        particlesToDensityCompute.SetTexture(kNormalize, "_DensityTexture3D", densityTexture3D);

        // If we recreated the particle output buffer at runtime, re-register with the native plugin.
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan &&
            particleOutputBuffer != null &&
            registeredParticleOutputNativePtr == IntPtr.Zero)
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

    private Vector3 ComputeVoxelSizeWS(Vector3 boundsMin, Vector3 boundsMax)
    {
        return new Vector3(
            (boundsMax.x - boundsMin.x) / Mathf.Max(1, volumeDims.x),
            (boundsMax.y - boundsMin.y) / Mathf.Max(1, volumeDims.y),
            (boundsMax.z - boundsMin.z) / Mathf.Max(1, volumeDims.z));
    }

    private float ComputeEffectiveSmoothingRadius(float requestedRadius, Vector3 voxelSizeWS)
    {
        float minRadius = Mathf.Max(voxelSizeWS.x, Mathf.Max(voxelSizeWS.y, voxelSizeWS.z)) * 2.0f;
        return Mathf.Max(requestedRadius, minRadius);
    }

    private void ConfigureSplatKernelParams(Vector3 boundsMin, Vector3 boundsMax)
    {
        float requestedRadius = smoothingRadiusWS >= 0f ? smoothingRadiusWS : computePlugin.smoothingRadius;

        // Ensure smoothing radius covers at least ~2 voxels of world space,
        // otherwise each particle collapses into isolated spikes.
        Vector3 voxelSizeWS = ComputeVoxelSizeWS(boundsMin, boundsMax);
        float effectiveRadius = ComputeEffectiveSmoothingRadius(requestedRadius, voxelSizeWS);
        particlesToDensityCompute.SetFloat(ID_SmoothingRadiusWS, Mathf.Max(0f, effectiveRadius));

        // Use kernelRadiusVoxels directly — no auto-override.
        particlesToDensityCompute.SetInt(ID_KernelRadiusVoxels, Mathf.Clamp(kernelRadiusVoxels, 0, 3));
    }

    private void LateUpdate()
    {
        if (!isActiveAndEnabled)
            return;

        // Unity lifecycle safety: these are non-serialized and can become null in some editor/play-mode setups.
        if (computePlugin == null)
            computePlugin = GetComponent<UseComputePlugin>();
        if (waterRenderer == null)
            EnsureVisualRenderer();
        if (mpb == null)
            mpb = new MaterialPropertyBlock();
        if (!_kernelsInitialized)
        {
            // If Start hasn't executed yet (e.g., enabled mid-frame), make sure kernels/material are ready.
            if (!TryInitializeKernelsAndMaterials())
                return;
        }

        if (computePlugin == null || particlesToDensityCompute == null || waterRenderer == null)
            return;

        if (useMarchingCubes != _lastUseMarchingCubes)
        {
            _lastUseMarchingCubes = useMarchingCubes;
            UpdateMaterial();
        }

        // Handle live edits of particleCount / volumeDims.
        EnsureBuffers();

        computePlugin.GetBoundsWS(out Vector3 boundsMin, out Vector3 boundsMax);

        // Compute params
        particlesToDensityCompute.SetInts(ID_VolumeDims, volumeDims.x, volumeDims.y, volumeDims.z);
        particlesToDensityCompute.SetInt(ID_ParticleCount, computePlugin.particleCount);
        particlesToDensityCompute.SetVector(ID_BoundsMinWS, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0f));
        particlesToDensityCompute.SetVector(ID_BoundsMaxWS, new Vector4(boundsMax.x, boundsMax.y, boundsMax.z, 0f));
        particlesToDensityCompute.SetFloat(ID_ParticleContribution, particleContribution);
        particlesToDensityCompute.SetFloat(ID_FixedPointScale, fixedPointScale);
        float invScale = 1.0f / Mathf.Max(1e-5f, Mathf.Max(1e-5f, particleContribution) * Mathf.Max(1e-5f, maxParticlesPerVoxel));
        particlesToDensityCompute.SetFloat(ID_InvDensityScale, invScale);
        ConfigureSplatKernelParams(boundsMin, boundsMax);

        // Dispatch clear
        int gx = Mathf.CeilToInt(volumeDims.x / 8.0f);
        int gy = Mathf.CeilToInt(volumeDims.y / 8.0f);
        int gz = Mathf.CeilToInt(volumeDims.z / 8.0f);
        particlesToDensityCompute.Dispatch(kClear, gx, gy, gz);

        // Dispatch splat
        int groups = Mathf.CeilToInt(computePlugin.particleCount / 256.0f);
        particlesToDensityCompute.Dispatch(kSplat, Mathf.Max(1, groups), 1, 1);

        // Convert atomic uint grid -> float 3D texture (hardware trilinear-capable).
        particlesToDensityCompute.Dispatch(kNormalize, gx, gy, gz);

        if (!useMarchingCubes)
        {
            // ============================
            // RAY MARCHING (proxy cube)
            // ============================
            if (waterRenderer != null)
            {
                Vector3 center = (boundsMin + boundsMax) * 0.5f;
                Vector3 size = (boundsMax - boundsMin);
                Transform visualTransform = waterRenderer.transform;
                bool movingSimulationRoot = computePlugin != null &&
                                            computePlugin.boundsAreLocalToTransform &&
                                            visualTransform == computePlugin.transform;

                // Guard against transform feedback loop:
                // GetBoundsWS uses simulation transform.position when bounds are local,
                // so writing that same transform's position from those bounds causes runaway drift.
                if (!movingSimulationRoot)
                    visualTransform.position = center;

                visualTransform.localScale = size;

                mpb.Clear();
                mpb.SetTexture(ID_PhysicsDensityGrid, densityTexture3D);
                mpb.SetVector(ID_PhysicsBoundsMinWS, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0f));
                mpb.SetVector(ID_PhysicsBoundsMaxWS, new Vector4(boundsMax.x, boundsMax.y, boundsMax.z, 0f));
                mpb.SetVector(ID_PhysicsVolumeDims, new Vector4(volumeDims.x, volumeDims.y, volumeDims.z, invScale));
                waterRenderer.SetPropertyBlock(mpb);
            }
        }
        else
        {
            // ============================
            // MARCHING CUBES (procedural)
            // ============================
            if (_mcRenderer == null)
            {
                if (marchingCubesCompute == null || marchingCubesLUT == null || marchingCubesMaterial == null)
                {
                    Debug.LogError("[PhysicsWaterPhaseBridge] Marching cubes requires: marchingCubesCompute, marchingCubesLUT, and marchingCubesMaterial.");
                    return;
                }
                _mcRenderer = new MarchingCubesRenderer(marchingCubesCompute, marchingCubesLUT, marchingCubesMaterial);
            }

            _mcRenderer.Render(
                densityTexture3D,
                volumeDims,
                boundsMin,
                boundsMax,
                Mathf.Clamp01(marchingCubesIsoLevel),
                gameObject.layer);
        }
    }

    private void OnDestroy()
    {
        try
        {
            SetUnityParticleOutputBuffer(IntPtr.Zero);
            registeredParticleOutputNativePtr = IntPtr.Zero;
        }
        catch (Exception)
        {
            // Ignore shutdown order issues
        }

        particleOutputBuffer?.Release();
        densityGrid?.Release();
        if (densityTexture3D != null)
        {
            densityTexture3D.Release();
            Destroy(densityTexture3D);
            densityTexture3D = null;
        }
        _mcRenderer?.Release();


    }
}
