#ifndef SSF_COMPOSITE_INCLUDED
#define SSF_COMPOSITE_INCLUDED

// ============================================================
// Pass: ScreenSpaceFluidComposite  (Simon Green Steps 4 + 5 + 6)
//
//   - Schlick Fresnel + reflection cubemap            (Step 4)
//   - Beer-Lambert volume absorption from thickness   (Step 5)
//   - Background refraction with N.xy offset          (Step 6)
//   - Wrapped diffuse + Blinn-Phong specular
//   - Optional light-view shadow attenuation
//
// Inputs:
//   _WaterSSFDepthSmooth = smoothed eye-depth        (RFloat, metres)
//   _WaterSSFNormals   = encoded view-space normals  (RGBAHalf)
//   _WaterSSFThickness = additive splat thickness    (RHalf)
//   _WaterSSFSceneCopy = scene colour snapshot       (camera format)
//
// Output: SV_Target colour + SV_Depth = water surface depth so
// later opaque/transparent passes occlude correctly.
// ============================================================

// Input: smoothed eye-depth written by the blur pass.
TEXTURE2D(_WaterSSFDepthSmooth);
TEXTURE2D(_WaterSSFSceneCopy);   SAMPLER(sampler_WaterSSFSceneCopy);
TEXTURE2D(_WaterSSFThickness);   SAMPLER(sampler_WaterSSFThickness);
TEXTURE2D(_WaterSSFNormals);     SAMPLER(sampler_WaterSSFNormals);

// Water look
half4  _FluidColor;

float  _FluidSmoothness;
float  _FresnelPower;
float  _FresnelR0;
float  _ThicknessAbsorption;
float  _ReflectionStrength;
float  _RefractionStrength;

// Light-view shadow
TEXTURE2D(_WaterSSFLightDepth);   SAMPLER(sampler_WaterSSFLightDepth);
float4x4 _WaterSSFLightVP;
float    _WaterSSFLightShadowEnabled;
float    _WaterSSFLightShadowStrength;
float    _WaterSSFLightShadowBias;

struct CompositeOut
{
    half4 color : SV_Target;
    float depth : SV_Depth;
};

// ---- Helpers ------------------------------------------------
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

// Light-view shadow lookup: project world position into the light's
// VP, do a single PCF tap.
float SSFLightShadowAtten(float3 posWS)
{
    if (_WaterSSFLightShadowEnabled < 0.5) return 1.0;

    float4 lc = mul(_WaterSSFLightVP, float4(posWS, 1.0));
    if (lc.w <= 0.0) return 1.0;
    float3 ndc = lc.xyz / lc.w;
    float2 uv  = ndc.xy * 0.5 + 0.5;
    if (any(uv < 0.0) || any(uv > 1.0)) return 1.0;

    // Light-view depth is the eye-depth from the LIGHT's POV
    // (positive distance along light forward direction in metres).
    float receiverEye = -lc.z; // OpenGL-style: light looks down its -Z
    float blockerEye  = SAMPLE_TEXTURE2D(_WaterSSFLightDepth, sampler_WaterSSFLightDepth, uv).r;
    if (blockerEye < 1e-4) return 1.0;

    float lit = (receiverEye <= blockerEye + _WaterSSFLightShadowBias) ? 1.0 : 0.0;
    return lerp(1.0, lit, saturate(_WaterSSFLightShadowStrength));
}

