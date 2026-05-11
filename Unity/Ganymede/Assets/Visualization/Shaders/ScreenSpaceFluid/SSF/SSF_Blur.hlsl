#ifndef SSF_BLUR_INCLUDED
#define SSF_BLUR_INCLUDED

// ============================================================
// Pass: ScreenSpaceFluidBlur  (Simon Green Step 2 — bilateral)
//
// Single-pass 2D bilateral filter. Samples a full (2r+1)x(2r+1)
// kernel around each pixel. No separability artefacts.
//
// Per-tap weight = spatial * range:
//   spatial = exp(-(tx^2 + ty^2) * blurScale^2)
//   range   = exp(-((sample - center) * invDepthSigma)^2)
//
// _BlurRadius      : kernel half-extent in pixels (1..8)
// _BlurSigma       : spatial sigma in pixels  (controls blurScale)
// _BlurDepthSigma  : eye-depth tolerance, RELATIVE to centre depth
//                    (sigmaZ = max(centerDepth * _BlurDepthSigma, 1e-4))
// ============================================================

TEXTURE2D(_WaterSSFDepthSource);
float4 _WaterSSFDepthTexelSize;        // (1/w, 1/h, w, h)

float  _BlurRadius;
float  _BlurSigma;
float  _BlurDepthSigma;

half4 fragSSFBlur(Varyings IN) : SV_Target
{
    float2 uv = IN.texcoord;

    float center = SAMPLE_TEXTURE2D(_WaterSSFDepthSource, sampler_PointClamp, uv).r;
    if (center < 1e-4) return 0.0;          // empty pixel — keep empty

    int   r            = (int)_BlurRadius;  // no clamp — designer controls freely
    float sig          = max(_BlurSigma, 0.1);
    float blurScale    = 1.0 / sig;
    float depthSigma   = max(center * _BlurDepthSigma, 1e-4);
    float depthFalloff = 1.0 / depthSigma;

    float sum  = 0.0;
    float wsum = 0.0;

    [loop] for (int ty = -r; ty <= r; ++ty)
    [loop] for (int tx = -r; tx <= r; ++tx)
    {
        float2 offset   = float2(tx, ty) * _WaterSSFDepthTexelSize.xy;
        float  tapDepth = SAMPLE_TEXTURE2D(_WaterSSFDepthSource,
                                           sampler_PointClamp,
                                           uv + offset).r;
        if (tapDepth < 1e-4) continue;

        float spatialSq = (tx * tx + ty * ty) * (blurScale * blurScale);
        float ws        = exp(-spatialSq);

        float range = (tapDepth - center) * depthFalloff;
        float wr    = exp(-(range * range));

        float w = ws * wr;
        sum  += tapDepth * w;
        wsum += w;
    }

    return half4((wsum > 1e-6) ? (sum / wsum) : center, 0, 0, 1);
}

#endif // SSF_BLUR_INCLUDED
