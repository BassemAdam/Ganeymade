#ifndef WATER_RAYMARCH_BACKGROUND_INCLUDED
#define WATER_RAYMARCH_BACKGROUND_INCLUDED

struct WaterRaymarchBackgroundData
{
    float2     backgroundScreenUV;
    float      sceneDistanceAlongRay;
    SurfaceHit surfaceHit;
};

struct WaterBackgroundContributions
{
    float3 reflectionContribution;
    float3 refractionContribution;
    float3 reflectedEnvironmentColor;
    float3 reflectedSSRColor;
    WaterSSRTraceResult ssrTrace;
};

float CalculateLiquidOpticalDepthAlongRay(float3 rayPosWS, float3 rayDirWS, float stepSize)
{
    if (dot(rayDirWS, rayDirWS) < 0.9)
        return 0.0;

    float safeStepSize = max(stepSize, 1e-4);
    float3 rayDir = normalize(rayDirWS);
    float2 boundsDistance = RayBoxDst(
        rayPosWS,
        rayDir,
        _PhysicsBoundsMinWS.xyz,
        _PhysicsBoundsMaxWS.xyz
    );

    float distanceToBounds = boundsDistance.x;
    float distanceThroughBounds = boundsDistance.y;
    if (distanceThroughBounds <= 1e-5)
        return 0.0;

    float nudge = safeStepSize * 0.5;
    float tinyNudge = max(1e-3, safeStepSize * 0.05);
    float3 entryPositionWS = rayPosWS + rayDir * (distanceToBounds + nudge);
    float maxDistance = max(0.0, distanceThroughBounds - nudge - tinyNudge);

    float travelled = 0.0;
    float opticalDepth = 0.0;
    [loop]
    while (travelled < maxDistance)
    {
        float3 samplePositionWS = entryPositionWS + rayDir * travelled;
        float density = max(0.0, SampleAdjustedLiquidDensityWS(samplePositionWS));
        opticalDepth += density * safeStepSize;
        travelled += safeStepSize;
    }

    return opticalDepth;
}

float3 RefineLiquidSurfacePositionWS(
    float3 outsidePositionWS,
    float3 insidePositionWS,
    float surfaceThreshold,
    int refinementIterations)
{
    float3 aWS = outsidePositionWS;
    float3 bWS = insidePositionWS;
    float da = SampleLiquidDensityForNormalWS(aWS) - surfaceThreshold;
    float db = SampleLiquidDensityForNormalWS(bWS) - surfaceThreshold;

    [loop]
    for (int iteration = 0; iteration < refinementIterations; iteration++)
    {
        float weightDenominator = abs(da) + abs(db);
        float midpointWeight = (weightDenominator > 1e-5)
            ? saturate(abs(da) / weightDenominator)
            : 0.5;
        float3 midpointWS = lerp(aWS, bWS, midpointWeight);
        float midpointDensity = SampleLiquidDensityForNormalWS(midpointWS) - surfaceThreshold;

        bool midpointMatchesA = (da < 0.0 && midpointDensity < 0.0)
                             || (da >= 0.0 && midpointDensity >= 0.0);
        if (midpointMatchesA)
        {
            aWS = midpointWS;
            da = midpointDensity;
        }
        else
        {
            bWS = midpointWS;
            db = midpointDensity;
        }
    }

    return 0.5 * (aWS + bWS);
}

float2 CalculateRefractedSceneUV(
    WaterRaymarchViewData viewData,
    WaterRaymarchVolumeData volumeData,
    SurfaceHit surfaceHit,
    float refractionStrength)
{
    if (!surfaceHit.hit || surfaceHit.totalInternalReflection || refractionStrength <= 1e-5)
        return viewData.screenUV;

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

    float2 uvOvershoot = max(float2(0, 0), abs(physicallyRefractedUV - 0.5) - 0.5);
    float  offScreen   = max(uvOvershoot.x, uvOvershoot.y);
    float  edgeFade    = 1.0 - saturate(offScreen * 20.0);

    return clamp(
        lerp(viewData.screenUV, physicallyRefractedUV, saturate(refractionStrength) * edgeFade),
        0.001,
        0.999
    );
}

