Shader "Custom/WaterLiquid"
{
    Properties
    {
        _WaterColor("Water Color", Color) = (0.1, 0.4, 0.6, 1.0)
        // The power of the Fresnel effect, controlling how strongly it affects the water's appearance. Higher values will make the effect more pronounced, especially at glancing angles.
        _FresnelPower("Fresnel Power", Range(1.0, 10.0)) = 3.
        // specular parameters 
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.9
        _SpecularStrength("Specular Strength", Range(0.0, 5.0)) = 1.5
        // refraction parameters
        _RefractionStrength("Refraction Strength", Range(0.0, 0.1)) = 0.02

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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl" // gives us SampleSceneColor(screenUV)
           
            #include "Assets/Art/Shaders/Includes/WaterHelpers.hlsl"

            // used to getmainlight
             #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            
             // STRUCTS
            struct MeshInput
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;  
                float2 uv : TEXCOORD0;
            };

            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS  : TEXCOORD2;
                float4 screenPos : TEXCOORD3; // clip space → [0,1] screen UV via xy/w
            };


            // RESOURCES
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _WaterColor;
                half _FresnelPower;
                half _Smoothness;
                half _SpecularStrength;
                half _RefractionStrength;
            CBUFFER_END
            

            // VERTEX SHADER
            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.screenPos = ComputeScreenPos(OUT.positionHCS); // packages clip pos for perspective-correct screen UV
               
               return OUT; 
            }

            // Voxels water particles so that when there is physics engine to move them it will 
            // very realistic visually 
            // FRAGMENT SHADER
            half4 frag(Interpolators IN) : SV_Target
            {   
                float fresnel = CalculateFresnel(IN.normalWS, IN.positionWS, _FresnelPower);

                Light mainLight = GetMainLight();
                float spec = CalculateSpecular(IN.normalWS, mainLight.direction, IN.positionWS, _Smoothness, _SpecularStrength);
                half3 specColor = spec * mainLight.color;

                half3 refraction = CalculateRefraction(IN.normalWS, IN.screenPos, _RefractionStrength);
                half3 finalColor = refraction + specColor;
                return half4(finalColor, fresnel);
            }

            ENDHLSL
        }
    }
}
