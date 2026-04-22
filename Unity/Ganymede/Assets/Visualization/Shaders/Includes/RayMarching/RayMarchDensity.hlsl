#ifndef RAY_MARCH_DENSITY_INCLUDED
#define RAY_MARCH_DENSITY_INCLUDED

Texture3D<float> _PhysicsDensityGrid;
SamplerState sampler_PhysicsDensityGrid;

float4 _PhysicsVolumeDims;    
float4 _PhysicsBoundsMinWS;   
float4 _PhysicsBoundsMaxWS;   

float SampleDensityWS(float3 samplePosWS, float densityOffset, float densityMultiplier)
{
    float3 sizeWS   = max(_PhysicsBoundsMaxWS.xyz - _PhysicsBoundsMinWS.xyz, 1e-5);
    float3 gridSize = max(_PhysicsVolumeDims.xyz, 1.0);
    float3 uvw      = (samplePosWS - _PhysicsBoundsMinWS.xyz) / sizeWS + 0.5 / gridSize;
    float rawDensity = _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw, 0).r;
    return (rawDensity + densityOffset) * densityMultiplier;
}

// rayDir is the fallback when the density gradient is too flat to determine surface orientation.
float3 GetSurfaceNormalWS(float3 posWS, float3 rayDir)
{
    float3 sizeWS   = max(_PhysicsBoundsMaxWS.xyz - _PhysicsBoundsMinWS.xyz, 1e-5);
    float3 gridSize = max(_PhysicsVolumeDims.xyz, 1.0);
    float3 uvw      = (posWS - _PhysicsBoundsMinWS.xyz) / sizeWS + 0.5 / gridSize;
    // 4-voxel half-span (8 voxels total) ensures samples straddle the SPH surface
    // transition rather than both landing inside the uniform-density interior,
    // which would make the gravity-driven vertical gradient dominate over the
    // actual surface-facing horizontal gradient at side surfaces.
    float3 eps      = 4.0 / gridSize;

    float dx = _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw + float3(eps.x, 0,     0    ), 0).r
             - _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw - float3(eps.x, 0,     0    ), 0).r;
    float dy = _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw + float3(0,     eps.y, 0    ), 0).r
             - _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw - float3(0,     eps.y, 0    ), 0).r;
    float dz = _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw + float3(0,     0,     eps.z), 0).r
             - _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw - float3(0,     0,     eps.z), 0).r;

    float3 gradient    = float3(dx, dy, dz);
    float  gradientLen = length(gradient);
    // Same threshold as original — accepts any detectable gradient.
    if (gradientLen >= 1e-4)
        return gradient / gradientLen;

    // Fallback: gradient is flat (both samples in uniform interior, empty space,
    // or thin-fluid where both samples exit the fluid). Use -rayDir: always opposes
    // the incoming ray and avoids the old nearest-box-face heuristic which returns
    // the wrong face for fluid that isn't touching the bounding box walls.
    return -normalize(rayDir);
}

#endif
