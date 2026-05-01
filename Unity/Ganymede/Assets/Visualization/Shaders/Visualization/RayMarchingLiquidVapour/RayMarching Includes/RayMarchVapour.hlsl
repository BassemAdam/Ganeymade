#ifndef RAY_MARCH_VAPOUR_INCLUDED
#define RAY_MARCH_VAPOUR_INCLUDED

// Vapour-specific visualization code lives here on purpose:
// - the physical G channel is the source-of-truth mask
// - any non-empty physical vapour cell becomes a presence gate
// - world-space domain-warped FBM provides the visual/procedural density
// - this mirrors the procedural path used by Custom/VapourVolume

bool HasPhysicalVapour(float rawVapourDensity)
{
    return rawVapourDensity > max(_VapourPresenceThreshold, 1e-6);
}

float CalculateVapourBoundsFadeWS(float3 posWS)
{
    float edgeSoftness = max(_EdgeSoftness, 0.0);
    if (edgeSoftness <= 1e-5)
        return 1.0;

    float3 sizeWS = max(_PhysicsBoundsMaxWS.xyz - _PhysicsBoundsMinWS.xyz, 1e-5);
    float3 uvw = saturate((posWS - _PhysicsBoundsMinWS.xyz) / sizeWS);
    float3 distanceToEdge = min(uvw, 1.0 - uvw);
    float closestEdge = min(distanceToEdge.x, min(distanceToEdge.y, distanceToEdge.z));
    return smoothstep(0.0, edgeSoftness, closestEdge);
}

float3 GetVapourNoiseDriftDirectionWS()
{
    float3 driftDir = _NoiseDriftDir.xyz;
    float driftLen2 = dot(driftDir, driftDir);
    return (driftLen2 > 1e-6) ? (driftDir * rsqrt(driftLen2)) : float3(0.0, 1.0, 0.0);
}

float RaymarchVapourHash3D(float3 p)
{
    p = frac(p * float3(443.897, 441.423, 437.195));
    p += dot(p, p.yzx + 19.19);
    return frac((p.x + p.y) * p.z);
}

float RaymarchVapourValueNoise3D(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);

    float c000 = RaymarchVapourHash3D(i + float3(0, 0, 0));
    float c100 = RaymarchVapourHash3D(i + float3(1, 0, 0));
    float c010 = RaymarchVapourHash3D(i + float3(0, 1, 0));
    float c110 = RaymarchVapourHash3D(i + float3(1, 1, 0));
    float c001 = RaymarchVapourHash3D(i + float3(0, 0, 1));
    float c101 = RaymarchVapourHash3D(i + float3(1, 0, 1));
    float c011 = RaymarchVapourHash3D(i + float3(0, 1, 1));
    float c111 = RaymarchVapourHash3D(i + float3(1, 1, 1));

    float x0 = lerp(c000, c100, u.x);
    float x1 = lerp(c010, c110, u.x);
    float x2 = lerp(c001, c101, u.x);
    float x3 = lerp(c011, c111, u.x);

    float y0 = lerp(x0, x1, u.y);
    float y1 = lerp(x2, x3, u.y);

    return lerp(y0, y1, u.z);
}

float RaymarchVapourFBM(float3 p, int octaves, float lacunarity, float gain)
{
    float value = 0.0;
    float amplitude = 0.5;
    float frequency = 1.0;
    float maxValue = 0.0;

    [loop]
    for (int i = 0; i < octaves; i++)
    {
        value += amplitude * RaymarchVapourValueNoise3D(p * frequency);
        maxValue += amplitude;
        amplitude *= gain;
        frequency *= lacunarity;
    }

    return value / max(maxValue, 1e-5);
}

float SampleVapourDensityProceduralWS(float3 posWS, float rawVapourDensity)
{
    if (!HasPhysicalVapour(rawVapourDensity))
        return 0.0;

    float3 driftDir = GetVapourNoiseDriftDirectionWS();
    float3 driftedPos = posWS + driftDir * (_Time.y * _NoiseDriftSpeed);
    float3 p = driftedPos / max(_NoiseScale, 1e-5);

    float3 warpOffset = float3(
        RaymarchVapourValueNoise3D(p * 0.7 + float3(1.72, 9.23, 5.41)),
        RaymarchVapourValueNoise3D(p * 0.7 + float3(8.31, 2.84, 3.26)),
        RaymarchVapourValueNoise3D(p * 0.7 + float3(4.17, 6.73, 1.92))
    ) * 2.0 - 1.0;

    float3 warpedP = p + warpOffset * 0.35;
    float rawNoise = RaymarchVapourFBM(warpedP, clamp(_NoiseOctaves, 1, 8), 2.0, 0.5);
    float noise01 = pow(saturate(rawNoise), max(_DensityPower, 0.01));

    float maskSoft = smoothstep(0.0, 0.2, rawVapourDensity);
    return saturate(noise01 * maskSoft);
}

float BuildVapourDensityWS(float3 posWS, float rawVapourDensity)
{
    if (!HasPhysicalVapour(rawVapourDensity))
        return 0.0;

    float edgeFade = CalculateVapourBoundsFadeWS(posWS);
    float proceduralDensity = SampleVapourDensityProceduralWS(posWS, rawVapourDensity);
    return saturate(proceduralDensity * edgeFade * _VapourDensityMultiplier);
}

float3 EvaluateSimpleVapourExtinction(float vapourDensity)
{
    return _VapourAbsorption * vapourDensity;
}

float3 EvaluateVapourDirectScatter(
    float vapourDensity,
    float stepSize,
    float shadowAtten,
    float3 lightColor)
{
    if (vapourDensity <= 1e-6)
        return 0.0;

    // God-ray mode: main-light shadows create the bright/dark shaft pattern,
    // but a floor keeps shadowed vapour visible instead of crushing to black.
    float shadowWithFloor = lerp(saturate(_VapourShadowFloor), 1.0, saturate(shadowAtten));
    float godRayFactor = lerp(1.0, shadowWithFloor, saturate(_VapourGodRayStrength));
    return vapourDensity * stepSize * godRayFactor * lightColor * _VapourBaseColor.rgb;
}

#endif