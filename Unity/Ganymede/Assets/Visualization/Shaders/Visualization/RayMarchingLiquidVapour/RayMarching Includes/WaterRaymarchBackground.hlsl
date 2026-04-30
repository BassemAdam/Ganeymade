#ifndef WATER_RAYMARCH_BACKGROUND_INCLUDED
#define WATER_RAYMARCH_BACKGROUND_INCLUDED

struct WaterRaymarchBackgroundData
{
    float2     backgroundScreenUV;
    float      sceneDistanceAlongRay;
    SurfaceHit surfaceHit;
};

SurfaceHit FindWaterSurfaceHit(
    WaterRaymarchViewData viewData,
    WaterRaymarchVolumeData volumeData,
    float maxSearchDistance,
    float stepSize,
    float isoLevel,
    float surfaceDetectionMargin)
{
    float safeStepSize = max(stepSize, 1e-4);
    float surfaceThreshold = isoLevel + surfaceDetectionMargin;
    bool cameraStartsInsideVolume = (volumeData.distanceToVolume < 1e-5);

    if (!cameraStartsInsideVolume)
    {
        float entryDensity = SampleAdjustedLiquidDensityWS(volumeData.entryPositionWS);
        if (entryDensity >= isoLevel)
            return MakeSurfaceHit(volumeData.entryPositionWS, viewData.viewRayDirectionWS, true);
    }

    SurfaceHit surfaceHit = NoSurfaceHit();
    float sampleDistance = volumeData.distanceToVolume + safeStepSize * viewData.blueNoiseValue;
    float previousDensity = SampleAdjustedLiquidDensityWS(
        viewData.cameraPositionWS + viewData.viewRayDirectionWS * sampleDistance
    );

    while (!surfaceHit.hit && sampleDistance < maxSearchDistance)
    {
        float3 samplePositionWS = viewData.cameraPositionWS + viewData.viewRayDirectionWS * sampleDistance;
        float currentDensity = SampleAdjustedLiquidDensityWS(samplePositionWS);

        bool enteringWater = previousDensity < surfaceThreshold && currentDensity >= surfaceThreshold;
        bool leavingWater = previousDensity >= surfaceThreshold && currentDensity < surfaceThreshold;
        if (enteringWater || leavingWater)
            surfaceHit = MakeSurfaceHit(samplePositionWS, viewData.viewRayDirectionWS, enteringWater);

        sampleDistance += safeStepSize;
        previousDensity = currentDensity;
    }

    return surfaceHit;
}

WaterRaymarchBackgroundData BuildWaterRaymarchBackgroundData(
    WaterRaymarchViewData viewData,
    WaterRaymarchVolumeData volumeData,
    float stepSize,
    float isoLevel,
    float surfaceDetectionMargin,
    float refractionStrength)
{
    WaterRaymarchBackgroundData backgroundData;
    backgroundData.backgroundScreenUV = viewData.screenUV;
    backgroundData.sceneDistanceAlongRay = SampleSceneDistanceAlongRay(viewData.screenUV, viewData.viewDepthDenominator);
    backgroundData.surfaceHit = NoSurfaceHit();

    if (backgroundData.sceneDistanceAlongRay <= volumeData.distanceToVolume)
        return backgroundData;

    // // Two-sample liquid presence test before committing to the full surface march.
    // // Sample liquid density at the volume entry and at the mid-point of the volume.
    // // If both return zero the scene is vapour-only (or boundary-only) — no iso-surface
    // // exists and the entire FindWaterSurfaceHit march can be skipped entirely.
    // {
    //     float3 midPointWS = volumeData.entryPositionWS
    //         + viewData.viewRayDirectionWS * (volumeData.distanceInsideVolume * 0.5);
    //     bool hasLiquid = SampleAdjustedLiquidDensityWS(volumeData.entryPositionWS) > 0.0
    //                   || SampleAdjustedLiquidDensityWS(midPointWS)                 > 0.0;
    //     if (!hasLiquid)
    //         return backgroundData;
    // }

    float initialSearchDistance = min(volumeData.volumeExitDistance, backgroundData.sceneDistanceAlongRay);
    backgroundData.surfaceHit = FindWaterSurfaceHit(
        viewData,
        volumeData,
        initialSearchDistance,
        stepSize,
        isoLevel,
        surfaceDetectionMargin
    );

    if (backgroundData.surfaceHit.hit)
    {
        float3 refractedDirectionVS = mul((float3x3)UNITY_MATRIX_V, backgroundData.surfaceHit.refractDir);
        backgroundData.backgroundScreenUV = clamp(
            viewData.screenUV + refractedDirectionVS.xy * refractionStrength,
            0.001,
            0.999
        );
        backgroundData.sceneDistanceAlongRay = SampleSceneDistanceAlongRay(
            backgroundData.backgroundScreenUV,
            viewData.viewDepthDenominator
        );
    }

    return backgroundData;
}

float3 ComposeWaterBackgroundColor(
    WaterRaymarchBackgroundData backgroundData,
    float2 originalScreenUV,
    float reflectionStrength)
{
    float3 refractedSceneColor = SAMPLE_TEXTURE2D(
        _CameraOpaqueTexture,
        sampler_CameraOpaqueTexture,
        backgroundData.backgroundScreenUV
    ).rgb;

    if (!backgroundData.surfaceHit.hit)
        return refractedSceneColor;

    float3 reflectedDirectionVS = mul((float3x3)UNITY_MATRIX_V, backgroundData.surfaceHit.reflectDir);
    float2 reflectedScreenUV = clamp(
        originalScreenUV + reflectedDirectionVS.xy * 0.08 * reflectionStrength,
        0.001,
        0.999
    );
    float3 reflectedSceneColor = SAMPLE_TEXTURE2D(
        _CameraOpaqueTexture,
        sampler_CameraOpaqueTexture,
        reflectedScreenUV
    ).rgb;

    float3 reflectedEnvironmentColor = GlossyEnvironmentReflection(
        backgroundData.surfaceHit.reflectDir,
        backgroundData.surfaceHit.posWS,
        0.0h,
        1.0h,
        originalScreenUV
    );

    float reflectedEnvironmentLuminance = dot(reflectedEnvironmentColor, float3(0.2126, 0.7152, 0.0722));
    float environmentReflectionWeight = saturate(reflectedEnvironmentLuminance * 4.0);
    float3 reflectedColor = lerp(reflectedSceneColor, reflectedEnvironmentColor, environmentReflectionWeight);
    float reflectionWeight = saturate(backgroundData.surfaceHit.fresnel * reflectionStrength);

    return lerp(
        refractedSceneColor,
        reflectedColor,
        reflectionWeight
    );
}

#endif
