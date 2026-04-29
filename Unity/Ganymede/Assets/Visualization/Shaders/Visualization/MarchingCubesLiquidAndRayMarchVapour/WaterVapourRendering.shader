Shader "Custom/VapourVolume"
{
    Properties
    {
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
        _VapourBaseColor        ("Vapour Base Color (lit cells)", Color) = (1.0, 1.0, 1.0, 1)
        _VapourShadowColor      ("Vapour Shadow / Ambient Fill (unlit cells)", Color) = (0.18, 0.22, 0.32, 1)
        _VapourShadowStrength   ("Shadow Fill Strength", Range(0.0, 2.0)) = 1.0
        _VapourAbsorption       ("Vapour Absorption (density -> opacity)", Range(0.1, 20.0)) = 8.0
        _VapourScatterG         ("Vapour Scatter Anisotropy", Range(0.0, 0.95)) = 0.5
        _VapourBackscatter      ("Vapour Backscatter (silver lining)", Range(0.0, 2.0)) = 0.4

        [Header(Raymarch)]
        _MarchSteps          ("March Steps", Range(8, 96)) = 40

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
            #include "WaterPhase/WaterPhaseHelpers.hlsl"

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

            CBUFFER_START(UnityPerMaterial)
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
                half4   _VapourShadowColor;
                float   _VapourShadowStrength;
                float   _VapourAbsorption;
                float   _VapourScatterG;
                float   _VapourBackscatter;

                int     _MarchSteps;
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

                float2 blueNoiseUV = frac(screenUV * _ScreenParams.xy * _BlueNoiseTex_TexelSize.xy * _BlueNoiseScale);
                float2 blueNoiseTimeOff = float2(0.75487766, 0.56984029) * frac(_Time.y * _BlueNoiseTimeSpeed);
                float2 blueNoiseSampleUV = frac(blueNoiseUV + blueNoiseTimeOff);
                float2 blueNoiseRG = SAMPLE_TEXTURE2D(_BlueNoiseTex, sampler_BlueNoiseTex, blueNoiseSampleUV).rg;

                // Vapour density per march sample = physics mask (low-freq, voxel grid)
                // × world-space domain-warped FBM (high-freq, true wispy detail).
                // This is the Option-B pipeline — the baked compute enhance pass is gone.
                WaterPhaseMarchResult phaseResult = RaymarchWaterPhase(
                entryWS, rayDir, lightDir, lightColor,
                marchSteps, marchDistance,
                _VapourScatterG, _VapourAbsorption,
                1.0,
                // Vapour-only renderer: force all density into the vapour branch by
                // setting the liquid threshold above the saturated density range.
                2.0, 0.01,
                _Time.y,
                driftDir, _NoiseDriftSpeed,
                _NoiseScale, noiseOctaves, _DensityPower,
                sceneLinearDepth, boundsMinOS, boundsMaxOS,
                _EdgeSoftness, screenUV,
                blueNoiseRG, _BlueNoiseStrength
                );

                float3 viewDirWS = normalize(IN.viewDirWS);

                float vapourAlpha01 = phaseResult.vapourAlpha;
                float vapourLitness = phaseResult.vapourLitness;

                // Direct sun in-scatter — already per-step shadow attenuated inside the
                // marcher. Spatial variation in this term IS the visible god-ray pattern.
                // Anything additive that doesn't track shadowing will flatten the contrast,
                // so the only other terms here are (a) extra dynamic lights, (b) a single
                // shadow/ambient fill scaled by the unlit fraction, and (c) silver lining.
                half3 directLight = phaseResult.vapourScatter * (half3)_VapourBaseColor.rgb
                                  + phaseResult.vapourScatterAdditional * (half3)_VapourBaseColor.rgb;

                // Shadow / sky fill — only adds color where the ray was unlit, so lit
                // pixels stay bright and shadow pixels read as the chosen tint instead
                // of pure black. This preserves the bright-vs-dark ratio that makes the
                // god rays visible.
                half3 shadowFill = (half3)_VapourShadowColor.rgb * _VapourShadowStrength
                                 * vapourAlpha01 * (1.0 - vapourLitness);

                // Henyey-Greenstein silver lining when sun is roughly behind the volume.
                // Gated by litness so it only appears where light actually reached the camera.
                float cosTheta = dot(-rayDir, lightDir);
                float backPhase = HenyeyGreenstein(cosTheta, _VapourScatterG);
                float hgNorm   = max(HenyeyGreenstein(0.0, _VapourScatterG), 1e-4);
                float backGlow = saturate(backPhase / (hgNorm * 8.0));
                half3 backscatter = backGlow * _VapourBackscatter * lightColor
                                  * vapourAlpha01 * vapourLitness;

                half3 vapourCol = directLight + shadowFill + backscatter;

                return half4(vapourCol, vapourAlpha01);
            }

            ENDHLSL
        }
    }
}
