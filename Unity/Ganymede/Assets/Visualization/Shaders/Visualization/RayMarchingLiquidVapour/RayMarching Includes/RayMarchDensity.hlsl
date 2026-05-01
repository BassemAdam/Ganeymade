#ifndef RAY_MARCH_DENSITY_INCLUDED
#define RAY_MARCH_DENSITY_INCLUDED

// R = liquid density (normalised 0..1), G = vapour density (normalised 0..1).
// Liquid has a sharp iso-surface → surface detection, normals, refraction.
// Vapour is purely volumetric → scattering/absorption only, never enters surface tests.
Texture3D<float2> _PhysicsDensityGrid;
SamplerState      sampler_PhysicsDensityGrid;
Texture3D<float4> _PhysicsNormalGrid;
SamplerState      sampler_PhysicsNormalGrid;

float4 _PhysicsVolumeDims;
float4 _PhysicsBoundsMinWS;
float4 _PhysicsBoundsMaxWS;

// World-space position → trilinear UVW inside the physics AABB.
float3 DensityGridUVW(float3 posWS)
{
    float3 sizeWS   = max(_PhysicsBoundsMaxWS.xyz - _PhysicsBoundsMinWS.xyz, 1e-5);
    float3 gridSize = max(_PhysicsVolumeDims.xyz, 1.0);
    return (posWS - _PhysicsBoundsMinWS.xyz) / sizeWS + 0.5 / gridSize;
}

// Raw two-channel sample. Use in the ray loop where both phases are needed.
float2 SampleDensityRG_WS(float3 posWS)
{
    return _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, DensityGridUVW(posWS), 0);
}

// Liquid channel only.
// Use for iso-surface detection, normal gradient estimation, and refraction.
// Vapour must never feed into surface tests — it has no sharp phase boundary.
float SampleLiquidDensityWS(float3 posWS)
{
    return SampleDensityRG_WS(posWS).r;
}

// Raw vapour channel. The visible vapour renderer normally uses the enhanced
// procedural sample below; this raw mask remains useful as the physics presence
// field that prevents wisps from leaking into empty space.
float SampleVapourDensityWS(float3 posWS)
{
    return SampleDensityRG_WS(posWS).g;
}

float AdjustLiquidDensity(float rawLiquidDensity)
{
    return max(rawLiquidDensity * _DensityMultiplier + _DensityOffset, 0.0);
}

float WaterRaymarchHash3D(float3 p)
{
    p = frac(p * float3(443.897, 441.423, 437.195));
    p += dot(p, p.yzx + 19.19);
    return frac((p.x + p.y) * p.z);
}

float WaterRaymarchValueNoise3D(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);

    float c000 = WaterRaymarchHash3D(i + float3(0, 0, 0));
    float c100 = WaterRaymarchHash3D(i + float3(1, 0, 0));
    float c010 = WaterRaymarchHash3D(i + float3(0, 1, 0));
    float c110 = WaterRaymarchHash3D(i + float3(1, 1, 0));
    float c001 = WaterRaymarchHash3D(i + float3(0, 0, 1));
    float c101 = WaterRaymarchHash3D(i + float3(1, 0, 1));
    float c011 = WaterRaymarchHash3D(i + float3(0, 1, 1));
    float c111 = WaterRaymarchHash3D(i + float3(1, 1, 1));

    float x0 = lerp(c000, c100, u.x);
    float x1 = lerp(c010, c110, u.x);
    float x2 = lerp(c001, c101, u.x);
    float x3 = lerp(c011, c111, u.x);

    float y0 = lerp(x0, x1, u.y);
    float y1 = lerp(x2, x3, u.y);

    return lerp(y0, y1, u.z);
}

float WaterRaymarchFBM(float3 p, int octaves, float lacunarity, float gain)
{
    float value = 0.0;
    float amplitude = 0.5;
    float frequency = 1.0;
    float maxValue = 0.0;

    [loop]
    for (int i = 0; i < octaves; i++)
    {
        value += amplitude * WaterRaymarchValueNoise3D(p * frequency);
        maxValue += amplitude;
        amplitude *= gain;
        frequency *= lacunarity;
    }

    return value / max(maxValue, 1e-5);
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
    return (driftLen2 > 1e-6) ? (driftDir * rsqrt(driftLen2)) : float3(0.0, -1.0, 0.0);
}

