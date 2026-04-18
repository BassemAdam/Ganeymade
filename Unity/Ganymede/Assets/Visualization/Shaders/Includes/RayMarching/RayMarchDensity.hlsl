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

float3 GetSurfaceNormalWS(float3 posWS)
{
    float3 sizeWS   = max(_PhysicsBoundsMaxWS.xyz - _PhysicsBoundsMinWS.xyz, 1e-5);
    float3 gridSize = max(_PhysicsVolumeDims.xyz, 1.0);
    float3 uvw      = (posWS - _PhysicsBoundsMinWS.xyz) / sizeWS;
    float3 eps      = 2.0 / gridSize;

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

    float3 distToMin = posWS - _PhysicsBoundsMinWS.xyz;
    float3 distToMax = _PhysicsBoundsMaxWS.xyz - posWS;
    float  nearestDist   = distToMin.x; float3 nearestNormal = float3(-1,  0,  0);
    if (distToMax.x < nearestDist) { nearestDist = distToMax.x; nearestNormal = float3( 1,  0,  0); }
    if (distToMin.y < nearestDist) { nearestDist = distToMin.y; nearestNormal = float3( 0, -1,  0); }
    if (distToMax.y < nearestDist) { nearestDist = distToMax.y; nearestNormal = float3( 0,  1,  0); }
    if (distToMin.z < nearestDist) { nearestDist = distToMin.z; nearestNormal = float3( 0,  0, -1); }
    if (distToMax.z < nearestDist) {                            nearestNormal = float3( 0,  0,  1); }
    return nearestNormal;
}

#endif
