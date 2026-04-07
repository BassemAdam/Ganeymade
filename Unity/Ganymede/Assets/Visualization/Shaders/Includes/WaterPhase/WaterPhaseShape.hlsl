#ifndef WATER_PHASE_SHAPE_INCLUDED
    #define WATER_PHASE_SHAPE_INCLUDED

    float ComputeRadialFade(float radialDist)
    {
        float radialFade = saturate(1.0 - radialDist);
        // Smoothstep-like shaping for a softer, rounder boundary.
        return radialFade * radialFade * (3.0 - 2.0 * radialFade);
    }

    float ComputeShapeMaskOS(float3 sampleOS,
    float3 boundsMinOS, float3 boundsMaxOS,
    float3 boundsCenterOS, float3 boundsExtentsOS,
    float edgeSoftness)
    {
        float axialFade = ComputeEdgeFade(sampleOS, boundsMinOS, boundsMaxOS, edgeSoftness);
        float3 normPos = (sampleOS - boundsCenterOS) / max(boundsExtentsOS, 1e-6);
        float radialDist = length(normPos);
        float radialFade = ComputeRadialFade(radialDist);
        return axialFade * radialFade;
    }

    float SampleShapeMaskWS(float3 sampleWS,
    float3 boundsMinOS, float3 boundsMaxOS,
    float3 boundsCenterOS, float3 boundsExtentsOS,
    float edgeSoftness)
    {
        float3 sampleOS = TransformWorldToObject(sampleWS);
        return ComputeShapeMaskOS(sampleOS, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);
    }

    float3 ComputeShapeNormalWS(float3 sampleWS,
    float3 boundsMinOS, float3 boundsMaxOS,
    float3 boundsCenterOS, float3 boundsExtentsOS,
    float edgeSoftness,
    float epsWS)
    {
        float e = max(epsWS, 1e-4);
        float3 dx = float3(e, 0.0, 0.0);
        float3 dy = float3(0.0, e, 0.0);
        float3 dz = float3(0.0, 0.0, e);

        float mx1 = SampleShapeMaskWS(sampleWS + dx, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);
        float mx0 = SampleShapeMaskWS(sampleWS - dx, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);
        float my1 = SampleShapeMaskWS(sampleWS + dy, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);
        float my0 = SampleShapeMaskWS(sampleWS - dy, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);
        float mz1 = SampleShapeMaskWS(sampleWS + dz, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);
        float mz0 = SampleShapeMaskWS(sampleWS - dz, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);

        float3 grad = float3(mx1 - mx0, my1 - my0, mz1 - mz0);
        float gradLen2 = dot(grad, grad);
        if (gradLen2 < 1e-10)
        return float3(0.0, 1.0, 0.0);

        // Shape mask is ~1 inside and ~0 outside, so gradient generally points inward.
        return normalize(-grad);
    }

#endif
