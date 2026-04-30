// =============================================================================
//  Screen-Space Fluid Rendering — Particle Depth + Thickness pass
//
//  Procedurally rasterises N SPH particles as camera-facing point sprites and,
//  per fragment, performs a ray-sphere intersection in view space.
//
//  PASS 0 ("ParticleDepth"):
//      Output SV_Depth (perspective-correct sphere depth) and linear eye-Z to
//      colour attachment 0. Depth-tested against the scene depth so opaque
//      geometry properly occludes fluid particles.
//
//  PASS 1 ("ParticleThickness"):
//      Additive blending; no depth write; outputs the through-sphere thickness
//      (= 2 * sqrt(1 - r^2) * radius) to a single-channel target. Sums to a
//      physical thickness used by Beer-Lambert in the composite pass.
//
//  Vertex layout (procedural, no mesh):
//      6 vertices per particle (2 triangles, CCW).
//      vid / 6 -> particle index.   vid % 6 -> corner.
// =============================================================================
Shader "Hidden/ScreenSpace/SS_ParticleDepth"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        // --------------------------------------------------------------------
        // PASS 0 — Sphere depth + linear eye-Z
        // --------------------------------------------------------------------
        Pass
        {
            Name "ParticleDepth"
            Tags { "LightMode" = "ParticleDepth" }
            ZWrite On
            ZTest LEqual
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Mirrors the C#/compute-shader Particle layout (stride = 64 B).
            struct Particle
            {
                float3 position;     float  density;
                float3 velocity;     float  pressure;
                float3 acceleration; float  mass;
                float  temperature;  int    phase;
                float  _pad0;        float  _pad1;
            };

            StructuredBuffer<Particle> _ParticleBuffer;
            float                       _SphereRadiusWS;
            uint                        _ParticleCount;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 quadUV     : TEXCOORD0;   // [-1, 1] over the quad
                float3 centerVS   : TEXCOORD1;   // sphere centre in view space
            };

            // Two triangles (CCW) covering [-1, 1] x [-1, 1].
            static const float2 kCorners[6] = {
                float2(-1.0, -1.0), float2( 1.0, -1.0), float2( 1.0,  1.0),
                float2(-1.0, -1.0), float2( 1.0,  1.0), float2(-1.0,  1.0)
            };

            Varyings vert(uint vid : SV_VertexID)
            {
                Varyings o;
                uint particleIdx = vid / 6u;
                uint cornerIdx   = vid % 6u;

                if (particleIdx >= _ParticleCount)
                {
                    // Degenerate vertex outside clip space — culled by rasteriser.
                    o.positionCS = float4(2.0, 2.0, 2.0, 1.0);
                    o.quadUV     = 0.0;
                    o.centerVS   = 0.0;
                    return o;
                }

                float3 centerWS = _ParticleBuffer[particleIdx].position;
                float3 centerVS = mul(UNITY_MATRIX_V, float4(centerWS, 1.0)).xyz;

                float2 corner = kCorners[cornerIdx];
                float3 cornerVS = centerVS + float3(corner * _SphereRadiusWS, 0.0);

                o.positionCS = mul(UNITY_MATRIX_P, float4(cornerVS, 1.0));
                o.quadUV     = corner;
                o.centerVS   = centerVS;
                return o;
            }

            struct FragOut
            {
                half4  color : SV_Target0;
                float  depth : SV_Depth;
            };

            FragOut frag(Varyings i)
            {
                FragOut o;
                float r2 = dot(i.quadUV, i.quadUV);
                if (r2 > 1.0)
                    discard;

                // Sphere surface in view space: shift toward the camera (positive Z in view = behind).
                // Unity view space is right-handed: camera looks down -Z.
                float zOffset    = sqrt(1.0 - r2) * _SphereRadiusWS;
                float3 surfaceVS = i.centerVS + float3(0.0, 0.0, zOffset);

                // Project surface point to clip space for SV_Depth.
                float4 surfaceCS = mul(UNITY_MATRIX_P, float4(surfaceVS, 1.0));
                o.depth = surfaceCS.z / surfaceCS.w;

                // Linear positive eye distance, in metres.
                o.color = half4(-surfaceVS.z, 0.0, 0.0, 1.0);
                return o;
            }
            ENDHLSL
        }

        // --------------------------------------------------------------------
        // PASS 1 — Additive sphere thickness
        // --------------------------------------------------------------------
        Pass
        {
            Name "ParticleThickness"
            Tags { "LightMode" = "ParticleThickness" }
            ZWrite Off
            // ZTest Always: this pass renders into an isolated RHalf target with no
            // depth attachment bound. With ZTest LEqual and no depth source the
            // rasteriser rejects every fragment -> the thickness texture stays black.
            // Scene-occlusion of fluid is handled later by the composite pass against
            // _CameraDepthTexture, so we don't need depth here.
            ZTest Always
            Cull Off
            Blend One One         // additive accumulation

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Particle
            {
                float3 position;     float  density;
                float3 velocity;     float  pressure;
                float3 acceleration; float  mass;
                float  temperature;  int    phase;
                float  _pad0;        float  _pad1;
            };

            StructuredBuffer<Particle> _ParticleBuffer;
            float                       _SphereRadiusWS;
            uint                        _ParticleCount;
            float                       _ThicknessScale;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 quadUV     : TEXCOORD0;
            };

            static const float2 kCorners[6] = {
                float2(-1.0, -1.0), float2( 1.0, -1.0), float2( 1.0,  1.0),
                float2(-1.0, -1.0), float2( 1.0,  1.0), float2(-1.0,  1.0)
            };

            Varyings vert(uint vid : SV_VertexID)
            {
                Varyings o;
                uint particleIdx = vid / 6u;
                uint cornerIdx   = vid % 6u;

                if (particleIdx >= _ParticleCount)
                {
                    o.positionCS = float4(2.0, 2.0, 2.0, 1.0);
                    o.quadUV     = 0.0;
                    return o;
                }

                float3 centerWS = _ParticleBuffer[particleIdx].position;
                float3 centerVS = mul(UNITY_MATRIX_V, float4(centerWS, 1.0)).xyz;
                float2 corner   = kCorners[cornerIdx];
                float3 cornerVS = centerVS + float3(corner * _SphereRadiusWS, 0.0);

                o.positionCS = mul(UNITY_MATRIX_P, float4(cornerVS, 1.0));
                o.quadUV     = corner;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float r2 = dot(i.quadUV, i.quadUV);
                if (r2 > 1.0)
                    discard;

                float thickness = 2.0 * sqrt(1.0 - r2) * _SphereRadiusWS * _ThicknessScale;
                return half4(thickness, 0.0, 0.0, 1.0);
            }
            ENDHLSL
        }
    }
}
