Shader "Custom/WaterReflectionProbeExperiment"
{
    Properties
    {
        [Enum(Manual Cubemap Reflect, 0, Raw unity_SpecCube0, 1, URP Glossy Environment, 2, Manual Cubemap ViewDir, 3)]
        _DebugMode ("Debug Mode", Float) = 0
        [NoScaleOffset] _ManualCubemap ("Manual Cubemap", Cube) = "" {}
        _PerceptualRoughness ("Perceptual Roughness", Range(0.0, 1.0)) = 0.0
        _MipLevel ("Manual Cubemap Mip", Range(0.0, 7.0)) = 0.0
        _Tint ("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "Forward"
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 screenUV   : TEXCOORD2;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float  _DebugMode;
                float  _PerceptualRoughness;
                float  _MipLevel;
            CBUFFER_END

            TEXTURECUBE(_ManualCubemap);
            SAMPLER(sampler_ManualCubemap);

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalize(normalInputs.normalWS);

                float4 screenPos = ComputeScreenPos(output.positionCS);
                output.screenUV = screenPos.xy / max(screenPos.w, 1e-5);
                return output;
            }

            float3 SampleManualCubemap(float3 sampleDirWS)
            {
                return SAMPLE_TEXTURECUBE_LOD(
                    _ManualCubemap,
                    sampler_ManualCubemap,
                    normalize(sampleDirWS),
                    _MipLevel
                ).rgb;
            }

            float3 SampleRawSceneSpecCube(float3 reflectDirWS)
            {
                half4 encoded = SAMPLE_TEXTURECUBE_LOD(
                    unity_SpecCube0,
                    samplerunity_SpecCube0,
                    normalize(reflectDirWS),
                    0
                );
                return DecodeHDREnvironment(encoded, unity_SpecCube0_HDR);
            }

            float3 SampleGlossyEnvironment(float3 reflectDirWS, float3 positionWS, float2 screenUV)
            {
                return GlossyEnvironmentReflection(
                    normalize(reflectDirWS),
                    positionWS,
                    saturate(_PerceptualRoughness),
                    1.0h,
                    screenUV
                );
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                float3 viewDirWS = normalize(_WorldSpaceCameraPos.xyz - input.positionWS);
                float3 reflectDirWS = reflect(-viewDirWS, normalWS);

                int mode = (int)round(_DebugMode);
                float3 color = 0.0;

                if (mode == 0)
                {
                    color = SampleManualCubemap(reflectDirWS);
                }
                else if (mode == 1)
                {
                    color = SampleRawSceneSpecCube(reflectDirWS);
                }
                else if (mode == 2)
                {
                    color = SampleGlossyEnvironment(reflectDirWS, input.positionWS, input.screenUV);
                }
                else
                {
                    color = SampleManualCubemap(-viewDirWS);
                }

                return half4(color * _Tint.rgb, 1.0);
            }
            ENDHLSL
        }
    }
}
