#ifndef RAY_MARCH_LIGHTING_INCLUDED
#define RAY_MARCH_LIGHTING_INCLUDED

float3 CalculateTransmittedSunLight(float3 posWS, float3 scatteringCoefficients, float densityOffset, float densityMultiplier, float lightStepSize)
{
    float3 sunDir = normalize(_MainLightPosition.xyz);
    float2 lightBoundsDst = RayBoxDst(posWS, sunDir, _PhysicsBoundsMinWS.xyz, _PhysicsBoundsMaxWS.xyz);
    float dstToSunExit = lightBoundsDst.x + lightBoundsDst.y;
    float lightOpticalDepth = 0.0;
    float distanceMarchedToLight = lightBoundsDst.x;
    while (distanceMarchedToLight < dstToSunExit)
    {
        float3 lightSamplePosWS = posWS + sunDir * distanceMarchedToLight;
        float density = SampleDensityWS(lightSamplePosWS, densityOffset, densityMultiplier);
        if (density > 0)
            lightOpticalDepth += density * lightStepSize;
        distanceMarchedToLight += lightStepSize;
    }
    return exp(-scatteringCoefficients * lightOpticalDepth);
}

#endif
