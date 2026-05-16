#ifndef SSF_CAUSTICS_INCLUDED
#define SSF_CAUSTICS_INCLUDED

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
float    _CausticsThicknessAttenuation;
float    _CausticsDepthAttenuation;
float4x4 _WaterSSFLightVP;
float    _WaterSSFLightShadowEnabled;
float    _WaterSSFLightShadowBias;
float    _ThicknessCutoff;

half4 fragSSFCaustics(Varyings IN) : SV_Target
{
    float2 uv = IN.texcoord;

    half3 sceneCol = SAMPLE_TEXTURE2D(_WaterSSFSceneCopy, sampler_WaterSSFSceneCopy, uv).rgb;

    if (_CausticsStrength <= 1e-4) return half4(sceneCol, 1);

    float rawDepth   = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, uv).r;
    float sceneEye   = LinearEyeDepth(rawDepth, _ZBufferParams);
    float waterEye   = SAMPLE_TEXTURE2D(_WaterSSFDepthSmooth, sampler_PointClamp, uv).r;
    float waterThick = SAMPLE_TEXTURE2D(_WaterSSFThickness, sampler_WaterSSFThickness, uv).r;

    if (waterEye < 1e-4 || waterThick < _ThicknessCutoff || sceneEye <= waterEye)
        return half4(sceneCol, 1);

    float3 sceneVS = SSFViewPosFromEyeDepth(uv, sceneEye);
    float3 sceneWS = mul(UNITY_MATRIX_I_V, float4(sceneVS, 1.0)).xyz;

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

    Light L       = GetMainLight();
    float3 Ldir   = normalize(-L.direction);
    float3 origin = sceneWS;
    float  t      = (origin.y - _CausticsPlaneY) / max(abs(Ldir.y), 1e-3) * sign(-Ldir.y);
    float3 hit    = origin + Ldir * t;

    float2 tileUV = hit.xz * max(_CausticsTiling, 1e-3) + _Time.y * _CausticsScrollSpeed;
    half3  caus   = SAMPLE_TEXTURE2D(_CausticsTex, sampler_CausticsTex, tileUV).rgb;

    float submerged  = max(sceneEye - waterEye, 0.0);
    float thickFade  = exp(-waterThick * _CausticsThicknessAttenuation);
    float depthFade  = exp(-submerged   * _CausticsDepthAttenuation);
    half3 add        = caus * (L.color * _CausticsStrength * thickFade * depthFade * lightVis);

    return half4(sceneCol + add, 1.0);
}

#endif
