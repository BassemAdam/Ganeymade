Shader "Custom/WaterScreenSpaceFluid"
{
    Properties
    {
        _ParticleRadius           ("Particle Radius (WS)",         Float) = 0.10
        _NRF_MaxFilterSize        ("NRF Max Filter Radius (px)",   Float) = 50
        _NRF_ProjectedParticleK   ("NRF Projected Particle K (0=auto)", Float) = 0
        _NRF_Mu                   ("NRF Mu (snap offset, m)",      Float) = 0.3
        _NRF_DepthThreshold       ("NRF Depth Threshold (m)",      Float) = 1.0
        _NormalStepPixels         ("Normal Step Pixels",           Range(1,4)) = 1

        [HDR] _FluidColor         ("Fluid Color",                  Color) = (0.549,0.863,0.941,1)

        _FluidSmoothness          ("Smoothness",                   Range(0,1)) = 0.96
        _FresnelPower             ("Fresnel Power",                Float) = 5
        _FresnelR0                ("Fresnel R0",                   Range(0,0.16)) = 0.02
        _ThicknessAbsorption      ("Thickness Absorption",         Float) = 1.75
        _ReflectionStrength       ("Reflection Strength",          Range(0,1)) = 1.0
        _RefractionStrength       ("Refraction Strength",          Range(0,8)) = 3.0

        [Header(Screen Space Reflections)]
        _SSF_SSR_Strength         ("SSR Blend Strength",           Range(0,2)) = 1.0
        _SSF_SSR_ColorBoost       ("SSR Color Boost",              Range(0,4)) = 1.0
        _SSF_SSR_StepSize         ("SSR Step Size (WS)",           Range(0.005,1)) = 0.05
        _SSF_SSR_MaxDistance      ("SSR Max Distance (WS)",        Range(0.1,40)) = 10.0
        _SSF_SSR_MaxSteps         ("SSR Max Steps",                Range(8,128)) = 64
        _SSF_SSR_Thickness        ("SSR Thickness Tolerance",      Range(0.001,2)) = 0.08
        _SSF_SSR_EdgeFadeWidth    ("SSR Edge Fade Width",          Range(0.01,0.5)) = 0.08
        _SSF_SSR_DebugVis         ("SSR Debug (0=off 1=refl-only)", Range(0,1)) = 0

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
