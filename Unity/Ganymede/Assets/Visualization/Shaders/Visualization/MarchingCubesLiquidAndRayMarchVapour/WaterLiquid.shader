// =============================================================================
// WaterLiquid.shader
// -----------------------------------------------------------------------------
// Surface shader for the marching-cubes liquid iso-surface.
//
// Inputs from the existing pipeline:
//   * `_MCVertices`        – StructuredBuffer<MCVertex> filled by
//                            MarchingCubesCompute.compute, indexed by SV_VertexID.
//   * `_WaterThicknessMap` – RHalf screen-space texture (linear metres) produced
//                            by WaterThicknessFeature + WaterThicknessGen.shader.
//
// Design rationale
// ----------------
// The MC mesh has *coarse, noisy normals* and no tangent frame.  In production
// fluid renderers (e.g. NVIDIA "Screen Space Fluid Rendering", God of War, etc.)
// the surface is shaded as follows:
//
//   • SMOOTH geometric normal drives REFRACTION — it must be stable so the
//     screen-space sampling stays on-pixel. Using a noisy/perturbed normal here
//     is the #1 cause of black streaks (the distorted UV walks off-screen).
//   • PERTURBED normal (geom + tiling ripples) drives ONLY specular / Fresnel /
//     environment reflection, where high-frequency wobble is what we want.
//   • Refraction sample fades to the deep-water tint near the screen edges, so
//     off-frame regions don't punch black holes.
//   • Reflection has a sky-colour fallback for scenes without a baked probe.
//
//                ripple-perturbed normal ─► Fresnel  ─►  spec  / reflection
//   geom normal ─►  refracted scene  ─► Beer-Lambert tint
//                                        │
//                                        ▼
//                          lerp(transmitted, reflection, F)  + spec  → fog → out
// =============================================================================
Shader "Custom/WaterLiquid"
{
    Properties
    {
        // ---- Body / volume ----------------------------------------------------
        [Header(Body Color)]
        _ShallowColor       ("Shallow Tint",          Color)            = (0.45, 0.85, 0.90, 1.0)
        _DeepColor          ("Deep Tint",             Color)            = (0.02, 0.10, 0.20, 1.0)
        _AbsorptionRate     ("Absorption Rate (1/m)", Range(0.05, 8.0)) = 1.5
        _ScatterStrength    ("In-scattering",         Range(0.0, 1.0))  = 0.25

        // ---- Surface micro-detail --------------------------------------------
        [Header(Ripples)]
        [NoScaleOffset] _NormalMap ("Ripple Normal Map", 2D)            = "bump" {}
        _NormalTiling       ("Tiling (per metre)",    Range(0.05, 4.0)) = 0.6
        _NormalStrength     ("Strength",              Range(0.0, 1.0))  = 0.25
        _NormalScrollSpeed  ("Scroll Speed",          Range(0.0, 1.0))  = 0.06

        // ---- Specular & reflection -------------------------------------------
        [Header(Specular and Reflection)]
        _Smoothness         ("Smoothness",            Range(0.0, 1.0))  = 0.92
        _SpecularStrength   ("Specular Strength",     Range(0.0, 4.0))  = 1.0
        _ReflectionStrength ("Reflection Strength",   Range(0.0, 1.0))  = 0.6
        _FresnelPower       ("Fresnel Power",         Range(1.0, 8.0))  = 5.0
        _SkyHorizonColor    ("Sky Horizon Fallback",  Color)            = (0.55, 0.65, 0.75, 1.0)
        _SkyZenithColor     ("Sky Zenith Fallback",   Color)            = (0.20, 0.35, 0.55, 1.0)

        // ---- Refraction ------------------------------------------------------
        [Header(Refraction)]
        _RefractionStrength ("Distortion",            Range(0.0, 0.1))  = 0.02
        _ChromaticAberration("Chromatic Aberration",  Range(0.0, 0.02)) = 0.002
        _EdgeFadePixels     ("Edge Fade (pixels)",    Range(8, 128))    = 32

        // ---- Optional caustics (multiplied into the refraction) --------------
        [Header(Caustics)]
        [NoScaleOffset] _CausticsTex ("Caustics (RGB)", 2D)             = "black" {}
        _CausticsTiling     ("Tiling (per metre)",    Range(0.05, 4.0)) = 0.5
        _CausticsScrollSpeed("Scroll Speed",          Range(0.0, 1.0))  = 0.10
        _CausticsStrength   ("Strength",              Range(0.0, 4.0))  = 1.0
        _CausticsDepthFade  ("Depth Fade (m)",        Range(0.1, 8.0))  = 1.5
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Transparent"
            "Queue"           = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "WaterLiquidForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // =====================================================================
            // 0. INPUT LAYOUT (matches MarchingCubesCompute.compute MCVertex)
            // =====================================================================
            struct MCVertex { float4 position; float4 normal; };
            StructuredBuffer<MCVertex> _MCVertices;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float4 screenPos   : TEXCOORD2;
                float  fogCoord    : TEXCOORD3;
                float  eyeDepth    : TEXCOORD4; // linear eye-space Z of the surface
            };

            // =====================================================================
            // 1. UNIFORMS
            // =====================================================================
            TEXTURE2D(_NormalMap);         SAMPLER(sampler_NormalMap);
            TEXTURE2D(_CausticsTex);       SAMPLER(sampler_CausticsTex);
            TEXTURE2D(_WaterThicknessMap); SAMPLER(sampler_WaterThicknessMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half  _AbsorptionRate;
                half  _ScatterStrength;

                half  _NormalTiling;
                half  _NormalStrength;
                half  _NormalScrollSpeed;

                half  _Smoothness;
                half  _SpecularStrength;
                half  _ReflectionStrength;
                half  _FresnelPower;
                half4 _SkyHorizonColor;
                half4 _SkyZenithColor;

                half  _RefractionStrength;
                half  _ChromaticAberration;
                half  _EdgeFadePixels;

                half  _CausticsTiling;
                half  _CausticsScrollSpeed;
                half  _CausticsStrength;
                half  _CausticsDepthFade;
            CBUFFER_END

            static const float WATER_F0 = 0.02;     // Schlick F0 for n=1.33

            // =====================================================================
            // 2. HELPERS
            // =====================================================================

            float FresnelSchlick(float NdotV)
            {
                float x = 1.0 - saturate(NdotV);
                return WATER_F0 + (1.0 - WATER_F0) * pow(x, _FresnelPower);
            }

            float3 TriplanarWeights(float3 n, float sharpness)
            {
                float3 w = pow(abs(n), sharpness);
                return w / max(w.x + w.y + w.z, 1e-5);
            }

            // Build a world-space ripple normal from a tiling normal map sampled
            // triplanarly.  We CLAMP the magnitude of the perturbation so the
            // resulting normal can never tip too far away from the geometric one –
            // that's what previously drove Fresnel to 1 and produced bright/black
            // patches on the silhouette.
            float3 SampleRippleNormalWS(float3 positionWS, float3 geomNormalWS)
            {
                float3 w = TriplanarWeights(geomNormalWS, 4.0);
                float  t = _Time.y * _NormalScrollSpeed;
                float  s = _NormalTiling;

                float2 uvX = positionWS.zy * s         + float2( t,        0.0   );
                float2 uvY = positionWS.xz * s         + float2( t,        t*0.5 );
                float2 uvZ = positionWS.xy * s         + float2( 0.0,      t     );
                float2 uvX2= positionWS.zy * s * 1.7   - float2( t*0.6,    t*0.4 );
                float2 uvY2= positionWS.xz * s * 1.7   - float2( t*0.5,    t*0.7 );
                float2 uvZ2= positionWS.xy * s * 1.7   + float2( t*0.4,   -t*0.6 );

                float3 nX = (UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvX ), _NormalStrength)
                          +  UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvX2), _NormalStrength)) * 0.5;
                float3 nY = (UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvY ), _NormalStrength)
                          +  UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvY2), _NormalStrength)) * 0.5;
                float3 nZ = (UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvZ ), _NormalStrength)
                          +  UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvZ2), _NormalStrength)) * 0.5;

                float3 perturb = float3(0.0, nX.x, nX.y) * w.x
                              +  float3(nY.x, 0.0, nY.y) * w.y
                              +  float3(nZ.x, nZ.y, 0.0) * w.z;

                // Hard cap – never let the perturbation exceed half a unit per axis.
                perturb = clamp(perturb, -0.5, 0.5);

                return normalize(geomNormalWS + perturb);
            }

            // Beer-Lambert per channel: red dies fastest.
            half3 BeerTransmittance(float thickness)
            {
                half3 extinction = _AbsorptionRate * (1.0 - _DeepColor.rgb);
                return exp(-thickness * extinction);
            }

            // How far inside [0,1] the UV is — 1 in the centre, 0 outside.  Used to
            // fade refraction toward the deep tint near screen edges so we never
            // sample garbage off-frame pixels.
            float ScreenEdgeFade(float2 uv)
            {
                float2 px       = _ScreenParams.xy * uv;            // pixel coords
                float2 distEdge = min(px, _ScreenParams.xy - px);   // pixels to nearest edge
                float  d        = min(distEdge.x, distEdge.y);
                return saturate(d / max(_EdgeFadePixels, 1.0));
            }

            // Refracted scene with chromatic split + edge-aware fade.  IMPORTANT:
            // the distortion is driven by the *geometric* normal, not the ripple
            // normal, so it stays smooth and stable (no off-screen jitter that
            // produces black streaks where the surface tilts sharply).
            half3 SampleRefraction(float3 geomNormalWS, float2 baseUV, float thickness,
                                   half3 deepFallback, out float refractionWeight)
            {
                float  thicknessGain = saturate(thickness * 0.5 + 0.25);
                float2 distort = geomNormalWS.xy * _RefractionStrength * thicknessGain;
                float2 chroma  = geomNormalWS.xy * _ChromaticAberration;

                float2 uvR = baseUV + distort + chroma;
                float2 uvG = baseUV + distort;
                float2 uvB = baseUV + distort - chroma;

                // Edge fade evaluated on the green (centre) tap is fine – the colour
                // channels never separate by more than a couple of pixels.
                refractionWeight = ScreenEdgeFade(uvG);

                half3 col;
                col.r = SampleSceneColor(saturate(uvR)).r;
                col.g = SampleSceneColor(saturate(uvG)).g;
                col.b = SampleSceneColor(saturate(uvB)).b;

                // Where we're off-screen (or close to it) replace the sample with
                // the deep fallback colour so distortion never creates dark streaks.
                return lerp(deepFallback, col, refractionWeight);
            }

            // Optional triplanar caustics, faded out with thickness.
            half3 SampleCaustics(float3 positionWS, float3 geomNormalWS, float thickness)
            {
                float3 w = TriplanarWeights(geomNormalWS, 4.0);
                float  t = _Time.y * _CausticsScrollSpeed;
                float  s = _CausticsTiling;

                half3 cX = SAMPLE_TEXTURE2D(_CausticsTex, sampler_CausticsTex, positionWS.zy * s + float2( t, 0   )).rgb;
                half3 cY = SAMPLE_TEXTURE2D(_CausticsTex, sampler_CausticsTex, positionWS.xz * s + float2( t, t*0.5)).rgb;
                half3 cZ = SAMPLE_TEXTURE2D(_CausticsTex, sampler_CausticsTex, positionWS.xy * s + float2( 0, t   )).rgb;
                half3 caustics = cX * w.x + cY * w.y + cZ * w.z;

                float depthFade = exp(-thickness / max(_CausticsDepthFade, 1e-3));
                return caustics * _CausticsStrength * depthFade;
            }

            // Cheap procedural sky for scenes without a reflection probe.  Vertical
            // gradient between zenith and horizon — keeps grazing-angle reflections
            // from collapsing to pure black when the env probe returns nothing.
            half3 SkyFallback(float3 reflectDir)
            {
                float h = saturate(reflectDir.y * 0.5 + 0.5);   // 0 = down, 1 = up
                return lerp(_SkyHorizonColor.rgb, _SkyZenithColor.rgb, h);
            }

            // =====================================================================
            // 3. VERTEX
            // =====================================================================
            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                MCVertex v       = _MCVertices[IN.vertexID];
                OUT.positionWS   = v.position.xyz;
                OUT.normalWS     = normalize(v.normal.xyz);
                OUT.positionHCS  = TransformWorldToHClip(OUT.positionWS);
                OUT.screenPos    = ComputeScreenPos(OUT.positionHCS);
                OUT.eyeDepth     = -TransformWorldToView(OUT.positionWS).z;
                OUT.fogCoord     = ComputeFogFactor(OUT.positionHCS.z);
                return OUT;
            }

            // =====================================================================
            // 4. FRAGMENT
            // =====================================================================
            half4 frag(Varyings IN, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                // ---- 4.1 Two normals: smooth (geom) and noisy (geom + ripples) ---
                float3 geomNormalWS = isFrontFace ? IN.normalWS : -IN.normalWS;
                float3 rippleNormal = SampleRippleNormalWS(IN.positionWS, geomNormalWS);

                float3 viewDirWS = normalize(_WorldSpaceCameraPos - IN.positionWS);
                float2 screenUV  = IN.screenPos.xy / IN.screenPos.w;

                // ---- 4.2 Thickness ----------------------------------------------
                // Primary source: the precomputed _WaterThicknessMap, which sums
                // (back-faces - front-faces) of the marching-cubes mesh in metres,
                // capped to the opaque scene depth. It can legitimately be 0 if:
                //   * the WaterThicknessFeature is missing from the URP renderer,
                //   * the procedural callback hasn't been registered yet, or
                //   * we're looking at a thin silhouette where front/back cancel.
                // Fallback: estimate single-sided thickness from scene depth as
                //   (sceneEyeDepth - surfaceEyeDepth)   — i.e. how far behind the
                // surface the opaque background sits. This matches what the
                // dedicated feature would have produced for the front face alone
                // and degrades the shader gracefully when the map isn't bound.
                float thicknessMap = max(SAMPLE_TEXTURE2D(_WaterThicknessMap,
                                                          sampler_WaterThicknessMap,
                                                          screenUV).r, 0.0);

                float rawSceneDepth      = SampleSceneDepth(screenUV);
                float sceneEyeDepth      = LinearEyeDepth(rawSceneDepth, _ZBufferParams);
                float depthFallback      = max(sceneEyeDepth - IN.eyeDepth, 0.0);

                // Use the map when it carries a meaningful value, otherwise the
                // depth-based estimate. Never zero out — keep at least a small
                // optical path so the body colour is visible at silhouettes.
                float thickness = (thicknessMap > 1e-3) ? thicknessMap : depthFallback;
                thickness = max(thickness, 0.05);

                // ---- 4.3 Lighting basis (uses the noisy normal for highlights) ---
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light  mainLight   = GetMainLight(shadowCoord);
                float  shadow      = mainLight.shadowAttenuation;

                float3 L     = normalize(mainLight.direction);
                float3 H     = normalize(L + viewDirWS);
                float  NdotL = saturate(dot(rippleNormal, L));
                float  NdotH = saturate(dot(rippleNormal, H));
                float  NdotV = saturate(dot(rippleNormal, viewDirWS));

                // ---- 4.4 Fresnel -------------------------------------------------
                float fresnel = FresnelSchlick(NdotV);

                // ---- 4.5 Refraction (driven by SMOOTH normal, edge-faded) --------
                half3 deepFallback = _DeepColor.rgb;
                float refractionW;
                half3 sceneCol = SampleRefraction(geomNormalWS, screenUV, thickness,
                                                  deepFallback, refractionW);

                half3 caustics = SampleCaustics(IN.positionWS, geomNormalWS, thickness);
                sceneCol *= (1.0 + caustics);

                // Beer-Lambert tinting toward deep colour.
                half3 transmittance = BeerTransmittance(thickness);
                half3 absorbed      = sceneCol * transmittance
                                    + _DeepColor.rgb * (1.0 - transmittance);

                // Subtle in-scattering glow under direct lighting.
                half3 inScatter = _ShallowColor.rgb * _ScatterStrength
                                * mainLight.color * (NdotL * 0.5 + 0.5) * shadow;

                half3 transmitted = absorbed + inScatter * transmittance;

                // ---- 4.6 Reflection (env probe + sky-colour fallback) -----------
                float  roughness   = max(1.0 - _Smoothness, 0.045);
                float3 reflectDir  = reflect(-viewDirWS, rippleNormal);
                half3  probeRefl   = GlossyEnvironmentReflection(reflectDir, IN.positionWS,
                                                                 roughness, 1.0, screenUV);

                // If the probe is essentially black (no baked probe in the scene),
                // fall back to a procedural sky so grazing angles don't read as
                // pitch-black holes on the silhouette.
                float  probeLum    = max(max(probeRefl.r, probeRefl.g), probeRefl.b);
                half3  reflection  = lerp(SkyFallback(reflectDir), probeRefl, saturate(probeLum * 4.0))
                                   * _ReflectionStrength;

                // ---- 4.7 Specular (Blinn-Phong, gated by Fresnel + shadow) -------
                float specPower = exp2(_Smoothness * 11.0 + 2.0);
                float specTerm  = pow(NdotH, specPower) * NdotL * shadow;
                half3 specular  = mainLight.color * specTerm * fresnel * _SpecularStrength;

                // ---- 4.8 Composite (energy conserving) ---------------------------
                half3 color = lerp(transmitted, reflection, fresnel) + specular;

                // ---- 4.9 Alpha ---------------------------------------------------
                float bodyAlpha = saturate(1.0 - exp(-thickness * _AbsorptionRate));
                half  alpha     = saturate(max(bodyAlpha, fresnel));

                // ---- 4.10 Fog ---------------------------------------------------
                color = MixFog(color, IN.fogCoord);

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
