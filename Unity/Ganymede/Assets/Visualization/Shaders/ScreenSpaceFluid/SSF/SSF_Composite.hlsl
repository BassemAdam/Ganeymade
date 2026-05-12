#ifndef SSF_COMPOSITE_INCLUDED
#define SSF_COMPOSITE_INCLUDED

// ============================================================
// Pass: ScreenSpaceFluidComposite  (Simon Green Steps 4 + 5 + 6)
//
//   - Schlick Fresnel + reflection cubemap            (Step 4)
//   - Beer-Lambert volume absorption from thickness   (Step 5)
//   - Background refraction with N.xy offset          (Step 6)
//   - Wrapped diffuse + Blinn-Phong specular
//   - Optional 3D world-space noise normal detail     (Step 7)
//   - Optional light-view shadow attenuation          (Step 7)
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
half4  _ShallowColor;
half4  _DeepColor;
half4  _FluidSpecularColor;
float  _FluidSmoothness;
float  _FresnelPower;
float  _FresnelR0;
float  _DiffuseWrap;
float  _DiffuseStrength;
float  _ThicknessAbsorption;
float  _AbsorptionRate;
float  _ReflectionStrength;
float  _RefractionStrength;
float  _RefractionBlur;
float  _RefractionThicknessScale;
float  _ThicknessCutoff;
float  _CompositeStrength;

// Surface noise (Step 7)
float  _SurfaceNoiseStrength;     // 0 = off
float  _SurfaceNoiseScale;        // world-space frequency
float  _SurfaceNoiseSpeed;        // animation speed (sec⁻¹)

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

half3 SampleBackground(float2 uv, float3 N_VS, float thickness)
{
    float2 texel    = 1.0 / _ScaledScreenParams.xy;
    float  distort  = _RefractionStrength * saturate(thickness * _RefractionThicknessScale);
    float  blurPx   = _RefractionBlur     * saturate(thickness * _RefractionThicknessScale);
    float2 offset   = N_VS.xy * distort * texel;
    float2 c        = clamp(uv + offset, 0.001, 0.999);
    float2 step     = texel * blurPx;

    half3 col = 0;
    col += SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, c).rgb;
    col += SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, clamp(c + float2( step.x, 0), 0.001, 0.999)).rgb;
    col += SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, clamp(c + float2(-step.x, 0), 0.001, 0.999)).rgb;
    col += SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, clamp(c + float2(0,  step.y), 0.001, 0.999)).rgb;
    col += SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, clamp(c + float2(0, -step.y), 0.001, 0.999)).rgb;
    return col / 5.0;
}

// 3D noise texture for surface normal perturbation.
// Assign a Repeat-wrapped 3D texture in the material (_SurfaceNoiseTex3D).
// When not assigned, Unity provides a 1×1×1 black fallback → gradient = 0 → no perturbation.
TEXTURE3D(_SurfaceNoiseTex3D);  SAMPLER(sampler_SurfaceNoiseTex3D);

float3 SSFPerturbNormalWS(float3 N, float3 posWS)
{
    if (_SurfaceNoiseStrength <= 1e-4) return N;
    float  t = _Time.y * _SurfaceNoiseSpeed;
    float  s = max(_SurfaceNoiseScale, 1e-3);
    // Animate with per-axis offsets so the pattern doesn't look planar.
    float3 P = posWS * s + float3(t, t * 0.7, t * 1.3);
    // Central-difference gradient — 3 extra taps in X, Y, Z.
    float e  = 0.04;
    float n0 = SAMPLE_TEXTURE3D(_SurfaceNoiseTex3D, sampler_SurfaceNoiseTex3D, P            ).r * 2.0 - 1.0;
    float nx = SAMPLE_TEXTURE3D(_SurfaceNoiseTex3D, sampler_SurfaceNoiseTex3D, P+float3(e,0,0)).r * 2.0 - 1.0;
    float ny = SAMPLE_TEXTURE3D(_SurfaceNoiseTex3D, sampler_SurfaceNoiseTex3D, P+float3(0,e,0)).r * 2.0 - 1.0;
    float nz = SAMPLE_TEXTURE3D(_SurfaceNoiseTex3D, sampler_SurfaceNoiseTex3D, P+float3(0,0,e)).r * 2.0 - 1.0;
    float3 grad = float3(nx - n0, ny - n0, nz - n0);
    return normalize(N + grad * _SurfaceNoiseStrength);
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

half3 ApplyDepthTint(half3 sceneColor, float thickness)
{
    float a   = exp(-thickness * _AbsorptionRate);
    half3 tint = lerp(_DeepColor.rgb, _ShallowColor.rgb, a);
    return lerp(tint, sceneColor * tint, a);
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

    // Optional surface noise perturbation
    nWS = SSFPerturbNormalWS(nWS, pWS);

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

    // ============================================================
    // STEP 5 — Blinn-Phong specular highlight (sun / directional light)
    //
    // The reference sets specular contribution to 0 for simplicity,
    // but a small highlight is critical for water to look realistic.
    // We keep it additive on top of the refract/reflect mix.
    // ============================================================
    Light  mainLight = GetMainLight();
    float3 Ldir      = normalize(mainLight.direction);
    float3 H         = normalize(Ldir + V);
    float  specPow   = exp2(_FluidSmoothness * 10.0 + 1.0);
    float  specular  = pow(saturate(dot(nWS, H)), specPow) * saturate(dot(nWS, Ldir));
    half3  spec      = _FluidSpecularColor.rgb * mainLight.color * specular;

    // ============================================================
    // Combine — mix refraction and reflection by Fresnel, add specular
    //
    //   result = lerp(refrColor, reflColor, fresnel) + spec
    //
    // This is the core formula from fluid.wgsl:
    //   finalColor = mix(refractionColor, reflectionColor, fresnel)
    // We add a specular highlight on top.
    // ============================================================
    half3 result = lerp(refrColor, reflColor * _ReflectionStrength, fresnel) + spec;

    // Optional light-view shadow attenuation
    result *= SSFLightShadowAtten(pWS);

    CompositeOut o;
    o.color = half4(result, 1.0);
    o.depth = SSFEyeDepthToHWDepth(eyeDepth);
    return o;
}

#endif // SSF_COMPOSITE_INCLUDED
