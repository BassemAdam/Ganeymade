#ifndef SSF_NORMALS_BLUR_INCLUDED
#define SSF_NORMALS_BLUR_INCLUDED

// ============================================================
// Pass: ScreenSpaceFluidNormalsBlur
//
// Separable Gaussian blur on the encoded normals (RGHalf).
// Run twice per frame: X direction then Y direction.
//
// Normals are stored as N.xy * 0.5 + 0.5 (RGHalf).
// Background pixels (depth == 0) are skipped — their output
// stays zero so composite can still discard them.
//
// filterSize = 5, sigma = filterSize/3 (same convention as
// the thickness blur).  Small enough to preserve shading
// detail while removing per-particle faceting.
// ============================================================

TEXTURE2D(_WaterSSFNormalsSource);
TEXTURE2D(_WaterSSFDepthSmooth);
// Shared uniforms set by RecordNormalsBlur in C#:
//   _WaterSSFDepthSmooth    – smoothed eye-depth (background == 0)
//   _WaterSSFDepthTexelSize – (1/w, 1/h, w, h)
//   _WaterSSFBlurDirection  – (1/w, 0) or (0, 1/h)
float4 _WaterSSFDepthTexelSize;
float2 _WaterSSFBlurDirection;

static const int   kNormalsFilterSize = 5;
static const float kNormalsSigmaInv2  = 1.0 / (2.0 * ((float)kNormalsFilterSize / 3.0) * ((float)kNormalsFilterSize / 3.0));

half4 fragSSFNormalsBlur(Varyings IN) : SV_Target
{
    float2 uv = IN.texcoord;

    float centerDepth = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uv).r;

    // Skip background pixels — preserve zero output for composite discard.
    if (centerDepth < 1e-4)
        return half4(0, 0, 0, 0);

    half2 centerN = SAMPLE_TEXTURE2D(_WaterSSFNormalsSource, sampler_LinearClamp, uv).rg;

    half2 sum  = centerN;
    float wsum = 1.0;

    [loop] for (int i = 1; i <= kNormalsFilterSize; i++)
    {
        float  gw   = exp(-(float)(i * i) * kNormalsSigmaInv2);
        float2 step = _WaterSSFBlurDirection * i;

        float2 uvN = uv - step;
        float2 uvP = uv + step;

        // Only include foreground neighbours.
        float dN = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uvN).r;
        float dP = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uvP).r;

        if (dN > 1e-4)
        {
            sum  += (half2)SAMPLE_TEXTURE2D(_WaterSSFNormalsSource, sampler_LinearClamp, uvN).rg * gw;
            wsum += gw;
        }
        if (dP > 1e-4)
        {
            sum  += (half2)SAMPLE_TEXTURE2D(_WaterSSFNormalsSource, sampler_LinearClamp, uvP).rg * gw;
            wsum += gw;
        }
    }

    half2 blurred = sum / (half)wsum;
    return half4(blurred, 0, 1);
}

#endif // SSF_NORMALS_BLUR_INCLUDED
