#ifndef SSF_COMPOSITE_INCLUDED
#define SSF_COMPOSITE_INCLUDED

#include "SSF_SSR.hlsl"

// _WaterSSFSceneCopy + sampler are declared in SSF_SSR.hlsl (included above).
TEXTURE2D(_WaterSSFDepthSmooth);
TEXTURE2D(_WaterSSFThickness);   SAMPLER(sampler_WaterSSFThickness);
TEXTURE2D(_WaterSSFNormals);     SAMPLER(sampler_WaterSSFNormals);

half4  _FluidColor;

float  _FluidSmoothness;
float  _FresnelPower;
float  _FresnelR0;
float  _ThicknessAbsorption;
float  _ReflectionStrength;
float  _RefractionStrength;

struct CompositeOut
{
    half4 color : SV_Target;
    float depth : SV_Depth;
};

float3 SSFWorldFromView(float3 vs) { return mul(UNITY_MATRIX_I_V, float4(vs, 1.0)).xyz; }
float3 SSFNormalWorldFromView(float3 nVS) { return normalize(mul((float3x3)UNITY_MATRIX_I_V, nVS)); }

float SchlickFresnel(float3 N, float3 V, float r0, float exponent)
{
    float ct = saturate(dot(N, V));
    return saturate(r0 + (1.0 - r0) * pow(1.0 - ct, max(exponent, 1.0)));
}

float3 SampleEnvironment(float3 R_WS, float3 posWS, float2 uv, float perceptualRoughness)
{
    return GlossyEnvironmentReflection(normalize(R_WS), posWS,
                                       saturate(perceptualRoughness),
                                       1.0h, uv);
}

CompositeOut fragSSFComposite(Varyings IN)
{
    float2 uv = IN.texcoord;

    float eyeDepth = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uv).r;
    half4  nEnc    = SAMPLE_TEXTURE2D(_WaterSSFNormals, sampler_WaterSSFNormals, uv);

    if (eyeDepth < 1e-4)
        discard;

    float2 nXY = nEnc.xy * 2.0 - 1.0;
    float3 nVS = normalize(float3(nXY, sqrt(max(0.0, 1.0 - dot(nXY, nXY)))));
    float3 pVS = SSFViewPosFromEyeDepth(uv, eyeDepth);
    float3 pWS = SSFWorldFromView(pVS);
    float3 nWS = SSFNormalWorldFromView(nVS);

    float3 V = normalize(_WorldSpaceCameraPos.xyz - pWS);

    float thickness = SAMPLE_TEXTURE2D(_WaterSSFThickness, sampler_WaterSSFThickness, uv).r;

    half3 transmittance = exp(-_ThicknessAbsorption * max(thickness, 0.0)
                              * max(1.0 - _FluidColor.rgb, 0.0));

    float3 incidentVS = normalize(pVS);
    float3 refrDirVS  = normalize(refract(incidentVS, nVS, 1.0 / 1.333));

    float4 curClip  = mul(UNITY_MATRIX_P, float4(pVS, 1.0));
    float4 exitClip = mul(UNITY_MATRIX_P, float4(pVS + refrDirVS * max(thickness, 0.0) * _RefractionStrength, 1.0));
    float2 deltaNDC = (exitClip.xy / exitClip.w) - (curClip.xy / curClip.w);
    float2 deltaUV  = deltaNDC * 0.5;
#if UNITY_UV_STARTS_AT_TOP
    deltaUV.y = -deltaUV.y;
#endif
    float2 refrUV = clamp(uv + deltaUV, 0.001, 0.999);

    half3 bgColor    = SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, refrUV).rgb;
    half3 refrColor  = bgColor * transmittance;

    float cosTheta = saturate(dot(nWS, V));
    float fresnel  = saturate(_FresnelR0 + (1.0 - _FresnelR0) * pow(1.0 - cosTheta, _FresnelPower));

    float3 R_WS     = reflect(-V, nWS);
    float  roughness = 1.0 - _FluidSmoothness;
    half3  reflColor = (half3)SampleEnvironment(R_WS, pWS, uv, roughness);

    if (R_WS.y < 0.0) fresnel *= 0.1;

    // Screen-Space Reflection: trace a ray from the fluid surface in the
    // reflection direction.  If it hits scene geometry, blend its colour
    // over the env-probe reflection.  Misses fall back to the probe.
    SSFSSRResult ssr = TraceSSFSSR(pWS, R_WS, uv);
    half3 finalRefl = reflColor * _ReflectionStrength;
    if (ssr.hit)
        finalRefl = lerp(finalRefl, ssr.hitColor, ssr.blendWeight);

    half3 result = lerp(refrColor, finalRefl, fresnel);

    // Debug: replace output with pure reflection (SSR hit or probe fallback)
    // to verify SSR coverage without full composite noise.
    if (_SSF_SSR_DebugVis > 0.5)
        result = finalRefl;

    CompositeOut o;
    o.color = half4(result, 1.0);
    o.depth = SSFEyeDepthToHWDepth(eyeDepth);
    return o;
}

#endif
