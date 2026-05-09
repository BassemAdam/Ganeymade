#ifndef WATER_RAYMARCH_VOLUME_INCLUDED
#define WATER_RAYMARCH_VOLUME_INCLUDED

struct WaterRaymarchVolumeData
{
    bool   intersectsVolume;
    float  distanceToVolume;
    float  distanceInsideVolume;
    float  volumeExitDistance;
    float3 entryPositionWS;
};

WaterRaymarchVolumeData BuildWaterRaymarchVolumeData(float3 cameraPositionWS, float3 viewRayDirectionWS)
{
    WaterRaymarchVolumeData volumeData;
    float2 intersectionDistances = RayBoxDst(cameraPositionWS, viewRayDirectionWS, _PhysicsBoundsMinWS.xyz, _PhysicsBoundsMaxWS.xyz);

    volumeData.distanceToVolume = intersectionDistances.x;
    volumeData.distanceInsideVolume = intersectionDistances.y;
    volumeData.intersectsVolume = (volumeData.distanceInsideVolume > 1e-5);
    volumeData.volumeExitDistance = volumeData.distanceToVolume + volumeData.distanceInsideVolume;
    volumeData.entryPositionWS = cameraPositionWS + viewRayDirectionWS * volumeData.distanceToVolume;
    return volumeData;
}

#endif