// Dedicated-vapour-style density enhancement:
//   physics G channel = low-frequency presence mask
//   drifting domain-warped FBM = high-frequency wispy structure
// The mask gate is intentional: noise can carve/split vapour, but it cannot
// invent steam in empty grid cells. With the copied sample-space drift formula,
// a default _NoiseDriftDir of (0,-1,0) makes visible wisps rise upward.
float SampleEnhancedVapourDensityWS(float3 posWS)
{
    float mask = SampleVapourDensityWS(posWS);
    if (mask < 0.0001)
        return 0.0;

    float3 driftedPos = posWS + GetVapourNoiseDriftDirectionWS() * (_Time.y * _NoiseDriftSpeed);
    float3 p = driftedPos / max(_NoiseScale, 1e-5);

    float3 warpOffset = float3(
        WaterRaymarchValueNoise3D(p * 0.7 + float3(1.72, 9.23, 5.41)),
        WaterRaymarchValueNoise3D(p * 0.7 + float3(8.31, 2.84, 3.26)),
        WaterRaymarchValueNoise3D(p * 0.7 + float3(4.17, 6.73, 1.92))
    ) * 2.0 - 1.0;

    float3 warpedP = p + warpOffset * 0.35;
    float rawNoise = WaterRaymarchFBM(warpedP, clamp(_NoiseOctaves, 1, 8), 2.0, 0.5);
    float noise01 = pow(saturate(rawNoise), max(_DensityPower, 0.01));

    float maskSoft = smoothstep(0.0, 0.2, mask);
    float edgeFade = CalculateVapourBoundsFadeWS(posWS);
    return saturate(noise01 * maskSoft * edgeFade * _VapourDensityMultiplier);
}

float3 GetEffectiveVapourExtinctionCoefficients()
{
    // Many existing raymarch materials had RGB vapour extinction at zero while
    // the dedicated vapour shader relied on scalar absorption. Keep the RGB tint
    // control, but give vapour a non-zero scalar extinction floor for self-shadowed
    // shafts/god rays.
    float extinctionFloor = max(_VapourAbsorption, 0.0) * 0.05;
    return max(_VapourScatteringCoefficients, float3(extinctionFloor, extinctionFloor, extinctionFloor));
}

// Artist-tweakable liquid density. Use this for iso-surface detection and
// normals so _DensityMultiplier/_DensityOffset visibly move/thicken the water
// surface instead of only changing volumetric absorption/scattering.
float SampleAdjustedLiquidDensityWS(float3 posWS)
{
    return AdjustLiquidDensity(SampleLiquidDensityWS(posWS));
}

float2 SampleAdjustedDensityRG_WS(float3 posWS)
{
    float2 raw = SampleDensityRG_WS(posWS);
    return float2(
        AdjustLiquidDensity(raw.x),
        SampleEnhancedVapourDensityWS(posWS)
    );
}

bool IsInsideDensityBoundsWS(float3 posWS)
{
    return all(posWS >= _PhysicsBoundsMinWS.xyz) && all(posWS <= _PhysicsBoundsMaxWS.xyz);
}

// Liquid density for gradient estimation. Outside the simulated volume is air,
// not clamped edge density, so liquid pressed against the box still produces an
// outward-facing boundary gradient instead of a smeared/zero normal.
float SampleLiquidDensityForNormalWS(float3 posWS)
{
    return IsInsideDensityBoundsWS(posWS) ? SampleAdjustedLiquidDensityWS(posWS) : 0.0;
}

float DistanceToClosestPhysicsBoundsFaceWS(float3 posWS)
{
    float3 distanceToMin = posWS - _PhysicsBoundsMinWS.xyz;
    float3 distanceToMax = _PhysicsBoundsMaxWS.xyz - posWS;
    float3 distanceToFace = min(distanceToMin, distanceToMax);
    return min(distanceToFace.x, min(distanceToFace.y, distanceToFace.z));
}

float3 SampleBakedSurfaceNormalWS(float3 posWS)
{
    float3 bakedNormal = _PhysicsNormalGrid.SampleLevel(
        sampler_PhysicsNormalGrid,
        DensityGridUVW(posWS),
        0
    ).xyz;

    float bakedLength = length(bakedNormal);
    return (bakedLength >= 1e-4) ? (bakedNormal / bakedLength) : float3(0.0, 0.0, 0.0);
}

// Forward declaration for Vulkan/HLSL compilers that require helper functions
// to be declared before first use inside other helper bodies.
float3 ClosestPhysicsBoundsFaceNormalWS(float3 posWS);

