#ifndef WATER_HELPERS_INCLUDED
#define WATER_HELPERS_INCLUDED

// Returns fresnel factor [0..1]
// 0 = camera looks straight at surface (transparent center)
// 1 = camera at grazing angle (opaque edge)
float CalculateFresnel(float3 normalWS, float3 positionWS, float power)
{
    float3 N = normalize(normalWS);
    float3 V = normalize(_WorldSpaceCameraPos - positionWS);
    return pow(1.0 - saturate(dot(N, V)), power);
}

// Returns specular intensity scaled by strength
// N = surface normal, L = light direction, V = view direction, smoothness = sharpness
float CalculateSpecular(float3 normalWS, float3 lightDir, float3 positionWS, float smoothness, float strength)
{
    float3 N = normalize(normalWS);
    float3 V = normalize(_WorldSpaceCameraPos - positionWS);
    float3 H = normalize(lightDir + V);         // halfway vector
    float NdotH = saturate(dot(N, H));
    float specPower = exp2(smoothness * 10.0 + 1.0); // convert smoothness to power
    return pow(NdotH, specPower) * strength;
}

// Returns environment reflection color sampled from reflection probe or skybox
// perceptualRoughness = 1 - smoothness selects mip level (sharp vs blurry)
half3 CalculateReflection(float3 normalWS, float3 positionWS, float smoothness, float4 screenPos)
{
    float3 N = normalize(normalWS);
    float3 V = normalize(_WorldSpaceCameraPos - positionWS);
    float3 R = reflect(-V, N);                                   // bounce view ray off surface
    half perceptualRoughness = 1.0 - smoothness;                 // smooth=sharp mip, rough=blurry mip
    float2 screenUV = screenPos.xy / screenPos.w;                // normalized screen space UV
    return GlossyEnvironmentReflection(R, positionWS, perceptualRoughness, 1.0, screenUV);
}

// Returns the refracted background color by offsetting screen UV using the surface normal
// Approximates Snell's Law: tilt of normal nudges the sample point, simulating light bending
half3 CalculateRefraction(float3 normalWS, float4 screenPos, float strength)
{
    float2 screenUV = screenPos.xy / screenPos.w;           // perspective divide -> [0,1]
    float2 offset = normalize(normalWS).xy * strength;      // tilt direction from normal
    return SampleSceneColor(screenUV + offset);             // sample opaque texture at distorted UV
}

#endif