#ifndef SSF_BLUR_2D_INCLUDED
#define SSF_BLUR_2D_INCLUDED

// ============================================================
// Pass 4 — ScreenSpaceFluidBlur2D
//
// Single-pass 2D bilateral Gaussian blur on raw eye-depth.
//
// Weight per sample = spatial_w × range_w:
//   spatial_w = exp(-‖offset‖² / (2·σ_s²))   → smooth over pixel distance
//   range_w   = exp(-(Δd)²   / (2·σ_d²))     → preserve depth edges
//
// _BlurDepthSigma is treated as a relative eye-depth tolerance:
//   sigmaZ = max(centerDepth * _BlurDepthSigma, 1e-4)
// This keeps the filter stable without relying on a frame-varying
// global min/max depth normalization pass.
//
// Params:
//   _BlurRadius     : kernel half-size in pixels (1..8)
//   _BlurSigma      : spatial Gaussian standard deviation (in pixels)
//   _BlurDepthSigma : relative range Gaussian threshold (unitless)
// ============================================================

// Source texture + texel size set explicitly from C# (no reliance on _BlitTexture/_BlitTexture_TexelSize)
TEXTURE2D(_WaterSSFInput);
float4 _WaterSSFInputTexelSize; // (1/w, 1/h, w, h)

float _BlurRadius;
float _BlurSigma;
float _BlurDepthSigma;

half4 fragBlur2D(Varyings IN) : SV_Target
{
    float2 uv    = IN.texcoord;
    float2 texel = _WaterSSFInputTexelSize.xy; // correct: set from C# with real camera dimensions

    int    r     = clamp((int)_BlurRadius, 1, 8);
    float  sig   = max(_BlurSigma,      0.1);

    float center = SAMPLE_TEXTURE2D(_WaterSSFInput, sampler_PointClamp, uv).r;
    if (center < 1e-4) return 0.0; // empty pixel — skip

    float depthSigma = max(center * _BlurDepthSigma, 1e-4);

    float sum = 0.0, wsum = 0.0;

    [loop] for (int dy = -r; dy <= r; ++dy)
    [loop] for (int dx = -r; dx <= r; ++dx)
    {
        float2 sampleUV = uv + float2(dx * texel.x, dy * texel.y);
        float  s        = SAMPLE_TEXTURE2D(_WaterSSFInput, sampler_PointClamp, sampleUV).r;
        if (s < 1e-4) continue; // skip empty neighbours

        float dist2    = float(dx * dx + dy * dy);
        float spatialW = exp(-dist2            / (2.0 * sig  * sig));
        float rangeW   = exp(-((s - center) * (s - center)) / (2.0 * depthSigma * depthSigma));
        float w        = spatialW * rangeW;
        sum  += s * w;
        wsum += w;
    }

    return half4((wsum > 1e-6) ? (sum / wsum) : center, 0, 0, 1);
}

#endif // SSF_BLUR_2D_INCLUDED
