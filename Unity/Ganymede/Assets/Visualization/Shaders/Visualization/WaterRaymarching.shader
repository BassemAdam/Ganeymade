Shader "Custom/WaterRaymarching"
{
    Properties
    {
        [Header(Volume Rendering)]
        _TintColor ("Tint Color", Color) = (0.20, 0.60, 1.00, 1.00)
        _DensityOffset ("Density Offset", Float) = 0.0
        _DensityMultiplier ("Density Multiplier", Float) = 1.0
        _ScatteringCoefficients ("Scattering Coefficients (RGB = per-channel extinction)", Color) = (0.8, 0.2, 0.05, 1.0)
        _StepSize ("Step Size", Range(0.001, 1.0)) = 0.05
        _LightStepSize ("Light Step Size", Range(0.001, 2.0)) = 0.2
        _BlueNoiseTex ("Blue Noise Texture", 2D) = "white" {}
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
            #include "../Includes/RayMarching/RayMarchGeometry.hlsl"
            #include "../Includes/RayMarching/RayMarchDensity.hlsl"

            struct MeshData
            {
                float4 positionOS : POSITION;
            };

            struct Interpoalaters
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _TintColor;
                float _DensityOffset;
                float _DensityMultiplier;
                float3 _ScatteringCoefficients;
                float _StepSize;
                float _LightStepSize;
            CBUFFER_END

            TEXTURE2D(_BlueNoiseTex);
            SAMPLER(sampler_BlueNoiseTex);

            // Marches from posWS toward the sun and returns how much sunlight is transmitted (Beer-Lambert).
            float3 CalculateTransmittedSunLight(float3 posWS)
            {
                float3 sunDir = normalize(_MainLightPosition.xyz);

                float2 lightBoundsDst = RayBoxDst(posWS, sunDir, _PhysicsBoundsMinWS.xyz, _PhysicsBoundsMaxWS.xyz);
                float dstToSunExit = lightBoundsDst.x + lightBoundsDst.y;

                float lightOpticalDepth = 0.0;
                float distanceMarchedToLight = lightBoundsDst.x;

                while (distanceMarchedToLight < dstToSunExit)
                {
                    float3 lightSamplePosWS = posWS + sunDir * distanceMarchedToLight;
                    float density = SampleDensityWS(lightSamplePosWS, _DensityOffset, _DensityMultiplier);

                    if (density > 0)
                        lightOpticalDepth += density * _LightStepSize;

                    distanceMarchedToLight += _LightStepSize;
                }

                // Beer-Lambert per channel: how much sunlight survives the path through the volume to posWS
                return exp(-_ScatteringCoefficients * lightOpticalDepth);
            }

            Interpoalaters vert(MeshData IN)
            {
                Interpoalaters OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            half4 frag(Interpoalaters IN) : SV_Target
            {
                float3 rayDirWS = normalize(IN.positionWS - _WorldSpaceCameraPos.xyz);

                float2 boundsDstInfo = RayBoxDst(_WorldSpaceCameraPos.xyz, rayDirWS, _PhysicsBoundsMinWS.xyz, _PhysicsBoundsMaxWS.xyz);
                float dstToBox = boundsDstInfo.x;
                float dstInsideBox = boundsDstInfo.y;

                if (dstInsideBox <= 1e-5)
                return half4(0, 0, 0, 0);

                float2 blueNoiseUV = frac(IN.positionHCS.xy / 1024.0); 
                float blueNoise = SAMPLE_TEXTURE2D(_BlueNoiseTex, sampler_BlueNoiseTex, blueNoiseUV).r;


                float currentDistance = dstToBox + _StepSize * blueNoise;
                float exitDistance = dstToBox + dstInsideBox;

                float3 FinalLight = 0;
          float3 viewTransmittance = 1.0;

                while (currentDistance < exitDistance)
                {
                    float3 samplePosWS = _WorldSpaceCameraPos.xyz + rayDirWS * currentDistance;
                    float density = SampleDensityWS(samplePosWS, _DensityOffset, _DensityMultiplier);
                    currentDistance += _StepSize;
                    if (density <= 0) continue;

                    float3 sunTransmittance = CalculateTransmittedSunLight(samplePosWS);

                    float3 inScattered = _MainLightColor.rgb * sunTransmittance * _ScatteringCoefficients * density * _StepSize;
                    FinalLight += inScattered * viewTransmittance;

                    viewTransmittance *= exp(-_ScatteringCoefficients * density * _StepSize);

                    if (max(viewTransmittance.r, max(viewTransmittance.g, viewTransmittance.b)) < 0.01) break;
                }

                float alpha = 1.0 - dot(viewTransmittance, float3(0.333, 0.333, 0.333));
                return half4(FinalLight, alpha);
            }
            ENDHLSL
        }
    }
}
