Shader "Custom/WaterPhase"
{
    Properties
    {
        [Header(Phase Control)]
        _DensityPhaseThreshold ("Density Threshold (vapour->liquid)", Range(0.0, 1.0)) = 0.55
        _PhaseTransitionWidth  ("Phase Transition Width", Range(0.01, 0.5)) = 0.15

        [Header(Shared Density Field)]
        _NoiseScale         ("Noise Scale", Range(0.1, 20.0)) = 2.0
        _NoiseDriftDir      ("Drift Direction", Vector) = (0, 1, 0, 0)
        _NoiseDriftSpeed    ("Drift Speed", Range(0.0, 5.0)) = 0.3
        _NoiseOctaves       ("Noise Octaves", Range(1, 8)) = 5
        _DensityPower       ("Density Sharpness", Range(0.1, 5.0)) = 1.5
        _EdgeSoftness       ("Edge Softness", Range(0.0, 0.5)) = 0.2

        [Header(Blue Noise Raymarching)]
        _BlueNoiseTex       ("Blue Noise Texture", 2D) = "gray" {}
        _BlueNoiseScale     ("Blue Noise Tiling", Range(0.25, 8.0)) = 1.0
        _BlueNoiseStrength  ("Blue Noise Jitter Strength", Range(0.0, 1.0)) = 1.0
        _BlueNoiseTimeSpeed ("Blue Noise Temporal Speed", Range(0.0, 4.0)) = 1.0

        [Header(Vapour Rendering)]
        _VapourBaseColor        ("Vapour Base Color", Color) = (1.0, 1.0, 1.0, 1)
        _VapourWarmColor        ("Vapour Warm Tint", Color) = (1.0, 0.92, 0.80, 1)
        _VapourCoolColor        ("Vapour Cool Tint", Color) = (0.80, 0.90, 1.0, 1)
        _TemperatureBlend       ("Temperature Blend", Range(0.0, 1.0)) = 0.6
        _VapourShadowColor      ("Vapour Shadow Tint", Color) = (0.04, 0.07, 0.18, 1)
        _VapourAmbientColor     ("Vapour Ambient Color", Color) = (0.05, 0.08, 0.15, 1)
        _VapourAmbientStrength  ("Vapour Ambient Strength", Range(0.0, 1.0)) = 0.35
        _VapourAmbientOcclusionProxy ("Vapour Ambient Occlusion Proxy", Range(0.0, 1.0)) = 0.6
        _VapourEmissionColor    ("Vapour Emission Color", Color) = (1.0, 0.95, 0.85, 1)
        _VapourEmissionStrength ("Vapour Emission Strength", Range(0.0, 3.0)) = 0.5
        _VapourAbsorption       ("Vapour Absorption", Range(0.1, 20.0)) = 8.0
        _VapourScatterG         ("Vapour Scatter Anisotropy", Range(0.0, 0.95)) = 0.5
        _VapourBackscatter      ("Vapour Backscatter Strength", Range(0.0, 2.0)) = 0.4
        _VapourFresnelPower     ("Vapour Fresnel Power", Range(1.0, 10.0)) = 3.0
        _VapourFresnelStrength  ("Vapour Fresnel Strength", Range(0.0, 2.0)) = 0.6

        [Header(Liquid Rendering)]
        _LiquidTint               ("Liquid Tint", Color) = (0.10, 0.40, 0.60, 1)
        _LiquidShallowColor       ("Liquid Shallow Color", Color) = (0.30, 0.80, 0.80, 1)
        _LiquidDeepColor          ("Liquid Deep Color", Color) = (0.02, 0.05, 0.15, 1)
        _LiquidAbsorptionRate     ("Liquid Absorption Rate", Range(0.1, 5.0)) = 1.2
        _LiquidBodyLightStrength  ("Liquid Body Light Strength", Range(0.0, 3.0)) = 0.35
        _LiquidOpacityCoeff       ("Liquid Opacity Coeff", Range(0.1, 30.0)) = 8.0
        _LiquidSmoothness         ("Liquid Smoothness", Range(0.0, 1.0)) = 0.9
        _LiquidSpecularStrength   ("Liquid Specular Strength", Range(0.0, 5.0)) = 1.2
        _LiquidReflectionStrength ("Liquid Reflection Strength", Range(0.0, 1.0)) = 0.45
        _LiquidRefractionStrength ("Liquid Refraction Strength", Range(0.0, 0.3)) = 0.05
        _LiquidFresnelPower       ("Liquid Fresnel Power", Range(1.0, 10.0)) = 5.0

        [Header(Subsurface Scattering)]
        _SSSColor             ("SSS Color", Color) = (0.15, 0.65, 0.55, 1)
        _SSSStrength          ("SSS Strength", Range(0.0, 3.0)) = 0.8
        _SSSPower             ("SSS Power", Range(1.0, 16.0)) = 4.0
        _SSSDistortion        ("SSS Normal Distortion", Range(0.0, 1.0)) = 0.3
        _SSSAmbient           ("SSS Ambient", Range(0.0, 0.5)) = 0.1
        _SSSThicknessScale    ("SSS Thickness Scale", Range(0.1, 5.0)) = 1.0

        [Header(Surface Texture)]
        _CausticsTex          ("Surface Texture (Triplanar)", 2D) = "black" {}
        _CausticsScale        ("Surface Texture Scale", Range(0.1, 10.0)) = 1.5
        _CausticsSpeed        ("Surface Texture Scroll Speed", Range(0.0, 2.0)) = 0.3
        _CausticsStrength     ("Surface Texture Strength", Range(0.0, 5.0)) = 1.0
        _CausticsDepthFade    ("Triplanar Blend Sharpness", Range(0.1, 10.0)) = 2.0
        _CausticsSplit        ("Surface Refraction Distortion", Range(0.0, 0.2)) = 0.02

        [Header(Raymarch)]
        _MarchSteps          ("March Steps", Range(8, 96)) = 40

        [Header(Physics Bridge)]
        _Density             ("Density (physics-set)", Range(0.0, 1.0)) = 1.0
        _PhysicsBlend        ("Physics Blend", Range(0.0, 1.0)) = 0.0

        [Header(Voxel Bounds Object Space)]
        _VoxelBoundsMin      ("Bounds Min", Vector) = (-0.5, -0.5, -0.5, 0)
        _VoxelBoundsMax      ("Bounds Max", Vector) = ( 0.5,  0.5,  0.5, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+100" "RenderPipeline" = "UniversalPipeline" }

        ZWrite Off
        Cull Front
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "../Includes/WaterPhaseHelpers.hlsl"

            struct MeshInput
            {
                float4 positionOS : POSITION;
            };

            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
            };

            TEXTURE2D_X_FLOAT(_CameraDepthTexture);
            SAMPLER(sampler_CameraDepthTexture);
            TEXTURE2D(_BlueNoiseTex);
            SAMPLER(sampler_BlueNoiseTex);
            float4 _BlueNoiseTex_TexelSize;
            TEXTURE2D(_CausticsTex);
            SAMPLER(sampler_CausticsTex);

            CBUFFER_START(UnityPerMaterial)
                float   _DensityPhaseThreshold;
                float   _PhaseTransitionWidth;

                float   _NoiseScale;
                float4  _NoiseDriftDir;
                float   _NoiseDriftSpeed;
                int     _NoiseOctaves;
                float   _DensityPower;
                float   _EdgeSoftness;
                float   _BlueNoiseScale;
                float   _BlueNoiseStrength;
                float   _BlueNoiseTimeSpeed;

                half4   _VapourBaseColor;
                half4   _VapourWarmColor;
                half4   _VapourCoolColor;
                float   _TemperatureBlend;
                half4   _VapourShadowColor;
                half4   _VapourAmbientColor;
                float   _VapourAmbientStrength;
                float   _VapourAmbientOcclusionProxy;
                half4   _VapourEmissionColor;
                float   _VapourEmissionStrength;
                float   _VapourAbsorption;
                float   _VapourScatterG;
                float   _VapourBackscatter;
                float   _VapourFresnelPower;
                float   _VapourFresnelStrength;

                half4   _LiquidTint;
                half4   _LiquidShallowColor;
                half4   _LiquidDeepColor;
                float   _LiquidAbsorptionRate;
                float   _LiquidBodyLightStrength;
                float   _LiquidOpacityCoeff;
                float   _LiquidSmoothness;
                float   _LiquidSpecularStrength;
                float   _LiquidReflectionStrength;
                float   _LiquidRefractionStrength;
                float   _LiquidFresnelPower;

                half4   _SSSColor;
                float   _SSSStrength;
                float   _SSSPower;
                float   _SSSDistortion;
                float   _SSSAmbient;
                float   _SSSThicknessScale;

                float   _CausticsScale;
                float   _CausticsSpeed;
                float   _CausticsStrength;
                float   _CausticsDepthFade;
                float   _CausticsSplit;

                int     _MarchSteps;
                float   _Density;
                float   _PhysicsBlend;
                float4  _VoxelBoundsMin;
                float4  _VoxelBoundsMax;
            CBUFFER_END

            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.viewDirWS = GetWorldSpaceViewDir(OUT.positionWS);
                OUT.screenPos = ComputeScreenPos(OUT.positionHCS);
                return OUT;
            }

            half4 frag(Interpolators IN) : SV_Target
            {
                float3 cameraWS = _WorldSpaceCameraPos.xyz;
                float3 boundsMinOS = (float3)_VoxelBoundsMin.xyz;
                float3 boundsMaxOS = (float3)_VoxelBoundsMax.xyz;

                float3 entryWS = 0.0;
                float3 rayDir = 0.0;
                float marchDistance = 0.0;

                if (!ComputeVoxelRaySegmentWS(
                cameraWS,
                IN.positionWS,
                boundsMinOS,
                boundsMaxOS,
                entryWS,
                rayDir,
                marchDistance
                ))
                {
                    return half4(0, 0, 0, 0);
                }

                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;
                float rawDepth = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, screenUV).r;
                float sceneLinearDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                
                Light mainLight = GetMainLight();
                float3 lightDir = normalize(mainLight.direction);
                half3 lightColor = mainLight.color;
                
                int marchSteps = _MarchSteps;
                int noiseOctaves = _NoiseOctaves;
                float3 driftDir = normalize((float3)_NoiseDriftDir.xyz);
                
                WaterPhaseMarchResult phaseResult;
                bool isLiquidMode = _DensityPhaseThreshold < 0.1;
                
                if (isLiquidMode)
                {
                    phaseResult = RaymarchWaterPhaseLiquid(
                    entryWS, rayDir, lightDir, lightColor,
                    marchSteps, marchDistance,
                    _VapourScatterG, _VapourAbsorption,
                    _LiquidOpacityCoeff,
                    _DensityPhaseThreshold, _PhaseTransitionWidth,
                    _Time.y, driftDir, _NoiseDriftSpeed,
                    _NoiseScale, noiseOctaves, _DensityPower,
                    _Density, _PhysicsBlend,
                    sceneLinearDepth, boundsMinOS, boundsMaxOS,
                    _EdgeSoftness, screenUV
                    );
                }
                else
                {
                    // Blue Noise Texture
                    float2 blueNoiseUV = frac(screenUV * _ScreenParams.xy * _BlueNoiseTex_TexelSize.xy * _BlueNoiseScale);
                    float2 blueNoiseTimeOffset = float2(0.75487766, 0.56984029) * frac(_Time.y * _BlueNoiseTimeSpeed);
                    float2 blueNoiseSampleUV = frac(blueNoiseUV + blueNoiseTimeOffset);
                    float2 blueNoiseRG = SAMPLE_TEXTURE2D(_BlueNoiseTex, sampler_BlueNoiseTex, blueNoiseSampleUV).rg;

                    phaseResult = RaymarchWaterPhase(
                    entryWS, rayDir, lightDir, lightColor,
                    marchSteps, marchDistance,
                    _VapourScatterG, _VapourAbsorption,
                    _LiquidOpacityCoeff,
                    _DensityPhaseThreshold, _PhaseTransitionWidth,
                    _Time.y, driftDir, _NoiseDriftSpeed,
                    _NoiseScale, noiseOctaves, _DensityPower,
                    _Density, _PhysicsBlend,
                    sceneLinearDepth, boundsMinOS, boundsMaxOS,
                    _EdgeSoftness, screenUV,
                    blueNoiseRG, _BlueNoiseStrength
                    );
                }

                half3 tempTint = lerp((half3)_VapourCoolColor.rgb, (half3)_VapourWarmColor.rgb, _TemperatureBlend);
                half3 vapourCol = 0.0;
                float vapourAlpha = 0.0;

                float3 viewDirWS = normalize(IN.viewDirWS);

                if (!isLiquidMode)
                {
                    half3 vapourColMain = phaseResult.vapourScatter * _VapourBaseColor.rgb * tempTint;
                    half3 vapourColAdd = phaseResult.vapourScatterAdditional * _VapourBaseColor.rgb * tempTint;

                    float vapourLitness = phaseResult.vapourLitness;
                    vapourColMain = lerp((half3)_VapourShadowColor.rgb * phaseResult.vapourAlpha, vapourColMain, vapourLitness);
                    vapourCol = vapourColMain;

                    float vapourTransmittance = saturate(1.0 - phaseResult.vapourAlpha);
                    float ambientOcclusionProxy = lerp(1.0, vapourTransmittance, _VapourAmbientOcclusionProxy);
                    vapourCol += _VapourAmbientColor.rgb * _VapourAmbientStrength * (1.0 - vapourLitness) * phaseResult.vapourAlpha * ambientOcclusionProxy;

                    float cosTheta = dot(-rayDir, lightDir);
                    float backPhase = HenyeyGreenstein(cosTheta, _VapourScatterG);
                    float hgNorm = HenyeyGreenstein(0.0, _VapourScatterG);
                    float backGlow = saturate(backPhase / (hgNorm * 8.0));

                    vapourCol += backGlow * _VapourBackscatter * lightColor * phaseResult.vapourAlpha * vapourLitness;
                    vapourCol += (half3)_VapourEmissionColor.rgb * _VapourEmissionStrength * vapourLitness * phaseResult.vapourAlpha;

                    float vapourFresnel = FresnelEdge(viewDirWS, -rayDir, _VapourFresnelPower);
                    vapourCol += vapourFresnel * _VapourFresnelStrength * lightColor * phaseResult.vapourAlpha * vapourLitness;

                    vapourAlpha = saturate(phaseResult.vapourAlpha * (1.0 + vapourFresnel * _VapourFresnelStrength * 0.35));

                    // Add flashlight/point-light scattering after the main-light shadow tinting.
                    vapourCol += vapourColAdd;
                }

                float liquidAlpha = saturate(phaseResult.liquidAlpha);
                half3 liquidCol = 0.0;

                if (liquidAlpha > 0.001)
                {


                    float3 surfaceWS = entryWS;
                    float3 surfaceNormalWS = -rayDir;
                    if (phaseResult.liquidSurfaceFound > 0.5)
                    {
                        surfaceWS = phaseResult.liquidSurfaceWS;
                        surfaceNormalWS = normalize(phaseResult.liquidSurfaceNormalWS);
                    }

                    // Use a shape-based normal for more believable droplet/surface wrapping,
                    // but keep the original view-ray normal when not in liquid mode.
                    float3 liquidNormal = -rayDir;
                    if (isLiquidMode)
                    liquidNormal = surfaceNormalWS;

                    float localSmoothness = _LiquidSmoothness;
                    float localSpecularStrength = _LiquidSpecularStrength;
                    float localReflectionStrength = _LiquidReflectionStrength;
                    float2 surfaceRefractDistort = 0.0;

                    // ── Surface texture detail (liquid only, triplanar wrap) ──
                    if (isLiquidMode && liquidAlpha > 0.01 && _CausticsStrength > 0.001)
                    {
                        half3 surfaceTex = SampleSurfaceTextureTriplanar(
                        TEXTURE2D_ARGS(_CausticsTex, sampler_CausticsTex),
                        surfaceWS, surfaceNormalWS,
                        _Time.y, _CausticsScale, _CausticsSpeed, _CausticsDepthFade
                        );

                        float surfaceLuma = saturate(dot(surfaceTex, half3(0.299, 0.587, 0.114)));
                        float surfaceCentered = (surfaceLuma - 0.5) * 2.0;

                        // Black textures stay effectively “off”; mid-gray stays neutral.
                        float surfaceEffect = clamp(surfaceCentered * surfaceLuma * _CausticsStrength, -1.0, 1.0) * liquidAlpha;

                        localSmoothness = saturate(localSmoothness + surfaceEffect * 0.15);
                        localSpecularStrength = max(0.0, localSpecularStrength * (1.0 + surfaceEffect * 0.6));
                        localReflectionStrength = saturate(localReflectionStrength * (1.0 + surfaceEffect * 0.2));

                        surfaceRefractDistort = (surfaceTex.rg * 2.0 - 1.0) * surfaceLuma * _CausticsSplit * _CausticsStrength * liquidAlpha;
                    }

                    float liquidFresnel = FresnelEdge(viewDirWS, liquidNormal, _LiquidFresnelPower);

                    float3 halfVec = normalize(lightDir + viewDirWS);
                    float ndh = saturate(dot(liquidNormal, halfVec));
                    float specPower = exp2(localSmoothness * 10.0 + 1.0);
                    float liquidSpec = pow(ndh, specPower) * localSpecularStrength;

                    float3 reflectDir = reflect(-viewDirWS, liquidNormal);
                    half perceptualRoughness = 1.0 - localSmoothness;
                    half3 liquidReflection = GlossyEnvironmentReflection(
                    reflectDir,
                    IN.positionWS,
                    perceptualRoughness,
                    1.0,
                    screenUV
                    ) * localReflectionStrength;

                    float2 refractOffset = liquidNormal.xy * _LiquidRefractionStrength * liquidAlpha + surfaceRefractDistort;
                    half3 refractedScene = SampleSceneColor(screenUV + refractOffset);
                    half3 liquidDepthCol = CalculateLiquidDepthColor(
                    refractedScene,
                    _LiquidShallowColor.rgb,
                    _LiquidDeepColor.rgb,
                    phaseResult.liquidDepth,
                    _LiquidAbsorptionRate
                    );

                    liquidCol = liquidDepthCol * _LiquidTint.rgb;

                    // ── Liquid body lighting (single-scatter approximation) ──
                    // Makes point/spot lights brighten the water body, not only specular.
                    // Uses Beer-Lambert transmittance to increase scatter with depth.
                    float liquidTransmittance = exp(-phaseResult.liquidDepth * _LiquidAbsorptionRate);
                    float liquidScatter = saturate(1.0 - liquidTransmittance);
                    float bodyMask = liquidScatter * liquidAlpha * (1.0 - liquidFresnel);
                    float ndlMain = saturate(dot(liquidNormal, lightDir));
                    liquidCol += (half3)_SSSColor.rgb * lightColor * ndlMain * bodyMask * _LiquidBodyLightStrength;

                    liquidCol += liquidSpec * lightColor;

                    #if defined(_ADDITIONAL_LIGHTS)
                        // Additional point/spot lights (including spot "flashlight") for liquid specular.
                        // Uses LIGHT_LOOP_* so it works in both Forward and Forward+ (clustered).
                        {
                            InputData inputData = (InputData)0;
                            inputData.normalizedScreenSpaceUV = screenUV;
                            inputData.positionWS = IN.positionWS;

                            uint additionalLightsCount = (uint)GetAdditionalLightsCount();
                            LIGHT_LOOP_BEGIN(additionalLightsCount)
                            Light additionalLight = GetAdditionalLight(lightIndex, IN.positionWS);
                            half3 radiance = additionalLight.color * (additionalLight.distanceAttenuation * additionalLight.shadowAttenuation);

                            float3 addDir = normalize((float3)additionalLight.direction);
                            float3 addHalfVec = normalize(addDir + viewDirWS);
                            float addNdh = saturate(dot(liquidNormal, addHalfVec));
                            float addSpec = pow(addNdh, specPower) * localSpecularStrength;
                            liquidCol += addSpec * radiance;

                            float addNdl = saturate(dot(liquidNormal, addDir));
                            liquidCol += (half3)_SSSColor.rgb * radiance * addNdl * bodyMask * _LiquidBodyLightStrength;
                            LIGHT_LOOP_END
                        }
                    #endif
                    liquidCol += liquidReflection * liquidFresnel;

                    // ── Subsurface scattering (liquid only) ──
                    if (isLiquidMode && phaseResult.liquidAlpha > 0.01)
                    {
                        float sssThickness = saturate(phaseResult.liquidDepth * _SSSThicknessScale);
                        half3 sss = ComputeSSS(
                        viewDirWS, lightDir, liquidNormal,
                        lightColor, _SSSColor.rgb,
                        _SSSStrength, _SSSPower, _SSSDistortion,
                        _SSSAmbient, sssThickness
                        );
                        liquidCol += sss * phaseResult.liquidAlpha;
                    }
                }
                // (Old caustics block removed: replaced by triplanar surface texture detail above.)

                half3 finalCol = vapourCol + liquidCol;
                float finalAlpha = saturate(1.0 - (1.0 - vapourAlpha) * (1.0 - liquidAlpha));

                return half4(finalCol, finalAlpha);
            }

            ENDHLSL
        }
    }
}
