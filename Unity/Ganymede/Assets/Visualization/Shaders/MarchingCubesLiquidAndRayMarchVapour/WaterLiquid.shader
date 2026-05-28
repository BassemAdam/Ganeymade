Shader "Custom/WaterLiquid"
{
    Properties
    {
        _WaterColor("Water Color", Color) = (0.1, 0.4, 0.6, 1.0)
        _FresnelPower("Fresnel Power", Range(1.0, 10.0)) = 5.0
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.9
        _SpecularStrength("Specular Strength", Range(0.0, 5.0)) = 1.5
        _ReflectionStrength("Reflection Strength", Range(0.0, 1.0)) = 0.5
        _RefractionStrength("Refraction Strength", Range(0.0, 0.3)) = 0.05
        _BlurRadius("Blur Radius", Range(0.0, 0.08)) = 0.03
        _MinAlpha("Min Alpha", Range(0.0, 1.0)) = 1

        [Header(Depth Absorption)]
        _ShallowColor("Shallow Water Color", Color) = (0.3, 0.8, 0.8, 1.0)
        _DeepColor("Deep Water Color", Color) = (0.02, 0.05, 0.15, 1.0)
        _AbsorptionRate("Absorption Rate", Range(0.1, 5.0)) = 1.0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue"="Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "WaterShading"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "WaterPhase/WaterHelpers.hlsl"

            // ---- Mesh input (Procedural path) ----
            struct MeshInput
            {
                uint vertexID : SV_VertexID;
            };

            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS  : TEXCOORD2;
                float4 screenPos : TEXCOORD3;
            };

            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_WaterThicknessMap);
            SAMPLER(sampler_WaterThicknessMap);

            // Marching-cubes procedural vertex stream.
            // Matches Vertex struct in MarchingCubesCompute.compute (float4+float4 = 32 bytes).
            // Triangle = 3 vertices = 96 bytes. DrawProceduralIndirect uses SV_VertexID to index.
            struct MCVertex
            {
                float4 position;   // xyz = world position, w = 1
                float4 normal;     // xyz = normal, w = 0
            };
            StructuredBuffer<MCVertex> _MCVertices;

            CBUFFER_START(UnityPerMaterial)
                half4 _WaterColor;
                half _FresnelPower;
                half _Smoothness;
                half _SpecularStrength;
                half _RefractionStrength;
                half _BlurRadius;
                half _MinAlpha;
                half _ReflectionStrength;
                half4 _ShallowColor;
                half4 _DeepColor;
                half _AbsorptionRate;
            CBUFFER_END

            // ---- Vertex shader ----
            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;

                // Procedural: read position/normal from the vertex buffer
                MCVertex v = _MCVertices[IN.vertexID];
                OUT.positionWS = v.position.xyz;
                OUT.normalWS   = normalize(v.normal.xyz);
                OUT.uv = float2(0.0, 0.0);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.screenPos = ComputeScreenPos(OUT.positionHCS);

                return OUT;
            }

            // ---- Fragment shader ----
            half4 frag(Interpolators IN, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                IN.normalWS = isFrontFace ? IN.normalWS : -IN.normalWS;

                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;

                float waterDepth = SAMPLE_TEXTURE2D(_WaterThicknessMap, sampler_WaterThicknessMap, screenUV).r;
                waterDepth = max(waterDepth, 0.0); // Clamp to prevent negative depths exploding the Math

                float fresnel = CalculateFresnel(IN.normalWS, IN.positionWS, _FresnelPower);

                Light mainLight = GetMainLight();
                float spec = CalculateSpecular(IN.normalWS, mainLight.direction, IN.positionWS, _Smoothness, _SpecularStrength);
                half3 specColor = spec * mainLight.color;

                half3 refraction = CalculateRefraction(IN.normalWS, IN.screenPos, _RefractionStrength, 1.0 - fresnel, _BlurRadius);
                half3 reflection = CalculateReflection(IN.normalWS, IN.positionWS, _Smoothness, IN.screenPos) * _ReflectionStrength;

                half3 depthTintedColor = CalculateDepthColor(refraction, _ShallowColor.rgb, _DeepColor.rgb, waterDepth, _AbsorptionRate);

                half alpha = max(fresnel, _MinAlpha);
                half3 desiredColor = depthTintedColor * _WaterColor.rgb + specColor + reflection;

                return half4(desiredColor, alpha);
            }

            ENDHLSL
        }
    }
}
