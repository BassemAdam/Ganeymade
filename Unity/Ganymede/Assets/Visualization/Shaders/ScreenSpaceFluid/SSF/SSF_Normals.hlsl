#ifndef SSF_NORMALS_INCLUDED
#define SSF_NORMALS_INCLUDED

// ============================================================
// Pass: ScreenSpaceFluidNormals  (Simon Green Step 3)
//
// Reconstruct view-space normals from the smoothed eye-depth via
// finite differences. Edge hack: pick the side with the smaller
// |Δz| so silhouette pixels do not pull samples from background.
//
// Output (RGBAHalf):
//   RGB = (N * 0.5 + 0.5)
//   A   = 1.0 valid / 0.0 empty
// ============================================================

// Input: smoothed eye-depth written by the blur pass.
TEXTURE2D(_WaterSSFDepthSmooth);
float4 _WaterSSFDepthTexelSize;            // (1/w, 1/h, w, h)

float _NormalStepPixels;

bool SSFTryEyePos(float2 uv, out float3 viewPos)
{
    float d = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uv).r;
    if (d < 1e-4) { viewPos = 0.0; return false; }
    viewPos = SSFViewPosFromEyeDepth(uv, d);
    return true;
}

half4 fragSSFNormals(Varyings IN) : SV_Target
{
    float2 uv = IN.texcoord;
    float2 ts = _WaterSSFDepthTexelSize.xy * max(_NormalStepPixels, 1.0);

    // Only require valid blurred depth — thickness is the composite's concern.
    float d = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uv).r;
    if (d < 1e-4) return half4(0.5, 0.5, 1.0, 0.0);

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

    // Degenerate case (flat surface, no valid neighbours): synthesise a
    // camera-facing tangent frame so we get a valid a=1 normal output.
    if (dot(ddx, ddx) < 1e-10)
        ddx = float3(_WaterSSFDepthTexelSize.x * 2.0 * d / UNITY_MATRIX_P[0][0], 0.0, 0.0);
    if (dot(ddy, ddy) < 1e-10)
        ddy = float3(0.0, _WaterSSFDepthTexelSize.y * 2.0 * d / UNITY_MATRIX_P[1][1], 0.0);

    // Simon Green reference: n = normalize(cross(ddx, ddy)).
    float3 N = normalize(cross(ddx, ddy));
    // Ensure normal faces toward the camera (view-space +Z convention).
    if (N.z < 0.0) N = -N;
    return half4(N * 0.5 + 0.5, 1.0);
}

#endif // SSF_NORMALS_INCLUDED
