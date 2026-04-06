#ifndef WATER_PHASE_LIGHTING_INCLUDED
    #define WATER_PHASE_LIGHTING_INCLUDED

    float HenyeyGreenstein(float cosTheta, float g)
    {
        float g2 = g * g;
        float denom = 1.0 + g2 - 2.0 * g * cosTheta;
        return (1.0 - g2) / pow(abs(denom), 1.5);
    }

    float FresnelEdge(float3 viewDir, float3 normal, float power)
    {
        float cosTheta = saturate(dot(viewDir, normal));
        return pow(1.0 - cosTheta, power);
    }

#endif
