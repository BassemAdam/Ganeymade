// =============================================================================
//  WaterVapourVO.shader
//  --------------------------------------------------------------------------
//  Alternative vapour renderer implementing the "Volumetric Occlusion" path
//  from the design notes:
//
//      * Optical thickness approximated by a linear function:
//            tau(x, w) ~= v0(x) + v1(x) . w
//        with v0/v1 estimated from a small spherical convolution over the
//        physics density grid (a single-level stand-in for the full
//        clip/mip hierarchy described in the source).
//      * Visibility T(x, w) = exp(-v0 + v1 . w) is therefore a SPHERICAL
//        GAUSSIAN, which composes analytically with:
//          - the SG-fit Henyey-Greenstein phase function, and
//          - the SH-projected environment lighting (we use the L0 + L1 bands
//            obtained from URP's SampleSH(normal) trick).
//      * Multiple scattering uses Kubelka-Munk effective extinction:
//            sigma_e = sigma_t * sqrt((1 - alpha*f)^2 - (alpha*b)^2)
//        with f, b derived from the HG g-parameter.
//      * Pop-suppression is handled by temporal blue-noise rotation on the
//        primary march jitter (so newly-revealed samples blend in smoothly).
//
//  Vapour position is sampled from the SHARED physics grid (G channel) — same
//  uniform contract as WaterVapourBest.shader, so the existing C# binding
//  pipeline works without changes.
// =============================================================================

