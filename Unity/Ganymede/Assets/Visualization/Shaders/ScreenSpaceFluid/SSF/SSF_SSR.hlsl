#ifndef SSF_SSR_INCLUDED
#define SSF_SSR_INCLUDED

// ---------------------------------------------------------------------------
// Screen-Space Reflection for the ScreenSpaceFluid composite pass.
// Mirrors the logic in WaterRaymarchSSR.hlsl but works entirely within
// the SSF coordinate / texture set — no blue-noise texture required.
// _WaterSSFSceneCopy is used for hit colour (already declared in SSF_Composite).
// SampleSceneDepth / LinearEyeDepth come from DeclareDepthTexture.hlsl
// which is included at the shader HLSLINCLUDE level.
// ---------------------------------------------------------------------------

// Scene copy — declared here so SSF_SSR.hlsl is self-contained.
// SSF_Composite.hlsl must NOT redeclare this pair.
TEXTURE2D(_WaterSSFSceneCopy); SAMPLER(sampler_WaterSSFSceneCopy);

float _SSF_SSR_Strength;
float _SSF_SSR_ColorBoost;
float _SSF_SSR_StepSize;
float _SSF_SSR_MaxDistance;
float _SSF_SSR_MaxSteps;
float _SSF_SSR_Thickness;
float _SSF_SSR_EdgeFadeWidth;
// Debug: 0 = normal composite, 1 = show finalRefl only
float _SSF_SSR_DebugVis;

struct SSFSSRResult
{
    bool   hit;
    float2 hitUV;
    half3  hitColor;
    float  blendWeight;
};

// --- helpers ----------------------------------------------------------------

// Project a world-space point to [0,1]² screen UV.
float2 SSFSSRProjectWS(float3 posWS)
{
    float4 clip   = TransformWorldToHClip(posWS);
    float4 screen = ComputeScreenPos(clip);
    return screen.xy / max(screen.w, 1e-5);
}

// Sample scene depth at uv and return the distance along the given world-space
// direction from the camera (i.e. "ray distance" not "eye depth").
float SSFSSRSceneRayDist(float2 uv, float3 toCamDir)
{
    float3 camFwd   = -UNITY_MATRIX_V[2].xyz;
    float  denom    = max(dot(toCamDir, camFwd), 1e-4);
    float  eyeDepth = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
    return eyeDepth / denom;
}

// Screen-edge fade: fade out as the hit UV approaches the border.
float SSFSSREdgeFade(float2 uv, float fadeWidth)
{
    float2 e = min(uv, 1.0 - uv);
    return saturate(min(e.x, e.y) / max(fadeWidth, 1e-4));
}

// Interleaved Gradient Noise – cheap per-pixel jitter, no texture required.
float SSFSSRJitter(float2 screenUV)
{
    float2 px = screenUV * _ScreenParams.xy;
    return frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715))));
}

// --- main trace -------------------------------------------------------------

SSFSSRResult TraceSSFSSR(float3 posWS, float3 reflDirWS, float2 screenUV)
{
    SSFSSRResult result;
    result.hit         = false;
    result.hitUV       = screenUV;
    result.hitColor    = 0.0;
    result.blendWeight = 0.0;

    if (_SSF_SSR_Strength <= 0.0) return result;

    reflDirWS = normalize(reflDirWS);

    float stepLen  = max(_SSF_SSR_StepSize,    1e-4);
    float maxDist  = max(_SSF_SSR_MaxDistance, stepLen);
    int   maxSteps = clamp((int)_SSF_SSR_MaxSteps, 1, 256);
    float thick    = max(_SSF_SSR_Thickness,   1e-4);

    float3 origin    = posWS + reflDirWS * 1e-3;
    float  jitter    = SSFSSRJitter(screenUV);
    float  dist      = stepLen * lerp(0.25, 1.0, jitter);
    float  prevDist  = dist;
    float  prevDelta = -1e6;

    float3 camFwd = -UNITY_MATRIX_V[2].xyz;

    [loop]
    for (int i = 0; i < maxSteps && dist <= maxDist; i++)
    {
        float3 sampleWS = origin + reflDirWS * dist;
        float2 sampleUV = SSFSSRProjectWS(sampleWS);

        if (any(sampleUV <= 0.001) || any(sampleUV >= 0.999)) break;

        float3 toCam  = sampleWS - _WorldSpaceCameraPos.xyz;
        float  rayLen = length(toCam);
        if (rayLen < 1e-6) { dist += stepLen; continue; }

        float  denom     = max(dot(toCam / rayLen, camFwd), 1e-4);
        float  sceneDist = LinearEyeDepth(SampleSceneDepth(sampleUV), _ZBufferParams) / denom;
        float  delta     = rayLen - sceneDist;

        bool crossed     = (delta >= 0.0) && (prevDelta <  0.0);
        bool withinThick = (delta >= 0.0) && (delta     <= thick);

        if (withinThick || crossed)
        {
            float2 hitUV = sampleUV;

            // Binary refinement (4 steps) to sharpen the hit location.
            if (crossed)
            {
                float lo = prevDist, hi = dist;
                [unroll(4)]
                for (int r = 0; r < 4; r++)
                {
                    float  mid   = 0.5 * (lo + hi);
                    float3 mWS   = origin + reflDirWS * mid;
                    float2 mUV   = SSFSSRProjectWS(mWS);
                    if (any(mUV <= 0.001) || any(mUV >= 0.999)) { hi = mid; continue; }

                    float3 toCamM  = mWS - _WorldSpaceCameraPos.xyz;
                    float  rLenM   = max(length(toCamM), 1e-6);
                    float  denomM  = max(dot(toCamM / rLenM, camFwd), 1e-4);
                    float  mDelta  = rLenM - LinearEyeDepth(SampleSceneDepth(mUV), _ZBufferParams) / denomM;

                    if (mDelta >= 0.0) { hi = mid; hitUV = mUV; }
                    else               { lo = mid; }
                }
            }

            float  edgeFade = SSFSSREdgeFade(hitUV, _SSF_SSR_EdgeFadeWidth);
            
            // Smoothly fade out reflections at boundaries to avoid harsh/noisy edges
            float  distFade = saturate((maxDist - dist) / max(maxDist * 0.2, 1e-4));
            float  stepFade = saturate(((float)maxSteps - (float)i) / max((float)maxSteps * 0.2, 1.0));
            float  depthFade = 1.0;
            if (!crossed)
            {
                depthFade = saturate((thick - delta) / max(thick * 0.25, 1e-4));
            }
            float  fade = edgeFade * min(distFade, min(stepFade, depthFade));

            half3  hitCol   = SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, hitUV).rgb
                              * max(_SSF_SSR_ColorBoost, 0.0);

            result.hit         = true;
            result.hitUV       = hitUV;
            result.hitColor    = hitCol;
            result.blendWeight = saturate(_SSF_SSR_Strength * fade);
            return result;
        }

        prevDist  = dist;
        prevDelta = delta;
        dist     += stepLen;
    }

    return result;
}

#endif // SSF_SSR_INCLUDED
