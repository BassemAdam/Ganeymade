#ifndef RAY_MARCH_VAPOUR_INCLUDED
#define RAY_MARCH_VAPOUR_INCLUDED

// Advected vapour noise texture.  Updated each frame by the AdvectVapourNoise
// compute kernel (semi-Lagrangian advection along the particle velocity field).
// Bound per-frame via MaterialPropertyBlock.
TEXTURE3D(_VapourNoiseTex);
SAMPLER(sampler_VapourNoiseTex);


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

float3 RaymarchVapourVectorNoise3D(float3 p)
{
    return float3(
        RaymarchVapourValueNoise3D(p + float3(1.72, 9.23, 5.41)),
        RaymarchVapourValueNoise3D(p + float3(8.31, 2.84, 3.26)),
        RaymarchVapourValueNoise3D(p + float3(4.17, 6.73, 1.92))
    ) * 2.0 - 1.0;
}

float CalculateVapourPhysicalMask(float rawVapourDensity)
{
    float threshold = max(_VapourPresenceThreshold, 1e-6);
    float fullDensity = max(_VapourFullDensity, threshold + 1e-5);
    return smoothstep(threshold, fullDensity, rawVapourDensity);
}

float CalculateVapourHeightFadeWS(float3 posWS)
{
    float dissipation = max(_VapourHeightDissipation, 0.0);
    if (dissipation <= 1e-5)
        return 1.0;

    float heightSize = max(_PhysicsBoundsMaxWS.y - _PhysicsBoundsMinWS.y, 1e-5);
    float height01 = saturate((posWS.y - _PhysicsBoundsMinWS.y) / heightSize);
    return exp2(-height01 * dissipation);
}

float SampleVapourDensityProceduralWS(float3 posWS, float rawVapourDensity)
{
    if (!HasPhysicalVapour(rawVapourDensity))
        return 0.0;

    // Large-scale shape: sample the GPU-advected noise texture.
    // This texture is updated each frame by the AdvectVapourNoise compute kernel,
    // which performs semi-Lagrangian advection along the particle velocity field.
    float3 uvw = DensityGridUVW(posWS);
    float advectedShape = _VapourNoiseTex.SampleLevel(sampler_VapourNoiseTex, uvw, 0);

    // High-frequency detail: lightweight FBM fixed in world space (no drift).
    // Adds wispy sub-voxel structure the 3D texture resolution can't store.
    float3 p = posWS / max(_NoiseScale, 1e-5);
    p.y /= max(_VapourVerticalStretch, 0.05);
    float3 flowA = RaymarchVapourVectorNoise3D(p * 0.55 + _Time.y * 0.015);
    float3 warpedP = p + flowA * max(_VapourWarpStrength, 0.0);
    int octaves = clamp(_NoiseOctaves, 1, 8);
    float detailNoise = RaymarchVapourFBM(warpedP, octaves, 2.0, 0.5);

    // Combine: advected texture provides the moving shape; detail adds fine structure.
    // Weight heavily toward the advected texture so streaming is clearly visible.
    float combined = advectedShape * 0.82 + detailNoise * 0.18;

    // Erosion
    float erosionNoise = RaymarchVapourFBM(
        warpedP * max(_VapourErosionScale, 0.01),
        min(octaves + 1, 8), 2.0, 0.5);
    float erodedNoise = combined - erosionNoise * max(_VapourErosionStrength, 0.0);

    float wispyNoise = smoothstep(_VapourCutoff, _VapourCutoff + max(_VapourSoftness, 1e-3), erodedNoise);
    float noise01 = pow(saturate(wispyNoise), max(_DensityPower, 0.01));

    float physicalMask = CalculateVapourPhysicalMask(rawVapourDensity);
    float heightFade = CalculateVapourHeightFadeWS(posWS);
    return saturate(noise01 * physicalMask * heightFade);
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

float EvaluateVapourPhase(float3 viewRayDirectionWS, float3 lightDirectionWS)
{
    float3 viewDir = normalize(viewRayDirectionWS);
    float3 lightDir = normalize(lightDirectionWS);
    float g = clamp(_VapourScatterG, -0.85, 0.85);
    float g2 = g * g;
    float cosTheta = clamp(dot(lightDir, viewDir), -1.0, 1.0);

    float hg = (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4), 1.5);
    float phase = lerp(1.0, hg, saturate(abs(g)));

    float rim = pow(saturate(-cosTheta), 4.0) * max(_VapourBackscatter, 0.0);
    return max(phase + rim, 0.0);
}

float3 EvaluateVapourDirectScatter(
    float vapourDensity,
    float stepSize,
    float shadowAtten,
    float3 lightColor,
    float3 viewRayDirectionWS,
    float3 lightDirectionWS)
{
    if (vapourDensity <= 1e-6)
        return 0.0;

    float shadowWithFloor = lerp(saturate(_VapourShadowFloor), 1.0, saturate(shadowAtten));
    float godRayFactor = lerp(1.0, shadowWithFloor, saturate(_VapourGodRayStrength));
    float phaseFactor = EvaluateVapourPhase(viewRayDirectionWS, lightDirectionWS);
    return vapourDensity * stepSize * godRayFactor * phaseFactor * lightColor * _VapourBaseColor.rgb;
}

#endif
