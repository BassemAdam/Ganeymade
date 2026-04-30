// =============================================================================
//  WaterVapourBest.shader
//  --------------------------------------------------------------------------
//  Drop-in replacement for the vapour-only renderer (works with the existing
//  WaterPhaseRaymarchRenderer / WaterPhaseMarchingCubesRenderer C# binding).
//
//  Pipeline = "Method 1" from the design notes:
//      "use ray-marching to the depth buffer
//       Can sample a single volume
//       Do light culling+sampling, shadowing etc during traversal.
//       No need to stash results in a froxel grid"
//
//  Goals:
//      * Vapour position is taken from the PHYSICS density grid (G channel)
//        — never from procedural noise alone — so the volume always matches
//        where the SPH simulation actually placed vapour particles.
//      * Volumetric god-rays from the directional sun (per-step main-light
//        shadow attenuation + a short secondary "light ray" sub-march for
//        self-shadowing where shadowmaps are unavailable).
//      * Spot/point lights interact correctly: per-step Forward+ light loop,
//        each light gets HG phase weighting + distance/cone attenuation +
//        URP additional-light shadows.
//      * Realistic multiple-scattering proxy via transmittance-weighted
//        ambient fill so deep volume cores don't look pure black.
// =============================================================================

Shader "Custom/WaterVapourBest"
{
    Properties
    {
        [Header(Physics Grid Coupling)]
        _DensityScale       ("Physics Density Scale (multiply G)",  Range(0.0, 8.0)) = 1.0
        _DensityFloor       ("Physics Density Floor (cull below)",  Range(0.0, 0.2)) = 0.001
        _NoiseDetailMix     ("Procedural Detail Mix (0=raw grid)",  Range(0.0, 1.0)) = 0.55

        [Header(Procedural Detail)]
        _NoiseScale         ("Noise World Scale",                    Range(0.05, 20.0)) = 1.5
        _NoiseDriftDir      ("Drift Direction",                      Vector)            = (0, 1, 0, 0)
        _NoiseDriftSpeed    ("Drift Speed",                          Range(0.0, 5.0))   = 0.30
        _NoiseOctaves       ("Detail Octaves",                       Range(1, 8))       = 5
        _DensityPower       ("Detail Sharpness",                     Range(0.1, 4.0))   = 1.40
        _DetailWarp         ("Domain Warp Strength",                 Range(0.0, 1.0))   = 0.45
        _EdgeSoftness       ("Bounds Edge Softness (OS units)",      Range(0.0, 0.5))   = 0.18

        [Header(Blue Noise Jitter)]
        _BlueNoiseTex       ("Blue Noise Texture",                   2D)                = "gray" {}
        _BlueNoiseScale     ("Blue Noise Tiling",                    Range(0.25, 8.0))  = 1.0
        _BlueNoiseStrength  ("Blue Noise Strength",                  Range(0.0, 1.0))   = 1.0
        _BlueNoiseTimeSpeed ("Blue Noise Temporal Speed",            Range(0.0, 4.0))   = 1.0

        [Header(Vapour Appearance)]
        _AlbedoColor        ("Single-Scatter Albedo",                Color)             = (0.96, 0.97, 1.00, 1)
        _ShadowColor        ("Shadow / Deep-Core Tint",              Color)             = (0.06, 0.09, 0.16, 1)
        _AmbientColor       ("Ambient (Sky) Color",                  Color)             = (0.30, 0.42, 0.62, 1)
        _AmbientStrength    ("Ambient Strength",                     Range(0.0, 3.0))   = 0.45
        _EmissionColor      ("Emission Tint",                        Color)             = (1.00, 0.93, 0.80, 1)
        _EmissionStrength   ("Emission Strength",                    Range(0.0, 4.0))   = 0.0
        _Absorption         ("Extinction (sigma_t)",                 Range(0.05, 30.0)) = 6.0
        _ScatterG           ("Henyey-Greenstein g",                  Range(-0.95, 0.95))= 0.55
        _Backscatter        ("Silver-Lining Strength",               Range(0.0, 4.0))   = 1.0

        [Header(God Rays)]
        _LightMarchSteps    ("Light Sub-March Steps",                Range(0, 16))      = 6
        _LightMarchDistance ("Light Sub-March Distance (WS)",        Range(0.0, 10.0))  = 1.5
        _LightAbsorption    ("Light Sub-March Absorption Scale",     Range(0.0, 4.0))   = 1.0

        [Header(Raymarch)]
        _MarchSteps         ("Primary March Steps",                  Range(8, 256))     = 64

        [Header(Voxel Bounds Object Space)]
        _VoxelBoundsMin     ("Bounds Min", Vector) = (-0.5, -0.5, -0.5, 0)
        _VoxelBoundsMax     ("Bounds Max", Vector) = ( 0.5,  0.5,  0.5, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Transparent"
            "Queue"          = "Transparent+100"
            "RenderPipeline" = "UniversalPipeline"
        }

        ZWrite Off
        Cull   Front
        ZTest  Always
        Blend  SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM

            #pragma vertex   vert
            #pragma fragment frag

            // URP shadow + light keywords (mirror the keywords used by
            // WaterPhase.shader so global state stays consistent).
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            // Reuse the existing AABB intersector / edge fade so behaviour matches
            // the rest of the WaterPhase pipeline (and we share helper bug-fixes).
            #include "WaterPhase/WaterPhaseGeometry.hlsl"
            #include "WaterPhase/WaterPhaseShape.hlsl"
            #include "WaterPhase/WaterPhaseLighting.hlsl"

            // -----------------------------------------------------------------
            //  Physics density grid (set globally by WaterPhaseDensityPipeline)
            //  R = liquid density, G = vapour density, both 0..1.
            //  Matches the binding contract used by WaterPhaseNoise.hlsl.
            // -----------------------------------------------------------------
            Texture3D<float2> _PhysicsDensityGrid;
            SamplerState      sampler_PhysicsDensityGrid;
            float4 _PhysicsBoundsMinWS;
            float4 _PhysicsBoundsMaxWS;
            float4 _PhysicsVolumeDims;

            TEXTURE2D_X_FLOAT(_CameraDepthTexture);
            SAMPLER(sampler_CameraDepthTexture);
            TEXTURE2D(_BlueNoiseTex);
            SAMPLER(sampler_BlueNoiseTex);
            float4 _BlueNoiseTex_TexelSize;

            CBUFFER_START(UnityPerMaterial)
                float   _DensityScale;
                float   _DensityFloor;
                float   _NoiseDetailMix;

                float   _NoiseScale;
                float4  _NoiseDriftDir;
                float   _NoiseDriftSpeed;
                int     _NoiseOctaves;
                float   _DensityPower;
                float   _DetailWarp;
                float   _EdgeSoftness;

                float   _BlueNoiseScale;
                float   _BlueNoiseStrength;
                float   _BlueNoiseTimeSpeed;

                half4   _AlbedoColor;
                half4   _ShadowColor;
                half4   _AmbientColor;
                float   _AmbientStrength;
                half4   _EmissionColor;
                float   _EmissionStrength;
                float   _Absorption;
                float   _ScatterG;
                float   _Backscatter;

                int     _LightMarchSteps;
                float   _LightMarchDistance;
                float   _LightAbsorption;

                int     _MarchSteps;
                float4  _VoxelBoundsMin;
                float4  _VoxelBoundsMax;
            CBUFFER_END

            // -----------------------------------------------------------------
            //  Local helpers (kept in-shader so the file is self-contained)
            // -----------------------------------------------------------------

            float Hash3D(float3 p)
            {
                p = frac(p * float3(443.897, 441.423, 437.195));
                p += dot(p, p.yzx + 19.19);
                return frac((p.x + p.y) * p.z);
            }

            float ValueNoise3D(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);

                float c000 = Hash3D(i + float3(0,0,0));
                float c100 = Hash3D(i + float3(1,0,0));
                float c010 = Hash3D(i + float3(0,1,0));
                float c110 = Hash3D(i + float3(1,1,0));
                float c001 = Hash3D(i + float3(0,0,1));
                float c101 = Hash3D(i + float3(1,0,1));
                float c011 = Hash3D(i + float3(0,1,1));
                float c111 = Hash3D(i + float3(1,1,1));

                float x0 = lerp(c000, c100, u.x);
                float x1 = lerp(c010, c110, u.x);
                float x2 = lerp(c001, c101, u.x);
                float x3 = lerp(c011, c111, u.x);

                float y0 = lerp(x0, x1, u.y);
                float y1 = lerp(x2, x3, u.y);
                return lerp(y0, y1, u.z);
            }

            float FBM(float3 p, int octaves)
            {
                float value = 0.0;
                float amp   = 0.5;
                float freq  = 1.0;
                float total = 0.0;
                for (int o = 0; o < octaves; o++)
                {
                    value += amp * ValueNoise3D(p * freq);
                    total += amp;
                    amp   *= 0.5;
                    freq  *= 2.0;
                }
                return value / max(total, 1e-5);
            }

            // Sample the physics vapour mask (G channel). Returns 0 outside bounds.
            float SamplePhysicsVapour(float3 worldPos)
            {
                float3 minWS = _PhysicsBoundsMinWS.xyz;
                float3 maxWS = _PhysicsBoundsMaxWS.xyz;
                float3 sizeWS = maxWS - minWS;
                float3 uvw = (worldPos - minWS) / max(sizeWS, 1e-5);
                if (any(uvw < 0.0) || any(uvw > 1.0))
                    return 0.0;
                return _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw, 0).g;
            }

            // The full per-step density used by the marcher.
            //
            //  density(x) = physicsMask * scale * detailModulation
            //
            // Where detailModulation is an FBM-driven [1-mix .. 1+mix] envelope
            // gated by physicsMask itself, so noise only sculpts where the SPH
            // grid says vapour exists. Setting _NoiseDetailMix to 0 collapses
            // the result to the raw physics field (no procedural component).
            float SampleVapourDensity(float3 worldPos, float time, float3 driftDir)
            {
                float mask = SamplePhysicsVapour(worldPos);
                if (mask < _DensityFloor)
                    return 0.0;

                float maskSoft = smoothstep(0.0, 0.20, mask);
                float base = mask * maskSoft * _DensityScale;

                if (_NoiseDetailMix > 0.001)
                {
                    float3 drifted = worldPos + driftDir * (time * _NoiseDriftSpeed);
                    float3 p = drifted / max(_NoiseScale, 1e-5);

                    float3 warp = float3(
                        ValueNoise3D(p * 0.7 + float3(1.72, 9.23, 5.41)),
                        ValueNoise3D(p * 0.7 + float3(8.31, 2.84, 3.26)),
                        ValueNoise3D(p * 0.7 + float3(4.17, 6.73, 1.92))
                    ) * 2.0 - 1.0;

                    float3 wp = p + warp * _DetailWarp;
                    float n  = FBM(wp, _NoiseOctaves);
                    n = pow(saturate(n), max(_DensityPower, 0.01));

                    // Symmetric modulation around the physics value: noise can
                    // brighten OR darken but never invents density outside mask.
                    float modulation = lerp(1.0, n * 2.0, _NoiseDetailMix);
                    base *= modulation;
                }

                return saturate(base);
            }

            // Short ray sub-march from the current sample TOWARD a light source,
            // returns transmittance along that segment. This is what produces the
            // visible god-ray / shaft pattern when the volume self-occludes a
            // light. Cheap because we only take a handful of steps.
            float LightTransmittance(float3 fromWS, float3 toLightDir, float time, float3 driftDir)
            {
                int steps = _LightMarchSteps;
                if (steps <= 0)
                    return 1.0;

                float distWS = max(_LightMarchDistance, 1e-3);
                float dt     = distWS / (float)steps;
                float tau    = 0.0; // optical depth
                float sigma  = _Absorption * _LightAbsorption;

                // Tiny offset to avoid self-sampling the current cell.
                float3 origin = fromWS + toLightDir * (dt * 0.5);

                [unroll(16)]
                for (int s = 0; s < 16; s++)
                {
                    if (s >= steps) break;
                    float3 p = origin + toLightDir * (dt * s);
                    float d = SampleVapourDensity(p, time, driftDir);
                    tau += d * sigma * dt;
                }
                return exp(-tau);
            }

            // -----------------------------------------------------------------
            //  Vertex / fragment
            // -----------------------------------------------------------------

            struct MeshInput
            {
                float4 positionOS : POSITION;
            };

            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 viewDirWS   : TEXCOORD1;
                float4 screenPos   : TEXCOORD2;
            };

            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.viewDirWS   = GetWorldSpaceViewDir(OUT.positionWS);
                OUT.screenPos   = ComputeScreenPos(OUT.positionHCS);
                return OUT;
            }

            half4 frag(Interpolators IN) : SV_Target
            {
                float3 cameraWS    = _WorldSpaceCameraPos.xyz;
                float3 boundsMinOS = _VoxelBoundsMin.xyz;
                float3 boundsMaxOS = _VoxelBoundsMax.xyz;

                float3 entryWS;
                float3 rayDir;
                float  marchDistance;
                if (!ComputeVoxelRaySegmentWS(cameraWS, IN.positionWS,
                    boundsMinOS, boundsMaxOS,
                    entryWS, rayDir, marchDistance))
                {
                    return 0;
                }

                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;
                float  rawDepth = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, screenUV).r;
                float  sceneZ   = LinearEyeDepth(rawDepth, _ZBufferParams);

                int steps    = max(_MarchSteps, 1);
                float stepSize = marchDistance / (float)steps;
                float sigma_t  = _Absorption;

                // Shape-mask precompute (mirrors WaterPhaseShape.hlsl usage).
                float3 boundsCenterOS  = (boundsMinOS + boundsMaxOS) * 0.5;
                float3 boundsExtentsOS = (boundsMaxOS - boundsMinOS) * 0.5;

                // Jitter (IGN + temporal blue-noise) — kills the "slab" banding
                // that makes a coarse march look like stacked cards.
                float2 pixelCoords = screenUV * _ScreenParams.xy;
                float ign = frac(52.9829189 * frac(dot(pixelCoords, float2(0.06711056, 0.00583715))));

                float2 bnUV = frac(screenUV * _ScreenParams.xy * _BlueNoiseTex_TexelSize.xy * _BlueNoiseScale);
                float2 bnOff = float2(0.75487766, 0.56984029) * frac(_Time.y * _BlueNoiseTimeSpeed);
                float bn = SAMPLE_TEXTURE2D(_BlueNoiseTex, sampler_BlueNoiseTex, frac(bnUV + bnOff)).r;
                float jitter = lerp(ign, bn, saturate(_BlueNoiseStrength));

                // Main light info.
                Light  mainLight  = GetMainLight();
                float3 lightDir   = normalize(mainLight.direction);
                half3  lightColor = mainLight.color;

                float cosThetaMain = dot(-rayDir, lightDir);
                float phaseMain    = HenyeyGreenstein(cosThetaMain, _ScatterG);

                float3 driftDir = normalize(_NoiseDriftDir.xyz);

                // Accumulators.
                float3 scattered     = 0;
                float  transmittance = 1.0;

                #if defined(_ADDITIONAL_LIGHTS)
                    InputData inputData = (InputData)0;
                    inputData.normalizedScreenSpaceUV = screenUV;
                    uint addLightCount = (uint)GetAdditionalLightsCount();
                #endif

                [loop]
                for (int i = 0; i < 256; i++)
                {
                    if (i >= steps) break;
                    float t = stepSize * (i + jitter);
                    float3 sampleWS = entryWS + rayDir * t;

                    // Stop at opaque scene geometry.
                    float eyeZ = -mul(UNITY_MATRIX_V, float4(sampleWS, 1)).z;
                    if (eyeZ >= sceneZ) break;

                    // Edge softening so the box silhouette never has a hard rectangle.
                    float3 sampleOS = TransformWorldToObject(sampleWS);
                    float shape = ComputeShapeMaskOS(sampleOS, boundsMinOS, boundsMaxOS,
                                                    boundsCenterOS, boundsExtentsOS, _EdgeSoftness);
                    if (shape <= 0.001) continue;

                    float density = SampleVapourDensity(sampleWS, _Time.y, driftDir) * shape;
                    if (density <= _DensityFloor) continue;

                    float stepTau     = density * sigma_t * stepSize;
                    float stepTransmit = exp(-stepTau);
                    float stepWeight  = transmittance * (1.0 - stepTransmit);

                    // ---- Main directional light ----
                    half mainShadow = 1.0;
                    #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                        float4 shadowCoord = TransformWorldToShadowCoord(sampleWS);
                        mainShadow = MainLightRealtimeShadow(shadowCoord);
                    #endif

                    // Volumetric self-shadowing toward the sun → god-ray pattern.
                    float lightTau = LightTransmittance(sampleWS, lightDir, _Time.y, driftDir);

                    half3 mainContribution = lightColor * mainShadow * lightTau * phaseMain;
                    scattered += stepWeight * _AlbedoColor.rgb * mainContribution;

                    // ---- Additional point/spot lights (Forward+ aware) ----
                    #if defined(_ADDITIONAL_LIGHTS)
                        inputData.positionWS = sampleWS;
                        LIGHT_LOOP_BEGIN(addLightCount)
                            Light L = GetAdditionalLight(lightIndex, sampleWS);
                            float3 Ldir = normalize(L.direction);

                            // Each point/spot light gets its own HG phase against the view ray.
                            float cosThetaL = dot(-rayDir, Ldir);
                            float phaseL    = HenyeyGreenstein(cosThetaL, _ScatterG);

                            // Short volumetric self-shadow toward the light.
                            float visL = LightTransmittance(sampleWS, Ldir, _Time.y, driftDir);

                            half3 radiance = L.color
                                           * (L.distanceAttenuation * L.shadowAttenuation)
                                           * visL * phaseL;
                            scattered += stepWeight * _AlbedoColor.rgb * radiance;
                        LIGHT_LOOP_END
                    #endif

                    // ---- Multiple-scattering proxy: ambient sky term ----
                    // Treats sky as an isotropic source; physically motivated
                    // as the low-order term of an SH × volumetric-occlusion fit.
                    half3 ambient = _AmbientColor.rgb * _AmbientStrength;
                    scattered += stepWeight * _AlbedoColor.rgb * ambient;

                    // Optional self-emission (hot steam / mist illumination).
                    if (_EmissionStrength > 0.0)
                        scattered += stepWeight * _EmissionColor.rgb * _EmissionStrength * density;

                    // Update Beer-Lambert transmittance.
                    transmittance *= stepTransmit;
                    if (transmittance < 0.005) break;
                }

                float alpha = saturate(1.0 - transmittance);

                // Deep-core tint — pulls extremely opaque cores toward the
                // chosen shadow color so they don't read as flat white.
                float coreMask = smoothstep(0.85, 1.0, alpha);
                scattered = lerp(scattered, _ShadowColor.rgb * alpha, coreMask * 0.35);

                // Silver-lining backscatter (subtle): bright halo when the sun
                // is roughly behind the volume from the viewer's perspective.
                float backGlow = saturate(phaseMain / max(HenyeyGreenstein(0.0, _ScatterG), 1e-4) / 8.0);
                scattered += backGlow * _Backscatter * lightColor * alpha;

                return half4(scattered, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
