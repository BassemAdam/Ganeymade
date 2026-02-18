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

#endif