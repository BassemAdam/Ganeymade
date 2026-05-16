#ifndef SSF_THICKNESS_BLUR_INCLUDED
#define SSF_THICKNESS_BLUR_INCLUDED

TEXTURE2D(_WaterSSFThicknessSource);
float4 _WaterSSFDepthTexelSize;
float2 _WaterSSFBlurDirection;

static const int   kThicknessFilterSize = 15;
static const float kThicknessSigmaInv2  = 1.0 / (2.0 * ((float)kThicknessFilterSize / 3.0) * ((float)kThicknessFilterSize / 3.0));

half4 fragSSFThicknessBlur(Varyings IN) : SV_Target
{
    float2 uv = IN.texcoord;

    float center = SAMPLE_TEXTURE2D(_WaterSSFThicknessSource, sampler_LinearClamp, uv).r;

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

#endif
