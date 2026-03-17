Shader "Custom/WaterVapour"
{
    Properties
    {
        // ---- APPEARANCE ---------------------------------------------------------
        // These are the first things an artist touches: colour palette and glow.
        [Header(Appearance)]
        [MainColor]
        _BaseColor          ("Base Color",              Color)            = (1.00, 1.00, 1.00, 1)
        _WarmColor          ("Warm Tint (hot steam)",   Color)            = (1.00, 0.92, 0.80, 1)
        _CoolColor          ("Cool Tint (cold mist)",   Color)            = (0.80, 0.90, 1.00, 1)
        _TemperatureBlend   ("Temperature  0=mist  1=steam", Range(0.0, 1.0))  = 0.6
        _ShadowColor        ("Shadow Side Tint",        Color)            = (0.04, 0.07, 0.18, 1)
        _EmissionColor      ("Emission Color",          Color)            = (1.00, 0.95, 0.85, 1)
        _EmissionStrength   ("Emission Strength",       Range(0.0,  3.0)) = 0.5

        // ---- NOISE --------------------------------------------------------------
        // Controls the shape and animation of the density field.
        [Header(Noise)]
        _NoiseScale         ("Noise Scale",             Range(0.1, 20.0)) = 2.0
        _NoiseDriftDir      ("Drift Direction",         Vector)           = (0, 1, 0, 0)
        _NoiseDriftSpeed    ("Drift Speed",              Range(0.0,  5.0)) = 0.3
        _NoiseOctaves       ("Noise Octaves",            Range(1, 8))      = 5

        // ---- VOLUMETRICS --------------------------------------------------------
        // Raymarching quality, absorption, lighting, and edge behaviour.
        [Header(Volumetrics)]
        _MarchSteps         ("March Steps",             Range(8, 64))     = 32
        _AbsorptionCoeff    ("Absorption",               Range(0.1, 20.0)) = 8.0
        _DensityPower       ("Density Sharpness",       Range(0.1,  5.0)) = 1.5
        _ScatterAnisotropy  ("Scatter Anisotropy (g)",  Range(0.0,  0.95))= 0.5
        _BackscatterStrength("Backscatter Strength",    Range(0.0,  2.0)) = 0.4
        _AmbientColor       ("Ambient / Shadow Color",  Color)            = (0.05, 0.08, 0.15, 1)
        _AmbientStrength    ("Ambient Strength",        Range(0.0,  1.0)) = 0.35
        _FresnelPower       ("Fresnel Power",           Range(1.0, 10.0)) = 3.0
        _FresnelStrength    ("Fresnel Brightness",      Range(0.0,  2.0)) = 0.6
        _EdgeSoftness       ("Edge Softness",           Range(0.0,  0.5)) = 0.2
        _SoftParticleRange  ("Soft Particle Range",     Range(0.0,  5.0)) = 1.0

        // ---- PHYSICS BRIDGE -----------------------------------------------------
        // Set at runtime by the physics engine. _Density and _PhysicsBlend must
        // keep these exact names so C# can find them with Renderer.SetFloat().
        [Header(Physics Bridge)]
        _Density            ("Density (physics-set)",   Range(0.0,  1.0)) = 1.0
        _PhysicsBlend       ("Physics Blend",           Range(0.0,  1.0)) = 0.0

        // ---- INTERNAL -----------------------------------------------------------
        // Object-space AABB used by the raymarcher. Match to the mesh extents.
        [Header(Voxel Bounds Object Space)]
        _VoxelBoundsMin     ("Bounds Min",              Vector)           = (-0.5, -0.5, -0.5, 0)
        _VoxelBoundsMax     ("Bounds Max",              Vector)           = ( 0.5,  0.5,  0.5, 0)
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
                float4 screenPos  : TEXCOORD3; // homogeneous screen coords — used to sample depth texture
            };

            // Scene depth — lets the ray stop when it hits opaque geometry
            TEXTURE2D_X_FLOAT(_CameraDepthTexture);
            SAMPLER(sampler_CameraDepthTexture);

            CBUFFER_START(UnityPerMaterial)
                // Appearance
                half4   _BaseColor;
                half4   _WarmColor;
                half4   _CoolColor;
                float   _TemperatureBlend;
                half4   _ShadowColor;
                half4   _EmissionColor;
                float   _EmissionStrength;
                // Noise
                float   _NoiseScale;
                float4  _NoiseDriftDir;
                float   _NoiseDriftSpeed;
                int     _NoiseOctaves;
                // Volumetrics
                int     _MarchSteps;
                float   _AbsorptionCoeff;
                float   _DensityPower;
                float   _ScatterAnisotropy;
                float   _BackscatterStrength;
                half4   _AmbientColor;
                float   _AmbientStrength;
                float   _FresnelPower;
                float   _FresnelStrength;
                float   _EdgeSoftness;
                float   _SoftParticleRange;
                // Physics bridge
                float   _Density;
                float   _PhysicsBlend;
                // Voxel bounds
                float4  _VoxelBoundsMin;
                float4  _VoxelBoundsMax;
            CBUFFER_END

            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.uv          = IN.uv;
                // GetWorldSpaceViewDir returns (cameraPos - positionWS), unnormalized
                // We normalize in the fragment shader where we need it per-pixel
                OUT.viewDirWS   = GetWorldSpaceViewDir(OUT.positionWS);
                // ComputeScreenPos gives homogeneous coords → divide by .w in frag for UV
                OUT.screenPos   = ComputeScreenPos(OUT.positionHCS);
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
                if (!ComputeVoxelRaySegmentWS(
                    cameraWS,
                    IN.positionWS,
                    boundsMinOS,
                    boundsMaxOS,
                    entryWS,
                    rayDir,
                    marchDistance
                ))
                {
                    return half4(0, 0, 0, 0);
                }

                // --- Scene depth: stop marching when we hit opaque geometry ---
                // Perspective-correct UV from screen-space homogeneous coords
                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;
                float rawDepth  = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, screenUV).r;
                // Convert to linear eye depth (distance along camera forward axis, in world units)
                float sceneLinearDepth = LinearEyeDepth(rawDepth, _ZBufferParams);

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
                    _Density, _PhysicsBlend,
                    sceneLinearDepth,
                    boundsMinOS, boundsMaxOS,
                    _EdgeSoftness,
                    screenUV
                );

                // ---- STEP 8 : Color & Appearance ----

                // --- Temperature-based base tint ---
                // Blends between a cool blue-white mist and a warm off-white steam.
                // _TemperatureBlend = 0 → cold fog, 1 → hot steam.
                half3 tempTint = lerp((half3)_CoolColor.rgb, (half3)_WarmColor.rgb, _TemperatureBlend);

                // Combine scattered light with base color and temperature tint
                half3 col = volume.rgb * _BaseColor.rgb * tempTint;

                // --- Lit vs shadow tinting ---
                // The raymarcher's scatter already encodes how much light reached
                // each step. Regions where scatter is low (shadow side) get tinted
                // with _ShadowColor. dot(lightDir, volume.rgb) is a rough proxy for
                // how lit this fragment is.
                float litness = saturate(length(volume.rgb) / max(length(lightColor), 0.001));
                col = lerp((half3)_ShadowColor.rgb * volume.a, col, litness);

                // --- Ambient / shadow-side fill ---
                col += _AmbientColor.rgb * _AmbientStrength * (1.0 - litness) * volume.a;

                // --- Backscatter halo ---
                float cosTheta  = dot(-rayDir, lightDir);
                float backPhase = HenyeyGreenstein(cosTheta, _ScatterAnisotropy);
                float hgNorm    = HenyeyGreenstein(0.0, _ScatterAnisotropy);
                float backGlow  = saturate(backPhase / (hgNorm * 8.0));
                col += backGlow * _BackscatterStrength * lightColor * volume.a;

                // --- Emission glow (lit face) ---
                // Adds a bright self-illumination on the most-lit regions of the vapor.
                // Simulates the intense white-out glow of steam in direct sunlight.
                // Scales with litness so only the sun-facing side emits.
                col += (half3)_EmissionColor.rgb * _EmissionStrength * litness * volume.a;

                // ---- STEP 7 : Fresnel & Edge Softness ----

                float3 viewDirWS = normalize(IN.viewDirWS);

                // --- Fresnel edge brightening ---
                float fresnel = FresnelEdge(viewDirWS, -rayDir, _FresnelPower);
                col += fresnel * _FresnelStrength * lightColor * volume.a;

                // --- Soft particles (geometry intersection fade) ---
                // Edge softness is now baked into each march step via the shape mask.
                // Soft particle fade handles only the surface-vs-geometry clip line.
                float fragLinearDepth = -mul(UNITY_MATRIX_V, float4(IN.positionWS, 1.0)).z;
                float softFade = ComputeSoftParticleFade(
                    sceneLinearDepth, fragLinearDepth, _SoftParticleRange
                );

                // ---- STEP 9 : Alpha & Transparency ----

                // Fresnel edge factor also boosts ALPHA at silhouette edges —
                // the vapour appears denser when viewed at a grazing angle,
                // matching the visual density of real steam at its edges.
                // We use a fraction of _FresnelStrength so the boost is subtle.
                float fresnelAlpha = 1.0 + fresnel * _FresnelStrength * 0.35;

                // Physics density gate:
                // When _PhysicsBlend > 0, _Density from the physics engine scales alpha.
                // _Density = 0  → vapor is invisible (empty voxel)
                // _Density = 1  → vapor at full opacity
                // When _PhysicsBlend = 0, physicsFade = 1 (pure noise preview, unaffected).
                float physicsFade = lerp(1.0, saturate(_Density), _PhysicsBlend);

                // Combine: march opacity × Fresnel × soft-particle fade × physics gate
                float finalAlpha = saturate(volume.a * fresnelAlpha * softFade * physicsFade);

                return half4(col, finalAlpha);
            }
            ENDHLSL
        }
    }
}