CompositeOut fragSSFComposite(Varyings IN)
{
    float2 uv = IN.texcoord;

    // -- Sample smoothed depth and encoded view-space normal from pipeline --
    float eyeDepth = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uv).r;
    half4  nEnc    = SAMPLE_TEXTURE2D(_WaterSSFNormals, sampler_WaterSSFNormals, uv);

    // Discard background pixels (no particle depth written here)
    if (eyeDepth < 1e-4)
        discard;

    // -- Reconstruct surface geometry from depth + normals --
    // Normals stored as RGHalf (XY only); Z is always >= 0 (view-space, faces camera).
    float2 nXY = nEnc.xy * 2.0 - 1.0;
    float3 nVS = normalize(float3(nXY, sqrt(max(0.0, 1.0 - dot(nXY, nXY)))));  // view-space normal
    float3 pVS = SSFViewPosFromEyeDepth(uv, eyeDepth);      // view-space position
    float3 pWS = SSFWorldFromView(pVS);                      // world-space position
    float3 nWS = SSFNormalWorldFromView(nVS);                // world-space normal

    float3 V = normalize(_WorldSpaceCameraPos.xyz - pWS);    // surface → camera

    // Thickness for volume absorption
    float thickness = SAMPLE_TEXTURE2D(_WaterSSFThickness, sampler_WaterSSFThickness, uv).r;

    // ============================================================
    // STEP 1 — Beer-Lambert Transmittance
    //
    // Water absorbs light differently per wavelength.
    // _FluidColor.rgb describes what colour is TRANSMITTED (i.e. least
    // absorbed). The complement (1 - color) is the absorption per channel:
    //
    //   transmittance = exp(-absorption * thickness)
    //                 = exp(-_ThicknessAbsorption * thickness * (1 - _FluidColor))
    //
    // Thick water → transmittance → 0 → scene behind is tinted to _FluidColor.
    // Thin water  → transmittance → 1 → scene behind is barely tinted.
    //
    // Matches: fluid.wgsl  transmittance = exp(-density * 10 * thickness * (1-diffuseColor))
    // ============================================================
    half3 transmittance = exp(-_ThicknessAbsorption * max(thickness, 0.0)
                              * max(1.0 - _FluidColor.rgb, 0.0));

    // ============================================================
    // STEP 2 — Physical Refraction  (Snell's law, IOR = 1.333 for water)
    //
    // refract(I, N, eta):
    //   I   = incident direction, pointing TOWARD the surface
    //         In view-space, camera is at origin, so I = normalize(pVS).
    //   N   = surface normal, pointing AWAY from the surface (toward camera)
    //         nVS already points toward camera (z > 0).
    //   eta = n1/n2 = 1.0/1.333  (air → water)
    //
    // We then project the exit point (surface + dir * thickness) to
    // screen space and compute the UV OFFSET from the current pixel.
    // Using a delta avoids any platform-specific Y-flip in NDC→UV.
    //
    // Matches: fluid.wgsl  refractionDirView = refract(rayDirView, normal, 1/1.333)
    //          + calcReflactedTexCoord projected exit point approach
    // ============================================================
    float3 incidentVS = normalize(pVS);                      // camera→surface in VS
    float3 refrDirVS  = normalize(refract(incidentVS, nVS, 1.0 / 1.333));

    // Project current surface position and refracted exit point to screen
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

    // ============================================================
    // STEP 3 — Schlick Fresnel
    //
    //   F(θ) = R0 + (1-R0)·(1-cosθ)^power
    //
    // cosθ = dot(N, V).  At θ=0 (looking straight down) → F≈R0 (mostly refraction).
    // At θ=90 (glancing)                                 → F≈1  (mostly reflection).
    //
    // Matches: fluid.wgsl  fresnel = F0 + (1-F0)*(1-dot(normal,-rayDir))^5
    // ============================================================
    float cosTheta = saturate(dot(nWS, V));
    float fresnel  = saturate(_FresnelR0 + (1.0 - _FresnelR0) * pow(1.0 - cosTheta, _FresnelPower));

    // ============================================================
    // STEP 4 — Environment Reflection (baked skybox × Fresnel)
    //
    // reflect(-V, N) gives the mirror direction in world space.
    // GlossyEnvironmentReflection samples the baked probe at the
    // mip level corresponding to the surface roughness.
    //
    // Matches: fluid.wgsl  reflectionColor = envmap.sample(reflect(rayDir, normal))
    // ============================================================
    float3 R_WS     = reflect(-V, nWS);
    float  roughness = 1.0 - _FluidSmoothness;
    half3  reflColor = (half3)SampleEnvironment(R_WS, pWS, uv, roughness);

    // Dampen reflection Fresnel when the mirror direction goes below the horizon.
    // Matches fluid.wgsl: fresnel = select(fresnel, 0.1 * fresnel, reflectionDirWorld.y < 0)
    // Below-horizon reflections pick up the floor/subsurface which is much dimmer.
    if (R_WS.y < 0.0) fresnel *= 0.1;

    // ============================================================
    // Combine — mix refraction and reflection by Fresnel
    //
    //   result = lerp(refrColor, reflColor, fresnel)
    //
    // Matches fluid.wgsl: finalColor = mix(refractionColor, reflectionColor, fresnel)
    // No Blinn-Phong specular — fluid.wgsl uses 0.0 * specular (none).
    // ============================================================
    half3 result = lerp(refrColor, reflColor * _ReflectionStrength, fresnel);

    // Optional light-view shadow attenuation
    result *= SSFLightShadowAtten(pWS);

    CompositeOut o;
    o.color = half4(result, 1.0);
    o.depth = SSFEyeDepthToHWDepth(eyeDepth);
    return o;
}

#endif // SSF_COMPOSITE_INCLUDED
