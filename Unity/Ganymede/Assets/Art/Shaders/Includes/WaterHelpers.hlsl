#ifndef WATER_HELPERS_INCLUDED
#define WATER_HELPERS_INCLUDED

float CalculateFresnel(float3 normalWS, float3 positionWS, float power)
{
    float3 N = normalize(normalWS);
    float3 V = normalize(_WorldSpaceCameraPos - positionWS);
    float NdotV = saturate(dot(N, V));
    float F0 = 0.02;  // water at normal incidence reflects only 2% of light
    return F0 + (1.0 - F0) * pow(1.0 - NdotV, power);
}


float CalculateSpecular(float3 normalWS, float3 lightDir, float3 positionWS, float smoothness, float strength)
{
    float3 N = normalize(normalWS);
    float3 V = normalize(_WorldSpaceCameraPos - positionWS);
    float3 H = normalize(lightDir + V);         // halfway vector
    float NdotH = saturate(dot(N, H));
    float specPower = exp2(smoothness * 10.0 + 1.0); // convert smoothness to power
    return pow(NdotH, specPower) * strength;
}


half3 CalculateReflection(float3 normalWS, float3 positionWS, float smoothness, float4 screenPos)
{
    float3 N = normalize(normalWS);
    float3 V = normalize(_WorldSpaceCameraPos - positionWS);
    float3 R = reflect(-V, N);                                   // bounce view ray off surface
    half perceptualRoughness = 1.0 - smoothness;                 // smooth=sharp mip, rough=blurry mip
    float2 screenUV = screenPos.xy / screenPos.w;                // normalized screen space UV
    return GlossyEnvironmentReflection(R, positionWS, perceptualRoughness, 1.0, screenUV);
}


half3 CalculateRefraction(float3 normalWS, float4 screenPos, float strength, float thickness, float blurRadius)
{
    float2 screenUV = screenPos.xy / screenPos.w;
    float2 offset = normalize(normalWS).xy * strength * thickness;
    float2 centerUV = screenUV + offset;

    float r = blurRadius * thickness;

    half3 col = 0;
    col += SampleSceneColor(centerUV);
    col += SampleSceneColor(centerUV + float2( r,  0.0));
    col += SampleSceneColor(centerUV + float2(-r,  0.0));
    col += SampleSceneColor(centerUV + float2(0.0,  r));
    col += SampleSceneColor(centerUV + float2(0.0, -r));
    col += SampleSceneColor(centerUV + float2( r,  r) * 0.707);
    col += SampleSceneColor(centerUV + float2(-r,  r) * 0.707);
    col += SampleSceneColor(centerUV + float2( r, -r) * 0.707);
    col += SampleSceneColor(centerUV + float2(-r, -r) * 0.707);
    col += SampleSceneColor(centerUV + float2( 2.0 * r, 0.0));
    col += SampleSceneColor(centerUV + float2(-2.0 * r, 0.0));
    col += SampleSceneColor(centerUV + float2(0.0,  2.0 * r));
    col += SampleSceneColor(centerUV + float2(0.0, -2.0 * r));
    return col / 13.0;
}


// =========================================================================
// DEPTH-BASED WATER ABSORPTION (Beer's Law)
// =========================================================================
// In real life, light is absorbed exponentially as it travels through water.
// Shallow water → you see the scene behind it clearly, tinted by the water color.
// Deep water → the scene is nearly invisible, replaced by a deep dark color.
//
// Parameters:
//   sceneColor    = the refracted color of what's behind the water
//   shallowColor  = the tint color for shallow water (e.g. light turquoise)
//   deepColor     = the color for very deep water (e.g. dark navy blue)
//   depth         = water thickness in meters (from _WaterThicknessMap)
//   absorptionRate = how fast light gets absorbed (higher = murkier water)
// =========================================================================
half3 CalculateDepthColor(half3 sceneColor, half3 shallowColor, half3 deepColor, float depth, float absorptionRate)
{
    // Beer's Law: light decays exponentially through a medium
    // absorption = 1.0 at depth=0 (fully see through), → 0.0 as depth increases (fully opaque water)
    float absorption = exp(-depth * absorptionRate);

    // Tint the scene color toward the shallow water color based on depth
    half3 tintedScene = lerp(shallowColor, sceneColor, absorption);

    // Blend between the tinted scene and the deep water color
    // As depth increases, absorption → 0, so we see more deepColor
    half3 finalColor = lerp(deepColor, tintedScene, absorption);

    return finalColor;
}

#endif