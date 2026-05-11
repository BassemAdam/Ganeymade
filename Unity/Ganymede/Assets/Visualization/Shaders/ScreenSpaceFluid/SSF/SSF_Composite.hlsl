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

// Cheap value-noise gradient using a 3D hash.
// Used to perturb the normal in WORLD space so detail moves with
// the fluid rather than swimming in screen space.
float SSFHash31(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float SSFValueNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * (3.0 - 2.0 * f);
    float n000 = SSFHash31(i + float3(0,0,0));
    float n100 = SSFHash31(i + float3(1,0,0));
    float n010 = SSFHash31(i + float3(0,1,0));
    float n110 = SSFHash31(i + float3(1,1,0));
    float n001 = SSFHash31(i + float3(0,0,1));
    float n101 = SSFHash31(i + float3(1,0,1));
    float n011 = SSFHash31(i + float3(0,1,1));
    float n111 = SSFHash31(i + float3(1,1,1));
    float nx00 = lerp(n000, n100, u.x);
    float nx10 = lerp(n010, n110, u.x);
    float nx01 = lerp(n001, n101, u.x);
    float nx11 = lerp(n011, n111, u.x);
    float nxy0 = lerp(nx00, nx10, u.y);
    float nxy1 = lerp(nx01, nx11, u.y);
    return lerp(nxy0, nxy1, u.z) * 2.0 - 1.0;
}

float3 SSFPerturbNormalWS(float3 N, float3 posWS)
{
    if (_SurfaceNoiseStrength <= 1e-4) return N;
    float t = _Time.y * _SurfaceNoiseSpeed;
    float s = max(_SurfaceNoiseScale, 1e-3);
    float3 P = posWS * s + float3(t, t * 0.7, t * 1.3);
    float e = 0.5;
    float n0 = SSFValueNoise(P);
    float nx = SSFValueNoise(P + float3(e, 0, 0));
    float ny = SSFValueNoise(P + float3(0, e, 0));
    float nz = SSFValueNoise(P + float3(0, 0, e));
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
    if (eyeDepth < 1e-4 || nEnc.a < 0.5)
        discard;

    // -- Reconstruct surface geometry from depth + normals --
    // Normals are stored as (N * 0.5 + 0.5) in RGB, unpack to [-1..1].
    float3 nVS = normalize(nEnc.xyz * 2.0 - 1.0);           // view-space normal
    float3 pVS = SSFViewPosFromEyeDepth(uv, eyeDepth);      // view-space position
    float3 pWS = SSFWorldFromView(pVS);                      // world-space position
    float3 nWS = SSFNormalWorldFromView(nVS);                // world-space normal

    float3 V    = normalize(_WorldSpaceCameraPos.xyz - pWS); // view direction (surface -> camera)

    // -- Get main directional light --
    Light  mainLight = GetMainLight();
    float3 Ldir      = normalize(mainLight.direction);

    // ============================================================
    // STEP 1 — Wrapped Diffuse (Half-Lambert)
    //
    // Standard Lambert: diffuse = max(dot(N, L), 0)
    //   Problem: back-facing pixels go fully black, harsh terminator.
    //
    // Half-Lambert (Valve, Half-Life 2):
    //   diffuse = dot(N, L) * 0.5 + 0.5
    //   Remaps [-1..1] -> [0..1], so the dark side never fully blacks out.
    //   Looks softer and works well for translucent/volumetric surfaces.
    // ============================================================
    float NdotL       = dot(nWS, Ldir);
    float diffuseWrap = NdotL * 0.5 + 0.5;                         // half-Lambert
    half3 diffuse     = _FluidColor.rgb * mainLight.color * diffuseWrap * _DiffuseStrength;

    // ============================================================
    // STEP 2 — Blinn-Phong Specular
    //
    // Phong uses the reflect vector R = reflect(-L, N).
    // Blinn-Phong uses the *half-vector* H = normalize(L + V) instead.
    //   - Cheaper (no reflect())
    //   - Slightly wider, smoother highlight
    //   - Intensity = saturate(dot(N, H))^shininess
    //
    // We map the [0..1] _FluidSmoothness property to a shininess
    // exponent via exp2(smoothness*10+1) so the slider feels linear.
    // ============================================================
    float3 H        = normalize(Ldir + V);
    float  specPow  = exp2(_FluidSmoothness * 10.0 + 1.0);         // smoothness -> shininess
    float  specular = pow(saturate(dot(nWS, H)), specPow)
                    * saturate(NdotL);                              // no spec on back face
    half3  spec     = _FluidSpecularColor.rgb * mainLight.color * specular;

    // ============================================================
    // STEP 3 — Schlick Fresnel
    //
    // Water becomes mirror-like at glancing angles (Fresnel effect).
    // Full Fresnel equations are expensive; Schlick's approximation:
    //
    //   F(theta) = R0 + (1 - R0) * (1 - cos(theta))^exponent
    //
    // where:
    //   cos(theta) = dot(N, V)    — angle between normal and view
    //   R0         ~ 0.02         — reflectance at normal incidence
    //   exponent   = 4 or 5       — controls edge sharpness
    //
    // At theta=0  (looking straight down): F ~ R0   (mostly refractive)
    // At theta=90 (glancing):              F ~ 1.0  (fully reflective)
    //
    // We use fresnel to scale how much specular we add at the edges.
    // ============================================================
    float cosTheta = saturate(dot(nWS, V));
    float fresnel  = saturate(_FresnelR0 + (1.0 - _FresnelR0) * pow(1.0 - cosTheta, _FresnelPower));

    // Fresnel boosts specular at glancing angles: base spec + edge boost
    half3 result = diffuse + spec * (0.35 + 0.65 * fresnel);

    CompositeOut o;
    o.color = half4(result, 1.0);
    o.depth = SSFEyeDepthToHWDepth(eyeDepth);
    return o;
}

#endif // SSF_COMPOSITE_INCLUDED
