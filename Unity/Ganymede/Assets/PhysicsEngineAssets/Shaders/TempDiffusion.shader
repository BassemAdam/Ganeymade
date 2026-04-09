Shader "Custom/TempTextureShader_URP"
{
    Properties
    {
        _TempTex  ("Temperature Volume", 3D) = "black" {}
        _MaxTemp  ("Max Temperature",  Float) = 100.0
        _BaseColor("Base Color",       Color) = (0.2, 0.2, 0.2, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Geometry"
        }
        LOD 100

        // ── Forward pass ────────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_TempTex);
            SAMPLER(sampler_TempTex);

            CBUFFER_START(UnityPerMaterial)
                float  _MaxTemp;
                float4 _BaseColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 localPos    : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.localPos    = IN.positionOS.xyz + 0.5;
                return OUT;
            }

            float4 HeatColor(float t)
            {
                t = saturate(t);
                float4 col;
                col.a = 1.0;
                col.r = saturate(t * 3.0);
                col.g = saturate(t * 3.0 - 1.0);
                col.b = saturate(t * 3.0 - 2.0);
                return col;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float  temp       = SAMPLE_TEXTURE3D(_TempTex, sampler_TempTex, IN.localPos).r;
                float  normalized = saturate(temp / _MaxTemp);
                float4 heat       = HeatColor(normalized);
                return lerp(_BaseColor, heat, normalized);
            }
            ENDHLSL
        }

        // ── Shadow caster pass ──────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex   shadowVert
            #pragma fragment shadowFrag

            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // These are declared inside Shadows.hlsl for the bias calculation
            float3 _LightDirection;
            float3 _LightPosition;

            CBUFFER_START(UnityPerMaterial)
                float  _MaxTemp;
                float4 _BaseColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings shadowVert(Attributes IN)
            {
                Varyings OUT;
                float3 posWS    = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);

                #ifdef _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - posWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                OUT.positionHCS = TransformWorldToHClip(ApplyShadowBias(posWS, normalWS, lightDir));
                return OUT;
            }

            float4 shadowFrag(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    FallBack Off
}