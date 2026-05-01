#ifndef RAY_MARCH_LIGHTING_INCLUDED
#define RAY_MARCH_LIGHTING_INCLUDED

// Simple liquid-only self-shadowing. Vapour self-shadowing and phase functions
// are intentionally left out while the vapour mask/texture visualization is
// being tuned step by step.
float3 CalculateTransmittedSunLightLiquid(
    float3 posWS,
    float3 liquidExtinction,
    float  lightStepSize)
{
    float3 sunDir       = normalize(_MainLightPosition.xyz);
    float2 lightBounds  = RayBoxDst(posWS, sunDir, _PhysicsBoundsMinWS.xyz, _PhysicsBoundsMaxWS.xyz);
    float  dstToSunExit = lightBounds.x + lightBounds.y;

    float3 opticalDepth = 0.0;
    float  dist         = lightBounds.x;

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
