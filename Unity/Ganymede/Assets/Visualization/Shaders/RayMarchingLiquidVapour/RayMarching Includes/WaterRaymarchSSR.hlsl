#ifndef WATER_RAYMARCH_SSR_INCLUDED
#define WATER_RAYMARCH_SSR_INCLUDED

struct WaterSSRTraceResult
{
    bool   hit;
    bool   offScreen;
    float2 hitUV;
    float3 hitColor;
    float  depthDelta;
    float  hitMask;
    float  edgeFade;
    float  thicknessFade;
    float  backfacePass;
    float  fadeFactor;
    float  blendWeight;
};

WaterSSRTraceResult MakeWaterSSRTraceResultDefault()
{
    WaterSSRTraceResult trace;
    trace.hit = false;
    trace.offScreen = false;
    trace.hitUV = float2(0.0, 0.0);
    trace.hitColor = float3(0.0, 0.0, 0.0);
    trace.depthDelta = 0.0;
    trace.hitMask = 0.0;
    trace.edgeFade = 0.0;
    trace.thicknessFade = 0.0;
    trace.backfacePass = 0.0;
    trace.fadeFactor = 0.0;
    trace.blendWeight = 0.0;
    return trace;
}

float3 WaterSSRCameraForwardWS()
{
    return normalize(-UNITY_MATRIX_V[2].xyz);
}

float WaterSSRComputeEdgeFade(float2 screenUV, float edgeFadeWidth)
{
    float2 edgeDistance = min(screenUV, 1.0 - screenUV);
    float minEdgeDistance = min(edgeDistance.x, edgeDistance.y);
    return saturate(minEdgeDistance / max(edgeFadeWidth, 1e-4));
}

bool WaterSSRProjectPoint(
    float3 samplePosWS,
    out float2 sampleUV,
    out float rayDistance,
    out float sceneDistance,
    out float depthDelta)
{
    sampleUV = ProjectWorldPositionToScreenUV(samplePosWS);

    if (any(sampleUV <= 0.001) || any(sampleUV >= 0.999))
    {
        rayDistance = 0.0;
        sceneDistance = 0.0;
        depthDelta = 0.0;
        return false;
    }

    float3 fromCameraToPointWS = samplePosWS - _WorldSpaceCameraPos.xyz;
    rayDistance = length(fromCameraToPointWS);
    if (rayDistance <= 1e-6)
    {
        sceneDistance = 0.0;
        depthDelta = 0.0;
        return false;
    }

    float3 viewDirectionWS = fromCameraToPointWS / rayDistance;
    float viewDepthDenominator = max(dot(viewDirectionWS, WaterSSRCameraForwardWS()), 1e-4);
    sceneDistance = SampleSceneDistanceAlongRay(sampleUV, viewDepthDenominator);
    depthDelta = rayDistance - sceneDistance;
    return true;
}

WaterSSRTraceResult TraceWaterScreenSpaceReflection(
    SurfaceHit surfaceHit,
    float2 sourceScreenUV)
{
    WaterSSRTraceResult trace = MakeWaterSSRTraceResultDefault();

    if (!surfaceHit.hit || _SSRStrength <= 1e-5)
        return trace;

    float3 reflectDirWS = normalize(surfaceHit.reflectDir);
    if (dot(reflectDirWS, reflectDirWS) < 0.9)
        return trace;

    float stepLength = max(_SSRStepSize, 1e-4);
    float maxDistance = max(_SSRMaxDistance, stepLength);
    int maxSteps = clamp((int)round(_SSRMaxSteps), 1, 512);
    float thickness = max(_SSRThickness, 1e-4);

    float3 rayOriginWS = surfaceHit.posWS + reflectDirWS * 1e-3;
    float jitter = SampleWaterBlueNoiseChannel(sourceScreenUV, 2);
    float currentDistance = stepLength * lerp(0.25, 1.0, jitter);
    float previousDistance = currentDistance;
    float previousDepthDelta = -1e6;

    [loop]
    for (int stepIndex = 0; stepIndex < maxSteps && currentDistance <= maxDistance; stepIndex++)
    {
        float3 samplePosWS = rayOriginWS + reflectDirWS * currentDistance;
        float2 sampleUV;
        float rayDistance;
        float sceneDistance;
        float depthDelta;

        if (!WaterSSRProjectPoint(samplePosWS, sampleUV, rayDistance, sceneDistance, depthDelta))
        {
            trace.offScreen = true;
            break;
        }

        bool crossedSurface = (depthDelta >= 0.0) && (previousDepthDelta < 0.0);
        bool depthWithinThickness = (depthDelta >= 0.0) && (depthDelta <= thickness);

        if (depthWithinThickness || crossedSurface)
        {
            float2 hitUV = sampleUV;
            float hitDepthDelta = depthDelta;

            if (crossedSurface)
            {
                float lowDistance = previousDistance;
                float highDistance = currentDistance;

                [unroll(4)]
                for (int refine = 0; refine < 4; refine++)
                {
                    float midDistance = 0.5 * (lowDistance + highDistance);
                    float3 midPosWS = rayOriginWS + reflectDirWS * midDistance;
                    float2 midUV;
                    float midRayDistance;
                    float midSceneDistance;
                    float midDepthDelta;

                    if (!WaterSSRProjectPoint(midPosWS, midUV, midRayDistance, midSceneDistance, midDepthDelta))
                    {
                        highDistance = midDistance;
                        continue;
                    }

                    if (midDepthDelta >= 0.0)
                    {
                        highDistance = midDistance;
                        hitUV = midUV;
                        hitDepthDelta = midDepthDelta;
                    }
                    else
                    {
                        lowDistance = midDistance;
                    }
                }
            }

            float3 sceneNormalWS = normalize(SampleSceneNormals(hitUV));
            float sceneNormalValid = step(1e-4, dot(sceneNormalWS, sceneNormalWS));
            float facingDot = dot(sceneNormalWS, -reflectDirWS);
            float backfacePass = (sceneNormalValid > 0.5)
                ? step(_SSRBackfaceThreshold, facingDot)
                : 1.0;

            if (hitDepthDelta <= thickness && backfacePass > 0.5)
            {
                trace.hit = true;
                trace.hitMask = 1.0;
                trace.hitUV = hitUV;
                trace.hitColor = SAMPLE_TEXTURE2D(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, hitUV).rgb;
                trace.depthDelta = hitDepthDelta;
                trace.edgeFade = WaterSSRComputeEdgeFade(hitUV, _SSREdgeFadeWidth);
                trace.thicknessFade = 1.0 - saturate(hitDepthDelta / thickness);
                trace.backfacePass = backfacePass;
                trace.fadeFactor = trace.edgeFade * trace.thicknessFade * trace.backfacePass;
                trace.blendWeight = saturate(_SSRStrength) * trace.fadeFactor;
                return trace;
            }
        }

        previousDistance = currentDistance;
        previousDepthDelta = depthDelta;
        currentDistance += stepLength;
    }

    return trace;
}

float3 ComposeSceneDepthDebugColor(float2 screenUV)
{
    float rawDepth = SampleSceneDepth(screenUV);
    float linearDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
    float nearWhiteDepth = 1.0 - saturate(linearDepth / max(_ProjectionParams.z, 1e-4));
    return nearWhiteDepth.xxx;
}

float3 ComposeSceneNormalDebugColor(float2 screenUV)
{
    float3 normalWS = normalize(SampleSceneNormals(screenUV));
    return normalWS * 0.5 + 0.5;
}

#endif