bool IsBottomPhysicsBoundsFaceWS(float3 positionWS)
{
    float3 clampedPositionWS = clamp(
        positionWS,
        _PhysicsBoundsMinWS.xyz,
        _PhysicsBoundsMaxWS.xyz
    );
    return ClosestPhysicsBoundsFaceNormalWS(clampedPositionWS).y < -0.5;
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

    SurfaceHit surfaceHit = NoSurfaceHit();
    float sampleDistance = volumeData.distanceToVolume;

    float previousDensity;
    float3 previousPositionWS;
    if (rayStartsInsideWater)
    {
        previousDensity    = SampleAdjustedLiquidDensityWS(viewData.cameraPositionWS);
        previousPositionWS = viewData.cameraPositionWS;
    }
    else
    {
        previousDensity = 0.0;
        float anchorOffset = max(0.0, sampleDistance - safeStepSize * 0.5);
        previousPositionWS = viewData.cameraPositionWS + viewData.viewRayDirectionWS * anchorOffset;
    }

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
                : 0.5;
            float3 outsidePositionWS = enteringWater ? previousPositionWS : samplePositionWS;
            float3 insidePositionWS = enteringWater ? samplePositionWS : previousPositionWS;
            int refineIterations = clamp((int)round(_SurfaceRefineIterations), 0, 8);
            float3 surfacePositionWS = (refineIterations > 0)
                ? RefineLiquidSurfacePositionWS(
                    outsidePositionWS,
                    insidePositionWS,
                    surfaceThreshold,
                    refineIterations)
                : lerp(previousPositionWS, samplePositionWS, crossingT);
            surfaceHit = MakeSurfaceHit(surfacePositionWS, viewData.viewRayDirectionWS, enteringWater);
        }

        sampleDistance += safeStepSize;
        previousDensity = currentDensity;
        previousPositionWS = samplePositionWS;
    }

    float boundaryTolerance = max(1e-3, safeStepSize * 1.5);
    bool searchReachedVolumeExit = maxSearchDistance >= volumeData.volumeExitDistance - boundaryTolerance;
    if (!surfaceHit.hit && searchReachedVolumeExit && previousDensity >= surfaceThreshold)
    {
        float3 boundaryPositionWS = viewData.cameraPositionWS
                                  + viewData.viewRayDirectionWS * volumeData.volumeExitDistance;
        if (!IsBottomPhysicsBoundsFaceWS(boundaryPositionWS))
        {
            float insideNudge = max(1e-3, safeStepSize * 0.25);
            float3 surfacePositionWS = clamp(
                boundaryPositionWS - viewData.viewRayDirectionWS * insideNudge,
                _PhysicsBoundsMinWS.xyz,
                _PhysicsBoundsMaxWS.xyz
            );
            surfaceHit = MakeSurfaceHit(surfacePositionWS, viewData.viewRayDirectionWS, !rayStartsInsideWater);
        }
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
    float originalSceneDistanceAlongRay = SampleSceneDistanceAlongRay(viewData.screenUV, viewData.viewDepthDenominator);
    backgroundData.sceneDistanceAlongRay = originalSceneDistanceAlongRay;
    backgroundData.surfaceHit = NoSurfaceHit();

    if (originalSceneDistanceAlongRay <= volumeData.distanceToVolume)
        return backgroundData;

    float initialSearchDistance = min(volumeData.volumeExitDistance, originalSceneDistanceAlongRay);
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
        float refractedSceneDistanceAlongRay = SampleSceneDistanceAlongRay(
            backgroundData.backgroundScreenUV,
            viewData.viewDepthDenominator
        );

        backgroundData.sceneDistanceAlongRay = (refractedSceneDistanceAlongRay > volumeData.distanceToVolume)
            ? refractedSceneDistanceAlongRay
            : volumeData.volumeExitDistance;
    }

    return backgroundData;
}

float3 LiquidTransmittance(float opticalDepth, float3 extinctionCoefficients)
{
    return exp(-opticalDepth * extinctionCoefficients);
}

float3 SampleRawSceneSpecCube(float3 reflectDirWS)
{
    half4 encoded = SAMPLE_TEXTURECUBE_LOD(
        unity_SpecCube0,
        samplerunity_SpecCube0,
        normalize(reflectDirWS),
        0
    );
    return DecodeHDREnvironment(encoded, unity_SpecCube0_HDR);
}

float3 SampleGlossyReflectionEnvironment(float3 reflectDirWS, float3 positionWS, float2 normalizedScreenUV)
{
    return GlossyEnvironmentReflection(
        normalize(reflectDirWS),
        positionWS,
        0.0h,
        1.0h,
        normalizedScreenUV
    );
}

