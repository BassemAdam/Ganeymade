#ifndef SSF_NORMALS_INCLUDED
#define SSF_NORMALS_INCLUDED

// ============================================================
// Pass 5 — ScreenSpaceFluidNormals
//
// Reconstructs view-space normals from the smoothed raw eye-depth
// depth texture via finite differences.
//
// Output: RGBAHalf
//   RGB = view-space normal encoded to [0..1]  (N * 0.5 + 0.5)
//   A   = validity flag: 1.0 = valid, 0.0 = empty/border pixel
// ============================================================

// Source texture + texel size set explicitly from C# (no reliance on _BlitTexture/_BlitTexture_TexelSize)
TEXTURE2D(_WaterSSFInput);
float4 _WaterSSFInputTexelSize; // (1/w, 1/h, w, h)
TEXTURE2D(_WaterSSFThickness);
SAMPLER(sampler_WaterSSFThickness);

float _NormalStepPixels;
float _ThicknessCutoff;

// Reconstruct view-space position from a UV and positive eye-depth in metres.
float3 ViewPosFromEyeDepth(float2 uv, float eyeDepth)
{
    float2 ndc     = uv * 2.0 - 1.0;
    float3 vs;
    vs.z = -eyeDepth;                               // Unity: view Z negative = forward
    vs.x = ndc.x * eyeDepth / UNITY_MATRIX_P[0][0];
    vs.y = ndc.y * eyeDepth / UNITY_MATRIX_P[1][1];
    return vs;
}

half4 fragNormals(Varyings IN) : SV_Target
{
    float2 uv = IN.texcoord;
    float2 ts = _WaterSSFInputTexelSize.xy * max(_NormalStepPixels, 1.0); // correct: set from C# with real camera dimensions

    float centerThickness = SAMPLE_TEXTURE2D(_WaterSSFThickness, sampler_WaterSSFThickness, uv).r;
    if (centerThickness < _ThicknessCutoff)
        return half4(0.5, 0.5, 1.0, 0.0);

    float d = SAMPLE_TEXTURE2D(_WaterSSFInput, sampler_PointClamp, uv).r;
    if (d < 1e-4) return half4(0.5, 0.5, 1.0, 0.0); // empty centre

    float3 p = ViewPosFromEyeDepth(uv, d);

    // Use the reference min-gradient technique: compare forward and backward
    // finite differences and pick whichever has the smaller depth discontinuity.
    // This avoids smeared normals at depth edges (e.g. water silhouette).
    float dxFwd  = SAMPLE_TEXTURE2D(_WaterSSFInput, sampler_PointClamp, uv + float2( ts.x, 0)).r;
    float dxBwd  = SAMPLE_TEXTURE2D(_WaterSSFInput, sampler_PointClamp, uv + float2(-ts.x, 0)).r;
    float dyFwd  = SAMPLE_TEXTURE2D(_WaterSSFInput, sampler_PointClamp, uv + float2(0,  ts.y)).r;
    float dyBwd  = SAMPLE_TEXTURE2D(_WaterSSFInput, sampler_PointClamp, uv + float2(0, -ts.y)).r;

    // Pick forward neighbour; fall back to backward if it has a smaller Z jump.
    // An empty neighbour (< 1e-4) is treated as a very large depth so we always
    // prefer the non-empty direction when one side is at the silhouette edge.
    float useXDepth = (dxFwd >= 1e-4) ? dxFwd : dxBwd;
    float useYDepth = (dyFwd >= 1e-4) ? dyFwd : dyBwd;

    // Among valid samples, prefer whichever has the smaller depth change
    if (dxFwd >= 1e-4 && dxBwd >= 1e-4 && abs(dxBwd - d) < abs(dxFwd - d)) useXDepth = dxBwd;
    if (dyFwd >= 1e-4 && dyBwd >= 1e-4 && abs(dyBwd - d) < abs(dyFwd - d)) useYDepth = dyBwd;

    bool hasXFwd = dxFwd >= 1e-4;
    bool hasXBwd = dxBwd >= 1e-4;
    bool hasYFwd = dyFwd >= 1e-4;
    bool hasYBwd = dyBwd >= 1e-4;

    float3 ddx = 0.0;
    float3 ddy = 0.0;

    if (hasXFwd && hasXBwd)
    {
        float3 pxFwd = ViewPosFromEyeDepth(uv + float2( ts.x, 0), dxFwd);
        float3 pxBwd = ViewPosFromEyeDepth(uv + float2(-ts.x, 0), dxBwd);
        ddx = pxFwd - pxBwd;
    }
    else if (useXDepth >= 1e-4)
    {
        float2 xUV  = uv + (useXDepth == dxFwd ? float2( ts.x, 0) : float2(-ts.x, 0));
        float3 px = ViewPosFromEyeDepth(xUV, useXDepth);
        ddx = px - p;
    }

    if (hasYFwd && hasYBwd)
    {
        float3 pyFwd = ViewPosFromEyeDepth(uv + float2(0,  ts.y), dyFwd);
        float3 pyBwd = ViewPosFromEyeDepth(uv + float2(0, -ts.y), dyBwd);
        ddy = pyFwd - pyBwd;
    }
    else if (useYDepth >= 1e-4)
    {
        float2 yUV  = uv + (useYDepth == dyFwd ? float2(0,  ts.y) : float2(0, -ts.y));
        float3 py = ViewPosFromEyeDepth(yUV, useYDepth);
        ddy = py - p;
    }

    if (dot(ddx, ddx) < 1e-8 || dot(ddy, ddy) < 1e-8)
        return half4(0.5, 0.5, 1.0, 0.0);

    float3 N   = normalize(cross(ddy, ddx));
    return half4(N * 0.5 + 0.5, 1.0);
}

#endif // SSF_NORMALS_INCLUDED
