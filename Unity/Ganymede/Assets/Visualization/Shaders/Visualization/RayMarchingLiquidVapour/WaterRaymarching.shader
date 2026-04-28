Shader "Custom/WaterRaymarching"
{
    Properties
    {
        [Header(Volume Rendering)]
        _StepSize ("Step Size", Range(0.001, 1.0)) = 0.05
        _BlueNoiseTex ("Blue Noise Texture", 2D) = "white" {}
        _IsoLevel ("Iso Level (surface threshold)", Float) = 0.01
        [Header(Liquid Phase)]
        _ScatteringCoefficients ("Liquid Extinction sigma_t (RGB)", Color) = (0.57, 0.06, 0.02, 1.0)
        _LiquidScatterColor ("Liquid Scatter Albedo (RGB)", Color) = (0.002, 0.004, 0.016, 1.0)
        _DensityMultiplier ("Liquid Density Multiplier", Float) = 1.0
        _DensityOffset ("Liquid Density Offset", Float) = 0.0
        _LightStepSize ("Liquid Shadow Step Size", Range(0.01, 2.0)) = 0.2
        [Header(Surface Optics)]
        _RefractionStrength ("Refraction Strength", Range(0.0, 0.5)) = 0.05
        _ReflectionStrength ("Reflection Strength", Range(0.0, 1.0)) = 0.5
        _SurfaceDetectionMargin ("Surface Detection Margin", Float) = 0.0
        [Header(Vapour Phase)]
        _VapourScatteringCoefficients ("Vapour Extinction sigma_t (RGB)", Color) = (0.05, 0.05, 0.05, 1.0)
        _VapourScatterColor ("Vapour Scatter Albedo (RGB)", Color) = (0.9, 0.9, 0.9, 1.0)
        _VapourDensityMultiplier ("Vapour Density Multiplier", Float) = 1.0
        _VapourPhaseG ("Vapour HG Anisotropy g", Range(-0.95, 0.95)) = 0.2
        _VapourLightStepSize ("Vapour Shadow Step Size", Range(0.01, 2.0)) = 0.4
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
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "RayMarching Includes/RayMarchGeometry.hlsl"
            #include "RayMarching Includes/RayMarchDensity.hlsl"
            #include "RayMarching Includes/RayMarchLighting.hlsl"
            #include "RayMarching Includes/RayMarchSurface.hlsl"

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
                float  _DensityMultiplier;           // volumetric scale only (not used for surface)
                float  _DensityOffset;               // volumetric bias only (not used for surface)
                float  _LightStepSize;
                // Surface optics
                float  _StepSize;
                float  _IsoLevel;
                float  _RefractionStrength;
                float  _ReflectionStrength;
                float  _SurfaceDetectionMargin;
                // Vapour volumetric
                float3 _VapourScatteringCoefficients; // sigma_t for vapour
                float3 _VapourScatterColor;
                float  _VapourDensityMultiplier;
                float  _VapourPhaseG;
                float  _VapourLightStepSize;
            CBUFFER_END

            TEXTURE2D(_BlueNoiseTex);
            SAMPLER(sampler_BlueNoiseTex);
            TEXTURE2D(_CameraOpaqueTexture);
            SAMPLER(sampler_CameraOpaqueTexture);

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

                float safeStepSize   = max(_StepSize, 1e-4);
                float currentDistance = volumeData.distanceToVolume + safeStepSize * viewData.blueNoiseValue;
                float exitDistance    = min(volumeData.volumeExitDistance, backgroundData.sceneDistanceAlongRay);

                // Cosine between view ray and sun — used for vapour HG phase function.
                float cosViewSun = dot(viewData.viewRayDirectionWS, normalize(_MainLightPosition.xyz));
                // Shadow step size: use the coarser of liquid/vapour settings for the combined march.
                float shadowStep = max(_LightStepSize, _VapourLightStepSize);

                while (currentDistance < exitDistance)
                {
                    float3 samplePositionWS = viewData.cameraPositionWS + viewData.viewRayDirectionWS * currentDistance;
                    currentDistance += safeStepSize;

                    // Scale each channel independently; offset applies to liquid only.
                    float2 raw = SampleDensityRG_WS(samplePositionWS);
                    float  dl  = max(raw.x * _DensityMultiplier + _DensityOffset, 0.0); // liquid
                    float  dv  = max(raw.y * _VapourDensityMultiplier,            0.0); // vapour

                    // Skip empty steps without paying for a shadow march.
                    if (dl + dv < 1e-6)
                        continue;

                    // Combined extinction for this step (σ_E = σ_t_liquid·dl + σ_t_vapour·dv).
                    float3 sigmaE = _ScatteringCoefficients * dl + _VapourScatteringCoefficients * dv;

                    // One combined shadow march — physically correct because both phases
                    // share the same sun ray path and the integral is additive.
                    float3 sunTransmittance = CalculateTransmittedSunLightRG(
                        samplePositionWS,
                        _ScatteringCoefficients,
                        _VapourScatteringCoefficients,
                        shadowStep
                    );

                    // God rays: query Unity's shadow map for external geometry occlusion.
                    #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                        float4 shadowCoord = TransformWorldToShadowCoord(samplePositionWS);
                        half shadowAtten = MainLightRealtimeShadow(shadowCoord);
                    #else
                        half shadowAtten = 1.0;
                    #endif

                    // In-scatter: liquid (isotropic) + vapour (HG anisotropic), one accumulation.
                    float  hgPhase   = HenyeyGreenstein(cosViewSun, _VapourPhaseG);
                    float3 inScatter = _MainLightColor.rgb
                        * sunTransmittance
                        * shadowAtten
                        * (_LiquidScatterColor * dl + _VapourScatterColor * (dv * hgPhase))
                        * safeStepSize;

                    accumulatedScatteredLight  += inScatter * remainingViewTransmittance;
                    remainingViewTransmittance *= exp(-sigmaE * safeStepSize);

                    if (max(remainingViewTransmittance.r, max(remainingViewTransmittance.g, remainingViewTransmittance.b)) < 0.01)
                        break;
                }

                float3 backgroundColor = ComposeWaterBackgroundColor(
                    backgroundData,
                    viewData.screenUV,
                    _ReflectionStrength
                );

                float3 finalColor = accumulatedScatteredLight
                                  + backgroundColor * remainingViewTransmittance;

                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
}
