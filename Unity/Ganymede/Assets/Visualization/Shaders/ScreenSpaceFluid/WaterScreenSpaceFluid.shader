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
    //   5 ScreenSpaceFluidComposite    Fresnel + Beer + refr + refl + shadow
    //   6 ScreenSpaceFluidThicknessBlur separable Gaussian blur on thickness (X or Y)
    //   7 ScreenSpaceFluidNormalsBlur  separable Gaussian blur on normals (X or Y)
    // ============================================================
    Properties
    {
        // -- Surface reconstruction --
        _ParticleRadius           ("Particle Radius (WS)",         Float) = 0.10
        // Narrow-Range Filter (Truong et al. 2018)
        // _NRF_ProjectedParticleK is auto-computed in C# (0 = auto); set non-zero to override.
        // Calibrated to: maxFilter=50, mu=3*radius, depthThresh=10*radius (matches reference).
        _NRF_MaxFilterSize        ("NRF Max Filter Radius (px)",   Float) = 50
        _NRF_ProjectedParticleK   ("NRF Projected Particle K (0=auto)", Float) = 0
        _NRF_Mu                   ("NRF Mu (snap offset, m)",      Float) = 0.3
        _NRF_DepthThreshold       ("NRF Depth Threshold (m)",      Float) = 1.0
        _NormalStepPixels         ("Normal Step Pixels",           Range(1,4)) = 1
        _ThicknessSplatSigma      ("Thickness Splat Sigma",        Range(0.15,1.0)) = 0.45

        // -- Water look --
        [HDR] _FluidColor         ("Fluid Color",                  Color) = (0.12,0.45,0.85,1)
        [HDR] _FluidSpecularColor ("Specular Color",               Color) = (0.9,0.95,1,1)

        _FluidSmoothness          ("Smoothness",                   Range(0,1)) = 0.96
        _FresnelPower             ("Fresnel Power",                Float) = 4
        _FresnelR0                ("Fresnel R0",                   Range(0,0.16)) = 0.02
        _ThicknessAbsorption      ("Thickness Absorption",         Float) = 2.4
        _ReflectionStrength       ("Reflection Strength",          Range(0,1)) = 0.7
        _RefractionStrength       ("Refraction Strength",          Range(0,8)) = 2.4

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
            Cull Off  ZWrite On  ZTest LEqual  Blend One Zero
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

        // 6 — separable Gaussian blur on the thickness map (X or Y direction)
        Pass
        {
            Name "ScreenSpaceFluidThicknessBlur"
            Cull Off  ZWrite Off  ZTest Always  Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment fragSSFThicknessBlur
            #include "SSF/SSF_ThicknessBlur.hlsl"
            ENDHLSL
        }

        // 7 — separable Gaussian blur on encoded normals (X or Y direction)
        Pass
        {
            Name "ScreenSpaceFluidNormalsBlur"
            Cull Off  ZWrite Off  ZTest Always  Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment fragSSFNormalsBlur
            #include "SSF/SSF_NormalsBlur.hlsl"
            ENDHLSL
        }
    }
}
