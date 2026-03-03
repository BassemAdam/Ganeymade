Shader "Custom/WaterVapour"
{
    Properties
    {
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}

        [Header(Noise)]
        _NoiseScale     ("Noise Scale",      Range(0.1, 10.0)) = 1.0
        _NoiseDriftDir  ("Drift Direction",  Vector)           = (0, 1, 0, 0)
        _NoiseDriftSpeed("Drift Speed",       Range(0.0,  5.0)) = 0.3
        _NoiseOctaves   ("Noise Octaves",     Range(1, 8))      = 5

        [Header(Density)]
        _DensityPower   ("Density Power",    Range(0.1, 5.0))  = 1.5
        _Density        ("Density (Physics)",Range(0.0, 1.0))  = 1.0
        _PhysicsBlend   ("Physics Blend",    Range(0.0, 1.0))  = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        // No depth write — vapor must not occlude objects behind it
        ZWrite Off

        // Standard alpha blending
        Blend SrcAlpha OneMinusSrcAlpha

        // No backface culling — vapor has no real surface orientation
        Cull Off

        Pass
        {
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../Includes/WaterVapourHelpers.hlsl"

            struct MeshInput
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1; // world-space position — drives noise & raymarching
                float3 viewDirWS  : TEXCOORD2; // world-space view direction — drives Fresnel & phase function
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                half4   _BaseColor;
                float4  _BaseMap_ST;
                // Noise
                float   _NoiseScale;
                float4  _NoiseDriftDir;
                float   _NoiseDriftSpeed;
                int     _NoiseOctaves;
                // Density
                float   _DensityPower;
                float   _Density;
                float   _PhysicsBlend;
            CBUFFER_END

            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.uv          = TRANSFORM_TEX(IN.uv, _BaseMap);
                // GetWorldSpaceViewDir returns (cameraPos - positionWS), unnormalized
                // We normalize in the fragment shader where we need it per-pixel
                OUT.viewDirWS   = GetWorldSpaceViewDir(OUT.positionWS);
                return OUT;
            }

            half4 frag(Interpolators IN) : SV_Target
            {
                // ---- STEP 4 VISUALIZATION : Density Field ----
                // Outputs the raw density as greyscale so we can see:
                //   - FBM wispy structure
                //   - Turbulence warp (swirling organic shapes)
                //   - Drift animation (noise scrolling over time)
                //   - Power curve effect (sharp vs soft edges via _DensityPower)
                //   - Physics bridge (_PhysicsBlend / _Density fading the whole thing)

                float density = SampleDensity(
                    IN.positionWS,
                    _Time.y,
                    normalize(_NoiseDriftDir.xyz),
                    _NoiseDriftSpeed,
                    _NoiseScale,
                    _NoiseOctaves,
                    _DensityPower,
                    _Density,
                    _PhysicsBlend
                );

                // Greyscale: bright = dense vapor, dark = empty
                half3 col = half3(density, density, density) * _BaseColor.rgb;

                // Alpha driven by density so edges are already transparent
                return half4(col, density);
            }
            ENDHLSL
        }
    }
}
