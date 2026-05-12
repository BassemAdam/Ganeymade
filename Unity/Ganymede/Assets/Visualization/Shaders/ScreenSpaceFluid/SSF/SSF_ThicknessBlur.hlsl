#ifndef SSF_THICKNESS_BLUR_INCLUDED
#define SSF_THICKNESS_BLUR_INCLUDED

// ============================================================
// Pass: ScreenSpaceFluidThicknessBlur
//
// Separable Gaussian blur on the raw thickness map.
// Matches reference gaussian.wgsl (filterSize = 15, sigma = filterSize/3).
//
// Run twice per frame: X direction then Y direction.
// Source texture is set via the global _WaterSSFThicknessSource so
// the same pass can be re-used for both X and Y with different inputs.
//
// Why blur thickness?
//   The raw thickness map has sharp per-particle boundaries.
//   A Gaussian blur distributes the volume signal smoothly across the
//   surface, preventing over-absorption artefacts at particle centres
//   and under-absorption at particle boundaries.
// ============================================================

TEXTURE2D(_WaterSSFThicknessSource);
// Reuse the shared blur uniforms already set by RecordThicknessBlur:
//   _WaterSSFDepthTexelSize  (1/w, 1/h, w, h)
//   _WaterSSFBlurDirection   (1/w, 0) or (0, 1/h)
float4 _WaterSSFDepthTexelSize;
float2 _WaterSSFBlurDirection;

// Filter half-radius in pixels.  Matches reference value of 15.
static const int   kThicknessFilterSize = 15;
static const float kThicknessSigmaInv2  = 1.0 / (2.0 * ((float)kThicknessFilterSize / 3.0) * ((float)kThicknessFilterSize / 3.0));

half4 fragSSFThicknessBlur(Varyings IN) : SV_Target
{
    float2 uv = IN.texcoord;

    float center = SAMPLE_TEXTURE2D(_WaterSSFThicknessSource, sampler_LinearClamp, uv).r;

    // Propagate empty pixels unchanged — no thickness here.
    if (center < 1e-6) return half4(0, 0, 0, 1);

    float sum  = center;
    float wsum = 1.0;

    [loop] for (int i = 1; i <= kThicknessFilterSize; i++)
    {
        float  gw   = exp(-(float)(i * i) * kThicknessSigmaInv2);
        float2 step = _WaterSSFBlurDirection * i;

        float dN = SAMPLE_TEXTURE2D(_WaterSSFThicknessSource, sampler_LinearClamp, uv - step).r;
        float dP = SAMPLE_TEXTURE2D(_WaterSSFThicknessSource, sampler_LinearClamp, uv + step).r;

        sum  += (dN + dP) * gw;
        wsum += 2.0 * gw;
    }

    return half4(sum / wsum, 0, 0, 1);
}

#endif // SSF_THICKNESS_BLUR_INCLUDED
