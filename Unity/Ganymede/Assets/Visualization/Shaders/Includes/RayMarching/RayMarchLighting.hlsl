#ifndef RAY_MARCH_LIGHTING_INCLUDED
#define RAY_MARCH_LIGHTING_INCLUDED

// Henyey-Greenstein phase function: g=0 → isotropic, g>0 → forward-scattering.
float HenyeyGreenstein(float cosTheta, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * 3.14159265 * pow(1.0 + g2 - 2.0 * g * cosTheta, 1.5));
}

// Single combined shadow march for dual-phase media.
// At each shadow step both channels are sampled once; combined extinction
// sigma_E = liquidExtinction*dl + vapourExtinction*dv is accumulated into
// one optical-depth integral, then converted to transmittance with a single exp().
// This is cheaper than two separate marches and physically equivalent when the
// phase contributions overlap (which they always do in a mixed liquid/vapour volume).
float3 CalculateTransmittedSunLightRG(
    float3 posWS,
    float3 liquidExtinction,
    float3 vapourExtinction,
    float  lightStepSize)
{
    float3 sunDir       = normalize(_MainLightPosition.xyz);
    float2 lightBounds  = RayBoxDst(posWS, sunDir, _PhysicsBoundsMinWS.xyz, _PhysicsBoundsMaxWS.xyz);
    float  dstToSunExit = lightBounds.x + lightBounds.y;

    float3 opticalDepth = 0.0;
    float  dist         = lightBounds.x;

    while (dist < dstToSunExit)
    {
        float2 d = SampleDensityRG_WS(posWS + sunDir * dist);
        float  dl = max(d.x, 0.0);
        float  dv = max(d.y, 0.0);
        opticalDepth += (liquidExtinction * dl + vapourExtinction * dv) * lightStepSize;
        dist += lightStepSize;
    }

    return exp(-opticalDepth);
}

#endif
