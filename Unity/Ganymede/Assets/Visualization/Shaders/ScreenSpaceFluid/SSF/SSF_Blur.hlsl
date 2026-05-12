#ifndef SSF_BLUR_INCLUDED
#define SSF_BLUR_INCLUDED

// ============================================================
// Pass: ScreenSpaceFluidBlur  — Narrow-Range Filter
//
// Based on "A Narrow-Range Filter for Screen-Space Fluid Rendering"
// (Truong et al., 2018). Run twice: X direction then Y direction.
//
// Key improvements over bilateral filter:
//
//  1. ADAPTIVE filter size — nearby particles get a larger kernel:
//       r = min(_NRF_MaxFilterSize, ceil(_NRF_ProjectedParticleK / depth))
//     This naturally blurs close particles more (they look bigger)
//     and preserves detail on distant ones.
//
//  2. Narrow-range threshold instead of soft Gaussian range weights:
//       depth < lowThresh            → weight = 0   (hard edge, skip)
//       depth > highThresh           → snap to center+_NRF_Mu (merges surface)
//       lowThresh <= depth <= high   → accept AND expand the window
//     The expanding window lets adjacent particle depths merge without
//     smearing across true depth discontinuities.
//
// Parameters:
//   _NRF_MaxFilterSize       max kernel half-radius in pixels (e.g. 20)
//   _NRF_ProjectedParticleK  ≈ blurScale * 2*radius * (screenH/2) / tan(fov/2)
//                             tune so filterSize≈1 at the far clip distance
//   _NRF_Mu                  snap offset when depth > highThresh (world metres)
//   _NRF_DepthThreshold      initial range window half-width    (world metres)
// ============================================================

TEXTURE2D(_WaterSSFDepthSource);
float4 _WaterSSFDepthTexelSize;    // (1/w, 1/h, w, h)
float2 _WaterSSFBlurDirection;     // (1/w, 0) for X pass, (0, 1/h) for Y pass

float  _NRF_MaxFilterSize;
float  _NRF_ProjectedParticleK;
float  _NRF_Mu;
float  _NRF_DepthThreshold;

half4 fragSSFBlur(Varyings IN) : SV_Target
{
    float2 uv = IN.texcoord;

    float center = SAMPLE_TEXTURE2D(_WaterSSFDepthSource, sampler_PointClamp, uv).r;
    if (center < 1e-4) return 0.0;    // empty pixel — keep empty

    // Adaptive half-radius: larger kernel for nearby (screen-large) particles
    float projK = max(_NRF_ProjectedParticleK, 1.0);
    int   r     = clamp((int)ceil(projK / center), 1, (int)max(_NRF_MaxFilterSize, 1.0));

    float sigma   = r * 0.5;
    float sigInv2 = 1.0 / max(2.0 * sigma * sigma, 1e-6);

    float mu          = _NRF_Mu;
    float depthThresh = max(_NRF_DepthThreshold, 1e-4);
    float higherBound = center + mu;

    // Independent expanding windows for the two directions
    float threshLowN = center - depthThresh,  threshHighN = center + depthThresh;
    float threshLowP = center - depthThresh,  threshHighP = center + depthThresh;

    float sum  = center;
    float wsum = 1.0;

    [loop] for (int i = 1; i <= r; i++)
    {
        float  gw   = exp(-(float)(i * i) * sigInv2);
        float2 step = _WaterSSFBlurDirection * i;

        float dN = SAMPLE_TEXTURE2D(_WaterSSFDepthSource, sampler_PointClamp, uv - step).r;
        float dP = SAMPLE_TEXTURE2D(_WaterSSFDepthSource, sampler_PointClamp, uv + step).r;

        float wN = gw, wP = gw;

        // ---- Negative direction ----
        if (dN < 1e-4 || dN < threshLowN)
        {
            wN = 0.0;                               // empty or below range → skip
        }
        else if (dN > threshHighN)
        {
            dN = higherBound;                       // above range → snap, still blends
        }
        else
        {
            threshLowN  = min(threshLowN,  dN - depthThresh);  // expand window
            threshHighN = max(threshHighN, dN + depthThresh);
        }

        // ---- Positive direction ----
        if (dP < 1e-4 || dP < threshLowP)
        {
            wP = 0.0;
        }
        else if (dP > threshHighP)
        {
            dP = higherBound;
        }
        else
        {
            threshLowP  = min(threshLowP,  dP - depthThresh);
            threshHighP = max(threshHighP, dP + depthThresh);
        }

        sum  += dN * wN + dP * wP;
        wsum += wN + wP;
    }

    return half4((wsum > 1e-6) ? (sum / wsum) : center, 0, 0, 1);
}

#endif // SSF_BLUR_INCLUDED
