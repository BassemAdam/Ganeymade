#ifndef SSF_CAUSTICS_INCLUDED
#define SSF_CAUSTICS_INCLUDED

// ============================================================
// Pass: ScreenSpaceFluidCaustics  (Simon Green Step 7 — caustics)
//
// Screen-space pass executed AFTER composite. For every pixel
// that is "underwater" (scene-depth is behind the smoothed water
// surface depth), it projects the world position onto a horizontal
// caustics plane along the main light direction, samples the
// designer-supplied caustics texture, and additively brightens the
// scene colour. Optionally modulated by the light-view shadow map
// (no caustic where the light cannot reach), and by the fluid
// thickness above the receiver (focused vs. diffuse light).
//
// Inputs:
//   _CameraOpaqueTexture  — bound by URP (or _WaterSSFSceneCopy)
//   _CameraDepthTexture   — bound by URP
//   _WaterSSFDepthSmooth  — smoothed water eye-depth
//   _WaterSSFThickness    — fluid thickness
//   _WaterSSFLightDepth   — light-view sphere depth (optional)
//   _CausticsTex          — designer texture
// ============================================================

// Input: smoothed eye-depth written by the blur pass.
TEXTURE2D(_WaterSSFDepthSmooth);
TEXTURE2D(_WaterSSFThickness);    SAMPLER(sampler_WaterSSFThickness);
TEXTURE2D(_WaterSSFSceneCopy);    SAMPLER(sampler_WaterSSFSceneCopy);
TEXTURE2D(_WaterSSFLightDepth);   SAMPLER(sampler_WaterSSFLightDepth);
TEXTURE2D(_CausticsTex);          SAMPLER(sampler_CausticsTex);

float4   _CausticsTex_ST;
float    _CausticsStrength;
float    _CausticsTiling;
float    _CausticsPlaneY;
float    _CausticsScrollSpeed;
float    _CausticsThicknessAttenuation; // higher = caustics fade faster with thickness above
float    _CausticsDepthAttenuation;     // higher = fade faster with submerged depth
float4x4 _WaterSSFLightVP;
float    _WaterSSFLightShadowEnabled;
float    _WaterSSFLightShadowBias;
float    _ThicknessCutoff;

half4 fragSSFCaustics(Varyings IN) : SV_Target
{
    float2 uv = IN.texcoord;

    half3 sceneCol = SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, uv).rgb;

    if (_CausticsStrength <= 1e-4) return half4(sceneCol, 1);

    // Reconstruct scene world position from URP camera depth.
    float rawDepth   = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, uv).r;
    float sceneEye   = LinearEyeDepth(rawDepth, _ZBufferParams);
    float waterEye   = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uv).r;
    float waterThick = SAMPLE_TEXTURE2D(_WaterSSFThickness, sampler_WaterSSFThickness, uv).r;

    // Only paint caustics where the scene is behind a fluid pixel.
    if (waterEye < 1e-4 || waterThick < _ThicknessCutoff || sceneEye <= waterEye)
        return half4(sceneCol, 1);

    float3 sceneVS = SSFViewPosFromEyeDepth(uv, sceneEye);
    float3 sceneWS = mul(UNITY_MATRIX_I_V, float4(sceneVS, 1.0)).xyz;

    // Light-view shadow: skip caustics where the light cannot reach.
    float lightVis = 1.0;
    if (_WaterSSFLightShadowEnabled > 0.5)
    {
        float4 lc = mul(_WaterSSFLightVP, float4(sceneWS, 1.0));
        if (lc.w > 0.0)
        {
            float2 luv = (lc.xy / lc.w) * 0.5 + 0.5;
            if (all(luv >= 0.0) && all(luv <= 1.0))
            {
                float receiver = -lc.z;
                float blocker  = SAMPLE_TEXTURE2D(_WaterSSFLightDepth, sampler_WaterSSFLightDepth, luv).r;
                if (blocker > 1e-4 && receiver > blocker + _WaterSSFLightShadowBias)
                    lightVis = 0.0;
            }
        }
    }

    if (lightVis < 0.5)
        return half4(sceneCol, 1);

    // Project sceneWS onto the caustics plane along the main light dir.
    Light L       = GetMainLight();
    float3 Ldir   = normalize(-L.direction); // light TRAVEL direction
    float3 origin = sceneWS;
    float  t      = (origin.y - _CausticsPlaneY) / max(abs(Ldir.y), 1e-3) * sign(-Ldir.y);
    float3 hit    = origin + Ldir * t;

    // Tile across the XZ plane, with optional scroll.
    float2 tileUV = hit.xz * max(_CausticsTiling, 1e-3) + _Time.y * _CausticsScrollSpeed;
    half3  caus   = SAMPLE_TEXTURE2D(_CausticsTex, sampler_CausticsTex, tileUV).rgb;

    // Attenuation: fade with thickness above receiver and with submerged depth.
    float submerged  = max(sceneEye - waterEye, 0.0);
    float thickFade  = exp(-waterThick * _CausticsThicknessAttenuation);
    float depthFade  = exp(-submerged   * _CausticsDepthAttenuation);
    half3 add        = caus * (L.color * _CausticsStrength * thickFade * depthFade * lightVis);

    return half4(sceneCol + add, 1.0);
}

#endif // SSF_CAUSTICS_INCLUDED
