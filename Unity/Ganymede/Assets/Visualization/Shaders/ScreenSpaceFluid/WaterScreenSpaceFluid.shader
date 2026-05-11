Shader "Custom/WaterScreenSpaceFluid"
{
    Properties
    {
        [Header(Surface Reconstruction)]
        _ParticleRadius      ("Particle Radius WS",   Float)       = 0.1
        _BlurRadius          ("Blur Radius (taps)",   Float)       = 3
        _BlurSigma           ("Blur Spatial Sigma",   Float)       = 2.0
        _BlurDepthSigma      ("Blur Depth Sigma",     Float)       = 0.03
        _NormalStepPixels    ("Normal Step Pixels",   Range(1,4))  = 2.0
        _ThicknessCutoff     ("Thickness Cutoff",     Float)       = 0.002

        [Header(Water Color)]
        _FluidColor          ("Fluid Color",          Color)       = (0.12, 0.45, 0.85, 1.0)
        _ShallowColor        ("Shallow Color",        Color)       = (0.25, 0.65, 0.85, 1.0)
        _DeepColor           ("Deep Color",           Color)       = (0.02, 0.08, 0.18, 1.0)

        [Header(Surface Lighting)]
        _FluidSpecularColor  ("Specular Color",       Color)       = (0.9, 0.95, 1.0, 1.0)
        _FluidSmoothness     ("Smoothness",           Range(0,1))  = 0.9
        _FresnelPower        ("Fresnel Power",        Float)       = 4.0
        _ThicknessAbsorption ("Thickness Absorption", Float)       = 2.0
        _AbsorptionRate      ("Depth Absorption",     Float)       = 1.4

        [Header(Optics)]
        _ReflectionStrength  ("Reflection Strength",  Range(0,1))  = 0.65
        _RefractionStrength  ("Refraction Strength",  Range(0,8))  = 2.0
        _RefractionBlur      ("Refraction Blur",      Range(0,6))  = 1.5
        _RefractionThicknessScale ("Refraction Thickness Scale", Float) = 0.35

        [Header(Output)]
        _CompositeStrength   ("Composite Strength",   Range(0,1))  = 0.9
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "SSF/SSF_Common.hlsl"
        ENDHLSL

        Pass
        {
            Name "ScreenSpaceFluidDepth"
            Cull Off
            ZWrite On
            ZTest LEqual
            Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vertDepth
            #pragma fragment fragDepth
            #include "SSF/SSF_Depth.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ScreenSpaceFluidThickness"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One One
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vertThick
            #pragma fragment fragThick
            #include "SSF/SSF_Depth.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ScreenSpaceFluidDepthRange"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment fragDepthRange
            #include "SSF/SSF_DepthRange.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ScreenSpaceFluidNormalizeDepth"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment fragNormalizeDepth
            #include "SSF/SSF_NormalizeDepth.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ScreenSpaceFluidBlur2D"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment fragBlur2D
            #include "SSF/SSF_Blur2D.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ScreenSpaceFluidNormals"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment fragNormals
            #include "SSF/SSF_Normals.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ScreenSpaceFluidComposite"
            Cull Off
            ZWrite On
            ZTest LEqual
            Blend One Zero
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment fragComposite
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #include "SSF/SSF_Composite.hlsl"
            ENDHLSL
        }
    }
}
