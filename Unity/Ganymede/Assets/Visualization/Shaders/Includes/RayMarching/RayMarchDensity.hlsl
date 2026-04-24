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

// Surface normal from the liquid density gradient.
// Vapour is excluded: volumetric fields have no meaningful iso-surface to differentiate.
// rayDir is the fallback when the gradient magnitude is below the noise floor.
float3 GetSurfaceNormalWS(float3 posWS, float3 rayDir)
{
    float3 gridSize = max(_PhysicsVolumeDims.xyz, 1.0);
    float3 uvw      = DensityGridUVW(posWS);
    // 4-voxel half-span ensures samples straddle the SPH surface transition.
    float3 eps      = 4.0 / gridSize;

    float dx = _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw + float3(eps.x, 0,     0    ), 0).r
             - _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw - float3(eps.x, 0,     0    ), 0).r;
    float dy = _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw + float3(0,     eps.y, 0    ), 0).r
             - _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw - float3(0,     eps.y, 0    ), 0).r;
    float dz = _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw + float3(0,     0,     eps.z), 0).r
             - _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw - float3(0,     0,     eps.z), 0).r;

    float3 gradient    = float3(dx, dy, dz);
    float  gradientLen = length(gradient);
    if (gradientLen >= 1e-4)
        return gradient / gradientLen;

    // Fallback: no detectable gradient — oppose the incoming ray.
    return -normalize(rayDir);
}

#endif
