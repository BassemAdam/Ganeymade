Shader "Custom/TempTexShader_URP"
{
    Properties
    {
        _TempTex  ("Temperature Volume", 3D) = "black" {}
        _MinTemp  ("Min Temperature", Float) = 0.0
        _MaxTemp  ("Max Temperature", Float) = 100.0
        _BaseColor("Base Color", Color) = (0.2, 0.2, 0.2, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType"= "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 100

        // Forward Lit pass 
        Pass
        {
            Name "ForwardLit"
            Tags {"LightMode" = "UniversalForward"}

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_TempTex);
            SAMPLER(sampler_TempTex);

            // Per-material uniforms 
            CBUFFER_START(UnityPerMaterial)
                float  _MinTemp;
                float  _MaxTemp;
                float4 _BaseColor;
                // World-space bounding box of the voxel grid.
                // Set by ThermalMaterialBridge every frame from VoxelTracerSystem.
                float3 _GridMin;
                float3 _GridMax;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 uvw : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(posWS);

                // Remap world position into the voxel grid's [0,1] UVW space.
                // This works correctly for any object size/position in the scene.
                float3 gridSize = _GridMax - _GridMin;
                OUT.uvw = (posWS - _GridMin) / max(gridSize, 0.0001);

                return OUT;
            }

            // black -> red -> yellow -> white heat ramp
            float4 HeatColor(float t)
            {
                t = saturate(t);
                float4 col;
                col.a = 1.0;
                col.r = saturate(t * 3.0);
                col.g = saturate(t * 3.0 - 1.0);
                col.b = saturate(t * 3.0 - 2.0);
                return col;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                // clamp UVW so out-of-range geometry doesn't sample outside the volume and silently return 0.
                float3 uvw = saturate(IN.uvw);
                float temp = SAMPLE_TEXTURE3D(_TempTex, sampler_TempTex, uvw).r;

                // Voxels with temp == 0 are air / uninitialized hence show the base colour.
                if (temp <= 0.0)
                    return _BaseColor;

                float range = max(_MaxTemp - _MinTemp, 0.001);
                float normalized = saturate((temp - _MinTemp) / range);
                float4 heat = HeatColor(normalized);

                // Blend: at minimum temp show base colour; at maximum show full heat.
                return lerp(_BaseColor, heat, normalized);
            }
            ENDHLSL
        }

        // Depth Only pass 
        Pass
        {
            Name "DepthOnly"
            Tags {"LightMode" = "DepthOnly"}

            ZWrite On
            ColorMask R    
            Cull Back

            HLSLPROGRAM
            #pragma vertex depthVert
            #pragma fragment depthFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float  _MinTemp;
                float  _MaxTemp;
                float4 _BaseColor;
                float3 _GridMin;
                float3 _GridMax;
            CBUFFER_END

            struct Attributes 
            {
                float4 positionOS : POSITION; 
                UNITY_VERTEX_INPUT_INSTANCE_ID 
            };
            struct Varyings 
            {
                float4 positionHCS : SV_POSITION; 
                UNITY_VERTEX_OUTPUT_STEREO 
            };

            Varyings depthVert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            float depthFrag(Varyings IN) : SV_Depth { return IN.positionHCS.z; }
            ENDHLSL
        }

        // Shadow Caster pass 
        Pass
        {
            Name "ShadowCaster"
            Tags {"LightMode" = "ShadowCaster"}

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Vulkan does not auto-declare these from Shadows.hlsl so explicit declaration is required.
            float3 _LightDirection;
            float3 _LightPosition;

            // All passes share the same CBUFFER layout for SRP batcher compatibility.
            CBUFFER_START(UnityPerMaterial)
                float  _MinTemp;
                float  _MaxTemp;
                float4 _BaseColor;
                float3 _GridMin;
                float3 _GridMax;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings shadowVert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);

                #ifdef _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - posWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                OUT.positionHCS = TransformWorldToHClip(ApplyShadowBias(posWS, normalWS, lightDir));
                return OUT;
            }

            float4 shadowFrag(Varyings IN) : SV_Target {return 0;}
            ENDHLSL
        }
    }

    FallBack Off
}