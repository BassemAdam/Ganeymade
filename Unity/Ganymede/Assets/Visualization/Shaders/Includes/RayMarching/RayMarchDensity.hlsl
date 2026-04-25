#ifndef RAY_MARCH_DENSITY_INCLUDED
#define RAY_MARCH_DENSITY_INCLUDED

// R = liquid density (normalised 0..1), G = vapour density (normalised 0..1).
// Liquid has a sharp iso-surface → surface detection, normals, refraction.
// Vapour is purely volumetric → scattering/absorption only, never enters surface tests.
Texture3D<float2> _PhysicsDensityGrid;
SamplerState      sampler_PhysicsDensityGrid;

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

// Vapour channel only. Use for volumetric scattering and absorption accumulation.
float SampleVapourDensityWS(float3 posWS)
{
    return SampleDensityRG_WS(posWS).g;
}

// Pre-baked normal grid written once per frame by BakeNormals kernel (ParticlesToDensityGrid.compute).
// Trilinear sample replaces the 6-tap per-fragment central-difference gradient.
Texture3D<float4> _PhysicsNormalGrid;
SamplerState      sampler_PhysicsNormalGrid;

// Surface normal from the pre-baked outward normal texture.
// rayDir is the fallback when the stored normal magnitude is below the noise floor.
float3 GetSurfaceNormalWS(float3 posWS, float3 rayDir)
{
    float3 sizeWS = max(_PhysicsBoundsMaxWS.xyz - _PhysicsBoundsMinWS.xyz, 1e-5);
    float3 uvw    = (posWS - _PhysicsBoundsMinWS.xyz) / sizeWS;
    float3 n   = _PhysicsNormalGrid.SampleLevel(sampler_PhysicsNormalGrid, uvw, 0).xyz;
    float  len = length(n);
    // Zero normal means this voxel has no detectable density gradient — it is not on a
    // surface. Return zero so MakeSurfaceHit can discard the hit instead of faking a normal.
    return (len >= 1e-4) ? (n / len) : float3(0.0, 0.0, 0.0);
}

#endif
