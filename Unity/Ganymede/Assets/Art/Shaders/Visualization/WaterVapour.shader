Shader "Custom/WaterVapour"
{
    Properties
    {
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        // No depth write — vapor must not occlude objects behind it
        ZWrite Off

        // Standard alpha blending
        Blend SrcAlpha OneMinusSrcAlpha

        // No backface culling — vapor has no real surface orientation
        Cull Off

        Pass
        {
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct MeshInput
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1; // world-space position — drives noise & raymarching
                float3 viewDirWS  : TEXCOORD2; // world-space view direction — drives Fresnel & phase function
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _BaseMap_ST;
            CBUFFER_END

            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.uv          = TRANSFORM_TEX(IN.uv, _BaseMap);
                // GetWorldSpaceViewDir returns (cameraPos - positionWS), unnormalized
                // We normalize in the fragment shader where we need it per-pixel
                OUT.viewDirWS   = GetWorldSpaceViewDir(OUT.positionWS);
                return OUT;
            }

            half4 frag(Interpolators IN) : SV_Target
            {
                half4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;
                return color;
            }
            ENDHLSL
        }
    }
}
