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

    // Near TIR the refracted ray exits the side wall almost horizontally, so
    // physicallyRefractedUV lands well outside [0,1].  Without this guard the
    // final clamp pins the UV to the screen edge, smearing that one texel
    // across the entire TIR boundary ring.  Fade the refraction offset back to
    // zero as soon as the exit point drifts off-screen so we fall back to the
    // unrefracted UV instead of clamping.
    float2 uvOvershoot = max(float2(0, 0), abs(physicallyRefractedUV - 0.5) - 0.5);
    float  offScreen   = max(uvOvershoot.x, uvOvershoot.y);
    // Start fading at the screen edge, fully gone after 5 % off-screen overshoot.
    float  edgeFade    = 1.0 - saturate(offScreen * 20.0);

    // Let _RefractionStrength art-direct the bend amount while keeping the bend
    // direction based on the real refracted ray leaving the cube/volume bounds.
    return clamp(
        lerp(viewData.screenUV, physicallyRefractedUV, saturate(refractionStrength) * edgeFade),
        0.001,
        0.999
    );
}

bool IsBottomPhysicsBoundsFaceWS(float3 positionWS)
{
    // Classify the exact ray-box exit using the same closest-face convention as
    // boundary normal blending. Ties intentionally stay with the earlier side
    // faces in ClosestPhysicsBoundsFaceNormalWS, so side/bottom edges still work
    // as side views instead of being rejected as bottom mirrors.
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

    // Seed the previous-step state for the crossing detector.
    // When the camera is outside the bounding box we must treat the ray origin
    // as pure air (density = 0) even if the box entry face has high liquid
    // density due to water pressed against the wall.  Using the sampled edge
    // density would make the first crossing look like 'leavingWater' instead of
    // 'enteringWater', flip the IOR to water->air, and trigger spurious total
    // internal reflection — causing full reflection with no Fresnel blend.
    float previousDensity;
    float3 previousPositionWS;
    if (rayStartsInsideWater)
    {
        // Camera is submerged: seed with actual density so the loop immediately
        // finds the exit (leavingWater) surface above/around it.
        previousDensity    = SampleAdjustedLiquidDensityWS(viewData.cameraPositionWS);
        previousPositionWS = viewData.cameraPositionWS;
    }
    else
    {
        // Camera is in air (inside box or outside). Force density = 0 so the
        // first crossing is always enteringWater = true -> air->water IOR.
        // Seeding from the actual box-entry face density is wrong when water is
        // pressed against that wall: it makes the first crossing look like
        // leavingWater, triggering spurious TIR and killing Fresnel from outside.
        previousDensity = 0.0;
        float anchorOffset = max(0.0, sampleDistance - safeStepSize * 0.5);
        previousPositionWS = viewData.cameraPositionWS + viewData.viewRayDirectionWS * anchorOffset;
    }

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

    // If dense liquid reaches the side/top of the simulated box, the ray leaves
    // water there even though there is no sampled density drop before the bounds
    // clip. Restore that as a legitimate side/top optical surface, but do not
    // treat the bottom face as water/air; the bottom is a container/floor edge
    // and was the source of the fake mirror reflection.
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

        // Always re-read scene depth at the refracted UV so the volume exit /
        // transmittance mask matches the pixel we are actually sampling. This
        // is the physically correct choice and prevents the original silhouette
        // from leaking back through the refracted image.
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

// Vector transmittance through liquid: matches reference shader's
// Transmittance(thickness) = exp(-thickness * extinctionCoeff). Liquid color
// emerges naturally from sigma_t * optical depth instead of a flat scalar
// exp(-density).
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

// URP-supported reflection probe / skybox sample with a raw spec-cube fallback.
// The fallback is intentionally kept because URP's glossy path and raw cube path
// are bound through slightly different shader variant paths on custom raymarch
// passes, especially while debugging transparent proxy renderers.
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

    if (!backgroundData.surfaceHit.hit)
        return contributions;

    float3 rawEnvColor = SampleReflectionEnvironment(
        backgroundData.surfaceHit.reflectDir,
        backgroundData.surfaceHit.posWS,
        originalScreenUV
    );
    // Keep reflectedEnvironmentColor as env * strength so debug views and the
    // "yellow = black env" guard work the same as before.
    contributions.reflectedEnvironmentColor = rawEnvColor * reflectionStrength;

    float physicalReflectWeight = backgroundData.surfaceHit.reflectWeight;

    // _ReflectionStrength controls the energy split, not just the color brightness.
    // At strength=0 all energy goes to refraction (transparent water, no reflection).
    // At strength=1 full physical Fresnel is applied.
    // This prevents the black-at-grazing bug: previously, low strength dimmed the
    // reflected color but left refractWeight = 1-physicalReflect ≈ 0 at grazing
    // angles, so both contributions collapsed to zero → black.
    float energyToReflect = physicalReflectWeight * saturate(reflectionStrength);
    float energyToRefract = 1.0 - energyToReflect;

    // Visibility boost/floor scales how brightly the reflected env is displayed
    // without changing the refraction weight, so boosting never darkens refraction.
    float visibleReflectWeight = saturate(
        max(energyToReflect, saturate(reflectionVisibilityFloor))
      * max(reflectionVisibilityBoost, 0.0)
    );

    if (backgroundData.surfaceHit.totalInternalReflection)
    {
        // TIR: physically no refraction, but when strength < 1 allow the
        // "missing" reflected energy to bleed through as scene colour so
        // TIR zones never go pure black just because strength is turned down.
        contributions.reflectionContribution = rawEnvColor * visibleReflectWeight;
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
        // Apply refractTransmittance here so the floor is correctly tinted by
        // the water column along the refracted ray from surface to floor.
        // This is especially important when the main march stops at the surface
        // (outside cameras) and remainingViewTransmittance stays at 1.0.
        contributions.reflectionContribution = rawEnvColor * visibleReflectWeight * reflectTransmittance;
        contributions.refractionContribution *= energyToRefract * refractTransmittance;
        return contributions;
    }

    contributions.reflectionContribution = rawEnvColor * visibleReflectWeight;
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
    // Bright magenta means the water raymarch never found a liquid surface, so
    // the missing reflection is a surface-hit problem rather than a cubemap one.
    if (!backgroundData.surfaceHit.hit)
        return float3(1.0, 0.0, 1.0);

    int mode = (int)round(debugViewMode);
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

    // Bright yellow means a surface exists but the reflected environment sample
    // is still black, so the issue is in the reflection source/binding path.
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

    // From outside (enteringWater = true), ComputeWaterBackgroundContributions already
    // applied refractTransmittance (surface→floor optical depth) to refractionContribution.
    // Multiplying by remainingViewTransmittance here would double-attenuate the floor
    // (remainingViewTransmittance ≈ exp(-σ×depth) ≈ refractTransmittance from outside),
    // making the floor invisible and breaking the Fresnel transparency effect.
    // From inside (enteringWater = false), remainingViewTransmittance is the water column
    // above the camera and is the correct single attenuation for the transmitted sky.
    float3 floorTransmittance = (backgroundData.surfaceHit.hit
                                 && backgroundData.surfaceHit.enteringWater > 0.5)
        ? float3(1.0, 1.0, 1.0)
        : remainingViewTransmittance;

    return contributions.reflectionContribution * surfaceViewTransmittance
         + contributions.refractionContribution * floorTransmittance;
}

#endif
