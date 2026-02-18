Shader "Custom/WaterLiquid"
{
    Properties
    {

    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            HLSLPROGRAM

            // INCLUDEs & DEFINES
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Art/Shaders/Includes/WaterHelpers.hlsl"

            // Structs
            struct MeshInput
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // Resources

            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)

            CBUFFER_END
            
            // Vertex Shader
            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;

                return OUT;
            }

            // Fragment Shader
            half4 frag(Interpolators IN) : SV_Target
            {
             
                return color;
            }

            ENDHLSL
        }
    }
}
