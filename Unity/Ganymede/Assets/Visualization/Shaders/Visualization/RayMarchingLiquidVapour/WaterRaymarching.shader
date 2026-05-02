Shader "Custom/WaterRaymarching"
{
    Properties
    {
        [Header(Volume Rendering)]
        _StepSize ("Step Size", Range(0.001, 1.0)) = 0.05
        _BlueNoiseTex ("Blue Noise Texture", 2D) = "gray" {}
        _IsoLevel ("Iso Level (surface threshold)", Float) = 0.01
        [Header(Debug)]
        [Enum(Off, 0, Reflection Only, 1, Surface Normal, 2, Reflection Direction, 3, Reflection Weight, 4, Reflection Contribution, 5, Refraction Contribution, 6, Background Mix, 7, View Transmittance, 8, Glossy Environment Raw, 9, SpecCube Raw, 10)]
        _DebugViewMode ("Debug View", Float) = 0
        [Header(Liquid Phase)]
        _ScatteringCoefficients ("Liquid Extinction sigma_t (RGB)", Color) = (0.57, 0.06, 0.02, 1.0)
        _LiquidScatterColor ("Liquid Scatter Albedo (RGB)", Color) = (0.002, 0.004, 0.016, 1.0)
        _DensityMultiplier ("Liquid Density Multiplier", Float) = 1.0
        _DensityOffset ("Liquid Density Offset", Float) = 0.0
        _LightStepSize ("Liquid Shadow Step Size", Range(0.01, 2.0)) = 0.2
        [Header(Surface Optics)]
        _RefractionStrength ("Refraction Strength", Range(0.0, 1.0)) = 1.0
        _ReflectionStrength ("Reflection Strength", Range(0.0, 1.0)) = 1.0
        _ReflectionVisibilityBoost ("Reflection Visibility Boost", Range(0.0, 16.0)) = 1.0
        _ReflectionVisibilityFloor ("Reflection Visibility Floor", Range(0.0, 1.0)) = 0.0
        _SurfaceDetectionMargin ("Surface Detection Margin", Float) = 0.0
        _SurfaceRefineIterations ("Surface Refine Iterations", Range(0, 8)) = 4
        _NormalSampleRadiusVoxels ("Normal Sample Radius (Voxels)", Range(0.5, 6.0)) = 1.0
        _BakedNormalBlend ("Baked Normal Blend", Range(0.0, 1.0)) = 0.0
        _BoundaryNormalBlendDistance ("Boundary Normal Blend Distance", Range(0.0, 2.0)) = 0.3
        _BoundaryNormalUpBiasPower ("Boundary Up Bias Power", Range(1.0, 12.0)) = 5.0
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
        _BlueNoiseScale ("Blue Noise Tiling", Range(0.25, 8.0)) = 1.0
        _BlueNoiseStrength ("Blue Noise Jitter Strength", Range(0.0, 1.0)) = 1.0
        _BlueNoiseTimeSpeed ("Blue Noise Temporal Speed", Range(0.0, 4.0)) = 1.0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+100" "RenderPipeline"="UniversalPipeline" }

        ZWrite Off
        Cull Front
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "ForwardWaterRaymarch"
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "RayMarching Includes/RayMarchGeometry.hlsl"

            struct WaterRaymarchMeshInput
            {
                float4 positionOS : POSITION;
            };

            struct WaterRaymarchVaryings
            {
                float4 rasterPosition : SV_POSITION;
                float4 normalizedScreenPosition : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
                // Liquid volumetric
                float3 _ScatteringCoefficients;      // sigma_t for liquid (extinction = scatter + absorb)
                float3 _LiquidScatterColor;          // scatter albedo tint
                float  _DensityMultiplier;           // liquid density scale for volume, surface detection, and normals
                float  _DensityOffset;               // liquid density bias for volume, surface detection, and normals
                float  _LightStepSize;
                // Surface optics
                float  _StepSize;
                float  _IsoLevel;
                float  _DebugViewMode;
                float  _RefractionStrength;
                float  _ReflectionStrength;
                float  _ReflectionVisibilityBoost;
                float  _ReflectionVisibilityFloor;
                float  _SurfaceDetectionMargin;
                float  _SurfaceRefineIterations;
                float  _NormalSampleRadiusVoxels;
                float  _BakedNormalBlend;
                float  _BoundaryNormalBlendDistance;
                float  _BoundaryNormalUpBiasPower;
                // Vapour rendering — procedural shape with shadow modulation for god rays.
                half4  _VapourBaseColor;
                float  _VapourAbsorption;
                float  _VapourGodRayStrength;
                float  _VapourShadowFloor;
                float  _VapourScatterG;
                float  _VapourBackscatter;
                float  _VapourPresenceThreshold;
                float  _VapourFullDensity;
                float  _VapourDensityMultiplier;
                // Vapour procedural structure — same defaults/formula family as Custom/VapourVolume.
                float  _NoiseScale;
                float4 _NoiseDriftDir;
                float  _NoiseDriftSpeed;
                int    _NoiseOctaves;
                float  _DensityPower;
                float  _VapourWarpStrength;
                float  _VapourErosionScale;
                float  _VapourErosionStrength;
                float  _VapourCutoff;
                float  _VapourSoftness;
                float  _VapourVerticalStretch;
                float  _VapourHeightDissipation;
                float  _EdgeSoftness;
                float  _BlueNoiseScale;
                float  _BlueNoiseStrength;
                float  _BlueNoiseTimeSpeed;
            CBUFFER_END

            TEXTURE2D(_BlueNoiseTex);
            SAMPLER(sampler_BlueNoiseTex);
            float4 _BlueNoiseTex_TexelSize;
            TEXTURE2D(_CameraOpaqueTexture);
            SAMPLER(sampler_CameraOpaqueTexture);

            #include "RayMarching Includes/RayMarchDensity.hlsl"
            #include "RayMarching Includes/RayMarchVapour.hlsl"
            #include "RayMarching Includes/RayMarchLighting.hlsl"
            #include "RayMarching Includes/RayMarchSurface.hlsl"
            #include "RayMarching Includes/WaterRaymarchView.hlsl"
            #include "RayMarching Includes/WaterRaymarchVolume.hlsl"
            #include "RayMarching Includes/WaterRaymarchBackground.hlsl"

            WaterRaymarchVaryings vert(WaterRaymarchMeshInput IN)
            {
                WaterRaymarchVaryings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.rasterPosition = TransformWorldToHClip(OUT.positionWS);
                OUT.normalizedScreenPosition = ComputeScreenPos(OUT.rasterPosition);
                return OUT;
            }

            half4 frag(WaterRaymarchVaryings IN) : SV_Target
            {
                WaterRaymarchViewData viewData = BuildWaterRaymarchViewData(IN.positionWS, IN.normalizedScreenPosition);
                WaterRaymarchVolumeData volumeData = BuildWaterRaymarchVolumeData(
                    viewData.cameraPositionWS,
                    viewData.viewRayDirectionWS
                );

                if (!volumeData.intersectsVolume)
                    return half4(0, 0, 0, 0);

                WaterRaymarchBackgroundData backgroundData = BuildWaterRaymarchBackgroundData(
                    viewData,
                    volumeData,
                    _StepSize,
                    _IsoLevel,
                    _SurfaceDetectionMargin,
                    _RefractionStrength
                );

                if (backgroundData.sceneDistanceAlongRay <= volumeData.distanceToVolume)
                    discard;

                float3 accumulatedScatteredLight  = 0.0;
                float3 remainingViewTransmittance = 1.0;
                float3 surfaceViewTransmittance   = 1.0;

                float safeStepSize   = max(_StepSize, 1e-4);
                float currentDistance = volumeData.distanceToVolume + safeStepSize * viewData.blueNoiseValue;
                float exitDistance    = min(volumeData.volumeExitDistance, backgroundData.sceneDistanceAlongRay);
                float surfaceDistanceAlongRay = backgroundData.surfaceHit.hit
                    ? distance(viewData.cameraPositionWS, backgroundData.surfaceHit.posWS)
                    : exitDistance;

                #if defined(_ADDITIONAL_LIGHTS)
                    InputData inputData = (InputData)0;
                    inputData.normalizedScreenSpaceUV = viewData.screenUV;
                    uint additionalLightsCount = (uint)GetAdditionalLightsCount();
                #endif

                while (currentDistance < exitDistance)
                {
                    float stepStartDistance = currentDistance;
                    float stepLength = min(safeStepSize, exitDistance - stepStartDistance);
                    float3 samplePositionWS = viewData.cameraPositionWS + viewData.viewRayDirectionWS * stepStartDistance;
                    currentDistance += stepLength;

                    // One raw grid sample gives us both phases:
                    //   R / phase 0 = liquid
                    //   G / phase 1 = vapour
                    // Vapour procedural shaping is skipped when the raw vapour
                    // channel is empty, so liquid-only steps avoid FBM work.
                    float2 rawDensity = SampleDensityRG_WS(samplePositionWS);
                    float  dl = AdjustLiquidDensity(rawDensity.x);
                    float  dv = (rawDensity.y > 1e-6) ? BuildVapourDensityWS(samplePositionWS, rawDensity.y) : 0.0;

                    // Skip empty steps without paying for a shadow march.
                    if (dl + dv < 1e-6)
                        continue;

                    float3 sigmaE = _ScatteringCoefficients * dl + EvaluateSimpleVapourExtinction(dv);

                    // Only liquid samples need the expensive liquid self-shadow
                    // march. Vapour-only samples skip this completely.
                    float3 sunTransmittance = 1.0;
                    if (dl > 1e-6)
                    {
                        sunTransmittance = CalculateTransmittedSunLightLiquid(
                            samplePositionWS,
                            _ScatteringCoefficients,
                            _LightStepSize
                        );
                    }

                    // Main-light shadowing drives both liquid shadows and vapour
                    // god rays. Vapour applies a visibility floor in
                    // EvaluateVapourDirectScatter so shadowed cells do not go black.
                    half shadowAtten = 1.0;
                    if (dl + dv > 1e-6)
                    {
                        #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                            float4 shadowCoord = TransformWorldToShadowCoord(samplePositionWS);
                            shadowAtten = MainLightRealtimeShadow(shadowCoord);
                        #endif
                    }

                    float3 liquidScatter = _MainLightColor.rgb
                        * sunTransmittance
                        * shadowAtten
                        * _LiquidScatterColor
                        * dl
                        * safeStepSize;
                    float3 vapourScatter = EvaluateVapourDirectScatter(
                        dv,
                        safeStepSize,
                        shadowAtten,
                        _MainLightColor.rgb,
                        viewData.viewRayDirectionWS,
                        normalize(_MainLightPosition.xyz)
                    );

                    #if defined(_ADDITIONAL_LIGHTS)
                        float3 vapourScatterAdditional = 0.0;
                        if (dv > 1e-6)
                        {
                            inputData.positionWS = samplePositionWS;
                            LIGHT_LOOP_BEGIN(additionalLightsCount)
                                Light additionalLight = GetAdditionalLight(lightIndex, samplePositionWS);
                                half3 radiance = additionalLight.color * additionalLight.distanceAttenuation;
                                vapourScatterAdditional += EvaluateVapourDirectScatter(
                                    dv,
                                    safeStepSize,
                                    1.0,
                                    radiance,
                                    viewData.viewRayDirectionWS,
                                    additionalLight.direction
                                );
                            LIGHT_LOOP_END
                        }
                        vapourScatter += vapourScatterAdditional;
                    #endif

                    float3 inScatter = liquidScatter + vapourScatter;
                    float3 stepTransmittance = exp(-sigmaE * stepLength);

                    accumulatedScatteredLight  += inScatter * remainingViewTransmittance;
                    remainingViewTransmittance *= stepTransmittance;

                    float surfaceStepLength = max(
                        0.0,
                        min(stepStartDistance + stepLength, surfaceDistanceAlongRay) - stepStartDistance
                    );
                    if (surfaceStepLength > 1e-6)
                        surfaceViewTransmittance *= exp(-sigmaE * surfaceStepLength);

                    if (max(remainingViewTransmittance.r, max(remainingViewTransmittance.g, remainingViewTransmittance.b)) < 0.01)
                        break;
                }

                if (_DebugViewMode > 0.5)
                {
                    float3 reflectionDebugColor = ComposeWaterDebugColor(
                        backgroundData,
                        viewData.screenUV,
                        _DebugViewMode,
                        _ScatteringCoefficients,
                        _ReflectionStrength,
                        _ReflectionVisibilityBoost,
                        _ReflectionVisibilityFloor,
                        surfaceViewTransmittance,
                        remainingViewTransmittance
                    );
                    return half4(reflectionDebugColor, 1.0);
                }

                float3 backgroundColor = ComposeWaterBackgroundColor(
                    backgroundData,
                    viewData.screenUV,
                    _ScatteringCoefficients,
                    _ReflectionStrength,
                    _ReflectionVisibilityBoost,
                    _ReflectionVisibilityFloor,
                    surfaceViewTransmittance,
                    remainingViewTransmittance
                );

                float3 finalColor = accumulatedScatteredLight + backgroundColor;

                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }

    CustomEditor "WaterRaymarchingShaderGUI"
}
