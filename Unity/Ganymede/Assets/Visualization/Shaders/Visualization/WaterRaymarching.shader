Shader "Custom/WaterRaymarching"
{
    Properties
    {
        [Header(Volume Rendering)]
        _DensityOffset ("Density Offset", Float) = 0.0
        _DensityMultiplier ("Density Multiplier", Float) = 1.0
        _ScatteringCoefficients ("Extinction / Absorption (RGB)", Color) = (0.57, 0.06, 0.02, 1.0)
        _ScatterColor ("Scatter Color (RGB)", Color) = (0.002, 0.004, 0.016, 1.0)
        _StepSize ("Step Size", Range(0.001, 1.0)) = 0.05
        _LightStepSize ("Light Step Size", Range(0.001, 2.0)) = 0.2
        _BlueNoiseTex ("Blue Noise Texture", 2D) = "white" {}
        _IsoLevel ("Iso Level (surface threshold)", Float) = 0.01
        [Header(Surface Optics)]
        _RefractionStrength ("Refraction Strength", Range(0.0, 0.5)) = 0.05
        _ReflectionStrength ("Reflection Strength", Range(0.0, 1.0)) = 0.5
        _SurfaceDetectionMargin ("Surface Detection Margin", Float) = 0.0
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "../Includes/RayMarching/RayMarchGeometry.hlsl"
            #include "../Includes/RayMarching/RayMarchDensity.hlsl"
            #include "../Includes/RayMarching/RayMarchLighting.hlsl"
            #include "../Includes/RayMarching/RayMarchSurface.hlsl"

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
                float _DensityOffset;
                float _DensityMultiplier;
                float3 _ScatteringCoefficients;
                float _StepSize;
                float _LightStepSize;
                float _IsoLevel;
                float3 _ScatterColor;
                float _RefractionStrength;
                float _ReflectionStrength;
                float _SurfaceDetectionMargin;
            CBUFFER_END

            TEXTURE2D(_BlueNoiseTex);
            SAMPLER(sampler_BlueNoiseTex);
            TEXTURE2D(_CameraOpaqueTexture);
            SAMPLER(sampler_CameraOpaqueTexture);

            #include "../Includes/RayMarching/WaterRaymarchView.hlsl"
            #include "../Includes/RayMarching/WaterRaymarchVolume.hlsl"
            #include "../Includes/RayMarching/WaterRaymarchBackground.hlsl"

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
                    _DensityOffset,
                    _DensityMultiplier,
                    _IsoLevel,
                    _SurfaceDetectionMargin,
                    _RefractionStrength
                );

                if (backgroundData.sceneDistanceAlongRay <= volumeData.distanceToVolume)
                    discard;

                float3 accumulatedScatteredLight = 0.0;
                float3 remainingViewTransmittance = 1.0;

                float safeStepSize = max(_StepSize, 1e-4);
                float currentDistance = volumeData.distanceToVolume + safeStepSize * viewData.blueNoiseValue;
                float exitDistance = min(volumeData.volumeExitDistance, backgroundData.sceneDistanceAlongRay);

                while (currentDistance < exitDistance)
                {
                    float3 samplePositionWS = viewData.cameraPositionWS + viewData.viewRayDirectionWS * currentDistance;
                    float density = SampleDensityWS(samplePositionWS, _DensityOffset, _DensityMultiplier);

                    currentDistance += safeStepSize;
                    if (density <= 0.0)
                        continue;

                    float3 sunTransmittance = CalculateTransmittedSunLight(
                        samplePositionWS,
                        _ScatteringCoefficients,
                        _DensityOffset,
                        _DensityMultiplier,
                        _LightStepSize
                    );

                    float3 inScatteredLight = _MainLightColor.rgb * sunTransmittance * _ScatterColor * density * safeStepSize;
                    accumulatedScatteredLight += inScatteredLight * remainingViewTransmittance;
                    remainingViewTransmittance *= exp(-_ScatteringCoefficients * density * safeStepSize);

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
