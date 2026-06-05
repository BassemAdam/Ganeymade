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
float  _SpecularStrength;
float  _RefractionThicknessFade;

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
    // GlossyEnvironmentReflection parameters:
    // 1. reflectVector (R_WS): World space direction along which to sample reflections (reflected view ray).
    // 2. positionWS (posWS): Used by Unity to select the correct reflection probe volume and apply box projection correction.
    // 3. perceptualRoughness: Roughness to select the corresponding cubemap mipmap level (sharp vs blurry).
    // 4. occlusion (1.0h): Occlusion factor. Water surfaces do not have ambient/reflection occlusion, so 1.0h is passed.
    // 5. normalizedScreenSpaceUV (uv): Used by URP for reflection probe blending and screen-space coordinates lookup.
    return GlossyEnvironmentReflection(normalize(R_WS), posWS,
                                       saturate(perceptualRoughness),
                                       1.0h, uv);
}

// GGX specular term for the main directional light.
// Water is highly specular so this produces the bright glints that make the surface
// read as liquid even when reflection strength is zero.
// NdotH: normal dot half-vector, roughness: 1 - smoothness.
float GGXSpecular(float NdotH, float roughness)
{
    float a  = roughness * roughness;
    float a2 = a * a;
    float d  = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d + 1e-7);
}

CompositeOut fragSSFComposite(Varyings IN)
{
    float2 uv = IN.texcoord;

    float eyeDepth = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uv).r;
    half4  nEnc    = SAMPLE_TEXTURE2D(_WaterSSFNormals, sampler_WaterSSFNormals, uv);

    // No fluid surface at this pixel, discard so the underlying scene shows through.
    if (eyeDepth < 1e-4)
        discard;

    // Decode the XY-encoded view-space normal and reconstruct Z from the unit sphere constraint.
    // The normals pass encodes normals as [0,1] so we remap back to [-1,1].
    float2 nXY = nEnc.xy * 2.0 - 1.0;
    float3 nVS = normalize(float3(nXY, sqrt(max(0.0, 1.0 - dot(nXY, nXY)))));
    float3 pVS = SSFViewPosFromEyeDepth(uv, eyeDepth);
    float3 pWS = SSFWorldFromView(pVS);
    float3 nWS = SSFNormalWorldFromView(nVS);

    float3 V = normalize(_WorldSpaceCameraPos.xyz - pWS);

    float thickness = SAMPLE_TEXTURE2D(_WaterSSFThickness, sampler_WaterSSFThickness, uv).r;

    // ----------------------------------------------------------------------
    // 1. REFRACTION — bend the view ray through the surface and read the scene
    //    behind the water, then tint it by how far the light travelled.
    // ----------------------------------------------------------------------
    // Refracted ray direction in view space. 1/1.333 is the reciprocal of
    // water's index of refraction (Snell's law). refract() returns 0 on total
    // internal reflection, which only happens looking out from inside; here the
    // incident ray always enters the surface so we get a valid direction.
    float3 incidentVS = normalize(pVS);
    float3 refrDirVS  = refract(incidentVS, nVS, 1.0 / 1.333);

    // Project the surface point and a point a short distance along the refracted
    // ray to screen space; the difference is the UV offset for sampling the
    // background. Scaling by thickness makes thicker water bend the view more.
    float  refrDepth = max(thickness, 0.0) * _RefractionStrength;
    float4 curClip   = mul(UNITY_MATRIX_P, float4(pVS, 1.0));
    float4 exitClip  = mul(UNITY_MATRIX_P, float4(pVS + refrDirVS * refrDepth, 1.0));
    float2 deltaNDC  = (exitClip.xy / exitClip.w) - (curClip.xy / curClip.w);
    float2 deltaUV   = deltaNDC * 0.5;
#if UNITY_UV_STARTS_AT_TOP
    deltaUV.y = -deltaUV.y;
