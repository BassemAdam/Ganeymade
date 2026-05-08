Shader "Hidden/WaterProxyIntervalGen"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ProxyEntryDistance"
            Cull Back
            ZWrite Off
            ZTest Always
            Blend One One
            BlendOp Min

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct MeshInput
            {
                uint vertexID : SV_VertexID;
            };

            struct MCVertex
            {
                float4 position;
                float4 normal;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            StructuredBuffer<MCVertex> _MCVertices;

            Varyings vert(MeshInput IN)
            {
                Varyings OUT;
                MCVertex vertex = _MCVertices[IN.vertexID];
                OUT.positionWS = vertex.position.xyz;
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                return float4(distance(_WorldSpaceCameraPos.xyz, IN.positionWS), 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ProxyExitDistance"
            Cull Front
            ZWrite Off
            ZTest Always
            Blend One One
            BlendOp Max

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct MeshInput
            {
                uint vertexID : SV_VertexID;
            };

            struct MCVertex
            {
                float4 position;
                float4 normal;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            StructuredBuffer<MCVertex> _MCVertices;

            Varyings vert(MeshInput IN)
            {
                Varyings OUT;
                MCVertex vertex = _MCVertices[IN.vertexID];
                OUT.positionWS = vertex.position.xyz;
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                return float4(distance(_WorldSpaceCameraPos.xyz, IN.positionWS), 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }
    }
}