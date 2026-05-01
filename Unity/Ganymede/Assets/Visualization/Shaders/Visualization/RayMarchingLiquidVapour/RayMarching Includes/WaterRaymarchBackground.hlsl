#ifndef WATER_RAYMARCH_BACKGROUND_INCLUDED
#define WATER_RAYMARCH_BACKGROUND_INCLUDED

struct WaterRaymarchBackgroundData
{
    float2     backgroundScreenUV;
    float      sceneDistanceAlongRay;
    SurfaceHit surfaceHit;
};

float2 CalculateRefractedSceneUV(
    WaterRaymarchViewData viewData,
    WaterRaymarchVolumeData volumeData,
    SurfaceHit surfaceHit,
    float refractionStrength)
{
    if (!surfaceHit.hit || surfaceHit.totalInternalReflection || refractionStrength <= 1e-5)
        return viewData.screenUV;

    // Nudge the origin along the refracted ray so the ray-box test starts inside
    // the water volume instead of immediately re-hitting the same surface.
    float3 refractDir = normalize(surfaceHit.refractDir);
    float3 refractOriginWS = surfaceHit.posWS + refractDir * 1e-3;
    float2 refractBounds = RayBoxDst(
        refractOriginWS,
        refractDir,
        _PhysicsBoundsMinWS.xyz,
        _PhysicsBoundsMaxWS.xyz
    );

    float distanceToExit = refractBounds.x + refractBounds.y;
    if (distanceToExit <= 1e-5)
        return viewData.screenUV;

    float3 refractedExitPositionWS = refractOriginWS + refractDir * distanceToExit;
    float2 physicallyRefractedUV = ProjectWorldPositionToScreenUV(refractedExitPositionWS);

    // Let _RefractionStrength art-direct the bend amount while keeping the bend
    // direction based on the real refracted ray leaving the cube/volume bounds.
    return clamp(
        lerp(viewData.screenUV, physicallyRefractedUV, saturate(refractionStrength)),
        0.001,
        0.999
    );
}

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
    bool rayStartsInsideVolume = (volumeData.distanceToVolume < 1e-5);
    bool rayStartsInsideWater = rayStartsInsideVolume
        && SampleAdjustedLiquidDensityWS(viewData.cameraPositionWS) >= surfaceThreshold;

    if (!rayStartsInsideVolume)
    {
        float entryDensity = SampleAdjustedLiquidDensityWS(volumeData.entryPositionWS);
        if (entryDensity >= surfaceThreshold)
            return MakeSurfaceHit(volumeData.entryPositionWS, viewData.viewRayDirectionWS, true);
    }

    SurfaceHit surfaceHit = NoSurfaceHit();
    float sampleDistance = volumeData.distanceToVolume;
    float previousDensity = rayStartsInsideWater
        ? SampleAdjustedLiquidDensityWS(viewData.cameraPositionWS)
        : SampleAdjustedLiquidDensityWS(viewData.cameraPositionWS + viewData.viewRayDirectionWS * sampleDistance);
    float3 previousPositionWS = rayStartsInsideWater
        ? viewData.cameraPositionWS
        : viewData.cameraPositionWS + viewData.viewRayDirectionWS * sampleDistance;
    float3 lastInsideWaterPositionWS = rayStartsInsideWater
        ? viewData.cameraPositionWS
        : viewData.cameraPositionWS + viewData.viewRayDirectionWS * sampleDistance;

    // Jitter only after establishing whether the ray begins inside water. If the
    // camera is just under the surface, starting the state check at a blue-noise
    // offset can skip over the exit crossing and make underwater normals unstable.
    sampleDistance += safeStepSize * viewData.blueNoiseValue;

    while (!surfaceHit.hit && sampleDistance < maxSearchDistance)
    {
        float3 samplePositionWS = viewData.cameraPositionWS + viewData.viewRayDirectionWS * sampleDistance;
        float currentDensity = SampleAdjustedLiquidDensityWS(samplePositionWS);

        bool enteringWater = previousDensity < surfaceThreshold && currentDensity >= surfaceThreshold;
        bool leavingWater = previousDensity >= surfaceThreshold && currentDensity < surfaceThreshold;
        if (enteringWater || leavingWater)
        {
            float densityDelta = currentDensity - previousDensity;
            float crossingT = (abs(densityDelta) > 1e-5)
                ? saturate((surfaceThreshold - previousDensity) / densityDelta)
                : 1.0;
            float3 surfacePositionWS = lerp(previousPositionWS, samplePositionWS, crossingT);
            surfaceHit = MakeSurfaceHit(surfacePositionWS, viewData.viewRayDirectionWS, enteringWater);
        }

        if (currentDensity >= surfaceThreshold)
            lastInsideWaterPositionWS = samplePositionWS;

        sampleDistance += safeStepSize;
        previousDensity = currentDensity;
        previousPositionWS = samplePositionWS;
    }

    // If the camera/ray is inside liquid and the liquid reaches the simulation
    // box edge, there may be no density drop before the volume exit. Treat the
    // last in-water sample as a Water→Air exit so boundary normals and IOR are
    // still valid instead of returning no/garbage surface.
    if (!surfaceHit.hit && previousDensity >= surfaceThreshold)
        surfaceHit = MakeSurfaceHit(lastInsideWaterPositionWS, viewData.viewRayDirectionWS, false);

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
        backgroundData.backgroundScreenUV = CalculateRefractedSceneUV(
            viewData,
            volumeData,
            backgroundData.surfaceHit,
            refractionStrength
        );
    }

    return backgroundData;
}

float3 ComposeWaterBackgroundColor(
    WaterRaymarchBackgroundData backgroundData,
    float2 originalScreenUV,
    float reflectionStrength,
    float2 reflectionScreenOffset,
    float reflectionVisibilityBoost,
    float reflectionVisibilityFloor)
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
        // Sampling is inverse to apparent image movement, so subtract the
        // artist offset: positive X/Y moves the reflection right/up on screen.
        originalScreenUV + reflectedDirectionVS.xy * 0.08 * reflectionStrength - reflectionScreenOffset,
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
    float boostedFresnel = saturate(backgroundData.surfaceHit.fresnel * max(reflectionVisibilityBoost, 1.0));
    float reflectionWeight = saturate(max(boostedFresnel, reflectionVisibilityFloor) * reflectionStrength);

    return lerp(
        refractedSceneColor,
        reflectedColor,
        reflectionWeight
    );
}

#endif
