Shader "Custom/WaterScreenSpaceFluid"
{
    // ============================================================
    // Simon Green Screen-Space Fluid Rendering — multi-pass shader.
    // Pass order matches the order of rendering each frame:
    //   0 ScreenSpaceFluidDepth        sphere impostor → eye-depth + HW Z
    //   1 ScreenSpaceFluidThickness    Gaussian splat (additive, no Z)
    //   2 ScreenSpaceFluidLightDepth   sphere impostor from light POV
    //   3 ScreenSpaceFluidBlur         separable bilateral on eye-depth
    //   4 ScreenSpaceFluidNormals      edge-aware finite differences
    //   5 ScreenSpaceFluidComposite    Fresnel + Beer + refr + refl + noise + shadow
    //   6 ScreenSpaceFluidCaustics     screen-space caustics projection
    // ============================================================
    Properties
    {
        // -- Surface reconstruction --
        _ParticleRadius           ("Particle Radius (WS)",         Float) = 0.10
        _BlurRadius               ("Blur Radius (taps)",           Float) = 6
        _BlurSigma                ("Blur Spatial Sigma (px)",      Float) = 3.5
        _BlurDepthSigma           ("Blur Depth Sigma (rel)",       Float) = 0.12
        _NormalStepPixels         ("Normal Step Pixels",           Range(1,4)) = 1
        _ThicknessCutoff          ("Thickness Cutoff",             Float) = 0.0005
        _ThicknessSplatSigma      ("Thickness Splat Sigma",        Range(0.15,1.0)) = 0.45

        // -- Water look --
        [HDR] _FluidColor         ("Fluid Color",                  Color) = (0.12,0.45,0.85,1)
        [HDR] _ShallowColor       ("Shallow Color",                Color) = (0.25,0.65,0.85,1)
        [HDR] _DeepColor          ("Deep Color",                   Color) = (0.02,0.08,0.18,1)
        [HDR] _FluidSpecularColor ("Specular Color",               Color) = (0.9,0.95,1,1)

        _FluidSmoothness          ("Smoothness",                   Range(0,1)) = 0.96
        _FresnelPower             ("Fresnel Power",                Float) = 4
        _FresnelR0                ("Fresnel R0",                   Range(0,0.16)) = 0.02
        _DiffuseWrap              ("Diffuse Wrap",                 Range(0,1)) = 0.22
        _DiffuseStrength          ("Diffuse Strength",             Range(0,2)) = 0.30
        _ThicknessAbsorption      ("Thickness Absorption",         Float) = 2.4
        _AbsorptionRate           ("Depth Absorption",             Float) = 1.4
        _ReflectionStrength       ("Reflection Strength",          Range(0,1)) = 0.7
        _RefractionStrength       ("Refraction Strength",          Range(0,8)) = 2.4
        _RefractionBlur           ("Refraction Blur",              Range(0,6)) = 1.75
        _RefractionThicknessScale ("Refraction Thickness Scale",   Float) = 0.35
        _CompositeStrength        ("Composite Strength",           Range(0,1)) = 0.96

        // -- Surface noise (Step 7) --
        _SurfaceNoiseStrength     ("Surface Noise Strength",       Range(0,1)) = 0.0
        _SurfaceNoiseScale        ("Surface Noise Scale (1/m)",    Float) = 8.0
        _SurfaceNoiseSpeed        ("Surface Noise Speed",          Float) = 0.4

        // -- Caustics (Step 7) --
        _CausticsTex              ("Caustics Texture",             2D) = "black" {}
        _CausticsStrength         ("Caustics Strength",            Range(0,4)) = 0.0
        _CausticsTiling           ("Caustics Tiling (1/m)",        Float) = 0.5
        _CausticsPlaneY           ("Caustics Plane Y (WS)",        Float) = 0.0
        _CausticsScrollSpeed      ("Caustics Scroll Speed",        Float) = 0.05
        _CausticsThicknessAttenuation ("Caustics Thickness Atten", Float) = 1.5
        _CausticsDepthAttenuation     ("Caustics Depth Atten",     Float) = 0.5
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "SSF/SSF_Common.hlsl"
        ENDHLSL

        // 0 — sphere impostor depth
        Pass
        {
            Name "ScreenSpaceFluidDepth"
            Cull Off  ZWrite On  ZTest LEqual  Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vertSSFDepth
            #pragma fragment fragSSFDepth
            #include "SSF/SSF_Depth.hlsl"
            ENDHLSL
        }

        // 1 — additive Gaussian thickness
        Pass
        {
            Name "ScreenSpaceFluidThickness"
            Cull Off  ZWrite Off  ZTest Always  Blend One One
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vertSSFThickness
            #pragma fragment fragSSFThickness
            #include "SSF/SSF_Thickness.hlsl"
            ENDHLSL
        }

        // 2 — sphere impostor depth from light POV
        Pass
        {
            Name "ScreenSpaceFluidLightDepth"
            Cull Off  ZWrite On  ZTest LEqual  Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vertSSFLightDepth
            #pragma fragment fragSSFLightDepth
            #include "SSF/SSF_LightDepth.hlsl"
            ENDHLSL
        }

        // 3 — separable bilateral blur on eye-depth
        Pass
        {
            Name "ScreenSpaceFluidBlur"
            Cull Off  ZWrite Off  ZTest Always  Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment fragSSFBlur
            #include "SSF/SSF_Blur.hlsl"
            ENDHLSL
        }

        // 4 — view-space normals from smoothed depth
        Pass
        {
            Name "ScreenSpaceFluidNormals"
            Cull Off  ZWrite Off  ZTest Always  Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment fragSSFNormals
            #include "SSF/SSF_Normals.hlsl"
            ENDHLSL
        }

        // 5 — composite (lighting, refraction, reflection, shadow, noise)
        Pass
        {
            Name "ScreenSpaceFluidComposite"
            Cull Off  ZWrite On  ZTest Always  Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment fragSSFComposite
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #include "SSF/SSF_Composite.hlsl"
            ENDHLSL
        }

        // 6 — screen-space caustics projection (post-composite)
        Pass
        {
            Name "ScreenSpaceFluidCaustics"
            Cull Off  ZWrite Off  ZTest Always  Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment fragSSFCaustics
            #include "SSF/SSF_Caustics.hlsl"
            ENDHLSL
        }
    }
}
