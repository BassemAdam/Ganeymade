Shader "Hidden/SSF_NarrowRangeFilter"
{
    // Pass 3: Narrow-Range Filter for depth smoothing.
    // Gaussian blur with adaptive depth-rejection to preserve fluid edges
    // while merging nearby particles into a smooth surface.
    // Based on "A Narrow-Range Filter for Screen-Space Fluid Rendering" (Truong & Yuksel, 2018)
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "SSF_NarrowRangeFilter"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_DepthTex);
            SAMPLER(sampler_DepthTex);

            float4 _DepthTex_TexelSize; // (1/w, 1/h, w, h)
            float2 _BlurDir;            // (1,0) for horizontal, (0,1) for vertical
            float _FilterSize;          // max kernel radius in pixels
            float _ProjectedParticleSize; // particle projected size constant

            struct VertexOutput
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            VertexOutput vert(uint vertexID : SV_VertexID)
            {
                VertexOutput o;
                // Full-screen triangle
                o.uv = float2((vertexID << 1) & 2, vertexID & 2);
                o.positionCS = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                    o.uv.y = 1.0 - o.uv.y;
                #endif
                return o;
            }

            float4 frag(VertexOutput i) : SV_Target
            {
                float depth = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, i.uv).r;

                // Background / no particle
                if (depth <= 0.0 || depth >= 1e4)
                    return float4(depth, 0, 0, 1);

                // Adaptive filter size based on projected particle size
                int filterRadius = min((int)_FilterSize, (int)ceil(_ProjectedParticleSize / depth));
                filterRadius = max(filterRadius, 1);

                float sigma = (float)filterRadius * 0.5;
                float sigmaInv2 = 1.0 / (2.0 * sigma * sigma);

                // Narrow-range thresholds
                float mu = depth * 0.03; // 3% of depth
                float depthThreshold = depth * 0.1; // 10% of depth
                float higherBound = depth + mu;

                float sum = depth;
                float wsum = 1.0;

                float thresholdLow = depth - depthThreshold;
                float thresholdHigh = depth + depthThreshold;

                [loop]
                for (int r = 1; r <= filterRadius; r++)
                {
                    float gaussW = exp(-(float)(r * r) * sigmaInv2);

                    // Sample in both directions along blur axis
                    float2 offset = _BlurDir * _DepthTex_TexelSize.xy * (float)r;
                    float sampleA = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, i.uv - offset).r;
                    float sampleB = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, i.uv + offset).r;

                    float wA = gaussW;
                    float wB = gaussW;

                    // Narrow-range rejection for sample A
                    if (sampleA < thresholdLow || sampleA <= 0.0)
                    {
                        wA = 0.0;
                    }
                    else if (sampleA > thresholdHigh)
                    {
                        sampleA = higherBound;
                    }
                    else
                    {
                        thresholdLow = min(thresholdLow, sampleA - depthThreshold);
                        thresholdHigh = max(thresholdHigh, sampleA + depthThreshold);
                    }

                    // Narrow-range rejection for sample B
                    if (sampleB < thresholdLow || sampleB <= 0.0)
                    {
                        wB = 0.0;
                    }
                    else if (sampleB > thresholdHigh)
                    {
                        sampleB = higherBound;
                    }
                    else
                    {
                        thresholdLow = min(thresholdLow, sampleB - depthThreshold);
                        thresholdHigh = max(thresholdHigh, sampleB + depthThreshold);
                    }

                    sum += sampleA * wA + sampleB * wB;
                    wsum += wA + wB;
                }

                return float4(sum / wsum, 0, 0, 1);
            }
            ENDHLSL
        }
    }
}
