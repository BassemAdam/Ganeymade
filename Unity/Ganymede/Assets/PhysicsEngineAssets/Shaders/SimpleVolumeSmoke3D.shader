Shader "Custom/SimpleVolumeSmoke3D"
{
    Properties
    {
        [Header(Volume Texture)]
        [NoScaleOffset]_VolumeTex ("Volume Texture", 3D) = "white" {}
        [NoScaleOffset]_DetailVolumeTex ("Detail Volume Texture", 3D) = "white" {}
        [NoScaleOffset]_ShapeVolumeTex ("Secondary Shape Volume", 3D) = "white" {}
        [NoScaleOffset]_FineDetailVolumeTex ("Fine Detail Volume", 3D) = "white" {}

        [Header(Detail Breakup)]
        _DetailStrength ("Detail Strength", Range(0.0, 1.0)) = 0.35
        _DetailTiling ("Detail Tiling", Range(0.5, 8.0)) = 2.0
        _DetailAnimationSpeed ("Detail Animation Speed", Range(0.0, 4.0)) = 0.8
        _DetailDirection ("Detail Direction", Vector) = (0.35, 1, 0.2, 0)

        [Header(Secondary Shape)]
        _ShapeBlendStrength ("Shape Blend Strength", Range(0.0, 1.0)) = 0.45
        _ShapeTiling ("Shape Tiling", Range(0.5, 6.0)) = 1.35
        _ShapeAnimationSpeed ("Shape Animation Speed", Range(0.0, 4.0)) = 0.3
        _ShapeDirection ("Shape Direction", Vector) = (0.15, 1, -0.08, 0)

        [Header(Fine Detail)]
        _FineDetailStrength ("Fine Detail Strength", Range(0.0, 1.0)) = 0.22
        _FineDetailTiling ("Fine Detail Tiling", Range(1.0, 12.0)) = 4.0
        _FineDetailAnimationSpeed ("Fine Detail Animation Speed", Range(0.0, 4.0)) = 1.15
        _FineDetailDirection ("Fine Detail Direction", Vector) = (-0.2, 1, 0.35, 0)

        [Header(Appearance)]
        [MainColor]_SmokeColor ("Smoke Color", Color) = (0.85, 0.9, 1.0, 1.0)
        _ShadowColor ("Shadow Color", Color) = (0.2, 0.24, 0.3, 1.0)
        _Density ("Density", Range(0.0, 8.0)) = 2.0
        _Opacity ("Opacity", Range(0.0, 2.0)) = 1.0
        _Brightness ("Brightness", Range(0.0, 4.0)) = 1.0
        _Contrast ("Contrast", Range(0.1, 4.0)) = 1.0
        _Cutoff ("Cutoff", Range(0.0, 1.0)) = 0.05

        [Header(Shape)]
        _EdgeFade ("Edge Fade", Range(0.001, 0.5)) = 0.08

        [Header(Animation)]
        _AutoAnimate ("Auto Animate", Float) = 1
        _AnimationSpeed ("Animation Speed", Range(0.0, 4.0)) = 0.5
        _ManualOffset ("Manual Offset", Range(0.0, 1.0)) = 0.0
        _InvertSequence ("Invert Sequence", Float) = 0
        _AnimationDirection ("Animation Direction", Vector) = (0, 1, 0, 0)

        [Header(Evolution)]
        _EvolutionSpeed ("Evolution Speed", Range(0.0, 4.0)) = 0.9
        _EvolutionStrength ("Evolution Strength", Range(0.0, 1.0)) = 0.65

        [Header(Raymarch)]
        _Steps ("Steps", Range(8, 128)) = 48
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Front
        ZTest LEqual

        Pass
        {
            Name "VolumeSmoke"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE3D(_VolumeTex);
            SAMPLER(sampler_VolumeTex);
            TEXTURE3D(_DetailVolumeTex);
            SAMPLER(sampler_DetailVolumeTex);
            TEXTURE3D(_ShapeVolumeTex);
            SAMPLER(sampler_ShapeVolumeTex);
            TEXTURE3D(_FineDetailVolumeTex);
            SAMPLER(sampler_FineDetailVolumeTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _SmokeColor;
                float4 _ShadowColor;
                float _DetailStrength;
                float _DetailTiling;
                float _DetailAnimationSpeed;
                float4 _DetailDirection;
                float _ShapeBlendStrength;
                float _ShapeTiling;
                float _ShapeAnimationSpeed;
                float4 _ShapeDirection;
                float _FineDetailStrength;
                float _FineDetailTiling;
                float _FineDetailAnimationSpeed;
                float4 _FineDetailDirection;
                float _Density;
                float _Opacity;
                float _Brightness;
                float _Contrast;
                float _Cutoff;
                float _EdgeFade;
                float _AutoAnimate;
                float _AnimationSpeed;
                float _ManualOffset;
                float _InvertSequence;
                float4 _AnimationDirection;
                float _EvolutionSpeed;
                float _EvolutionStrength;
                int _Steps;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionOS  : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionOS = IN.positionOS.xyz;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            bool RayBoxIntersection(float3 rayOriginOS, float3 rayDirOS, out float tMin, out float tMax)
            {
                float3 boxMin = float3(-0.5, -0.5, -0.5);
                float3 boxMax = float3( 0.5,  0.5,  0.5);

                float3 invDir = 1.0 / max(abs(rayDirOS), 1e-6) * sign(rayDirOS);
                float3 t0 = (boxMin - rayOriginOS) * invDir;
                float3 t1 = (boxMax - rayOriginOS) * invDir;
                float3 tSmall = min(t0, t1);
                float3 tBig = max(t0, t1);

                tMin = max(max(tSmall.x, tSmall.y), tSmall.z);
                tMax = min(min(tBig.x, tBig.y), tBig.z);

                return tMax > max(tMin, 0.0);
            }

            float EdgeFade3D(float3 uvw, float edgeFade)
            {
                float3 lowFade = smoothstep(0.0, edgeFade, uvw);
                float3 highFade = smoothstep(0.0, edgeFade, 1.0 - uvw);
                float3 fade = lowFade * highFade;
                return fade.x * fade.y * fade.z;
            }

            float SampleAnimatedSlicesMain(float3 uvw, float phase)
            {
                float xy = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, frac(float3(uvw.xy, phase + uvw.z))).r;
                float xz = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, frac(float3(uvw.xz, phase * 0.73 + uvw.y))).r;
                float yz = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, frac(float3(uvw.yz, phase * 1.21 + uvw.x))).r;
                return (xy + xz + yz) * (1.0 / 3.0);
            }

            float SampleAnimatedSlicesDetail(float3 uvw, float phase)
            {
                float xy = SAMPLE_TEXTURE3D(_DetailVolumeTex, sampler_DetailVolumeTex, frac(float3(uvw.xy, phase + uvw.z))).r;
                float xz = SAMPLE_TEXTURE3D(_DetailVolumeTex, sampler_DetailVolumeTex, frac(float3(uvw.xz, phase * 0.81 + uvw.y))).r;
                float yz = SAMPLE_TEXTURE3D(_DetailVolumeTex, sampler_DetailVolumeTex, frac(float3(uvw.yz, phase * 1.13 + uvw.x))).r;
                return (xy + xz + yz) * (1.0 / 3.0);
            }

            float SampleAnimatedSlicesShape(float3 uvw, float phase)
            {
                float xy = SAMPLE_TEXTURE3D(_ShapeVolumeTex, sampler_ShapeVolumeTex, frac(float3(uvw.xy, phase + uvw.z))).r;
                float xz = SAMPLE_TEXTURE3D(_ShapeVolumeTex, sampler_ShapeVolumeTex, frac(float3(uvw.xz, phase * 0.67 + uvw.y))).r;
                float yz = SAMPLE_TEXTURE3D(_ShapeVolumeTex, sampler_ShapeVolumeTex, frac(float3(uvw.yz, phase * 1.09 + uvw.x))).r;
                return (xy + xz + yz) * (1.0 / 3.0);
            }

            float SampleAnimatedSlicesFine(float3 uvw, float phase)
            {
                float xy = SAMPLE_TEXTURE3D(_FineDetailVolumeTex, sampler_FineDetailVolumeTex, frac(float3(uvw.xy, phase + uvw.z))).r;
                float xz = SAMPLE_TEXTURE3D(_FineDetailVolumeTex, sampler_FineDetailVolumeTex, frac(float3(uvw.xz, phase * 0.91 + uvw.y))).r;
                float yz = SAMPLE_TEXTURE3D(_FineDetailVolumeTex, sampler_FineDetailVolumeTex, frac(float3(uvw.yz, phase * 1.31 + uvw.x))).r;
                return (xy + xz + yz) * (1.0 / 3.0);
            }

            float SmoothLoopBlend(float phase)
            {
                return 0.5 - 0.5 * cos(phase * 6.28318530718);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 cameraOS = TransformWorldToObject(GetCameraPositionWS());
                float3 rayDirOS = normalize(IN.positionOS - cameraOS);
                float sequenceOffset = (_AutoAnimate > 0.5) ? frac(_Time.y * _AnimationSpeed) : frac(_ManualOffset);
                float evolutionPhase = frac(_Time.y * _EvolutionSpeed);
                float evolutionPhaseB = frac(evolutionPhase + 0.5);
                float evolutionBlend = SmoothLoopBlend(evolutionPhase);
                float3 animationDirection = _AnimationDirection.xyz;
                float detailSequenceOffset = frac(_Time.y * _DetailAnimationSpeed);
                float3 detailDirection = _DetailDirection.xyz;
                float shapeSequenceOffset = frac(_Time.y * _ShapeAnimationSpeed);
                float3 shapeDirection = _ShapeDirection.xyz;
                float fineDetailSequenceOffset = frac(_Time.y * _FineDetailAnimationSpeed);
                float3 fineDetailDirection = _FineDetailDirection.xyz;
                float animationDirLength = length(animationDirection);
                float detailDirLength = length(detailDirection);
                float shapeDirLength = length(shapeDirection);
                float fineDetailDirLength = length(fineDetailDirection);
                animationDirection = (animationDirLength > 1e-5) ? (animationDirection / animationDirLength) : float3(0.0, 1.0, 0.0);
                detailDirection = (detailDirLength > 1e-5) ? (detailDirection / detailDirLength) : float3(0.35, 1.0, 0.2);
                shapeDirection = (shapeDirLength > 1e-5) ? (shapeDirection / shapeDirLength) : float3(0.15, 1.0, -0.08);
                fineDetailDirection = (fineDetailDirLength > 1e-5) ? (fineDetailDirection / fineDetailDirLength) : float3(-0.2, 1.0, 0.35);
                if (_InvertSequence > 0.5)
                {
                    sequenceOffset = 1.0 - sequenceOffset;
                }

                float tEnter = 0.0;
                float tExit = 0.0;
                if (!RayBoxIntersection(cameraOS, rayDirOS, tEnter, tExit))
                {
                    return half4(0, 0, 0, 0);
                }

                float startT = max(tEnter, 0.0);
                float marchLength = tExit - startT;
                if (marchLength <= 0.0)
                {
                    return half4(0, 0, 0, 0);
                }

                int steps = clamp(_Steps, 8, 128);
                float stepSize = marchLength / steps;
                float3 stepOS = rayDirOS * stepSize;
                float3 samplePosOS = cameraOS + rayDirOS * (startT + stepSize * 0.5);
                float3 rayDirWS = normalize(IN.positionWS - GetCameraPositionWS());

                Light mainLight = GetMainLight();
                float lightAmount = saturate(0.35 + 0.65 * dot(-rayDirWS, normalize(mainLight.direction)));
                float3 litColor = lerp(_ShadowColor.rgb, _SmokeColor.rgb, lightAmount) * mainLight.color.rgb;

                float3 accumColor = 0.0;
                float accumAlpha = 0.0;

                [loop]
                for (int i = 0; i < 128; i++)
                {
                    if (i >= steps || accumAlpha >= 0.99)
                    {
                        break;
                    }

                    float3 localUVW = samplePosOS + 0.5;
                    float3 baseUVW = frac(localUVW + animationDirection * sequenceOffset);
                    float3 detailUVW = frac(baseUVW * _DetailTiling + detailDirection * detailSequenceOffset);
                    float3 shapeUVW = frac(localUVW * _ShapeTiling + shapeDirection * shapeSequenceOffset + float3(0.31, 0.17, 0.59));
                    float3 fineDetailUVW = frac(localUVW * _FineDetailTiling + fineDetailDirection * fineDetailSequenceOffset + float3(0.63, 0.11, 0.27));

                    float densityStatic = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, baseUVW).r;
                    float detailStatic = SAMPLE_TEXTURE3D(_DetailVolumeTex, sampler_DetailVolumeTex, detailUVW).r;
                    float shapeStatic = SAMPLE_TEXTURE3D(_ShapeVolumeTex, sampler_ShapeVolumeTex, shapeUVW).r;
                    float fineStatic = SAMPLE_TEXTURE3D(_FineDetailVolumeTex, sampler_FineDetailVolumeTex, fineDetailUVW).r;

                    float densityAnimatedA = SampleAnimatedSlicesMain(baseUVW, evolutionPhase);
                    float densityAnimatedB = SampleAnimatedSlicesMain(baseUVW, evolutionPhaseB);
                    float densityAnimated = lerp(densityAnimatedA, densityAnimatedB, evolutionBlend);

                    float detailAnimatedA = SampleAnimatedSlicesDetail(detailUVW, frac(evolutionPhase * 1.17 + 0.13));
                    float detailAnimatedB = SampleAnimatedSlicesDetail(detailUVW, frac(evolutionPhaseB * 1.17 + 0.13));
                    float detailAnimated = lerp(detailAnimatedA, detailAnimatedB, evolutionBlend);

                    float shapeAnimatedA = SampleAnimatedSlicesShape(shapeUVW, frac(evolutionPhase * 0.83 + 0.37));
                    float shapeAnimatedB = SampleAnimatedSlicesShape(shapeUVW, frac(evolutionPhaseB * 0.83 + 0.37));
                    float shapeAnimated = lerp(shapeAnimatedA, shapeAnimatedB, evolutionBlend);

                    float fineAnimatedA = SampleAnimatedSlicesFine(fineDetailUVW, frac(evolutionPhase * 1.41 + 0.61));
                    float fineAnimatedB = SampleAnimatedSlicesFine(fineDetailUVW, frac(evolutionPhaseB * 1.41 + 0.61));
                    float fineAnimated = lerp(fineAnimatedA, fineAnimatedB, evolutionBlend);

                    float density = lerp(densityStatic, densityAnimated, _EvolutionStrength);
                    float detailDensity = lerp(detailStatic, detailAnimated, _EvolutionStrength);
                    float shapeDensity = lerp(shapeStatic, shapeAnimated, _EvolutionStrength);
                    float fineDetailDensity = lerp(fineStatic, fineAnimated, _EvolutionStrength);

                    float broadShape = lerp(density, density * 0.65 + shapeDensity * 0.75, _ShapeBlendStrength);
                    float mediumDetail = lerp(1.0, lerp(0.78, 1.22, detailDensity), _DetailStrength);
                    float fineDetail = lerp(1.0, lerp(0.9, 1.1, fineDetailDensity), _FineDetailStrength);
                    density = broadShape * mediumDetail * fineDetail;
                    density = saturate((density - _Cutoff) / max(1.0 - _Cutoff, 1e-5));
                    density = pow(saturate(density), _Contrast);
                    density *= EdgeFade3D(baseUVW, _EdgeFade);

                    float sampleAlpha = 1.0 - exp(-density * _Density * _Opacity * stepSize);
                    float3 sampleColor = litColor * density * _Brightness;

                    accumColor += (1.0 - accumAlpha) * sampleColor * sampleAlpha;
                    accumAlpha += (1.0 - accumAlpha) * sampleAlpha;

                    samplePosOS += stepOS;
                }

                return half4(accumColor, accumAlpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
