Shader "Custom/ScreenSpaceFluidComposite"
{
    // Pass 4: Final compositing shader.
    // Reconstructs normals from smoothed depth, applies Fresnel reflection/refraction,
    // Beer's law absorption using thickness, and specular highlights.
    Properties
    {
        [Header(Water Color)]
        _WaterColor ("Water Tint", Color) = (0.1, 0.4, 0.7, 1)
        _AbsorptionCoeff ("Absorption Coefficient", Range(0.1, 20.0)) = 5.0
        _SpecularPower ("Specular Power", Range(16, 1024)) = 256
        _FresnelBias ("Fresnel Bias (F0)", Range(0.0, 0.1)) = 0.02
        _RefractionStrength ("Refraction Strength", Range(0.0, 3.0)) = 1.5
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+200" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "SSF_Composite"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile _ _SSF_DEBUG

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            TEXTURE2D(_FilteredDepthTex);
            SAMPLER(sampler_FilteredDepthTex);
            TEXTURE2D(_ThicknessTex);
            SAMPLER(sampler_ThicknessTex);
            TEXTURECUBE(_SSF_EnvCube);
            SAMPLER(sampler_SSF_EnvCube);

            float4 _FilteredDepthTex_TexelSize;
            float4x4 _SSF_InvProjectionMatrix;
            float4x4 _SSF_ProjectionMatrix;
            float4x4 _SSF_InvViewMat;
            float4x4 _SSF_ViewMatrix;

            float4 _WaterColor;
            float _AbsorptionCoeff;
            float _SpecularPower;
            float _FresnelBias;
            float _RefractionStrength;

            struct VertexOutput
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            VertexOutput vert(uint vertexID : SV_VertexID)
            {
                VertexOutput o;
                o.uv = float2((vertexID << 1) & 2, vertexID & 2);
                o.positionCS = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                    o.uv.y = 1.0 - o.uv.y;
                #endif
                return o;
            }

            float3 ViewPosFromDepth(float2 uv, float linearDepth)
            {
                float4 ndc = float4(uv * 2.0 - 1.0, 0.0, 1.0);
                ndc.z = -_SSF_ProjectionMatrix[2].z + _SSF_ProjectionMatrix[2].w / linearDepth;
                float4 viewPos = mul(_SSF_InvProjectionMatrix, ndc);
                return viewPos.xyz / viewPos.w;
            }

            float SampleDepth(float2 uv)
            {
                return SAMPLE_TEXTURE2D(_FilteredDepthTex, sampler_FilteredDepthTex, uv).r;
            }

            float4 frag(VertexOutput i) : SV_Target
            {
                #ifdef _SSF_DEBUG
                    // Debug: visualize raw depth texture to verify intermediate passes
                    float rawDepth = SampleDepth(i.uv);
                    // If depth is at clear value (1e5), show red. Otherwise show green scaled.
                    if (rawDepth >= 1e4)
                        return float4(1, 0, 0, 1); // RED = no depth data
                    if (rawDepth <= 0.0)
                        return float4(0, 0, 1, 1); // BLUE = zero/negative depth
                    // GREEN = valid depth (brighter = closer)
                    float viz = saturate(1.0 - rawDepth / 50.0);
                    return float4(0, viz, 0, 1);
                #endif

                float depth = SampleDepth(i.uv);

                if (depth <= 0.0 || depth >= 1e4)
                    return float4(0, 0, 0, 0);

                float thickness = SAMPLE_TEXTURE2D(_ThicknessTex, sampler_ThicknessTex, i.uv).r;

                // Reconstruct view-space position
                float3 viewPos = ViewPosFromDepth(i.uv, depth);

                // Reconstruct normal from depth derivatives (central differences)
                float2 texel = _FilteredDepthTex_TexelSize.xy;
                float depthL = SampleDepth(i.uv - float2(texel.x, 0));
                float depthR = SampleDepth(i.uv + float2(texel.x, 0));
                float depthU = SampleDepth(i.uv - float2(0, texel.y));
                float depthD = SampleDepth(i.uv + float2(0, texel.y));

                float3 posL = ViewPosFromDepth(i.uv - float2(texel.x, 0), depthL);
                float3 posR = ViewPosFromDepth(i.uv + float2(texel.x, 0), depthR);
                float3 posU = ViewPosFromDepth(i.uv - float2(0, texel.y), depthU);
                float3 posD = ViewPosFromDepth(i.uv + float2(0, texel.y), depthD);

                // Use smallest delta for robustness at edges
                float3 ddx_pos = (abs(depthR - depth) < abs(depth - depthL)) ? (posR - viewPos) : (viewPos - posL);
                float3 ddy_pos = (abs(depthD - depth) < abs(depth - depthU)) ? (posD - viewPos) : (viewPos - posU);
                float3 normal = normalize(cross(ddy_pos, ddx_pos));

                // View direction (in view space, looking along -Z)
                float3 viewDir = normalize(viewPos);

                // Lighting in view space
                float3 worldNormal = mul((float3x3)_SSF_InvViewMat, normal);
                float3 worldViewDir = mul((float3x3)_SSF_InvViewMat, viewDir);

                // Main light
                Light mainLight = GetMainLight();
                float3 lightDirView = mul((float3x3)_SSF_ViewMatrix, mainLight.direction);
                float3 H = normalize(lightDirView - viewDir);
                float specular = pow(max(0, dot(H, normal)), _SpecularPower);
                float diffuse = max(0, dot(lightDirView, normal)) * 0.3;

                // Fresnel (Schlick)
                float cosTheta = max(0, dot(normal, -viewDir));
                float fresnel = _FresnelBias + (1.0 - _FresnelBias) * pow(1.0 - cosTheta, 5.0);

                // Beer's law transmittance
                float3 absorb = float3(1, 1, 1) - _WaterColor.rgb;
                float3 transmittance = exp(-_AbsorptionCoeff * thickness * absorb);

                // Refraction: offset UV by normal to sample background
                float2 refrUV = i.uv + normal.xy * texel * _RefractionStrength * min(thickness, 2.0);
                refrUV = clamp(refrUV, 0.001, 0.999);
                float3 bgColor = SampleSceneColor(refrUV);
                float3 refractionColor = bgColor * transmittance + _WaterColor.rgb * (1.0 - transmittance);

                // Reflection: cubemap or sky fallback
                float3 reflectDir = reflect(worldViewDir, worldNormal);
                float3 reflectionColor = SAMPLE_TEXTURECUBE_LOD(_SSF_EnvCube, sampler_SSF_EnvCube, reflectDir, 0).rgb;
                // If no cubemap, use a sky-ish fallback
                float3 skyFallback = lerp(float3(0.6, 0.8, 1.0), float3(0.1, 0.3, 0.8), saturate(reflectDir.y));
                reflectionColor = max(reflectionColor, skyFallback);

                // Composite
                float3 finalColor = lerp(refractionColor, reflectionColor, fresnel);
                finalColor += mainLight.color * specular * 0.5;
                finalColor += diffuse * _WaterColor.rgb * mainLight.color;

                // Alpha: opaque where fluid exists, blending at edges
                float alpha = saturate(thickness * 20.0 + 0.7);

                return float4(finalColor, alpha);
            }
            ENDHLSL
        }
    }
}
