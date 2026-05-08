#ifndef RAY_MARCH_LIGHTING_INCLUDED
#define RAY_MARCH_LIGHTING_INCLUDED

// Simple liquid-only self-shadowing. Vapour self-shadowing and phase functions
// are intentionally left out while the vapour mask/texture visualization is
// being tuned step by step.
float3 CalculateTransmittedSunLightLiquid(
    float3 posWS,
    float3 liquidExtinction,
    float  lightStepSize,
    float  shadowJitter = 0.5)
{
    float3 sunDir       = normalize(_MainLightPosition.xyz);
    float2 lightBounds  = RayBoxDst(posWS, sunDir, _PhysicsBoundsMinWS.xyz, _PhysicsBoundsMaxWS.xyz);
    float  dstToSunExit = lightBounds.x + lightBounds.y;

    float3 opticalDepth = 0.0;
    // Jitter the first shadow step by a fraction of lightStepSize — exactly the
    // same pattern as the view march: distanceToVolume + safeStepSize * blueNoiseValue.
    // Without the multiply the offset is raw world-space (0-1 m) and has no
    // relationship to the step size, so banding is unchanged.
    float  dist         = lightBounds.x + lightStepSize * shadowJitter;

    while (dist < dstToSunExit)
    {
        float3 samplePositionWS = posWS + sunDir * dist;
        float dl = SampleAdjustedLiquidDensityWS(samplePositionWS);
        opticalDepth += liquidExtinction * dl * lightStepSize;
        dist += lightStepSize;
    }

    return exp(-opticalDepth);
}

#endif
