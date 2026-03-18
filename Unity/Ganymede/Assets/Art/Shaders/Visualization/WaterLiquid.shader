Shader "Custom/WaterLiquid"
{
    Properties
    {
        _WaterColor("Water Color", Color) = (0.1, 0.4, 0.6, 1.0)
        // The power of the Fresnel effect, controlling how strongly it affects the water's appearance. Higher values will make the effect more pronounced, especially at glancing angles.
        _FresnelPower("Fresnel Power", Range(1.0, 10.0)) = 5.0
        // specular parameters 
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.9
        _SpecularStrength("Specular Strength", Range(0.0, 5.0)) = 1.5
        // reflection parameters
        _ReflectionStrength("Reflection Strength", Range(0.0, 1.0)) = 0.5

        // refraction parameters
        _RefractionStrength("Refraction Strength", Range(0.0, 0.3)) = 0.05
        _BlurRadius("Blur Radius", Range(0.0, 0.08)) = 0.03

        _MinAlpha("Min Alpha", Range(0.0, 1.0)) = 1  // ensures cubes always contribute opacity for accumulation

        // depth-based color parameters
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
            // PIPELINES STATES
            
            Blend SrcAlpha OneMinusSrcAlpha // src*A + dst*B Alpha Blending 

            //Now i write to depth buffer,
            // but i will still be sorted in the transparent queue, 
            //so it will be drawn after opaque objects and before other transparent objects.
            // This allows for correct depth testing against opaque geometry 
            //while still allowing for proper blending with other transparent objects.
            // for blending with opaque object i handle this in refraction 
            ZWrite On 
            Cull Off

            HLSLPROGRAM

            // INCLUDEs & DEFINES
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"          
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl" // gives us SampleSceneColor(screenUV)
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Assets/Art/Shaders/Includes/WaterHelpers.hlsl"
            
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
                float4 screenPos : TEXCOORD3; 
            };


            // RESOURCES
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_WaterThicknessMap);
            SAMPLER(sampler_WaterThicknessMap);

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
            

            // VERTEX SHADER
            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.screenPos = ComputeScreenPos(OUT.positionHCS); 
               
               return OUT; 
            }


            // FRAGMENT SHADER
            half4 frag(Interpolators IN) : SV_Target
            {   
                // Screen UV for sampling screen-space textures
                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;

                // Sample the water thickness at this pixel (in meters)
                float waterDepth = SAMPLE_TEXTURE2D(_WaterThicknessMap, sampler_WaterThicknessMap, screenUV).r;

                // Fresnel 
                // for blending reflection and refraction based on view angle
                float fresnel = CalculateFresnel(IN.normalWS, IN.positionWS, _FresnelPower);

                // Specular and Light Color
                Light mainLight = GetMainLight();
                float spec = CalculateSpecular(IN.normalWS, mainLight.direction, IN.positionWS, _Smoothness, _SpecularStrength);
                half3 specColor = spec * mainLight.color;

                // reflection and refraction
                half3 refraction = CalculateRefraction(IN.normalWS, IN.screenPos, _RefractionStrength, 1.0 - fresnel, _BlurRadius); 
                half3 reflection = CalculateReflection(IN.normalWS, IN.positionWS, _Smoothness, IN.screenPos) * _ReflectionStrength;

                // Apply depth-based absorption (Beer's Law)
                half3 depthTintedColor = CalculateDepthColor(refraction, _ShallowColor.rgb, _DeepColor.rgb, waterDepth, _AbsorptionRate);

                half alpha = max(fresnel, _MinAlpha);

                half3 desiredColor = depthTintedColor * _WaterColor.rgb + specColor + reflection;

                return half4(desiredColor, alpha);
            }

            ENDHLSL
        }
    }
}