float3 ApplyBoundaryNormalBlend(float3 posWS, float3 volumeNormal)
{
    float boundaryBlendDistance = max(_BoundaryNormalBlendDistance, 0.0);
    if (boundaryBlendDistance <= 1e-5)
        return volumeNormal;

    // Smoothly flatten normals near the six simulation-box faces to avoid jagged
    // wall-contact artifacts. Up-facing top-surface normals keep their detail.
    float distanceToFace = max(DistanceToClosestPhysicsBoundsFaceWS(posWS), 0.0);
    float faceWeight = 1.0 - smoothstep(0.0, boundaryBlendDistance, distanceToFace);
    float upBiasReduction = 1.0 - pow(saturate(volumeNormal.y), max(_BoundaryNormalUpBiasPower, 1.0));
    faceWeight *= upBiasReduction;

    float3 faceNormal = ClosestPhysicsBoundsFaceNormalWS(posWS);
    return normalize(lerp(volumeNormal, faceNormal, saturate(faceWeight)));
}

float3 ClosestPhysicsBoundsFaceNormalWS(float3 posWS)
{
    float3 distanceToMin = posWS - _PhysicsBoundsMinWS.xyz;
    float3 distanceToMax = _PhysicsBoundsMaxWS.xyz - posWS;

    float minDistance = distanceToMin.x;
    float3 faceNormal = float3(-1.0, 0.0, 0.0);

    if (distanceToMax.x < minDistance)
    {
        minDistance = distanceToMax.x;
        faceNormal = float3(1.0, 0.0, 0.0);
    }
    if (distanceToMin.y < minDistance)
    {
        minDistance = distanceToMin.y;
        faceNormal = float3(0.0, -1.0, 0.0);
    }
    if (distanceToMax.y < minDistance)
    {
        minDistance = distanceToMax.y;
        faceNormal = float3(0.0, 1.0, 0.0);
    }
    if (distanceToMin.z < minDistance)
    {
        minDistance = distanceToMin.z;
        faceNormal = float3(0.0, 0.0, -1.0);
    }
    if (distanceToMax.z < minDistance)
    {
        faceNormal = float3(0.0, 0.0, 1.0);
    }

    return faceNormal;
}

float3 CalculateLiquidGradientNormalWS(float3 posWS)
{
    float3 sizeWS = max(_PhysicsBoundsMaxWS.xyz - _PhysicsBoundsMinWS.xyz, 1e-5);
    float3 dims   = max(_PhysicsVolumeDims.xyz, 1.0);
    float3 voxelSizeWS = sizeWS / dims;
    float3 e = max(voxelSizeWS * max(_NormalSampleRadiusVoxels, 0.5), 1e-5);

    // Central finite differences: 6 density samples around the hit point.
    // This is the negative density gradient, so it points from dense liquid into air.
    float dx = (SampleLiquidDensityForNormalWS(posWS - float3(e.x, 0.0, 0.0)) - SampleLiquidDensityForNormalWS(posWS + float3(e.x, 0.0, 0.0))) / (2.0 * e.x);
    float dy = (SampleLiquidDensityForNormalWS(posWS - float3(0.0, e.y, 0.0)) - SampleLiquidDensityForNormalWS(posWS + float3(0.0, e.y, 0.0))) / (2.0 * e.y);
    float dz = (SampleLiquidDensityForNormalWS(posWS - float3(0.0, 0.0, e.z)) - SampleLiquidDensityForNormalWS(posWS + float3(0.0, 0.0, e.z))) / (2.0 * e.z);

    float3 normal = float3(dx, dy, dz);
    float len = length(normal);
    return (len >= 1e-4) ? (normal / len) : float3(0.0, 0.0, 0.0);
}

// Surface normal calculated on-the-fly from the liquid density field.
// The vapour channel is intentionally ignored because vapour has no sharp surface.
float3 GetSurfaceNormalWS(float3 posWS, float3 rayDir)
{
    float3 runtimeNormal = CalculateLiquidGradientNormalWS(posWS);
    float3 bakedNormal = SampleBakedSurfaceNormalWS(posWS);
    float runtimeValid = step(1e-8, dot(runtimeNormal, runtimeNormal));
    float bakedValid = step(1e-8, dot(bakedNormal, bakedNormal));

    float3 volumeNormal = runtimeNormal;
    if (runtimeValid > 0.5 && bakedValid > 0.5)
    {
        // Blend control for look-dev:
        //   0.00 = pure runtime normal
        //   0.25 = baked normal contributes 25%
        //   1.00 = pure baked normal
        volumeNormal = normalize(lerp(runtimeNormal, bakedNormal, saturate(_BakedNormalBlend)));
    }
    else if (runtimeValid < 0.5 && bakedValid > 0.5)
    {
        volumeNormal = bakedNormal;
    }

    if (dot(volumeNormal, volumeNormal) < 1e-8)
        return float3(0.0, 0.0, 0.0);

    return ApplyBoundaryNormalBlend(posWS, volumeNormal);
}

#endif
