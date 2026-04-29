#ifndef WATER_PHASE_RAYMARCH_INCLUDED
    #define WATER_PHASE_RAYMARCH_INCLUDED

    struct WaterPhaseMarchResult
    {
        float3 vapourScatter;
        float3 vapourScatterAdditional;
        float vapourAlpha;
        float liquidAlpha;
        float liquidDepth;
        float vapourLitness;

        // Liquid surface hit (front-most) for surface texturing / shading.
        float3 liquidSurfaceWS;
        float3 liquidSurfaceNormalWS;
        float  liquidSurfaceFound;
    };

    WaterPhaseMarchResult RaymarchWaterPhase(
    float3 rayOrigin, float3 rayDir,
    float3 lightDir, half3 lightColor,
    int marchSteps, float marchDistance,
    float vapourG, float vapourAbsorption,
    float liquidOpacityCoeff,
    float phaseThreshold, float phaseWidth,
    float time,
    float3 driftDir, float driftSpeed,
    float noiseScale, int octaves, float densityPower,
    float sceneLinearDepth,
    float3 boundsMinOS, float3 boundsMaxOS,
    float edgeSoftness,
    float2 screenUV,
    float2 blueNoiseRG,
    float blueNoiseStrength,
    float densityMultiplier = 1.0,
    float densityOffset = 0.0)
    {
        WaterPhaseMarchResult result;
        result.vapourScatter = 0.0;
        result.vapourScatterAdditional = 0.0;
        result.vapourAlpha = 0.0;
        result.liquidAlpha = 0.0;
        result.liquidDepth = 0.0;
        result.vapourLitness = 0.0;

        result.liquidSurfaceWS = 0.0;
        result.liquidSurfaceNormalWS = float3(0.0, 1.0, 0.0);
        result.liquidSurfaceFound = 0.0;

        float stepSize = marchDistance / max((float)marchSteps, 1.0);
        float vapourTransmit = 1.0;

        float cosTheta = dot(-rayDir, lightDir);

        float3 boundsCenter = (boundsMinOS + boundsMaxOS) * 0.5;
        float3 boundsExtents = (boundsMaxOS - boundsMinOS) * 0.5;

        float3 boundsMinWS = TransformObjectToWorld(boundsMinOS);
        float3 boundsMaxWS = TransformObjectToWorld(boundsMaxOS);
        float boundsDiagWS = max(distance(boundsMinWS, boundsMaxWS), 1e-4);
        float normalEpsWS = clamp(boundsDiagWS * 0.005, 0.001, 0.05);

        // Used to drive vapour "lit vs shadow" shading. This must consider additional lights
        // too, otherwise a flashlight-lit volume still gets treated as "unlit".
        float accumulatedLitEnergy = 0.0;
        float accumulatedMaxEnergy = 0.0;

        float2 pixelCoords = screenUV * _ScreenParams.xy;
        float ignJitter = frac(52.9829189 * frac(dot(pixelCoords, float2(0.06711056, 0.00583715))));
        float blueNoiseWeight = saturate(blueNoiseStrength);

        // Blue-noise seed rotates over time to reduce static pattern lock while
        // keeping deterministic distribution per-pixel within a frame.
        float blueSeed = frac(blueNoiseRG.x + blueNoiseRG.y * 0.754877666 + time * 0.61803398875);
        float blueStride = 0.61803398875 + blueNoiseRG.y * 0.14589803;

        #if defined(_ADDITIONAL_LIGHTS)
            // Required for Forward+ (clustered) additional lights. In non-clustered Forward,
            // LIGHT_LOOP_BEGIN becomes a simple for-loop and does not reference inputData.
            InputData inputData = (InputData)0;
            inputData.normalizedScreenSpaceUV = screenUV;
            uint additionalLightsCount = (uint)GetAdditionalLightsCount();
        #endif

        for (int i = 0; i < marchSteps; i++)
        {
            float blueJitter = frac(blueSeed + (i + 1.0) * blueStride);
            float jitter = lerp(ignJitter, blueJitter, blueNoiseWeight);
            float3 samplePos = rayOrigin + rayDir * (stepSize * (i + jitter));

            float sampleEyeDepth = -mul(UNITY_MATRIX_V, float4(samplePos, 1.0)).z;
            if (sampleEyeDepth >= sceneLinearDepth)
            break;

            float3 sampleOS = TransformWorldToObject(samplePos);
            float shapeMask = ComputeShapeMaskOS(sampleOS, boundsMinOS, boundsMaxOS, boundsCenter, boundsExtents, edgeSoftness);

            if (shapeMask <= 0.001)
            continue;

            // Per-march vapour density: physics grid is a low-frequency presence
            // mask, world-space FBM provides the visible wispy detail. This is
            // the "Option B" pipeline — no baked enhancement in the compute pass.
            float density = SampleVapourDensityProcedural(
                samplePos, time, driftDir, driftSpeed,
                noiseScale, octaves, densityPower
            ) * shapeMask;

            density = saturate(density * densityMultiplier + densityOffset);

            if (density <= 0.001)
            continue;

            float liquidPhase = smoothstep(phaseThreshold - phaseWidth, phaseThreshold + phaseWidth, density);
            float vapourPhase = 1.0 - liquidPhase;

            float vapourDensity = density * vapourPhase;
            float liquidDensity = density * liquidPhase;

            if (vapourDensity > 0.0001)
            {
                float absorption = vapourDensity * vapourAbsorption * stepSize;
                float stepTransmit = exp(-absorption);
                float stepAlpha = 1.0 - stepTransmit;
                float phase = HenyeyGreenstein(cosTheta, vapourG);

                half shadowAtten = 1.0;
                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                    float4 shadowCoord = TransformWorldToShadowCoord(samplePos);
                    shadowAtten = MainLightRealtimeShadow(shadowCoord);
                #endif

                result.vapourScatter += vapourTransmit * vapourDensity * stepSize * phase * (lightColor * shadowAtten);

                #if defined(_ADDITIONAL_LIGHTS)
                    // Add point/spot lights (and their colors) into the volumetric scattering.
                    // These are accumulated separately so main-light shadow tinting does not
                    // incorrectly darken flashlight/point-light beams.
                    float additionalEnergy = 0.0;
                    inputData.positionWS = samplePos;
                    LIGHT_LOOP_BEGIN(additionalLightsCount)
                    Light additionalLight = GetAdditionalLight(lightIndex, samplePos);
                    half3 radiance = additionalLight.color * (additionalLight.distanceAttenuation * additionalLight.shadowAttenuation);
                    additionalEnergy += Luminance((real3)radiance);
                    float addCosTheta = dot(-rayDir, additionalLight.direction);
                    float addPhase = HenyeyGreenstein(addCosTheta, vapourG);
                    result.vapourScatterAdditional += vapourTransmit * vapourDensity * stepSize * addPhase * radiance;
                    LIGHT_LOOP_END
                #endif

                // Energy-weighted litness: main light can be shadowed, additional lights are
                // treated as fully lit unless you enable additional-light shadows.
                float mainEnergy = Luminance((real3)lightColor);
                float weight = vapourTransmit * stepAlpha;

                #if defined(_ADDITIONAL_LIGHTS)
                    float litEnergy = mainEnergy * shadowAtten + additionalEnergy;
                    float maxEnergy = mainEnergy + additionalEnergy;
                #else
                    float litEnergy = mainEnergy * shadowAtten;
                    float maxEnergy = mainEnergy;
                #endif

                accumulatedLitEnergy += weight * litEnergy;
                accumulatedMaxEnergy += weight * maxEnergy;

                vapourTransmit *= stepTransmit;
            }

            if (liquidDensity > 0.0001)
            {
                if (result.liquidSurfaceFound < 0.5)
                {
                    result.liquidSurfaceWS = samplePos;
                    result.liquidSurfaceNormalWS = ComputeShapeNormalWS(
                    samplePos,
                    boundsMinOS, boundsMaxOS,
                    boundsCenter, boundsExtents,
                    edgeSoftness,
                    normalEpsWS
                    );
                    result.liquidSurfaceFound = 1.0;
                }

                float stepAlpha = saturate(liquidDensity * liquidOpacityCoeff * stepSize);
                result.liquidAlpha += (1.0 - result.liquidAlpha) * stepAlpha;
                result.liquidDepth += liquidDensity * stepSize;
            }

            if (vapourTransmit < 0.01 && result.liquidAlpha > 0.99)
            break;
        }

        result.vapourAlpha = 1.0 - vapourTransmit;
        result.vapourLitness = accumulatedMaxEnergy > 0.0001 ? saturate(accumulatedLitEnergy / accumulatedMaxEnergy) : 0.0;
        return result;
    }

    // ── Liquid-optimised raymarch: IGN jitter only, no blue noise, no per-step shadows ──
    WaterPhaseMarchResult RaymarchWaterPhaseLiquid(
    float3 rayOrigin, float3 rayDir,
    int marchSteps, float marchDistance, float liquidOpacityCoeff,
    float phaseThreshold, float phaseWidth,
    float time,
    float3 driftDir, float driftSpeed,
    float noiseScale, int octaves,
    float densityPower,
    float physicsDensity, float physicsBlend,
    float sceneLinearDepth,
    float3 boundsMinOS, float3 boundsMaxOS,
    float edgeSoftness,
    float2 screenUV,
    float noiseDetailStrength = 0.0)
    {
        WaterPhaseMarchResult result;
        result.vapourScatter = 0.0;
        result.vapourScatterAdditional = 0.0;
        result.vapourAlpha = 0.0;
        result.liquidAlpha = 0.0;
        result.liquidDepth = 0.0;
        result.vapourLitness = 0.0;
        result.liquidSurfaceWS = 0.0;
        result.liquidSurfaceNormalWS = float3(0.0, 1.0, 0.0);
        result.liquidSurfaceFound = 0.0;

        float stepSize = marchDistance / max((float)marchSteps, 1.0);

        float3 boundsCenter = (boundsMinOS + boundsMaxOS) * 0.5;
        float3 boundsExtents = (boundsMaxOS - boundsMinOS) * 0.5;

        float3 boundsMinWS = TransformObjectToWorld(boundsMinOS);
        float3 boundsMaxWS = TransformObjectToWorld(boundsMaxOS);
        float boundsDiagWS = max(distance(boundsMinWS, boundsMaxWS), 1e-4);
        float normalEpsWS = clamp(boundsDiagWS * 0.005, 0.001, 0.05);

        float2 pixelCoords = screenUV * _ScreenParams.xy;
        float jitter = frac(52.9829189 * frac(dot(pixelCoords, float2(0.06711056, 0.00583715))));

        for (int i = 0; i < marchSteps; i++)
        {
            float3 samplePos = rayOrigin + rayDir * (stepSize * (i + jitter));

            float sampleEyeDepth = -mul(UNITY_MATRIX_V, float4(samplePos, 1.0)).z;
            if (sampleEyeDepth >= sceneLinearDepth)
            break;

            float3 sampleOS = TransformWorldToObject(samplePos);
            float shapeMask = ComputeShapeMaskOS(sampleOS, boundsMinOS, boundsMaxOS, boundsCenter, boundsExtents, edgeSoftness);

            if (shapeMask <= 0.001)
            continue;

            float density = SampleDensity(
            samplePos, time,
            driftDir, driftSpeed,
            noiseScale, octaves,
            densityPower,
            physicsDensity, physicsBlend,
            noiseDetailStrength
            ) * shapeMask;

            if (density <= 0.001)
            continue;

            float liquidPhase = smoothstep(phaseThreshold - phaseWidth, phaseThreshold + phaseWidth, density);
            float liquidDensity = density * liquidPhase;

            if (liquidDensity > 0.0001)
            {
                if (result.liquidSurfaceFound < 0.5)
                {
                    result.liquidSurfaceWS = samplePos;
                    result.liquidSurfaceNormalWS = ComputeShapeNormalWS(
                    samplePos,
                    boundsMinOS, boundsMaxOS,
                    boundsCenter, boundsExtents,
                    edgeSoftness,
                    normalEpsWS
                    );
                    result.liquidSurfaceFound = 1.0;
                }

                float stepAlpha = saturate(liquidDensity * liquidOpacityCoeff * stepSize);
                result.liquidAlpha += (1.0 - result.liquidAlpha) * stepAlpha;
                result.liquidDepth += liquidDensity * stepSize;
            }

        }

        return result;
    }

#endif
