#ifndef SSF_BLUR_INCLUDED
#define SSF_BLUR_INCLUDED

TEXTURE2D(_WaterSSFDepthSource);
float4 _WaterSSFDepthTexelSize;
float2 _WaterSSFBlurDirection;

float  _NRF_MaxFilterSize;
float  _NRF_ProjectedParticleK;
float  _NRF_Mu;
float  _NRF_DepthThreshold;

half4 fragSSFBlur(Varyings IN) : SV_Target
{
    float2 uv = IN.texcoord;

    float center = SAMPLE_TEXTURE2D(_WaterSSFDepthSource, sampler_PointClamp, uv).r;
    if (center < 1e-4) return 0.0;

    float projK = max(_NRF_ProjectedParticleK, 1.0);
    int   r     = clamp((int)ceil(projK / center), 1, (int)max(_NRF_MaxFilterSize, 1.0));

    float sigma   = r * 0.5;
    float sigInv2 = 1.0 / max(2.0 * sigma * sigma, 1e-6);

    float mu          = _NRF_Mu;
    float depthThresh = max(_NRF_DepthThreshold, 1e-4);
    float higherBound = center + mu;

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

        if (dN < 1e-4 || dN < threshLowN)
        {
            wN = 0.0;
        }
        else if (dN > threshHighN)
        {
            dN = higherBound;
        }
        else
        {
            threshLowN  = min(threshLowN,  dN - depthThresh);
            threshHighN = max(threshHighN, dN + depthThresh);
        }

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

#endif
