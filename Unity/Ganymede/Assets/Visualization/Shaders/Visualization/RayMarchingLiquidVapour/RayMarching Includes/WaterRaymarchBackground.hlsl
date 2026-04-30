#ifndef WATER_RAYMARCH_BACKGROUND_INCLUDED
#define WATER_RAYMARCH_BACKGROUND_INCLUDED

struct WaterRaymarchBackgroundData
{
    float2     backgroundScreenUV;
    float      sceneDistanceAlongRay;
    SurfaceHit surfaceHit;
    bool       cameraInsideLiquid;
    float      surfaceDistanceAlongRay; // distance from camera to first surface hit (for Beer-Lambert through liquid)
};

// Bisection refinement: given an "outside" t=a (density < threshold) and an
// "inside" t=b (density >= threshold), tighten to roughly step/16 accuracy in
// 4 trilinear taps. Lands the surface hit on the actual iso-surface so the
// pre-baked normal sample is read at the correct location.
float RefineSurfaceCrossing(
    WaterRaymarchViewData viewData,
    float a, float b,
    float threshold,
    bool searchEntry)
{
    [unroll]
    for (int k = 0; k < 4; k++)
    {
        float m  = 0.5 * (a + b);
        float dm = SampleLiquidDensityWS(viewData.cameraPositionWS + viewData.viewRayDirectionWS * m);
        // searchEntry == true  : a is outside (dm<thr → keep), b is inside  (dm>=thr → keep)
        // searchEntry == false : a is inside  (dm>=thr → keep), b is outside (dm<thr → keep)
        bool mIsOutside = (dm < threshold);
        if (searchEntry ? mIsOutside : !mIsOutside)
            a = m;
        else
            b = m;
    }
    return 0.5 * (a + b);
}