#endif

    // Clamp the maximum screen-space offset. A hard cap (in UV units) stops thin
    // silhouette pixels — where the reconstructed normal points sideways — from
    // sampling far across the screen and producing the circular "bubble" halo.
    // _RefractionThicknessFade now acts as an inverse cap: higher = tighter clamp.
    float  maxOffset = 0.5 / max(_RefractionThicknessFade, 0.01);
    float  offLen    = length(deltaUV);
    if (offLen > maxOffset) deltaUV *= maxOffset / max(offLen, 1e-5);

    float2 refrUV = clamp(uv + deltaUV, 0.002, 0.998);

    // Depth guard: only use the refracted UV if the scene there is behind the water
    // surface.  Without this, an object above the water whose screen projection
    // overlaps refrUV will be incorrectly bent/distorted through the surface.
    // eyeDepth is already the water surface eye-depth (from _WaterSSFDepthSmooth).
    // We compare it directly against the scene eye-depth at the refracted UV —
    // both are in the same LinearEyeDepth space so no per-ray denominator is needed.
    float refrSceneEyeDepth = LinearEyeDepth(SampleSceneDepth(refrUV), _ZBufferParams);
    float refrDepthMargin   = 0.05;   // 5 cm soft ramp to avoid hard-edge popping
    float refrDepthValidity = saturate((refrSceneEyeDepth - eyeDepth) / refrDepthMargin);
    refrUV = lerp(uv, refrUV, refrDepthValidity);

    half3 bgColor = SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, refrUV).rgb;

    // Beer-Lambert absorption. The fluid colour is treated as transmission colour:
    // the longer the path through the water, the more it is tinted toward that hue.
    // saturate() on the colour keeps HDR-bright inputs from inverting the sign.
    float3 absorbColor   = saturate(_FluidColor.rgb);
    half3  transmittance = exp(-_ThicknessAbsorption * max(thickness, 0.0) * (1.0 - absorbColor));
    half3  refrColor     = bgColor * transmittance;

    // ----------------------------------------------------------------------
    // 2. REFLECTION — environment probe plus optional screen-space reflection.
    //    This is ALWAYS evaluated (never gated by thickness) so the surface
    //    reads as water at every pixel, including thin edges.
    // ----------------------------------------------------------------------
    float3 R_WS      = reflect(-V, nWS);
    float  roughness = 1.0 - _FluidSmoothness;
    half3  reflColor = (half3)SampleEnvironment(R_WS, pWS, uv, roughness);

    // Screen-Space Reflection: trace from the surface along R_WS. If it hits scene
    // geometry, blend that colour over the probe fallback for sharper reflections.
    SSFSSRResult ssr = TraceSSFSSR(pWS, R_WS, uv);
    half3 finalRefl  = reflColor;
    if (ssr.hit)
        finalRefl = lerp(finalRefl, ssr.hitColor, ssr.blendWeight);

    // ----------------------------------------------------------------------
    // 3. FRESNEL — blend refraction and reflection by viewing angle. Head-on the
    //    water is clear (refraction); at grazing angles it mirrors (reflection).
    // ----------------------------------------------------------------------
    float cosTheta = saturate(dot(nWS, V));
    float fresnel  = saturate(_FresnelR0 + (1.0 - _FresnelR0) * pow(1.0 - cosTheta, _FresnelPower));

    // Reduce reflection when the mirror direction points down into the ground,
    // which would otherwise reflect the floor up through the surface unnaturally.
    if (R_WS.y < 0.0) fresnel *= 0.25;

    float effectiveFresnel = fresnel * saturate(_ReflectionStrength);
    half3 result = lerp(refrColor, finalRefl, effectiveFresnel);

    // ----------------------------------------------------------------------
    // 4. SPECULAR — sharp glint from the main directional light. This is the
    //    single most important cue that sells the surface as liquid, so it is
    //    NOT gated by thickness; it fires on every lit surface pixel.
    //
    //    We read _MainLightPosition/_MainLightColor directly rather than calling
    //    GetMainLight() because in a blit pass the shadow keywords are inactive
    //    and GetMainLight() can return a garbage colour. The _MainLight* globals
    //    are set by URP every frame and are always valid here.
    // ----------------------------------------------------------------------
    float3 L          = normalize(_MainLightPosition.xyz);
    half3  lightColor = _MainLightColor.rgb;
    float3 H          = normalize(V + L);
    float  NdotH      = saturate(dot(nWS, H));
    float  NdotL      = saturate(dot(nWS, L));
    float  spec       = GGXSpecular(NdotH, roughness) * NdotL;
    result += (half3)(spec * _SpecularStrength) * lightColor;

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
