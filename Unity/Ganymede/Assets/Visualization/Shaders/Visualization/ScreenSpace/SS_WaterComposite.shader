// =============================================================================
//  Screen-Space Fluid Rendering — Composite
//
//  Final pass that turns the filtered eye-depth + thickness textures into a
//  shaded water surface, blended over the existing scene colour.
//
//  Pipeline per pixel (where _FluidDepth.r > 0):
//      1. Reconstruct view-space position from linear eye depth.
//      2. Normal-from-depth: normalise(cross(ddy(viewPos), ddx(viewPos))).
//         (Sign chosen so the normal faces the camera.)
//      3. Fresnel (Schlick) for reflection / refraction split.
//      4. Refraction: sample _CameraOpaqueTexture at UV perturbed by normal.xy,
//         then attenuate by Beer-Lambert using thickness * extinction.
//      5. Reflection: tint by sky / ambient (cheap sample of unity_SpecCube0).
//      6. Specular: tiny Blinn-Phong highlight from the URP main light.
//      7. Blend over scene by alpha = lerp(fresnel, 1, surfaceCoverage).
//
//  Inputs:
//      _FluidDepth      R32F  (linear eye Z, metres)
//      _FluidThickness  RHalf (accumulated through-sphere thickness, metres)
//      _CameraOpaqueTexture, _CameraDepthTexture (URP standard)
// =============================================================================
Shader "Hidden/ScreenSpace/SS_WaterComposite"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "ScreenSpaceWaterComposite"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_FluidDepth);
            SAMPLER(sampler_FluidDepth);
            TEXTURE2D(_FluidThickness);
            SAMPLER(sampler_FluidThickness);
            TEXTURE2D(_CameraOpaqueTexture);
            SAMPLER(sampler_CameraOpaqueTexture);

            float4 _FluidDepth_TexelSize;          // (1/w, 1/h, w, h)

            // Material parameters --------------------------------------------------
            float3 _LiquidExtinction;              // sigma_t (m^-1) per RGB channel
            float3 _LiquidScatterTint;             // medium-thickness in-scatter colour
            float3 _LiquidDeepTint;                // deep / grazing-angle tint
            float  _RefractionStrengthSS;          // UV displacement scale (screen units)
            float  _ReflectionStrengthSS;          // sky reflection scale
            float  _SpecularPower;                 // blinn-phong exponent
            float  _SpecularIntensity;             // multiplier on highlight brightness
            float  _F0;                            // Fresnel at normal incidence (water ~ 0.02)
            float  _MinThicknessForOpaque;         // m -> alpha=1 above this thickness
            int    _DebugMode;                     // 0=normal, 1=depth, 2=thickness, 3=normal, 4=hit
            float  _DebugDepthRange;               // metres mapped to red in mode 1
            float  _DebugThicknessRange;           // metres mapped to white in mode 2

            // Heatmap colormap: black -> blue -> cyan -> green -> yellow -> red -> white.
            // Wide dynamic range so low values are visible (not crushed to black).
            float3 Heatmap(float t)
            {
                t = saturate(t);
                // Six-stop ramp. The first stop is bright blue (not black) so any non-zero
                // value is visibly distinguishable from background.
                float3 c0 = float3(0.0, 0.2, 1.0);   // bright blue
                float3 c1 = float3(0.0, 1.0, 1.0);   // cyan
                float3 c2 = float3(0.0, 1.0, 0.0);   // green
                float3 c3 = float3(1.0, 1.0, 0.0);   // yellow
                float3 c4 = float3(1.0, 0.0, 0.0);   // red
                float3 c5 = float3(1.0, 1.0, 1.0);   // white (clipping indicator)
                if (t < 0.20) return lerp(c0, c1, t / 0.20);
                if (t < 0.40) return lerp(c1, c2, (t - 0.20) / 0.20);
                if (t < 0.60) return lerp(c2, c3, (t - 0.40) / 0.20);
                if (t < 0.80) return lerp(c3, c4, (t - 0.60) / 0.20);
                return                lerp(c4, c5, (t - 0.80) / 0.20);
            }

            // -- Reconstruct view-space position from eye depth -------------------
            float3 ViewPosFromEyeDepth(float2 uv, float eyeDepth)
            {
                float2 ndc = uv * 2.0 - 1.0;
                // Inverse-projection of the unit clip ray (ndc.x, ndc.y, 1, 1).
                float4 clipRay = float4(ndc, 1.0, 1.0);
                float4 viewRayH = mul(UNITY_MATRIX_I_P, clipRay);
                float3 viewRay  = viewRayH.xyz / viewRayH.w;
                // viewRay.z is negative under Unity view space. Scale so view.z = -eyeDepth.
                return viewRay * (eyeDepth / -viewRay.z);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                float fluidEyeZ = SAMPLE_TEXTURE2D(_FluidDepth, sampler_FluidDepth, uv).r;

                // Debug visualisations — these IGNORE the early-out so a black screen
                // can be distinguished from "depth pass actually wrote zero everywhere".
                // Background pixels return alpha=0 so the underlying scene shows through
                // (the composite uses Blend SrcAlpha OneMinusSrcAlpha).
                if (_DebugMode == 1) // depth, heatmap normalised by _DebugDepthRange
                {
                    if (fluidEyeZ <= 0.0) return half4(0, 0, 0, 0);
                    float t = fluidEyeZ / max(_DebugDepthRange, 0.0001);
                    return half4(Heatmap(t), 1.0);
                }
                if (_DebugMode == 2) // thickness, heatmap normalised by _DebugThicknessRange
                {
                    float th = SAMPLE_TEXTURE2D(_FluidThickness, sampler_FluidThickness, uv).r;
                    if (th <= 0.0) return half4(0, 0, 0, 0);
                    float t = th / max(_DebugThicknessRange, 1e-5);
                    return half4(Heatmap(t), 1.0);
                }
                if (_DebugMode == 4)
                {
                    // Approximate particles-per-pixel: thickness / (single-sphere through-thickness).
                    // A single sphere viewed through its centre contributes 2*r of thickness, so
                    // count = thickness / (2 * sphereRadius). We don't have sphere radius here so
                    // we use _DebugThicknessRange as a "per-particle thickness contribution"
                    // calibration knob: lower it to amplify visibility.
                    float th = SAMPLE_TEXTURE2D(_FluidThickness, sampler_FluidThickness, uv).r;
                    if (th <= 0.0) return half4(0, 0, 0, 0);
                    float countNormalised = th / max(_DebugThicknessRange, 1e-5);
                    return half4(Heatmap(countNormalised * 0.1), 1.0); // *0.1 so dense regions don't all clip to white
                }
                if (_DebugMode == 5)
                {
                    // Silhouette outline only: white pixel if any 3x3 neighbour has different
                    // hit state. Useful for seeing the exact boundary of the fluid coverage.
                    float c   = fluidEyeZ > 0.0 ? 1.0 : 0.0;
                    float dr  = SAMPLE_TEXTURE2D(_FluidDepth, sampler_FluidDepth, uv + float2( _FluidDepth_TexelSize.x, 0)).r > 0.0 ? 1.0 : 0.0;
                    float dl  = SAMPLE_TEXTURE2D(_FluidDepth, sampler_FluidDepth, uv + float2(-_FluidDepth_TexelSize.x, 0)).r > 0.0 ? 1.0 : 0.0;
                    float du2 = SAMPLE_TEXTURE2D(_FluidDepth, sampler_FluidDepth, uv + float2(0,  _FluidDepth_TexelSize.y)).r > 0.0 ? 1.0 : 0.0;
                    float dd2 = SAMPLE_TEXTURE2D(_FluidDepth, sampler_FluidDepth, uv + float2(0, -_FluidDepth_TexelSize.y)).r > 0.0 ? 1.0 : 0.0;
                    float edge = abs(c - dr) + abs(c - dl) + abs(c - du2) + abs(c - dd2);
                    return edge > 0.0 ? half4(1, 0, 1, 1) : half4(0, 0, 0, 0);
                }

                if (fluidEyeZ <= 0.0)
                    return half4(0.0, 0.0, 0.0, 0.0);

                // Occlusion test against scene depth (in case opaque geometry has come in front).
                float sceneDeviceDepth = SampleSceneDepth(uv);
                float sceneEyeZ        = LinearEyeDepth(sceneDeviceDepth, _ZBufferParams);
                if (sceneEyeZ < fluidEyeZ - 1e-4)
                    return half4(0.0, 0.0, 0.0, 0.0);

                // ---- View-space position + neighbour-aware normal -----------------
                float3 viewPos = ViewPosFromEyeDepth(uv, fluidEyeZ);

                // Use 1-tap finite differences so silhouettes stay sharp without
                // depending on hardware ddx_fine support.
                float2 du = float2(_FluidDepth_TexelSize.x, 0.0);
                float2 dv = float2(0.0, _FluidDepth_TexelSize.y);

                float dRight = SAMPLE_TEXTURE2D(_FluidDepth, sampler_FluidDepth, uv + du).r;
                float dLeft  = SAMPLE_TEXTURE2D(_FluidDepth, sampler_FluidDepth, uv - du).r;
                float dUp    = SAMPLE_TEXTURE2D(_FluidDepth, sampler_FluidDepth, uv + dv).r;
                float dDown  = SAMPLE_TEXTURE2D(_FluidDepth, sampler_FluidDepth, uv - dv).r;

                // Pick the closer neighbour on each axis when one side is background
                // (=0) or jumps too far. This kills silhouette streaking.
                float depthBand = max(0.05, fluidEyeZ * 0.05);
                bool rightOk = (dRight > 0.0) && abs(dRight - fluidEyeZ) < depthBand;
                bool leftOk  = (dLeft  > 0.0) && abs(dLeft  - fluidEyeZ) < depthBand;
                bool upOk    = (dUp    > 0.0) && abs(dUp    - fluidEyeZ) < depthBand;
                bool downOk  = (dDown  > 0.0) && abs(dDown  - fluidEyeZ) < depthBand;

                float3 ddxView;
                if (rightOk && leftOk)       ddxView = 0.5 * (ViewPosFromEyeDepth(uv + du, dRight) - ViewPosFromEyeDepth(uv - du, dLeft));
                else if (rightOk)            ddxView =       (ViewPosFromEyeDepth(uv + du, dRight) - viewPos);
                else if (leftOk)             ddxView =       (viewPos                              - ViewPosFromEyeDepth(uv - du, dLeft));
                else                         ddxView = float3(_FluidDepth_TexelSize.x, 0, 0);

                float3 ddyView;
                if (upOk && downOk)          ddyView = 0.5 * (ViewPosFromEyeDepth(uv + dv, dUp)    - ViewPosFromEyeDepth(uv - dv, dDown));
                else if (upOk)               ddyView =       (ViewPosFromEyeDepth(uv + dv, dUp)    - viewPos);
                else if (downOk)             ddyView =       (viewPos                              - ViewPosFromEyeDepth(uv - dv, dDown));
                else                         ddyView = float3(0, _FluidDepth_TexelSize.y, 0);

                float3 normalVS = normalize(cross(ddyView, ddxView));
                if (normalVS.z < 0.0) normalVS = -normalVS; // ensure facing camera (+Z toward camera in view space)
                float3 normalWS = normalize(mul((float3x3)UNITY_MATRIX_I_V, normalVS));

                if (_DebugMode == 3) return half4(normalVS * 0.5 + 0.5, 1.0);
                // ---- View / light vectors in world space --------------------------
                float3 viewPosWS = mul(UNITY_MATRIX_I_V, float4(viewPos, 1.0)).xyz;
                float3 V         = normalize(_WorldSpaceCameraPos - viewPosWS);
                float  NdotV     = saturate(dot(normalWS, V));

                // ---- Fresnel (Schlick) -------------------------------------------
                // Physical reflectance term used to blend refraction vs reflection.
                float F0      = max(_F0, 1e-3);
                float fresnel = saturate(F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0));

                // ---- Refraction backdrop -----------------------------------------
                // Push UVs by normal.xy in screen-space (cheap planar approximation).
                // Scale by thickness so calm thin water barely distorts and thick water bends a lot.
                float thickness = SAMPLE_TEXTURE2D(_FluidThickness, sampler_FluidThickness, uv).r;
                float2 refractUV = uv + normalVS.xy * _RefractionStrengthSS * saturate(thickness * 4.0);
                refractUV = saturate(refractUV);
                half3 refracted = SAMPLE_TEXTURE2D(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, refractUV).rgb;

                // ---- Beer-Lambert through-water tint ------------------------------
                // Light travelling through water is absorbed per channel: T = exp(-sigma * d).
                // The remainder is filled by in-scattered light coloured by _LiquidScatterTint.
                float3 transmittance = exp(-_LiquidExtinction * thickness);
                float3 scatter       = _LiquidScatterTint * (1.0 - transmittance);
                float3 throughWater  = refracted * transmittance + scatter;

                // Deep-water tint blend: at thick columns (or grazing angles) blend toward the
                // deep colour so the water doesn't look uniformly cyan everywhere.
                float deepBlend = saturate(thickness * 0.7) * (0.5 + 0.5 * (1.0 - NdotV));
                throughWater = lerp(throughWater, _LiquidDeepTint, deepBlend);

                // ---- Reflection (sky cube) ---------------------------------------
                float3 R = reflect(-V, normalWS);
                half4 envSample = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, R, 0.0);
                half3 envColor  = DecodeHDREnvironment(envSample, unity_SpecCube0_HDR) * _ReflectionStrengthSS;

                // ---- Specular highlight from main light --------------------------
                Light mainLight = GetMainLight();
                float3 H        = normalize(V + mainLight.direction);
                float  spec     = pow(saturate(dot(normalWS, H)), max(_SpecularPower, 1.0));
                // Energy-conserving specular: scale by fresnel so highlights are stronger at
                // grazing angles, plus user-tunable intensity for art-direction.
                float3 specCol  = mainLight.color.rgb * spec * fresnel * _SpecularIntensity;

                // ---- Final colour mix --------------------------------------------
                // Blend refraction/in-scatter with sky reflection by Fresnel, then add specular.
                float3 surfaceColor = lerp(throughWater, envColor, fresnel) + specCol;

                // ---- Composite alpha ----------------------------------------------
                // Alpha is governed by:
                //   - thickness coverage (thin water = transparent; thick = opaque)
                //   - Fresnel (grazing edges always show some highlight)
                // We deliberately remove the previous 0.15 floor: realistic water near a
                // single-particle silhouette should be almost invisible, not a grey haze.
                float coverage = saturate(thickness / max(_MinThicknessForOpaque, 1e-3));
                float alpha    = saturate(max(coverage, fresnel));

                return half4(surfaceColor, alpha);
            }
            ENDHLSL
        }
    }
}
