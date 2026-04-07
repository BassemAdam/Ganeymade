Shader "Custom/ViewThicknessMap"
{
   SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+100" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 screenPos : TEXCOORD0;
            };

            // Declare our global texture!
            TEXTURE2D(_WaterThicknessMap);
            SAMPLER(sampler_WaterThicknessMap);

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.screenPos = ComputeScreenPos(OUT.positionHCS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;
                
                // Read the thickness data
                float thickness = SAMPLE_TEXTURE2D(_WaterThicknessMap, sampler_WaterThicknessMap, screenUV).r;

                // The thickness is in real-world meters. 
                // A monitor can only show colors from 0.0 (black) to 1.0 (white).
                // We divide by 10.0 here so that 10 meters of water = solid white, and 5 meters = gray.
                // You can tweak this number to make the map easier to see.
                float visual = thickness / 10.0;

                return half4(visual, visual, visual, 1.0);
            }
            ENDHLSL
        }
    }
}