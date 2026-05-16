#ifndef SSF_NORMALS_BLUR_INCLUDED
#define SSF_NORMALS_BLUR_INCLUDED

TEXTURE2D(_WaterSSFNormalsSource);
TEXTURE2D(_WaterSSFDepthSmooth);
float4 _WaterSSFDepthTexelSize;
float2 _WaterSSFBlurDirection;

static const int   kNormalsFilterSize = 5;
static const float kNormalsSigmaInv2  = 1.0 / (2.0 * ((float)kNormalsFilterSize / 3.0) * ((float)kNormalsFilterSize / 3.0));

half4 fragSSFNormalsBlur(Varyings IN) : SV_Target
{
    float2 uv = IN.texcoord;

    float centerDepth = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uv).r;

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

#endif
