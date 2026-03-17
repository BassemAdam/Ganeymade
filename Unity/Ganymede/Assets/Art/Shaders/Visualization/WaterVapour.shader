Shader "Custom/WaterVapour"
{
    Properties
    {
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}

        [Header(Noise)]
        _NoiseScale     ("Noise Scale",      Range(0.1, 10.0)) = 1.0
        _NoiseDriftDir  ("Drift Direction",  Vector)           = (0, 1, 0, 0)
        _NoiseDriftSpeed("Drift Speed",       Range(0.0,  5.0)) = 0.3
        _NoiseOctaves   ("Noise Octaves",     Range(1, 8))      = 5

        [Header(Density)]
        _DensityPower   ("Density Power",    Range(0.1, 5.0))  = 1.5
        _Density        ("Density (Physics)",Range(0.0, 1.0))  = 1.0
        _PhysicsBlend   ("Physics Blend",    Range(0.0, 1.0))  = 0.0

        [Header(Raymarching)]
        _MarchSteps     ("March Steps",      Range(4, 64))     = 24
        _MarchDistance  ("March Distance",   Range(0.1, 10.0)) = 1.0
        _AbsorptionCoeff("Absorption",        Range(0.1, 20.0)) = 8.0

        [Header(Volumetric Lighting)]
        _ScatterAnisotropy  ("Scatter Anisotropy (g)", Range(0.0, 0.95)) = 0.5
        _BackscatterStrength("Backscatter Strength",   Range(0.0, 2.0))  = 0.4
        _AmbientColor       ("Ambient / Shadow Color", Color)            = (0.05, 0.08, 0.15, 1)
        _AmbientStrength    ("Ambient Strength",       Range(0.0, 1.0))  = 0.35

        [Header(Voxel Bounds (Object Space))]
        _VoxelBoundsMin("Bounds Min", Vector) = (-0.5, -0.5, -0.5, 0)
        _VoxelBoundsMax("Bounds Max", Vector) = ( 0.5,  0.5,  0.5, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent" 
            "RenderPipeline" = "UniversalPipeline"
        }

        // No depth write — vapor must not occlude objects behind it
        ZWrite Off

        // Standard alpha blending
        Blend SrcAlpha OneMinusSrcAlpha

        // No backface culling — vapor has no real surface orientation
        Cull Off

        Pass
        {
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "../Includes/WaterVapourHelpers.hlsl"

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            struct MeshInput
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1; // world-space position — drives noise & raymarching
                float3 viewDirWS  : TEXCOORD2; // world-space view direction — drives Fresnel & phase function
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                half4   _BaseColor;
                float4  _BaseMap_ST;
                // Noise
                float   _NoiseScale;
                float4  _NoiseDriftDir;
                float   _NoiseDriftSpeed;
                int     _NoiseOctaves;
                // Density
                float   _DensityPower;
                float   _Density;
                float   _PhysicsBlend;
                // Raymarching
                int     _MarchSteps;
                float   _MarchDistance;
                float   _AbsorptionCoeff;
                // Volumetric Lighting
                float   _ScatterAnisotropy;
                float   _BackscatterStrength;
                half4   _AmbientColor;
                float   _AmbientStrength;
                // Voxel bounds in object space (for robust camera-inside marching)
                float4  _VoxelBoundsMin;
                float4  _VoxelBoundsMax;
            CBUFFER_END

            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.uv          = TRANSFORM_TEX(IN.uv, _BaseMap);
                // GetWorldSpaceViewDir returns (cameraPos - positionWS), unnormalized
                // We normalize in the fragment shader where we need it per-pixel
                OUT.viewDirWS   = GetWorldSpaceViewDir(OUT.positionWS);
                return OUT;
            }

            half4 frag(Interpolators IN) : SV_Target
            {
                // ---- STEP 6 VISUALIZATION : Volumetric Lighting ----
                // Real directional light from URP (direction + color).
                // Henyey-Greenstein phase function responds to the angle between
                // the view ray and the light direction:
                //   - Vapor backlit (light behind it) → bright white/gold halo
                //   - Vapor frontlit                  → darker grey
                //   - Shadow side tinted by _AmbientColor (cool blue-grey)
                // Backscatter term adds the halo glow on silhouette edges.

                float3 cameraWS = _WorldSpaceCameraPos.xyz;
                float3 entryWS = 0.0;
                float3 rayDir  = 0.0;
                float  marchDistance = 0.0;
                float3 boundsMinOS = (float3)_VoxelBoundsMin.xyz;
                float3 boundsMaxOS = (float3)_VoxelBoundsMax.xyz;
                float  maxMarchDistance = (float)_MarchDistance;
                if (!ComputeVoxelRaySegmentWS(
                    cameraWS,
                    IN.positionWS,
                    boundsMinOS,
                    boundsMaxOS,
                    maxMarchDistance,
                    entryWS,
                    rayDir,
                    marchDistance
                ))
                {
                    return half4(0, 0, 0, 0);
                }

                // --- Real URP main directional light ---
                Light mainLight   = GetMainLight();
                float3 lightDir   = normalize(mainLight.direction);
                half3  lightColor = mainLight.color;

                int    marchSteps = _MarchSteps;
                int    noiseOctaves = _NoiseOctaves;
                float3 driftDir = normalize((float3)_NoiseDriftDir.xyz);

                float4 volume = RaymarchVapour(
                    entryWS, rayDir,
                    lightDir, lightColor,
                    marchSteps, marchDistance,
                    _ScatterAnisotropy,
                    _AbsorptionCoeff,
                    _Time.y,
                    driftDir, _NoiseDriftSpeed,
                    _NoiseScale, noiseOctaves,
                    _DensityPower,
                    _Density, _PhysicsBlend
                );

                half3 col = volume.rgb * _BaseColor.rgb;

                // --- Ambient / shadow-side fill ---
                // Adds a cool tint to regions that receive little direct scatter.
                // Prevents vapor in shadow from being pure black.
                col += _AmbientColor.rgb * _AmbientStrength * volume.a;

                // --- Backscatter halo ---
                // When light is behind the vapor (cosTheta > 0 from the viewer’s side)
                // the HG phase is very high. We add a post-march glow term so the
                // vapor silhouette brightens toward the light source — the classic
                // steam-in-sunlight look.
                float cosTheta  = dot(-rayDir, lightDir);
                float backPhase = HenyeyGreenstein(cosTheta, _ScatterAnisotropy);
                // Normalize backPhase so it stays in a usable range (HG can spike high)
                float hgNorm    = HenyeyGreenstein(0.0, _ScatterAnisotropy); // reference at 90°
                float backGlow  = saturate(backPhase / (hgNorm * 8.0));       // relative brightness
                col += backGlow * _BackscatterStrength * lightColor * volume.a;

                return half4(col, volume.a);
            }
            ENDHLSL
        }
    }
}