float3 SampleReflectionEnvironment(float3 reflectDirWS, float3 positionWS, float2 normalizedScreenUV)
{
    float3 glossyEnvironment = SampleGlossyReflectionEnvironment(
        reflectDirWS,
        positionWS,
        normalizedScreenUV
    );

    float glossyMax = max(glossyEnvironment.r, max(glossyEnvironment.g, glossyEnvironment.b));
    if (glossyMax > 1e-4)
        return glossyEnvironment;

    return SampleRawSceneSpecCube(reflectDirWS);
}

WaterBackgroundContributions ComputeWaterBackgroundContributions(
    WaterRaymarchBackgroundData backgroundData,
    float2 originalScreenUV,
    float3 extinctionCoefficients,
    float reflectionStrength,
    float reflectionVisibilityBoost,
    float reflectionVisibilityFloor)
{
    WaterBackgroundContributions contributions;
    contributions.reflectionContribution = 0.0;
    contributions.refractionContribution = SAMPLE_TEXTURE2D(
        _CameraOpaqueTexture,
        sampler_CameraOpaqueTexture,
        backgroundData.backgroundScreenUV
    ).rgb;
    contributions.reflectedEnvironmentColor = 0.0;
    contributions.reflectedSSRColor = 0.0;
    contributions.ssrTrace = MakeWaterSSRTraceResultDefault();

    if (!backgroundData.surfaceHit.hit)
        return contributions;

    contributions.ssrTrace = TraceWaterScreenSpaceReflection(
        backgroundData.surfaceHit,
        originalScreenUV
    );
    contributions.reflectedSSRColor = contributions.ssrTrace.hitColor;

    float3 rawEnvColor = SampleReflectionEnvironment(
        backgroundData.surfaceHit.reflectDir,
        backgroundData.surfaceHit.posWS,
        originalScreenUV
    );
    float3 combinedReflectSourceColor = lerp(
        rawEnvColor,
        contributions.ssrTrace.hitColor,
        contributions.ssrTrace.blendWeight
    );
    contributions.reflectedEnvironmentColor = combinedReflectSourceColor * reflectionStrength;

    float physicalReflectWeight = backgroundData.surfaceHit.reflectWeight;

    float energyToReflect = physicalReflectWeight * saturate(reflectionStrength);
    float energyToRefract = 1.0 - energyToReflect;

    float visibleReflectWeight = saturate(
        max(energyToReflect, saturate(reflectionVisibilityFloor))
      * max(reflectionVisibilityBoost, 0.0)
    );

    if (backgroundData.surfaceHit.totalInternalReflection)
    {
        contributions.reflectionContribution = combinedReflectSourceColor * visibleReflectWeight;
        contributions.refractionContribution *= energyToRefract;
        return contributions;
    }

    float densityProbeStepSize = max(_LightStepSize, _StepSize);
    float densityAlongRefractRay = CalculateLiquidOpticalDepthAlongRay(
        backgroundData.surfaceHit.posWS + backgroundData.surfaceHit.refractDir * 1e-3,
        backgroundData.surfaceHit.refractDir,
        densityProbeStepSize
    );
    float densityAlongReflectRay = CalculateLiquidOpticalDepthAlongRay(
        backgroundData.surfaceHit.posWS + backgroundData.surfaceHit.reflectDir * 1e-3,
        backgroundData.surfaceHit.reflectDir,
        densityProbeStepSize
    );

    float refractScore = densityAlongRefractRay * energyToRefract;
    float reflectScore = densityAlongReflectRay * energyToReflect;
    bool traceRefractedRay = refractScore >= reflectScore;

    float3 refractTransmittance = LiquidTransmittance(densityAlongRefractRay, extinctionCoefficients);
    float3 reflectTransmittance = LiquidTransmittance(densityAlongReflectRay, extinctionCoefficients);

    if (traceRefractedRay)
    {
        contributions.reflectionContribution = combinedReflectSourceColor * visibleReflectWeight * reflectTransmittance;
        contributions.refractionContribution *= energyToRefract * refractTransmittance;
        return contributions;
    }

    contributions.reflectionContribution = combinedReflectSourceColor * visibleReflectWeight;
    contributions.refractionContribution *= energyToRefract * refractTransmittance;
    return contributions;
}

