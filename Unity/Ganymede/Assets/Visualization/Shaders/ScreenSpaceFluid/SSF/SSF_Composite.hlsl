#ifndef SSF_COMPOSITE_INCLUDED
#define SSF_COMPOSITE_INCLUDED

// ============================================================
// Pass 6 — ScreenSpaceFluidComposite
//
// Reads:
//   _BlitTexture        = smoothed eye-depth  (mask / opacity driver)
//   _WaterSSFSceneCopy  = scene background colour
//   _WaterSSFThickness  = accumulated chord length   (Beer-Lambert absorption)
//   _WaterSSFNormals    = encoded view-space normals  (lighting + Fresnel)
//
// Produces Fresnel-shaded water:
//   - Beer-Lambert: thick fluid absorbs scene background → fluid colour
//   - Fresnel (Schlick): glancing angles reflect more
//   - Phong specular from main light
//   - Result alpha-blended over scene using Fresnel + thickness
//   - SV_Depth output writes the reconstructed water surface depth into
//     the hardware depth buffer so opaque geometry correctly occludes water
//     and water correctly occludes geometry behind it.
//     Empty pixels are discarded (clip) so the existing scene depth is preserved.
// ============================================================

TEXTURE2D(_WaterSSFInput);      // smoothed eye-depth — bound from C# via SetGlobalTexture
TEXTURE2D(_WaterSSFSceneCopy);  SAMPLER(sampler_WaterSSFSceneCopy);
TEXTURE2D(_WaterSSFThickness);  SAMPLER(sampler_WaterSSFThickness);
TEXTURE2D(_WaterSSFNormals);    SAMPLER(sampler_WaterSSFNormals);

half4  _FluidColor;
half4  _ShallowColor;
half4  _DeepColor;
half4  _FluidSpecularColor;
float  _FluidSmoothness;
float  _FresnelPower;
float  _ThicknessAbsorption;
float  _AbsorptionRate;
float  _ReflectionStrength;
float  _RefractionStrength;
float  _RefractionBlur;
float  _RefractionThicknessScale;
float  _ThicknessCutoff;
float  _CompositeStrength;

// Output struct — colour target + hardware depth write-back
struct CompositeOut
{
    half4 color : SV_Target;
    float depth : SV_Depth;
};

#define IOR_AIR   1.0003
#define IOR_WATER 1.3330

// Main light direction in view-space (falls back to camera-forward if none)
float3 MainLightDirVS()
{
    return normalize(mul((float3x3)UNITY_MATRIX_V, _MainLightPosition.xyz));
}

// Reconstruct clip-space depth from positive eye-depth in metres.
float EyeDepthToNDC(float eyeDepth)
{
    // View-space convention: camera looks down -Z, so viewPos.z = -eyeDepth
    float4 clipPos  = mul(UNITY_MATRIX_P, float4(0.0, 0.0, -eyeDepth, 1.0));
    return clipPos.z / clipPos.w;
}

float3 ViewPosFromEyeDepth(float2 uv, float eyeDepth)
{
    float2 ndc = uv * 2.0 - 1.0;
    return float3(
        ndc.x * eyeDepth / UNITY_MATRIX_P[0][0],
        ndc.y * eyeDepth / UNITY_MATRIX_P[1][1],
        -eyeDepth);
}

float3 WorldPosFromView(float3 positionVS)
{
    return mul(UNITY_MATRIX_I_V, float4(positionVS, 1.0)).xyz;
}

float3 WorldNormalFromView(float3 normalVS)
{
    return normalize(mul((float3x3)UNITY_MATRIX_I_V, normalVS));
}

float CalculateReflectance(float3 inDir, float3 normal, float iorA, float iorB)
{
    float refractRatio = iorA / iorB;
    float cosAngleIn = saturate(-dot(inDir, normal));
    float sinSqrAngleOfRefraction = refractRatio * refractRatio * (1.0 - cosAngleIn * cosAngleIn);
    if (sinSqrAngleOfRefraction >= 1.0)
        return 1.0;

    float cosAngleOfRefraction = sqrt(max(0.0, 1.0 - sinSqrAngleOfRefraction));

    float rPerpendicular = (iorA * cosAngleIn - iorB * cosAngleOfRefraction)
                         / max(iorA * cosAngleIn + iorB * cosAngleOfRefraction, 1e-6);
    rPerpendicular *= rPerpendicular;

    float rParallel = (iorB * cosAngleIn - iorA * cosAngleOfRefraction)
                    / max(iorB * cosAngleIn + iorA * cosAngleOfRefraction, 1e-6);
    rParallel *= rParallel;

    return saturate((rPerpendicular + rParallel) * 0.5);
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

float3 SampleGlossyReflectionEnvironment(float3 reflectDirWS, float3 positionWS, float2 normalizedScreenUV, float perceptualRoughness)
{
    return GlossyEnvironmentReflection(
        normalize(reflectDirWS),
        positionWS,
        saturate(perceptualRoughness),
        1.0h,
        normalizedScreenUV
    );
}

float3 SampleReflectionEnvironment(float3 reflectDirWS, float3 positionWS, float2 normalizedScreenUV, float perceptualRoughness)
{
    float3 glossyEnvironment = SampleGlossyReflectionEnvironment(
        reflectDirWS,
        positionWS,
        normalizedScreenUV,
        perceptualRoughness
    );

    float glossyMax = max(glossyEnvironment.r, max(glossyEnvironment.g, glossyEnvironment.b));
    if (glossyMax > 1e-4)
        return glossyEnvironment;

    return SampleRawSceneSpecCube(reflectDirWS);
}

half3 SampleSceneRefraction(float2 uv, float3 normalVS, float thickness)
{
    float2 texel = 1.0 / _ScaledScreenParams.xy;
    float distortionPixels = _RefractionStrength * saturate(thickness * _RefractionThicknessScale);
    float blurPixels = _RefractionBlur * saturate(thickness * _RefractionThicknessScale);
    float2 refractOffset = normalVS.xy * distortionPixels * texel;
    float2 centerUV = clamp(uv + refractOffset, 0.001, 0.999);
    float2 blurStep = texel * blurPixels;

    half3 color = 0.0;
    color += SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, centerUV).rgb;
    color += SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, clamp(centerUV + float2( blurStep.x, 0.0), 0.001, 0.999)).rgb;
    color += SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, clamp(centerUV + float2(-blurStep.x, 0.0), 0.001, 0.999)).rgb;
    color += SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, clamp(centerUV + float2(0.0,  blurStep.y), 0.001, 0.999)).rgb;
    color += SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, clamp(centerUV + float2(0.0, -blurStep.y), 0.001, 0.999)).rgb;
    return color / 5.0;
}

