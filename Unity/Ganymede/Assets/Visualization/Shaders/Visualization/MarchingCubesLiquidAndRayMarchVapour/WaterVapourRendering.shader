Shader "Custom/VapourVolume"
{
    Properties
    {
        [Header(Vapour Raymarch)]
        _StepSize ("Step Size", Range(0.001, 1.0)) = 0.05
        _BlueNoiseTex ("Blue Noise Texture", 2D) = "gray" {}
        _BlueNoiseScale ("Blue Noise Tiling", Range(0.25, 8.0)) = 1.0
        _BlueNoiseStrength ("Blue Noise Jitter Strength", Range(0.0, 1.0)) = 1.0
        _BlueNoiseTimeSpeed ("Blue Noise Temporal Speed", Range(0.0, 4.0)) = 1.0
        [Header(Vapour Rendering)]
        _VapourBaseColor ("Vapour Base Color", Color) = (1.0, 1.0, 1.0, 1)
        _VapourAbsorption ("Vapour Absorption (density -> opacity)", Range(0.1, 20.0)) = 8.0
        _VapourGodRayStrength ("Vapour God Ray Strength", Range(0.0, 1.0)) = 1.0
        _VapourShadowFloor ("Vapour Shadow Visibility Floor", Range(0.0, 1.0)) = 0.25
        _VapourScatterG ("Vapour Forward Scatter", Range(-0.8, 0.8)) = 0.35
        _VapourBackscatter ("Vapour Backscatter Rim", Range(0.0, 2.0)) = 0.25
        [Header(Vapour Physics Gate)]
        _VapourPresenceThreshold ("Physical Vapour Presence Threshold", Range(0.0, 0.1)) = 0.0001
        _VapourFullDensity ("Physical Vapour Full Density", Range(0.001, 1.0)) = 0.2
        _VapourDensityMultiplier ("Final Vapour Density Multiplier", Float) = 1.0
        [Header(Vapour Structure)]
        _NoiseScale ("Noise Scale", Range(0.1, 20.0)) = 2.0
        _NoiseDriftDir ("Drift Direction", Vector) = (0, 1, 0, 0)
        _NoiseDriftSpeed ("Drift Speed", Range(0.0, 5.0)) = 0.3
        _NoiseOctaves ("Noise Octaves", Range(1, 8)) = 5
        _DensityPower ("Vapour Density Sharpness", Range(0.1, 5.0)) = 1.5
        _VapourWarpStrength ("Flow Warp Strength", Range(0.0, 2.0)) = 0.65
        _VapourErosionScale ("Erosion Noise Scale", Range(0.25, 8.0)) = 2.75
        _VapourErosionStrength ("Erosion Strength", Range(0.0, 2.0)) = 0.35
        _VapourCutoff ("Wispy Cutoff", Range(0.0, 1.0)) = 0.12
        _VapourSoftness ("Wispy Softness", Range(0.01, 1.0)) = 0.45
        _VapourVerticalStretch ("Vertical Stretch", Range(0.1, 6.0)) = 1.75
        _VapourHeightDissipation ("Height Dissipation", Range(0.0, 6.0)) = 0.25
        _EdgeSoftness ("Vapour Bounds Edge Softness", Range(0.0, 0.5)) = 0.2
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+100" "RenderPipeline" = "UniversalPipeline" }

        ZWrite Off
        Cull Front
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "../RayMarchingLiquidVapour/RayMarching Includes/RayMarchGeometry.hlsl"

            struct MeshInput
            {
                float4 positionOS : POSITION;
            };

            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
            };

            TEXTURE2D(_BlueNoiseTex);
            SAMPLER(sampler_BlueNoiseTex);
            float4 _BlueNoiseTex_TexelSize;
            Texture3D<float2> _PhysicsDensityGrid;
            SamplerState      sampler_PhysicsDensityGrid;
            float4 _PhysicsVolumeDims;
            float4 _PhysicsBoundsMinWS;
            float4 _PhysicsBoundsMaxWS;

            CBUFFER_START(UnityPerMaterial)
                float   _StepSize;
                float   _BlueNoiseScale;
                float   _BlueNoiseStrength;
                float   _BlueNoiseTimeSpeed;

                half4   _VapourBaseColor;
                float   _VapourAbsorption;
                float   _VapourGodRayStrength;
                float   _VapourShadowFloor;
                float   _VapourScatterG;
                float   _VapourBackscatter;
                float   _VapourPresenceThreshold;
                float   _VapourFullDensity;
                float   _VapourDensityMultiplier;

                float   _NoiseScale;
                float4  _NoiseDriftDir;
                float   _NoiseDriftSpeed;
                int     _NoiseOctaves;
                float   _DensityPower;
                float   _VapourWarpStrength;
                float   _VapourErosionScale;
                float   _VapourErosionStrength;
                float   _VapourCutoff;
                float   _VapourSoftness;
                float   _VapourVerticalStretch;
                float   _VapourHeightDissipation;
                float   _EdgeSoftness;
            CBUFFER_END

            float3 DensityGridUVW(float3 posWS)
            {
                float3 sizeWS = max(_PhysicsBoundsMaxWS.xyz - _PhysicsBoundsMinWS.xyz, 1e-5);
                float3 gridSize = max(_PhysicsVolumeDims.xyz, 1.0);
                return (posWS - _PhysicsBoundsMinWS.xyz) / sizeWS + 0.5 / gridSize;
            }

            float SampleRawVapourDensityWS(float3 posWS)
            {
                return _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, DensityGridUVW(posWS), 0).g;
            }

            float SampleVapourBlueNoise(float2 screenUV)
            {
                float2 pixelCoords = screenUV * _ScaledScreenParams.xy;
                float ignJitter = frac(52.9829189 * frac(dot(pixelCoords, float2(0.06711056, 0.00583715))));

                float2 blueNoiseUV = frac(pixelCoords * _BlueNoiseTex_TexelSize.xy * max(_BlueNoiseScale, 0.01));
                float2 timeOffset = float2(0.75487766, 0.56984029) * frac(_Time.y * _BlueNoiseTimeSpeed);
                float blueNoise = SAMPLE_TEXTURE2D(_BlueNoiseTex, sampler_BlueNoiseTex, frac(blueNoiseUV + timeOffset)).r;

                return lerp(ignJitter, blueNoise, saturate(_BlueNoiseStrength));
            }

            #include "../RayMarchingLiquidVapour/RayMarching Includes/RayMarchVapour.hlsl"

            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.screenPos = ComputeScreenPos(OUT.positionHCS);
                return OUT;
            }

            half4 frag(Interpolators IN) : SV_Target
            {
                float3 cameraWS = _WorldSpaceCameraPos.xyz;
                float3 rayDir = normalize(IN.positionWS - cameraWS);
                float2 volumeIntersection = RayBoxDst(cameraWS, rayDir, _PhysicsBoundsMinWS.xyz, _PhysicsBoundsMaxWS.xyz);
                float distanceToVolume = volumeIntersection.x;
                float distanceInsideVolume = volumeIntersection.y;

                if (distanceInsideVolume <= 1e-5)
                    return half4(0, 0, 0, 0);

                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;
                float3 cameraForwardWS = -UNITY_MATRIX_V[2].xyz;
                float viewDepthDenominator = max(dot(rayDir, cameraForwardWS), 1e-4);
                float rawDepth = SampleSceneDepth(screenUV);
                float sceneLinearDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float sceneDistanceAlongRay = sceneLinearDepth / viewDepthDenominator;

                if (sceneDistanceAlongRay <= distanceToVolume)
                    discard;

                float safeStepSize = max(_StepSize, 1e-4);
                float currentDistance = distanceToVolume + safeStepSize * SampleVapourBlueNoise(screenUV);
                float exitDistance = min(distanceToVolume + distanceInsideVolume, sceneDistanceAlongRay);

                float3 accumulatedScatteredLight = 0.0;
                float3 remainingViewTransmittance = 1.0;
                float3 mainLightDirectionWS = normalize(_MainLightPosition.xyz);

                #if defined(_ADDITIONAL_LIGHTS)
                    InputData inputData = (InputData)0;
                    inputData.normalizedScreenSpaceUV = screenUV;
                    uint additionalLightsCount = (uint)GetAdditionalLightsCount();
                #endif

                while (currentDistance < exitDistance)
                {
                    float3 samplePositionWS = cameraWS + rayDir * currentDistance;
                    currentDistance += safeStepSize;

                    float rawVapourDensity = SampleRawVapourDensityWS(samplePositionWS);
                    float vapourDensity = (rawVapourDensity > 1e-6) ? BuildVapourDensityWS(samplePositionWS, rawVapourDensity) : 0.0;

                    if (vapourDensity < 1e-6)
                        continue;

                    half shadowAtten = 1.0;
                    #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                        float4 shadowCoord = TransformWorldToShadowCoord(samplePositionWS);
                        shadowAtten = MainLightRealtimeShadow(shadowCoord);
                    #endif

                    float3 vapourScatter = EvaluateVapourDirectScatter(
                        vapourDensity,
                        safeStepSize,
                        shadowAtten,
                        _MainLightColor.rgb,
                        rayDir,
                        mainLightDirectionWS
                    );

                    #if defined(_ADDITIONAL_LIGHTS)
                        inputData.positionWS = samplePositionWS;
                        LIGHT_LOOP_BEGIN(additionalLightsCount)
                            Light additionalLight = GetAdditionalLight(lightIndex, samplePositionWS);
                            half3 radiance = additionalLight.color * additionalLight.distanceAttenuation;
                            vapourScatter += EvaluateVapourDirectScatter(
                                vapourDensity,
                                safeStepSize,
                                1.0,
                                radiance,
                                rayDir,
                                additionalLight.direction
                            );
                        LIGHT_LOOP_END
                    #endif

                    accumulatedScatteredLight += vapourScatter * remainingViewTransmittance;
                    remainingViewTransmittance *= exp(-EvaluateSimpleVapourExtinction(vapourDensity) * safeStepSize);

                    if (max(remainingViewTransmittance.r, max(remainingViewTransmittance.g, remainingViewTransmittance.b)) < 0.01)
                        break;
                }

                float vapourAlpha = saturate(1.0 - remainingViewTransmittance.r);
                if (vapourAlpha <= 1e-5)
                    return half4(0, 0, 0, 0);

                // WaterRaymarching composites volumetric vapour over the scene inside
                // an opaque pass. This vapour-only shader is transparent, so output an
                // un-premultiplied colour that produces the same accumulated-scatter +
                // background-transmittance result under SrcAlpha/OneMinusSrcAlpha.
                float3 unpremultipliedVapour = accumulatedScatteredLight / max(vapourAlpha, 1e-4);
                return half4(unpremultipliedVapour, vapourAlpha);
            }

            ENDHLSL
        }
    }
}