float3 ComposeWaterDebugColor(
    WaterRaymarchBackgroundData backgroundData,
    float2 originalScreenUV,
    float debugViewMode,
    float3 extinctionCoefficients,
    float reflectionStrength,
    float reflectionVisibilityBoost,
    float reflectionVisibilityFloor,
    float3 surfaceViewTransmittance,
    float3 remainingViewTransmittance)
{
    int mode = (int)round(debugViewMode);
    if (mode == 14)
        return ComposeSceneDepthDebugColor(originalScreenUV);

    if (mode == 15)
        return ComposeSceneNormalDebugColor(originalScreenUV);

    if (!backgroundData.surfaceHit.hit)
    {
        if (mode == 16 || mode == 17 || mode == 18)
            return 0.0;
        return float3(1.0, 0.0, 1.0);
    }

    if (mode == 2)
    {
        float3 n = normalize(backgroundData.surfaceHit.normal);
        return n * 0.5 + 0.5;
    }

    if (mode == 3)
    {
        float3 r = normalize(backgroundData.surfaceHit.reflectDir);
        return r * 0.5 + 0.5;
    }

    if (mode == 11)
    {
        float3 n = normalize(backgroundData.surfaceHit.outwardNormal);
        return n * 0.5 + 0.5;
    }

    if (mode == 12)
    {
        float enter = saturate(backgroundData.surfaceHit.enteringWater);
        return lerp(float3(1.0, 0.0, 0.0), float3(0.0, 1.0, 0.0), enter);
    }

    if (mode == 13)
    {
        return backgroundData.surfaceHit.totalInternalReflection
            ? float3(1.0, 1.0, 1.0)
            : float3(0.0, 0.0, 0.0);
    }

    if (mode == 4)
    {
        return backgroundData.surfaceHit.reflectWeight.xxx;
    }

    if (mode == 9)
    {
        return SampleGlossyReflectionEnvironment(
            backgroundData.surfaceHit.reflectDir,
            backgroundData.surfaceHit.posWS,
            originalScreenUV
        );
    }

    if (mode == 10)
    {
        return SampleRawSceneSpecCube(backgroundData.surfaceHit.reflectDir);
    }

    WaterBackgroundContributions contributions = ComputeWaterBackgroundContributions(
        backgroundData,
        originalScreenUV,
        extinctionCoefficients,
        max(reflectionStrength, 1e-3),
        reflectionVisibilityBoost,
        reflectionVisibilityFloor
    );
    float3 reflectedEnvironmentColor = contributions.reflectedEnvironmentColor;

    if (mode == 16)
        return float3(contributions.ssrTrace.hitMask, 0.0, 0.0);

    if (mode == 17)
        return contributions.reflectedSSRColor;

    if (mode == 18)
        return contributions.ssrTrace.fadeFactor.xxx;

    if (max(reflectedEnvironmentColor.r, max(reflectedEnvironmentColor.g, reflectedEnvironmentColor.b)) < 1e-4)
        return float3(1.0, 1.0, 0.0);

    if (mode == 1)
        return reflectedEnvironmentColor;

    if (mode == 5)
        return contributions.reflectionContribution * surfaceViewTransmittance;
    if (mode == 6)
        return contributions.refractionContribution * remainingViewTransmittance;
    if (mode == 7)
        return contributions.reflectionContribution * surfaceViewTransmittance
             + contributions.refractionContribution * remainingViewTransmittance;
    if (mode == 8)
        return remainingViewTransmittance;

    return reflectedEnvironmentColor;
}

float3 ComposeWaterBackgroundColor(
    WaterRaymarchBackgroundData backgroundData,
    float2 originalScreenUV,
    float3 extinctionCoefficients,
    float reflectionStrength,
    float reflectionVisibilityBoost,
    float reflectionVisibilityFloor,
    float3 surfaceViewTransmittance,
    float3 remainingViewTransmittance)
{
    WaterBackgroundContributions contributions = ComputeWaterBackgroundContributions(
        backgroundData,
        originalScreenUV,
        extinctionCoefficients,
        reflectionStrength,
        reflectionVisibilityBoost,
        reflectionVisibilityFloor
    );

    float3 floorTransmittance = (backgroundData.surfaceHit.hit
                                 && backgroundData.surfaceHit.enteringWater > 0.5)
        ? float3(1.0, 1.0, 1.0)
        : remainingViewTransmittance;

    return contributions.reflectionContribution * surfaceViewTransmittance
         + contributions.refractionContribution * floorTransmittance;
}

#endif
