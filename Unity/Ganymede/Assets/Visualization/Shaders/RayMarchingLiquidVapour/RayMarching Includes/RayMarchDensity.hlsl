#ifndef RAY_MARCH_DENSITY_INCLUDED
#define RAY_MARCH_DENSITY_INCLUDED

// R = liquid density (normalised 0..1), G = secondary non-liquid phase density.
// This include owns generic density sampling and liquid/surface helpers only.
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

// Combined proxy density used ONLY for the marching-cubes shell that bounds the
// raymarch interval. This deliberately treats liquid/vapour as a union field.
float SampleCombinedDensityWS(float3 posWS)
{
    float2 densityRG = SampleDensityRG_WS(posWS);
    return max(densityRG.r, densityRG.g);
}

// Liquid channel only. Use for iso-surface detection, normal gradient estimation,
// and refraction. Other phases must never feed into surface tests.
float SampleLiquidDensityWS(float3 posWS)
{
    return SampleDensityRG_WS(posWS).r;
}

float AdjustLiquidDensity(float rawLiquidDensity)
{
    return max(rawLiquidDensity * _DensityMultiplier + _DensityOffset, 0.0);
}

// Artist-tweakable liquid density. Use this for iso-surface detection and
// normals so _DensityMultiplier/_DensityOffset visibly move/thicken the water
// surface instead of only changing volumetric absorption/scattering.
float SampleAdjustedLiquidDensityWS(float3 posWS)
{
    return AdjustLiquidDensity(SampleLiquidDensityWS(posWS));
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
// The secondary channel is intentionally ignored because it has no sharp surface.
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
