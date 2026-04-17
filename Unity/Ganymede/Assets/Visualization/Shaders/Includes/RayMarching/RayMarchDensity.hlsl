#ifndef RAY_MARCH_DENSITY_INCLUDED
#define RAY_MARCH_DENSITY_INCLUDED

Texture3D<float> _PhysicsDensityGrid;
SamplerState sampler_PhysicsDensityGrid;

float4 _PhysicsVolumeDims;    
float4 _PhysicsBoundsMinWS;   
float4 _PhysicsBoundsMaxWS;   

float SampleDensityWS(float3 samplePosWS, float densityOffset, float densityMultiplier)
{
    float3 sizeWS    = max(_PhysicsBoundsMaxWS.xyz - _PhysicsBoundsMinWS.xyz, 1e-5);
    float3 gridSize  = max(_PhysicsVolumeDims.xyz, 1.0);
    float3 uvw       = (samplePosWS - _PhysicsBoundsMinWS.xyz) / sizeWS + 0.5 / gridSize;

    float rawDensity = _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw, 0).r;
    return (rawDensity + densityOffset) * densityMultiplier;
}

#endif
