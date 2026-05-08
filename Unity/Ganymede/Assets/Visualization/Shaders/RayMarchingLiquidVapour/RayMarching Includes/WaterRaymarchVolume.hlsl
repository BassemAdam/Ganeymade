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

Texture2D<float> _WaterProxyEntryDistanceMap;
SamplerState sampler_WaterProxyEntryDistanceMap;
Texture2D<float> _WaterProxyExitDistanceMap;
SamplerState sampler_WaterProxyExitDistanceMap;

float SampleProxyEntryDistance(float2 screenUV)
{
    return _WaterProxyEntryDistanceMap.SampleLevel(sampler_WaterProxyEntryDistanceMap, screenUV, 0);
}

float SampleProxyExitDistance(float2 screenUV)
{
    return _WaterProxyExitDistanceMap.SampleLevel(sampler_WaterProxyExitDistanceMap, screenUV, 0);
}

WaterRaymarchVolumeData BuildWaterRaymarchVolumeData(WaterRaymarchViewData viewData)
{
    WaterRaymarchVolumeData volumeData;
    float2 intersectionDistances = RayBoxDst(
        viewData.cameraPositionWS,
        viewData.viewRayDirectionWS,
        _PhysicsBoundsMinWS.xyz,
        _PhysicsBoundsMaxWS.xyz);

    float boxEntryDistance = intersectionDistances.x;
    float boxDistanceInside = intersectionDistances.y;
    float boxExitDistance = boxEntryDistance + boxDistanceInside;

    volumeData.distanceToVolume = boxEntryDistance;
    volumeData.distanceInsideVolume = boxDistanceInside;
    volumeData.intersectsVolume = (volumeData.distanceInsideVolume > 1e-5);
    volumeData.volumeExitDistance = boxExitDistance;
    volumeData.entryPositionWS = viewData.cameraPositionWS + viewData.viewRayDirectionWS * volumeData.distanceToVolume;

    if (_UseMarchingCubesProxy <= 0.5 || !volumeData.intersectsVolume)
        return volumeData;

    float proxyExitDistance = SampleProxyExitDistance(viewData.screenUV);
    if (proxyExitDistance <= 1e-5)
    {
        volumeData.intersectsVolume = false;
        volumeData.distanceInsideVolume = 0.0;
        volumeData.volumeExitDistance = volumeData.distanceToVolume;
        return volumeData;
    }

    bool cameraInsideProxy = SampleCombinedDensityWS(viewData.cameraPositionWS) >= _ProxyIsoLevel;
    float proxyEntryDistance = cameraInsideProxy ? 0.0 : SampleProxyEntryDistance(viewData.screenUV);

    if (!cameraInsideProxy && proxyEntryDistance >= 1e5)
    {
        volumeData.intersectsVolume = false;
        volumeData.distanceInsideVolume = 0.0;
        volumeData.volumeExitDistance = volumeData.distanceToVolume;
        return volumeData;
    }

    volumeData.distanceToVolume = cameraInsideProxy
        ? 0.0
        : max(proxyEntryDistance, boxEntryDistance);
    volumeData.volumeExitDistance = min(proxyExitDistance, boxExitDistance);
    volumeData.distanceInsideVolume = max(0.0, volumeData.volumeExitDistance - volumeData.distanceToVolume);
    volumeData.intersectsVolume = (volumeData.distanceInsideVolume > 1e-5);
    volumeData.entryPositionWS = viewData.cameraPositionWS + viewData.viewRayDirectionWS * volumeData.distanceToVolume;
    return volumeData;
}

#endif
