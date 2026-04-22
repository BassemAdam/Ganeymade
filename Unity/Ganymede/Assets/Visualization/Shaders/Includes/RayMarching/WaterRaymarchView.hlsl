#ifndef WATER_RAYMARCH_VIEW_INCLUDED
#define WATER_RAYMARCH_VIEW_INCLUDED

struct WaterRaymarchViewData
{
    float3 cameraPositionWS;
    float3 viewRayDirectionWS;
    float2 screenUV;
    float  viewDepthDenominator;
    float  blueNoiseValue;
};

float SampleSceneDistanceAlongRay(float2 screenUV, float viewDepthDenominator)
{
    float rawDepth = SampleSceneDepth(screenUV);
    float eyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
    return eyeDepth / viewDepthDenominator;
}

float SampleWaterBlueNoise(float2 screenUV)
{
    float2 screenPixelPosition = screenUV * _ScaledScreenParams.xy;
    float2 blueNoiseUV = frac(screenPixelPosition / 1024.0);
    return SAMPLE_TEXTURE2D(_BlueNoiseTex, sampler_BlueNoiseTex, blueNoiseUV).r;
}

WaterRaymarchViewData BuildWaterRaymarchViewData(float3 worldPositionWS, float4 normalizedScreenPosition)
{
    WaterRaymarchViewData viewData;
    viewData.cameraPositionWS = _WorldSpaceCameraPos.xyz;
    viewData.viewRayDirectionWS = normalize(worldPositionWS - viewData.cameraPositionWS);
    viewData.screenUV = normalizedScreenPosition.xy / normalizedScreenPosition.w;

    float3 cameraForwardWS = -UNITY_MATRIX_V[2].xyz;
    viewData.viewDepthDenominator = max(dot(viewData.viewRayDirectionWS, cameraForwardWS), 1e-4);
    viewData.blueNoiseValue = SampleWaterBlueNoise(viewData.screenUV);
    return viewData;
}

#endif
