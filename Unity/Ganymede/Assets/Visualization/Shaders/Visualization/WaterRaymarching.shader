Shader "Custom/WaterRaymarching"
{
    Properties
    {
        [Header(Volume Rendering)]
        _TintColor ("Tint Color", Color) = (0.20, 0.60, 1.00, 1.00)
        _DensityOffset ("Density Offset", Float) = 0.0
        _DensityMultiplier ("Density Multiplier", Float) = 1.0
        _Absorption ("Absorption", Range(0.0, 10.0)) = 2.0
        _StepSize ("Step Size", Range(0.001, 1.0)) = 0.05
        _MaxSteps ("Max Steps", Range(8, 2048)) = 256
        _Jitter ("Jitter", Range(0.0, 1.0)) = 1.0
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

            struct MeshData
            {
                float4 positionOS : POSITION;
            };

            struct Interpoalaters
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Texture3D<float> _PhysicsDensityGrid;
            SamplerState sampler_PhysicsDensityGrid;

            CBUFFER_START(UnityPerMaterial)
                half4 _TintColor;
                float _DensityOffset;
                float _DensityMultiplier;
                float _Absorption;
                float _StepSize;
                int _MaxSteps;
                float _Jitter;
            CBUFFER_END

            // Set by PhysicsWaterPhaseBridge via MaterialPropertyBlock.
            float4 _PhysicsVolumeDims;
            float4 _PhysicsBoundsMinWS;
            float4 _PhysicsBoundsMaxWS;

            Interpoalaters vert(MeshData IN)
            {
                Interpoalaters OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            // Modified to evaluate in World Space like the "perfect" shader
            float2 RayBoxDst(float3 rayOriginWS, float3 rayDirWS, float3 bminWS, float3 bmaxWS)
            {
                float3 invDir = 1.0 / max(abs(rayDirWS), 1e-6) * sign(rayDirWS);
                float3 t0 = (bminWS - rayOriginWS) * invDir;
                float3 t1 = (bmaxWS - rayOriginWS) * invDir;

                float3 tmin = min(t0, t1);
                float3 tmax = max(t0, t1);

                float dstA = max(max(tmin.x, tmin.y), tmin.z);
                float dstB = min(min(tmax.x, tmax.y), tmax.z);

                float dstToBox = max(0.0, dstA);
                float dstInsideBox = max(0.0, dstB - dstToBox);
                
                return float2(dstToBox, dstInsideBox);
            }

            float SampleDensityWS(float3 samplePosWS)
            {
                float3 sizeWS = max(_PhysicsBoundsMaxWS.xyz - _PhysicsBoundsMinWS.xyz, 1e-5);
                float3 gridSize = max(_PhysicsVolumeDims.xyz, 1.0);
                float3 uvw = (samplePosWS - _PhysicsBoundsMinWS.xyz) / sizeWS + 0.5 / gridSize;

                float rawDensity = _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw, 0).r;
                return rawDensity;
                
            }

            half4 frag(Interpoalaters IN) : SV_Target
            {
                float3 rayDirWS = normalize(IN.positionWS - _WorldSpaceCameraPos.xyz);


                float2 boundsDstInfo = RayBoxDst(_WorldSpaceCameraPos.xyz, rayDirWS, _PhysicsBoundsMinWS.xyz, _PhysicsBoundsMaxWS.xyz);
                float dstToBox = boundsDstInfo.x;
                float dstInsideBox = boundsDstInfo.y;



                if (dstInsideBox <= 1e-5)
                return half4(0, 0, 0, 0);

                float stepSize = max(_StepSize, 0.001);
                int maxIterations = min(_MaxSteps, 2048);

                // Simple pseudo-random hash for jitter based on screen position
                float2 scPos = IN.positionHCS.xy;
                float noise = frac(sin(dot(scPos, float2(12.9898, 78.233))) * 43758.5453);

                // Add jitter
                float t = dstToBox + stepSize * lerp(0.5, noise, _Jitter);
                float endT = dstToBox + dstInsideBox;

                float opticalDepth = 0.0;

                [loop]
                for (int i = 0; i < maxIterations; i++)
                {
                    if (t >= endT)
                        break;

                    float3 samplePosWS = _WorldSpaceCameraPos.xyz + rayDirWS * t;
                    float density = SampleDensityWS(samplePosWS) * _DensityMultiplier;

                    if (density > 0)
                        opticalDepth += density * stepSize;

                    t += stepSize;
                }

                float alpha = saturate(1.0 - exp(-_Absorption * opticalDepth));
                return half4(alpha, alpha, alpha, alpha);
            }
            ENDHLSL
        }
    }
}
