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
                float _Absorption;
                float _StepSize;
            CBUFFER_END

            TEXTURE2D(_BlueNoiseTex);
            SAMPLER(sampler_BlueNoiseTex);

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

                float stepSize = max(_StepSize, 0.001);

                float currentDistance = dstToBox + stepSize * blueNoise;
                float exitDistance = dstToBox + dstInsideBox;

                float opticalDepth = 0.0;

                // March from entry to exit using stepSize
                while (currentDistance < exitDistance)
                {
                    float3 samplePosWS = _WorldSpaceCameraPos.xyz + rayDirWS * currentDistance;
                    float density = SampleDensityWS(samplePosWS, _DensityOffset, _DensityMultiplier);

                    if (density > 0)
                        opticalDepth += density * stepSize;

                    currentDistance += stepSize;
                }

                float alpha = saturate(1.0 - exp(-_Absorption * opticalDepth));
                return half4(alpha, alpha, alpha, alpha);
            }
            ENDHLSL
        }
    }
}
