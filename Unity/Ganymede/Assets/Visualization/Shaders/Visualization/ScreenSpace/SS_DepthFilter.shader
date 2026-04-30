// =============================================================================
//  Screen-Space Fluid Rendering — Narrow-Range Depth Filter
//
//  Iterative bilateral-style filter on the eye-space depth buffer produced by
//  SS_ParticleDepth. Based on Truong & Yuksel (HPG 2018) — "Narrow-Range Filter
//  for Screen-Space Fluid Rendering". The "narrow range" idea: clamp neighbour
//  depth contributions to a small band [centre - mu, centre + mu] before
//  applying the spatial Gaussian weight. This preserves silhouettes and thin
//  features that simple bilateral filters over-smooth.
//
//  Run for N iterations (ping-pong) before normals are reconstructed.
//
//  Inputs:
//      _SourceDepth   : R32F / RHalf  -- linear eye depth (positive metres)
//      _FilterRadius  : pixels (radius of separable filter taps each side)
//      _DepthSigma    : metres        -- narrow-range half-width (mu)
//      _SpatialSigma  : pixels
//      _DepthFilterDirection : (1,0) horizontal pass, (0,1) vertical pass
// =============================================================================
Shader "Hidden/ScreenSpace/SS_DepthFilter"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "NarrowRangeDepthFilter"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_SourceDepth);
            SAMPLER(sampler_SourceDepth);

            float4 _SourceDepth_TexelSize; // (1/w, 1/h, w, h)
            float2 _DepthFilterDirection;
            float  _DepthSigma;            // narrow-range half-width (metres)
            float  _SpatialSigma;          // gaussian sigma (pixels)
            int    _FilterRadius;          // tap count each side

            half Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                float centerDepth = SAMPLE_TEXTURE2D(_SourceDepth, sampler_SourceDepth, uv).r;

                // Background pixel: no fluid here — pass through (typically 0 = no-hit).
                if (centerDepth <= 0.0)
                    return 0.0;

                float invSpatial2 = 1.0 / max(_SpatialSigma * _SpatialSigma * 2.0, 1e-6);
                float invDepth2   = 1.0 / max(_DepthSigma   * _DepthSigma   * 2.0, 1e-6);

                float weightSum = 1.0;
                float depthSum  = centerDepth;

                int radius = max(_FilterRadius, 1);

                [loop]
                for (int t = 1; t <= radius; ++t)
                {
                    float2 offset = _DepthFilterDirection * _SourceDepth_TexelSize.xy * (float)t;
                    float dPos = SAMPLE_TEXTURE2D(_SourceDepth, sampler_SourceDepth, uv + offset).r;
                    float dNeg = SAMPLE_TEXTURE2D(_SourceDepth, sampler_SourceDepth, uv - offset).r;

                    float spatialW = exp(-(float)(t * t) * invSpatial2);

                    // Narrow-range: clamp neighbour depth to the centre +/- mu band.
                    // This is the key stability change vs. plain bilateral.
                    float dPosClamped = clamp(dPos, centerDepth - _DepthSigma, centerDepth + _DepthSigma);
                    float dNegClamped = clamp(dNeg, centerDepth - _DepthSigma, centerDepth + _DepthSigma);

                    // Reject background (0) samples entirely.
                    float vPos = (dPos > 0.0) ? 1.0 : 0.0;
                    float vNeg = (dNeg > 0.0) ? 1.0 : 0.0;

                    // Depth weight on the difference between *clamped* sample and centre.
                    float wPos = spatialW * exp(-((dPosClamped - centerDepth) * (dPosClamped - centerDepth)) * invDepth2) * vPos;
                    float wNeg = spatialW * exp(-((dNegClamped - centerDepth) * (dNegClamped - centerDepth)) * invDepth2) * vNeg;

                    depthSum  += dPosClamped * wPos + dNegClamped * wNeg;
                    weightSum += wPos + wNeg;
                }

                return (half)(depthSum / weightSum);
            }
            ENDHLSL
        }
    }
}