half3 CalculateDepthColor(half3 sceneColor, half3 shallowColor, half3 deepColor, float depth, float absorptionRate)
{
    float absorption = exp(-depth * absorptionRate);
    half3 waterTint = lerp(deepColor, shallowColor, absorption);
    return lerp(waterTint, sceneColor * waterTint, absorption);
}

CompositeOut fragComposite(Varyings IN)
{
    float2 uv = IN.texcoord;

    float  eyeDepth  = SAMPLE_TEXTURE2D(_WaterSSFInput,   sampler_PointClamp,      uv).r;
    half4  normalEnc = SAMPLE_TEXTURE2D(_WaterSSFNormals, sampler_WaterSSFNormals, uv);
    float  thickness = SAMPLE_TEXTURE2D(_WaterSSFThickness, sampler_WaterSSFThickness, uv).r;

    // No fluid at this pixel — discard entirely so the scene colour and the
    // hardware depth written by opaque passes are both preserved unchanged.
    if (eyeDepth < 1e-4 || normalEnc.a < 0.5 || thickness < _ThicknessCutoff)
        discard;

    float3 normalVS = normalize(normalEnc.xyz * 2.0 - 1.0);
    float3 positionVS = ViewPosFromEyeDepth(uv, eyeDepth);
    float3 positionWS = WorldPosFromView(positionVS);
    float3 normalWS = WorldNormalFromView(normalVS);
    float3 viewDirWS = normalize(_WorldSpaceCameraPos.xyz - positionWS);
    float3 incidentWS = -viewDirWS;

    float fresnelExact = CalculateReflectance(incidentWS, normalWS, IOR_AIR, IOR_WATER);
    float fresnelArt = pow(saturate(1.0 - saturate(dot(normalWS, viewDirWS))), _FresnelPower);
    float fresnel = saturate(max(fresnelExact, 0.02 + 0.98 * fresnelArt));

    half3 refractedScene = SampleSceneRefraction(uv, normalVS, thickness);
    half3 depthTintedRefraction = CalculateDepthColor(
        refractedScene,
        _ShallowColor.rgb,
        _DeepColor.rgb,
        thickness,
        _AbsorptionRate);
    float3 transmittance = exp(-thickness * _ThicknessAbsorption * max(_FluidColor.rgb, 1e-3));
    half3 fluidRefraction = lerp(_FluidColor.rgb, depthTintedRefraction, saturate(transmittance));

    float perceptualRoughness = saturate(1.0 - _FluidSmoothness);
    float3 reflectDirWS = reflect(incidentWS, normalWS);
    half3 reflection = SampleReflectionEnvironment(reflectDirWS, positionWS, uv, perceptualRoughness) * _ReflectionStrength;

    Light mainLight = GetMainLight();
    float3 L = normalize(mainLight.direction);
    float3 H = normalize(L + viewDirWS);
    float specPow = exp2(_FluidSmoothness * 10.0 + 1.0);
    float spec = pow(saturate(dot(normalWS, H)), specPow) * saturate(dot(normalWS, L));
    half3 specCol = _FluidSpecularColor.rgb * mainLight.color * spec;

    half3 surfaceColor = reflection * fresnel + specCol * (0.35 + 0.65 * fresnel);
    half3 result = lerp(fluidRefraction, surfaceColor + fluidRefraction, saturate(fresnel));
    float thicknessMask = saturate((thickness - _ThicknessCutoff) / max(_ThicknessCutoff, 1e-4));
    float alpha = saturate((1.0 - dot(transmittance, float3(0.3333, 0.3333, 0.3333))) + fresnel * 0.35) * thicknessMask * _CompositeStrength;

    // Reconstruct the water surface NDC depth and write it back to the
    // hardware depth buffer.  The ZTest LEqual on the pass ensures the GPU
    // discards this fragment if an opaque object is already closer, so no
    // extra manual depth comparison is needed in the shader.
    CompositeOut o;
    o.color = half4(result, alpha);
    o.depth = EyeDepthToNDC(eyeDepth);
    return o;
}

#endif // SSF_COMPOSITE_INCLUDED
