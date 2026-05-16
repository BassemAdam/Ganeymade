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

float2 ProjectWorldPositionToScreenUV(float3 positionWS)
{
    float4 clipPosition = TransformWorldToHClip(positionWS);
    float4 screenPosition = ComputeScreenPos(clipPosition);
    return screenPosition.xy / max(screenPosition.w, 1e-5);
}

float SampleWaterBlueNoise(float2 screenUV)
{
    float2 pixelCoords = screenUV * _ScaledScreenParams.xy;
    float ignJitter = frac(52.9829189 * frac(dot(pixelCoords, float2(0.06711056, 0.00583715))));

    float2 blueNoiseUV = frac(pixelCoords * _BlueNoiseTex_TexelSize.xy * max(_BlueNoiseScale, 0.01));
    float4 blueNoiseSample = SAMPLE_TEXTURE2D_LOD(_BlueNoiseTex, sampler_BlueNoiseTex, blueNoiseUV, 0);

    uint channelIdx = (uint)floor(_Time.y * max(_BlueNoiseTimeSpeed, 0.01)) % 4;
    float blueNoise = blueNoiseSample[channelIdx];

    return lerp(ignJitter, blueNoise, saturate(_BlueNoiseStrength));
}

float SampleWaterBlueNoiseChannel(float2 screenUV, uint channelOffset)
{
    float2 pixelCoords = screenUV * _ScaledScreenParams.xy;
    float2 blueNoiseUV = frac(pixelCoords * _BlueNoiseTex_TexelSize.xy * max(_BlueNoiseScale, 0.01));
    float4 blueNoiseSample = SAMPLE_TEXTURE2D_LOD(_BlueNoiseTex, sampler_BlueNoiseTex, blueNoiseUV, 0);
    uint channelIdx = ((uint)floor(_Time.y * max(_BlueNoiseTimeSpeed, 0.01)) + channelOffset) % 4;
    return blueNoiseSample[channelIdx];
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