Shader "Custom/WaterVapourVO"
{
    Properties
    {
        [Header(Physics Grid Coupling)]
        _DensityScale       ("Physics Density Scale",                Range(0.0, 8.0))   = 1.0
        _DensityFloor       ("Physics Density Floor",                Range(0.0, 0.2))   = 0.001
        _NoiseDetailMix     ("Procedural Detail Mix",                Range(0.0, 1.0))   = 0.40

        [Header(Procedural Detail)]
        _NoiseScale         ("Noise World Scale",                    Range(0.05, 20.0)) = 1.5
        _NoiseDriftDir      ("Drift Direction",                      Vector)            = (0, 1, 0, 0)
        _NoiseDriftSpeed    ("Drift Speed",                          Range(0.0, 5.0))   = 0.30
        _NoiseOctaves       ("Detail Octaves",                       Range(1, 8))       = 4
        _DensityPower       ("Detail Sharpness",                     Range(0.1, 4.0))   = 1.20
        _EdgeSoftness       ("Bounds Edge Softness",                 Range(0.0, 0.5))   = 0.18

        [Header(Volumetric Occlusion)]
        _VOSampleRadiusWS   ("VO Convolution Radius (WS)",           Range(0.05, 5.0))  = 0.7
        _VOSampleCount      ("VO Sample Count",                      Range(4, 32))      = 12
        _VOAttenuation      ("VO Strength (-> v0 scale)",            Range(0.0, 4.0))   = 1.0
        _VODirectionalBias  ("VO Directional (-> v1 scale)",         Range(0.0, 4.0))   = 1.0

        [Header(Multiple Scattering)]
        _MSAlbedo           ("Multi-Scatter Albedo (Color)",         Color)             = (0.95, 0.97, 1.00, 1)
        _MSEnable           ("Enable Effective Extinction",          Range(0.0, 1.0))   = 1.0

        [Header(Lighting)]
        _AmbientStrength    ("Sky / SH Ambient Strength",            Range(0.0, 4.0))   = 1.0
        _SunStrength        ("Direct Sun Strength",                  Range(0.0, 4.0))   = 1.0
        _ScatterG           ("Henyey-Greenstein g",                  Range(-0.95, 0.95))= 0.55
        _SGSharpness        ("Phase SG Sharpness Override (0=auto)", Range(0.0, 64.0))  = 0.0

        [Header(Blue Noise Jitter)]
        _BlueNoiseTex       ("Blue Noise Texture",                   2D)                = "gray" {}
        _BlueNoiseScale     ("Blue Noise Tiling",                    Range(0.25, 8.0))  = 1.0
        _BlueNoiseStrength  ("Blue Noise Strength",                  Range(0.0, 1.0))   = 1.0
        _BlueNoiseTimeSpeed ("Blue Noise Temporal Speed",            Range(0.0, 4.0))   = 1.0

        [Header(Raymarch)]
        _MarchSteps         ("Primary March Steps",                  Range(8, 256))     = 48
        _Absorption         ("Base Extinction (sigma_t)",            Range(0.05, 30.0)) = 6.0

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

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #include "WaterPhase/WaterPhaseGeometry.hlsl"
            #include "WaterPhase/WaterPhaseShape.hlsl"
            #include "WaterPhase/WaterPhaseLighting.hlsl"

            // -----------------------------------------------------------------
            //  Physics density grid (shared globals).
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
                float   _EdgeSoftness;

                float   _VOSampleRadiusWS;
                int     _VOSampleCount;
                float   _VOAttenuation;
                float   _VODirectionalBias;

                half4   _MSAlbedo;
                float   _MSEnable;

                float   _AmbientStrength;
                float   _SunStrength;
                float   _ScatterG;
                float   _SGSharpness;

                float   _BlueNoiseScale;
                float   _BlueNoiseStrength;
                float   _BlueNoiseTimeSpeed;

                int     _MarchSteps;
                float   _Absorption;

                float4  _VoxelBoundsMin;
                float4  _VoxelBoundsMax;
            CBUFFER_END

            // -----------------------------------------------------------------
            //  Shared helpers (kept local; same noise routines as Best variant).
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
                float c000=Hash3D(i), c100=Hash3D(i+float3(1,0,0));
                float c010=Hash3D(i+float3(0,1,0)), c110=Hash3D(i+float3(1,1,0));
                float c001=Hash3D(i+float3(0,0,1)), c101=Hash3D(i+float3(1,0,1));
                float c011=Hash3D(i+float3(0,1,1)), c111=Hash3D(i+float3(1,1,1));
                float x0=lerp(c000,c100,u.x), x1=lerp(c010,c110,u.x);
                float x2=lerp(c001,c101,u.x), x3=lerp(c011,c111,u.x);
                return lerp(lerp(x0,x1,u.y), lerp(x2,x3,u.y), u.z);
            }
            float FBM(float3 p, int octaves)
            {
                float v=0, a=0.5, f=1.0, t=0;
                for (int o=0;o<octaves;o++){ v+=a*ValueNoise3D(p*f); t+=a; a*=0.5; f*=2.0; }
                return v / max(t, 1e-5);
            }

            float SamplePhysicsVapour(float3 worldPos)
            {
                float3 minWS = _PhysicsBoundsMinWS.xyz;
                float3 maxWS = _PhysicsBoundsMaxWS.xyz;
                float3 uvw = (worldPos - minWS) / max(maxWS - minWS, 1e-5);
                if (any(uvw < 0.0) || any(uvw > 1.0)) return 0.0;
                return _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw, 0).g;
            }

            // sigma_t at world position (the per-step density used by the marcher).
            float SampleSigmaT(float3 worldPos, float time, float3 driftDir)
            {
                float mask = SamplePhysicsVapour(worldPos);
                if (mask < _DensityFloor) return 0.0;
                float maskSoft = smoothstep(0.0, 0.20, mask);
                float base = mask * maskSoft * _DensityScale;
                if (_NoiseDetailMix > 0.001)
                {
                    float3 d = worldPos + driftDir * (time * _NoiseDriftSpeed);
                    float3 p = d / max(_NoiseScale, 1e-5);
                    float n = pow(saturate(FBM(p, _NoiseOctaves)), max(_DensityPower, 0.01));
                    base *= lerp(1.0, n * 2.0, _NoiseDetailMix);
                }
                return saturate(base) * _Absorption;
            }

            // -----------------------------------------------------------------
            //  Volumetric Occlusion estimator
            //  --------------------------------------------------------------
            //   Replaces the full clipmap/SH hierarchy with a single-level
            //   spherical convolution. We sum sigma_t over a Fibonacci-sphere
            //   neighbourhood inside _VOSampleRadiusWS:
            //
            //      v0 ~= sum_i  sigma_t(x + r * d_i)            (isotropic)
            //      v1 ~= sum_i  sigma_t(x + r * d_i) * 3 * d_i  (directional)
            //
            //   Both are scaled by 1/N and tunable strengths so artists can
            //   bias the look without re-deriving the integral.
            //
            //   Then visibility along any direction omega is the closed-form
            //      T(x, omega) = exp(-v0 + v1 . omega)         (a spherical gaussian)
            // -----------------------------------------------------------------
            void EstimateVO(float3 worldPos, float time, float3 driftDir,
                            out float v0, out float3 v1)
            {
                v0 = 0;
                v1 = 0;

                int   N = max(_VOSampleCount, 1);
                float r = _VOSampleRadiusWS;

                // Fibonacci sphere — even angular coverage with very few taps.
                const float GA = 2.39996322972865332; // golden angle (rad)
                [loop]
                for (int i = 0; i < 32; i++)
                {
                    if (i >= N) break;
                    float t  = (i + 0.5) / (float)N;
                    float z  = 1.0 - 2.0 * t;
                    float rr = sqrt(max(0.0, 1.0 - z*z));
                    float ph = i * GA;
                    float3 d = float3(rr * cos(ph), rr * sin(ph), z);

                    float3 sp = worldPos + d * r;
                    float  s  = SampleSigmaT(sp, time, driftDir);
                    v0 += s;
                    v1 += s * 3.0 * d;
                }

                float invN = 1.0 / (float)N;
                v0 *= invN * _VOAttenuation;
                v1 *= invN * _VODirectionalBias;
            }

            // Closed-form visibility from a VO fit, evaluated along direction w.
            // Clamped to [0,1] because the linear fit can briefly overshoot in
            // very thin regions.
            float VOVisibility(float v0, float3 v1, float3 w)
            {
                return saturate(exp(-v0 + dot(v1, w)));
            }

            // SG fit to Henyey-Greenstein. The standard practical fit uses
            //      k ~= 2 * g / (1 - g^2)
            // which matches HG forward lobe sharpness at g in (0, ~0.85).
            float SGSharpnessFromG(float g)
            {
                if (_SGSharpness > 0.001) return _SGSharpness;
                float gg = clamp(abs(g), 0.0, 0.95);
                return 2.0 * gg / max(1.0 - gg * gg, 1e-3);
            }

            // Normalised SG phase: p(u; k) = k * exp(k * u) / (4*pi*sinh(k))
            // We drop the 4*pi factor (folded into intensity tuning) but keep
            // the sinh normaliser so changing g doesn't change total energy.
            float SGPhase(float u, float k)
            {
                float ku = k * u;
                // sinh(k) for k > ~12 saturates fast; use stable form.
                float s  = (k > 12.0) ? 0.5 * exp(k) : sinh(k);
                return k * exp(ku) / max(s, 1e-5);
            }

            // Multiple-scattering effective extinction (Kubelka-Munk fit).
            // alpha is the single-scattering albedo magnitude.
            float EffectiveExtinction(float sigma_t, float alpha, float g)
            {
                float gg = clamp(abs(g), 0.0, 0.99);
                float gSafe = max(gg, 1e-3);
                float f = (1.0 + gSafe) / (2.0 * gSafe) *
                          (1.0 - (1.0 - gSafe) / sqrt(1.0 + gSafe * gSafe));
                float b = 1.0 - f;
                float term = (1.0 - alpha * f);
                float disc = max(term * term - (alpha * b) * (alpha * b), 1e-4);
                return sigma_t * sqrt(disc);
            }

            // -----------------------------------------------------------------
            //  Vertex / fragment
            // -----------------------------------------------------------------

            struct MeshInput { float4 positionOS : POSITION; };
            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float4 screenPos   : TEXCOORD1;
            };

            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
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

                int   steps    = max(_MarchSteps, 1);
                float stepSize = marchDistance / (float)steps;

                float3 boundsCenterOS  = (boundsMinOS + boundsMaxOS) * 0.5;
                float3 boundsExtentsOS = (boundsMaxOS - boundsMinOS) * 0.5;

                // Pop-suppression: temporal blue noise + IGN, exactly the same
                // rotation scheme as the Best variant — newly-revealed samples
                // average over time instead of popping.
                float2 pixelCoords = screenUV * _ScreenParams.xy;
                float ign = frac(52.9829189 * frac(dot(pixelCoords, float2(0.06711056, 0.00583715))));
                float2 bnUV = frac(screenUV * _ScreenParams.xy * _BlueNoiseTex_TexelSize.xy * _BlueNoiseScale);
                float2 bnOff = float2(0.75487766, 0.56984029) * frac(_Time.y * _BlueNoiseTimeSpeed);
                float bn = SAMPLE_TEXTURE2D(_BlueNoiseTex, sampler_BlueNoiseTex, frac(bnUV + bnOff)).r;
                float jitter = lerp(ign, bn, saturate(_BlueNoiseStrength));

                Light  mainLight  = GetMainLight();
                float3 lightDir   = normalize(mainLight.direction);
                half3  lightColor = mainLight.color * _SunStrength;

                float k         = SGSharpnessFromG(_ScatterG);
                float cosThetaM = dot(-rayDir, lightDir);
                float phaseMain = SGPhase(cosThetaM, k);

                // Albedo -> grayscale alpha for KM extinction; per-channel albedo
                // is reapplied multiplicatively on output.
                float albedoMag = saturate(dot(_MSAlbedo.rgb, half3(0.299, 0.587, 0.114)));

                float3 driftDir = normalize(_NoiseDriftDir.xyz);

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

                    float eyeZ = -mul(UNITY_MATRIX_V, float4(sampleWS, 1)).z;
                    if (eyeZ >= sceneZ) break;

                    float3 sampleOS = TransformWorldToObject(sampleWS);
                    float shape = ComputeShapeMaskOS(sampleOS, boundsMinOS, boundsMaxOS,
                                                    boundsCenterOS, boundsExtentsOS, _EdgeSoftness);
                    if (shape <= 0.001) continue;

                    float sigma_t = SampleSigmaT(sampleWS, _Time.y, driftDir) * shape;
                    if (sigma_t <= 0.0001) continue;

                    // ---- VO fit at this sample ----
                    float v0; float3 v1;
                    EstimateVO(sampleWS, _Time.y, driftDir, v0, v1);

                    // ---- Effective extinction (multiple scattering) ----
                    float sigma_e = lerp(sigma_t, EffectiveExtinction(sigma_t, albedoMag, _ScatterG), _MSEnable);

                    float stepTau     = sigma_e * stepSize;
                    float stepTransmit = exp(-stepTau);
                    float stepWeight  = transmittance * (1.0 - stepTransmit);

                    // ---- Direct sun via VO visibility ----
                    half mainShadow = 1.0;
                    #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                        float4 sc = TransformWorldToShadowCoord(sampleWS);
                        mainShadow = MainLightRealtimeShadow(sc);
                    #endif
                    float visSun = VOVisibility(v0, v1, lightDir);
                    half3 sunTerm = lightColor * mainShadow * visSun * phaseMain;
                    scattered += stepWeight * _MSAlbedo.rgb * sunTerm;

                    // ---- Environment / SH ambient via SG x SH analytic product ----
                    // Sample URP's per-vertex SH at the volume sample. We integrate
                    // it against the VO visibility by evaluating SH along v1 (the
                    // dominant occluded direction): this is the cheap closed-form
                    // approximation of "integral of SH lighting times SG visibility"
                    // that the design notes call out.
                    float v1len = length(v1);
                    float3 v1dir = v1len > 1e-4 ? v1 / v1len : float3(0,1,0);
                    half3 sh = SampleSH(v1dir);
                    // Visibility along the dominant unoccluded direction.
                    float visEnv = VOVisibility(v0, v1, v1dir);
                    half3 envTerm = sh * _AmbientStrength * visEnv;
                    scattered += stepWeight * _MSAlbedo.rgb * envTerm;

                    // ---- Additional point/spot lights, each with VO visibility ----
                    #if defined(_ADDITIONAL_LIGHTS)
                        inputData.positionWS = sampleWS;
                        LIGHT_LOOP_BEGIN(addLightCount)
                            Light L = GetAdditionalLight(lightIndex, sampleWS);
                            float3 Ldir = normalize(L.direction);
                            float visL = VOVisibility(v0, v1, Ldir);
                            float phaseL = SGPhase(dot(-rayDir, Ldir), k);
                            half3 rad = L.color
                                      * (L.distanceAttenuation * L.shadowAttenuation)
                                      * visL * phaseL;
                            scattered += stepWeight * _MSAlbedo.rgb * rad;
                        LIGHT_LOOP_END
                    #endif

                    transmittance *= stepTransmit;
                    if (transmittance < 0.005) break;
                }

                float alpha = saturate(1.0 - transmittance);
                return half4(scattered, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