SurfaceHit FindWaterSurfaceHit(
    WaterRaymarchViewData viewData,
    WaterRaymarchVolumeData volumeData,
    float maxSearchDistance,
    float stepSize,
    float isoLevel,
    float surfaceDetectionMargin,
    bool  cameraInsideLiquid,
    out float surfaceDistanceOut)
{
    float safeStepSize = max(stepSize, 1e-4);
    float entryThreshold = isoLevel + surfaceDetectionMargin;
    float exitThreshold  = max(isoLevel - surfaceDetectionMargin, 1e-5);

    // searchEntry: looking for an air→liquid crossing. When the camera already
    // sits inside the liquid we instead look for a liquid→air crossing (exit surface).
    bool searchEntry = !cameraInsideLiquid;

    SurfaceHit surfaceHit = NoSurfaceHit();
    surfaceDistanceOut = maxSearchDistance;

    // Early entry test: if the volume entry point is already inside liquid, treat that
    // as the surface hit. This only applies when the camera is OUTSIDE the volume AABB.
    bool cameraStartsInsideVolume = (volumeData.distanceToVolume < 1e-5);
    if (!cameraStartsInsideVolume && searchEntry)
    {
        float entryDensity = SampleLiquidDensityWS(volumeData.entryPositionWS);
        if (entryDensity >= entryThreshold)
        {
            surfaceDistanceOut = volumeData.distanceToVolume;
            return MakeSurfaceHit(volumeData.entryPositionWS, viewData.viewRayDirectionWS, true);
        }
    }

    float prevDistance = volumeData.distanceToVolume + safeStepSize * viewData.blueNoiseValue;
    float previousDensity = SampleLiquidDensityWS(
        viewData.cameraPositionWS + viewData.viewRayDirectionWS * prevDistance
    );
    float sampleDistance = prevDistance + safeStepSize;

    while (!surfaceHit.hit && sampleDistance < maxSearchDistance)
    {
        float3 samplePositionWS = viewData.cameraPositionWS + viewData.viewRayDirectionWS * sampleDistance;
        float currentDensity = SampleLiquidDensityWS(samplePositionWS);

        // Hysteresis: separate thresholds for entering vs leaving prevents the
        // surface from re-triggering on density noise just past the iso-surface.
        bool enteringWater = (previousDensity < entryThreshold) && (currentDensity >= entryThreshold);
        bool leavingWater  = (previousDensity >= exitThreshold) && (currentDensity <  exitThreshold);

        bool crossing = searchEntry ? enteringWater : leavingWater;
        if (crossing)
        {
            float refinedT = RefineSurfaceCrossing(
                viewData,
                prevDistance, sampleDistance,
                searchEntry ? entryThreshold : exitThreshold,
                searchEntry
            );
            float3 refinedPos = viewData.cameraPositionWS + viewData.viewRayDirectionWS * refinedT;
            // For an exit crossing the ray is coming FROM water — pass enteringWater=false
            // so MakeSurfaceHit uses the correct IOR pair (water→air) and Schlick R0.
            surfaceHit = MakeSurfaceHit(refinedPos, viewData.viewRayDirectionWS, searchEntry);
            if (surfaceHit.hit)
                surfaceDistanceOut = refinedT;
        }

        prevDistance    = sampleDistance;
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
    backgroundData.cameraInsideLiquid = false;
    backgroundData.surfaceDistanceAlongRay = backgroundData.sceneDistanceAlongRay;

    if (backgroundData.sceneDistanceAlongRay <= volumeData.distanceToVolume)
        return backgroundData;

    // Detect "camera underwater": camera origin is inside the AABB AND inside the
    // liquid iso-surface. Drives an exit-surface search and an underwater Beer-Lambert
    // tint on the background colour, both of which are required for "looking around
    // while submerged" to feel like water rather than a tinted air volume.
    bool cameraStartsInsideVolume = (volumeData.distanceToVolume < 1e-5);
    if (cameraStartsInsideVolume)
    {
        float cameraDensity = SampleLiquidDensityWS(viewData.cameraPositionWS);
        backgroundData.cameraInsideLiquid = (cameraDensity >= isoLevel);
    }

    float initialSearchDistance = min(volumeData.volumeExitDistance, backgroundData.sceneDistanceAlongRay);
    float surfaceT = initialSearchDistance;
    backgroundData.surfaceHit = FindWaterSurfaceHit(
        viewData,
        volumeData,
        initialSearchDistance,
        stepSize,
        isoLevel,
        surfaceDetectionMargin,
        backgroundData.cameraInsideLiquid,
        surfaceT
    );
    backgroundData.surfaceDistanceAlongRay = surfaceT;

    if (backgroundData.surfaceHit.hit)
    {
        // Depth-aware refraction offset: scale the screen-space refraction nudge by
        // how much water the refracted ray traverses. Pure screen-space refraction
        // makes everything wobble identically regardless of distance ("rubber sheet"),
        // but unbounded depth scaling shoots the UV off the screen and samples the
        // sky → the water turns white.
        // saturate( . / refLen ) keeps the scale in [0..1] so a 1m-deep tank and a
        // 10m-deep tank produce visually similar refraction strength while still
        // letting shallow geometry refract less than deep geometry.
        float waterDepthAlongRay = max(backgroundData.sceneDistanceAlongRay - surfaceT, 0.0);
        float depthRefScale = saturate(waterDepthAlongRay * 0.5);  // 0..1 over the first ~2m
        float3 refractedDirectionVS = mul((float3x3)UNITY_MATRIX_V, backgroundData.surfaceHit.refractDir);
        float2 refractionOffset = refractedDirectionVS.xy * refractionStrength * (0.25 + 0.75 * depthRefScale);

        float farThreshold = SampleSceneDistanceAlongRay(viewData.screenUV, viewData.viewDepthDenominator) * 4.0 + 100.0;
        float2 refractedUV = clamp(viewData.screenUV + refractionOffset, 0.001, 0.999);
        float refractedDist = SampleSceneDistanceAlongRay(refractedUV, viewData.viewDepthDenominator);

        // Reject samples that are (a) in front of the water surface (foreground geometry
        // piercing through; would create a doubled silhouette) or (b) further than the
        // sky/far-plane heuristic (refraction shooting off-screen into sky → white).
        // In either case shrink the offset toward zero by halving up to 3 times.
        [unroll]
        for (int shrink = 0; shrink < 3; shrink++)
        {
            bool inFront = refractedDist <  surfaceT;
            bool toSky   = refractedDist >  farThreshold;
            if (!inFront && !toSky) break;
            refractionOffset *= 0.5;
            refractedUV = clamp(viewData.screenUV + refractionOffset, 0.001, 0.999);
            refractedDist = SampleSceneDistanceAlongRay(refractedUV, viewData.viewDepthDenominator);
        }
        backgroundData.backgroundScreenUV    = refractedUV;
        backgroundData.sceneDistanceAlongRay = refractedDist;
    }

    return backgroundData;
}

float3 ComposeWaterBackgroundColor(
    WaterRaymarchBackgroundData backgroundData,
    WaterRaymarchViewData viewData,
    float2 originalScreenUV,
    float reflectionStrength,
    float3 liquidExtinction,
    float3 vapourExtinction,
    float  shadowStepSize)
{
    float3 refractedSceneColor = SAMPLE_TEXTURE2D(
        _CameraOpaqueTexture,
        sampler_CameraOpaqueTexture,
        backgroundData.backgroundScreenUV
    ).rgb;

    // Caustic / floor-shadow modulation: attenuate the background colour by the
    // sun-ray transmittance through the liquid, evaluated at the refracted scene
    // hit point. Bright stripes appear where the sun reaches the floor through
    // thin liquid; shadowed bands appear where it punches through dense regions.
    if (backgroundData.surfaceHit.hit)
    {
        float3 refractedHitWS = viewData.cameraPositionWS
            + viewData.viewRayDirectionWS * backgroundData.sceneDistanceAlongRay;
        float3 sunT = CalculateTransmittedSunLightRG(
            refractedHitWS,
            liquidExtinction,
            vapourExtinction,
            shadowStepSize
        );
        // Soft caustic: blend toward sun-transmittance — never fully blacks out
        // the floor (so the caustic reads as variation, not as a hard shadow).
        refractedSceneColor *= lerp(float3(1, 1, 1), sunT, 0.85);
    }

    // Underwater Beer–Lambert tint on the background. This is what gives the
    // submerged look its identity: distant geometry quickly fades to the
    // absorption colour of water, independent of in-scatter or sun direction.
    if (backgroundData.cameraInsideLiquid)
    {
        float dCam = backgroundData.sceneDistanceAlongRay;
        float3 underwaterTint = exp(-liquidExtinction * dCam);
        refractedSceneColor *= underwaterTint;
    }

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
