#ifndef SSF_NORMALIZE_DEPTH_INCLUDED
#define SSF_NORMALIZE_DEPTH_INCLUDED

// ============================================================
// Pass 3 — ScreenSpaceFluidNormalizeDepth
//
// Maps raw eye-depth [metres] → [0..1] using the depth-range
// texture computed by the previous pass.
//
// Empty pixels (depth ≈ 0) output exactly 0.0 so downstream
// passes can use the < 1e-4 test to detect them.
// Occupied pixels output values in [1e-4 .. 1.0] so a near-plane
// particle is never mistaken for an empty pixel.
// ============================================================

TEXTURE2D(_WaterSSFInput);    // bound from C# via ctx.cmd.SetGlobalTexture before draw
TEXTURE2D(_WaterSSFDepthRange);
SAMPLER(sampler_WaterSSFDepthRange);

half4 fragNormalizeDepth(Varyings IN) : SV_Target
{
    float raw = SAMPLE_TEXTURE2D(_WaterSSFInput, sampler_LinearClamp, IN.texcoord).r;
    if (raw < 1e-5) return 0.0; // empty pixel

    float2 range = SAMPLE_TEXTURE2D(_WaterSSFDepthRange, sampler_PointClamp, float2(0.5, 0.5)).rg;
    float  span  = max(range.g - range.r, 0.001);

    // Map [minDepth..maxDepth] → [0..1], then push away from 0 slightly
    // so near-plane pixels don't trigger the empty-pixel guard in blur/normals.
    float norm = saturate((raw - range.r) / span);
    norm = norm * (1.0 - 2e-4) + 1e-4; // remap [0,1] → [1e-4, ~1]
    return half4((half)norm, 0, 0, 1);
}

#endif // SSF_NORMALIZE_DEPTH_INCLUDED
