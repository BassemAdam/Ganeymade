#ifndef RAY_MARCH_DENSITY_INCLUDED
#define RAY_MARCH_DENSITY_INCLUDED

Texture3D<float2> _PhysicsDensityGrid;
SamplerState      sampler_PhysicsDensityGrid;
Texture3D<float4> _PhysicsNormalGrid;
SamplerState      sampler_PhysicsNormalGrid;

float4 _PhysicsVolumeDims;
float4 _PhysicsBoundsMinWS;
float4 _PhysicsBoundsMaxWS;

float3 DensityGridUVW(float3 posWS)
{
    float3 sizeWS   = max(_PhysicsBoundsMaxWS.xyz - _PhysicsBoundsMinWS.xyz, 1e-5);
    float3 gridSize = max(_PhysicsVolumeDims.xyz, 1.0);
    return (posWS - _PhysicsBoundsMinWS.xyz) / sizeWS + 0.5 / gridSize;
}

float2 SampleDensityRG_WS(float3 posWS)
{
    return _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, DensityGridUVW(posWS), 0);
}

float SampleLiquidDensityWS(float3 posWS)
{
    return SampleDensityRG_WS(posWS).r;
}

float AdjustLiquidDensity(float rawLiquidDensity)
{
    return max(rawLiquidDensity * _DensityMultiplier + _DensityOffset, 0.0);
}

float SampleAdjustedLiquidDensityWS(float3 posWS)
{
    return AdjustLiquidDensity(SampleLiquidDensityWS(posWS));
}

bool IsInsideDensityBoundsWS(float3 posWS)
{
    return all(posWS >= _PhysicsBoundsMinWS.xyz) && all(posWS <= _PhysicsBoundsMaxWS.xyz);
}

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

float3 ClosestPhysicsBoundsFaceNormalWS(float3 posWS);

float3 ApplyBoundaryNormalBlend(float3 posWS, float3 volumeNormal)
{
    float boundaryBlendDistance = max(_BoundaryNormalBlendDistance, 0.0);
    if (boundaryBlendDistance <= 1e-5)
        return volumeNormal;

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

    float dx = (SampleLiquidDensityForNormalWS(posWS - float3(e.x, 0.0, 0.0)) - SampleLiquidDensityForNormalWS(posWS + float3(e.x, 0.0, 0.0))) / (2.0 * e.x);
    float dy = (SampleLiquidDensityForNormalWS(posWS - float3(0.0, e.y, 0.0)) - SampleLiquidDensityForNormalWS(posWS + float3(0.0, e.y, 0.0))) / (2.0 * e.y);
    float dz = (SampleLiquidDensityForNormalWS(posWS - float3(0.0, 0.0, e.z)) - SampleLiquidDensityForNormalWS(posWS + float3(0.0, 0.0, e.z))) / (2.0 * e.z);

    float3 normal = float3(dx, dy, dz);
    float len = length(normal);
    return (len >= 1e-4) ? (normal / len) : float3(0.0, 0.0, 0.0);
}

float3 GetSurfaceNormalWS(float3 posWS, float3 rayDir)
{
    float3 runtimeNormal = CalculateLiquidGradientNormalWS(posWS);
    float3 bakedNormal = SampleBakedSurfaceNormalWS(posWS);
    float runtimeValid = step(1e-8, dot(runtimeNormal, runtimeNormal));
    float bakedValid = step(1e-8, dot(bakedNormal, bakedNormal));

    float3 volumeNormal = runtimeNormal;
    if (runtimeValid > 0.5 && bakedValid > 0.5)
    {
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
