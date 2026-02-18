Shader "Custom/WaterLiquid"
{
    Properties
    {
        // Depth Based Coloring
        _ShallowColor("Shallow Color", Color) = (0.20, 0.60, 0.70, 1.0)
        _DeepColor("Deep Color", Color) = (0.00, 0.08, 0.18, 1.0)
        _Alpha("Water Alpha", Range(0.0, 1.0)) = 0.75
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue"="Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            // PIPELINES STATES
            
            Blend SrcAlpha OneMinusSrcAlpha // src*A + dst*B Alpha Blending

            ZWrite Off //DONT WRITE TO DEPTH BUFFER


            HLSLPROGRAM
            
            // INCLUDEs & DEFINES
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Art/Shaders/Includes/WaterHelpers.hlsl"


            // STRUCTS
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


            // RESOURCES
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half _Alpha;
            CBUFFER_END
            

            // VERTEX SHADER
            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS);
                OUT.uv = IN.uv;
                return OUT;
            }


            // FRAGMENT SHADER
            half4 frag(Interpolators IN) : SV_Target
            {
                half4 color = half4(_ShallowColor.rgb, _Alpha);
                return color;
            }

            ENDHLSL
        }
    }
}
