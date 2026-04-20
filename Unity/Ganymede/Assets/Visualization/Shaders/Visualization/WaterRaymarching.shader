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

                float2 screenUV = IN.positionHCS.xy / _ScaledScreenParams.xy;

                float rawDepth  = SampleSceneDepth(screenUV);
                float eyeDepth  = LinearEyeDepth(rawDepth, _ZBufferParams);
                float3 camFwdWS = -UNITY_MATRIX_V[2].xyz;
                float sceneDist = eyeDepth / dot(rayDirWS, camFwdWS);

                if (sceneDist <= dstToBox)
                    discard;

                float2 blueNoiseUV = frac(IN.positionHCS.xy / 1024.0);
                float blueNoise = SAMPLE_TEXTURE2D(_BlueNoiseTex, sampler_BlueNoiseTex, blueNoiseUV).r;

                float currentDistance = dstToBox + _StepSize * blueNoise;
                float exitDistance = min(dstToBox + dstInsideBox, sceneDist);

                float3 FinalLight = 0;
                float3 viewTransmittance = 1.0;

                float3 entryPosWS = _WorldSpaceCameraPos.xyz + rayDirWS * dstToBox;
                float entryDensity = SampleDensityWS(entryPosWS, _DensityOffset, _DensityMultiplier);

                bool cameraInsideBox = (dstToBox < 1e-5);
                SurfaceHit surfaceHit = NoSurfaceHit();
                if (!cameraInsideBox && entryDensity >= _IsoLevel)
                    surfaceHit = MakeSurfaceHit(entryPosWS, rayDirWS, true);

                float prevDensity = SampleDensityWS(_WorldSpaceCameraPos.xyz + rayDirWS * currentDistance, _DensityOffset, _DensityMultiplier);

                while (currentDistance < exitDistance)
                {
                    float3 samplePosWS = _WorldSpaceCameraPos.xyz + rayDirWS * currentDistance;
                    float density = SampleDensityWS(samplePosWS, _DensityOffset, _DensityMultiplier);

                    if (!surfaceHit.hit)
                    {
                        float surfaceThreshold = _IsoLevel + _SurfaceDetectionMargin;
                        bool airToWater = prevDensity < surfaceThreshold && density >= surfaceThreshold;
                        bool waterToAir = prevDensity >= surfaceThreshold && density < surfaceThreshold;
                        if (airToWater || waterToAir)
                            surfaceHit = MakeSurfaceHit(samplePosWS, rayDirWS, airToWater);
                    }

                    currentDistance += _StepSize;
                    prevDensity = density;
                    if (density <= 0) continue;

                    float3 sunTransmittance = CalculateTransmittedSunLight(samplePosWS, _ScatteringCoefficients, _DensityOffset, _DensityMultiplier, _LightStepSize);

                    float3 inScattered = _MainLightColor.rgb * sunTransmittance * _ScatterColor * density * _StepSize;
                    FinalLight += inScattered * viewTransmittance;
                    viewTransmittance *= exp(-_ScatteringCoefficients * density * _StepSize);

                    if (max(viewTransmittance.r, max(viewTransmittance.g, viewTransmittance.b)) < 0.01) break;
                }

                float3 backgroundColor;
                if (surfaceHit.hit)
                {
                    float3 reflColor    = GlossyEnvironmentReflection(surfaceHit.reflectDir, surfaceHit.posWS, 0.0h, 1.0h, screenUV);
                    float3 refractDirVS = mul((float3x3)UNITY_MATRIX_V, surfaceHit.refractDir);
                    float2 refractUV    = clamp(screenUV + refractDirVS.xy * _RefractionStrength, 0.001, 0.999);
                    float3 refrColor    = SAMPLE_TEXTURE2D(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, refractUV).rgb;
                    // Fresnel-Schlick blend: head-on → refraction, grazing → reflection.
                    backgroundColor = lerp(refrColor, reflColor, surfaceHit.fresnel * _ReflectionStrength);
                }
                else
                {
                    backgroundColor = SAMPLE_TEXTURE2D(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, screenUV).rgb;
                }

                FinalLight += backgroundColor * viewTransmittance;
                return half4(FinalLight, 1.0);
            }
            ENDHLSL
        }
    }
}
