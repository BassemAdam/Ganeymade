#ifndef SSF_NORMALS_INCLUDED
#define SSF_NORMALS_INCLUDED

TEXTURE2D(_WaterSSFDepthSmooth);
float4 _WaterSSFDepthTexelSize;

float _NormalStepPixels;

bool SSFTryEyePos(float2 uv, out float3 viewPos)
{
    float d = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uv).r;
    if (d < 1e-4) { viewPos = 0.0; return false; }
    viewPos = SSFViewPosFromEyeDepth(uv, d);
    return true;
}

half2 fragSSFNormals(Varyings IN) : SV_Target
{
    float2 uv = IN.texcoord;
    float2 ts = _WaterSSFDepthTexelSize.xy * max(_NormalStepPixels, 1.0);

    float d = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uv).r;
    if (d < 1e-4) return half2(0.5, 0.5);

    float3 p = SSFViewPosFromEyeDepth(uv, d);

    float3 pxF, pxB, pyF, pyB;
    bool hxF = SSFTryEyePos(uv + float2( ts.x, 0.0), pxF);
    bool hxB = SSFTryEyePos(uv + float2(-ts.x, 0.0), pxB);
    bool hyF = SSFTryEyePos(uv + float2(0.0,  ts.y), pyF);
    bool hyB = SSFTryEyePos(uv + float2(0.0, -ts.y), pyB);

    float3 ddx  = hxF ? (pxF - p) : float3(0,0,0);
    float3 ddx2 = hxB ? (p - pxB) : float3(0,0,0);
    if (!hxF || (hxB && abs(ddx.z) > abs(ddx2.z))) ddx = ddx2;

    float3 ddy  = hyF ? (pyF - p) : float3(0,0,0);
    float3 ddy2 = hyB ? (p - pyB) : float3(0,0,0);
    if (!hyF || (hyB && abs(ddy.z) > abs(ddy2.z))) ddy = ddy2;

    if (dot(ddx, ddx) < 1e-10)
        ddx = float3(_WaterSSFDepthTexelSize.x * 2.0 * d / UNITY_MATRIX_P[0][0], 0.0, 0.0);
    if (dot(ddy, ddy) < 1e-10)
        ddy = float3(0.0, _WaterSSFDepthTexelSize.y * 2.0 * d / UNITY_MATRIX_P[1][1], 0.0);

    float3 N = normalize(cross(ddx, ddy));
    if (N.z < 0.0) N = -N;
    return half2(N.xy * 0.5 + 0.5);
}

#endif
