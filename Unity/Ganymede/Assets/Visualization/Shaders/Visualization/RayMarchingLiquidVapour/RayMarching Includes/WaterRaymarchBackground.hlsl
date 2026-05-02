#ifndef WATER_RAYMARCH_BACKGROUND_INCLUDED
#define WATER_RAYMARCH_BACKGROUND_INCLUDED

struct WaterRaymarchBackgroundData
{
    float2     backgroundScreenUV;
    float      sceneDistanceAlongRay;
    SurfaceHit surfaceHit;
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
    float da = SampleAdjustedLiquidDensityWS(aWS) - surfaceThreshold;
    float db = SampleAdjustedLiquidDensityWS(bWS) - surfaceThreshold;

    [loop]
    for (int iteration = 0; iteration < refinementIterations; iteration++)
    {
        float weightDenominator = abs(da) + abs(db);
        float midpointWeight = (weightDenominator > 1e-5)
            ? saturate(abs(da) / weightDenominator)
            : 0.5;
        float3 midpointWS = lerp(aWS, bWS, midpointWeight);
        float midpointDensity = SampleAdjustedLiquidDensityWS(midpointWS) - surfaceThreshold;

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

// Direct skybox/probe cubemap sample. URP's GlossyEnvironmentReflection often
// returns near-black for scenes without a baked reflection probe contribution,
// which makes Fresnel reflections invisible. Sampling unity_SpecCube0 directly
// gives us the actual skybox the scene is rendering, so reflections always have
// a real environment color regardless of probe baking state.
float3 SampleReflectionEnvironment(float3 reflectDirWS)
{
    float4 encoded = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, reflectDirWS, 0);
    float3 envColor = DecodeHDREnvironment(encoded, unity_SpecCube0_HDR);
    return envColor;
}

float3 ComposeWaterBackgroundColor(
    WaterRaymarchBackgroundData backgroundData,
    float2 originalScreenUV,
    float3 extinctionCoefficients,
    float reflectionStrength)
{
    // Refracted scene sample: equivalent of the reference shader continuing
    // its ray through the fluid volume and hitting the environment on the
    // other side. We replace its full path-traced environment hit with a
    // single screen-space sample at the refracted UV.
    float3 refractedSceneColor = SAMPLE_TEXTURE2D(
        _CameraOpaqueTexture,
        sampler_CameraOpaqueTexture,
        backgroundData.backgroundScreenUV
    ).rgb;

    if (!backgroundData.surfaceHit.hit)
        return refractedSceneColor;

    // Reflection environment: equivalent of the reference's Light(reflectDir)
    // for the weak/loser path. Direct skybox cubemap sample so the reflection
    // contribution is always physically visible at grazing angles, instead of
    // collapsing to black when no reflection probe is baked.
    float3 reflectedEnvironmentColor = SampleReflectionEnvironment(backgroundData.surfaceHit.reflectDir)
                                     * reflectionStrength;

    float reflectWeight = backgroundData.surfaceHit.reflectWeight;
    float refractWeight = backgroundData.surfaceHit.refractWeight;

    // Total internal reflection: no refracted contribution at all, exactly
    // like the reference shader collapses to the reflected path.
    if (backgroundData.surfaceHit.totalInternalReflection)
        return reflectedEnvironmentColor;

    // Greedy single-path selection from the reference shader. The "winning"
    // path is sampled at full quality (scene texture for refraction, env probe
    // for reflection). The "losing" path is approximated immediately, weighted
    // by Fresnel and attenuated by the optical depth it would have travelled.
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

    float refractScore = densityAlongRefractRay * refractWeight;
    float reflectScore = densityAlongReflectRay * reflectWeight;
    bool traceRefractedRay = refractScore >= reflectScore;

    float3 refractTransmittance = LiquidTransmittance(densityAlongRefractRay, extinctionCoefficients);
    float3 reflectTransmittance = LiquidTransmittance(densityAlongReflectRay, extinctionCoefficients);

    // Composition mirrors the reference shader's accumulation:
    //   light += winning_path * weight
    //   light += losing_path  * weight * transmittance(losing path)
    if (traceRefractedRay)
    {
        return refractedSceneColor       * refractWeight
             + reflectedEnvironmentColor * reflectWeight * reflectTransmittance;
    }

    return reflectedEnvironmentColor * reflectWeight
         + refractedSceneColor       * refractWeight * refractTransmittance;
}

#endif
