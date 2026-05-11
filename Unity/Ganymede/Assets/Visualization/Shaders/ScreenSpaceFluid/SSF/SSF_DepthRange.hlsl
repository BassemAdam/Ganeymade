#ifndef SSF_DEPTH_RANGE_INCLUDED
#define SSF_DEPTH_RANGE_INCLUDED

// ============================================================
// Pass 2 — ScreenSpaceFluidDepthRange
//
// Renders to a 1×1 RGHalf target (single fragment invocation).
// Samples the raw eye-depth texture on a 16×16 sparse grid and
// outputs [minDepth, maxDepth] in metres.
//
// This is consumed by the NormalizeDepth pass to map the actual
// visible fluid depth range to [0..1], making blur sigma values
// intuitive regardless of camera distance.
//
// Output: R = minDepth (metres), G = maxDepth (metres)
// Fallback when no fluid is visible: R=0, G=1 (identity range)
// ============================================================

TEXTURE2D(_WaterSSFInput); // bound from C# via ctx.cmd.SetGlobalTexture before draw

#define RANGE_GRID 16   // 16×16 = 256 sparse samples

half4 fragDepthRange(Varyings IN) : SV_Target
{
    float minD =  1e20;
    float maxD = -1e20;
    bool  any  = false;

    [loop] for (int y = 0; y < RANGE_GRID; ++y)
    [loop] for (int x = 0; x < RANGE_GRID; ++x)
    {
        float2 uv = float2((x + 0.5) / (float)RANGE_GRID,
                           (y + 0.5) / (float)RANGE_GRID);
        float  d  = SAMPLE_TEXTURE2D(_WaterSSFInput, sampler_LinearClamp, uv).r;
        if (d > 1e-5)
        {
            minD = min(minD, d);
            maxD = max(maxD, d);
            any  = true;
        }
    }

    if (!any) return half4(0.0, 1.0, 0, 1); // no fluid — identity range
    return half4((half)minD, (half)maxD, 0, 1);
}

#endif // SSF_DEPTH_RANGE_INCLUDED
