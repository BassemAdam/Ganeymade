Shader "Hidden/SSF_Depth"
{
    // Pass 1: Render particles as view-aligned billboards.
    // Fragment performs sphere ray-test to compute per-pixel view-space depth.
    // neighborCount modulates the billboard radius for fine splash detail.
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite On
        ZTest LEqual
        Cull Off

        Pass
        {
            Name "SSF_DepthPass"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Particle
            {
                float3 position;
                float  density;
                float3 velocity;
                float  pressure;
                float3 acceleration;
                float  mass;
                float  temperature;
                int    phase;
                float  latentHeatAccum;
                float  fixedId;
                float  neighborCount;
                float  _pad0;
                float  _pad1;
                float  _pad2;
            };

            StructuredBuffer<Particle> _ParticleBuffer;
            float _ParticleRadius;
            float _SprayThreshold;
            float4x4 _SSF_ViewMatrix;
            float4x4 _SSF_ProjMatrix;

            struct VertexOutput
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 viewPos : TEXCOORD1;
                float radius : TEXCOORD2;
            };

            VertexOutput vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                VertexOutput o;

                // 6 verts per quad (2 triangles)
                static const float2 corners[6] = {
                    float2( 0.5,  0.5),
                    float2( 0.5, -0.5),
                    float2(-0.5, -0.5),
                    float2( 0.5,  0.5),
                    float2(-0.5, -0.5),
                    float2(-0.5,  0.5)
                };

                Particle p = _ParticleBuffer[instanceID];

                // Only render liquid particles (phase 0)
                // Temporarily disabled phase check for debugging
                // if (p.phase != 0)
                // {
                //     o.positionCS = float4(0, 0, 0, 0);
                //     o.uv = 0;
                //     o.viewPos = 0;
                //     o.radius = 0;
                //     return o;
                // }

                // Modulate radius by neighbor count: fewer neighbors = smaller (spray)
                float nFactor = saturate(p.neighborCount / max(_SprayThreshold * 2.0, 1.0));
                float radius = _ParticleRadius * lerp(0.4, 1.0, nFactor);

                float3 viewPos = mul(_SSF_ViewMatrix, float4(p.position, 1.0)).xyz;
                float2 corner = corners[vertexID] * radius * 2.0;

                float4 billboardPos = float4(viewPos + float3(corner, 0.0), 1.0);
                o.positionCS = mul(_SSF_ProjMatrix, billboardPos);
                o.uv = corners[vertexID] + 0.5;
                o.viewPos = viewPos;
                o.radius = radius;

                return o;
            }

            struct FragOutput
            {
                float depth : SV_Target0;
                float fragDepth : SV_Depth;
            };

            FragOutput frag(VertexOutput i)
            {
                FragOutput o;

                // Sphere ray-test in billboard UV space
                float2 nxy = i.uv * 2.0 - 1.0;
                float r2 = dot(nxy, nxy);
                if (r2 > 1.0)
                    discard;

                float nz = sqrt(1.0 - r2);
                float3 sphereOffset = float3(nxy, nz) * i.radius;
                float3 realViewPos = i.viewPos + sphereOffset;

                float4 clipPos = mul(_SSF_ProjMatrix, float4(realViewPos, 1.0));
                o.fragDepth = clipPos.z / clipPos.w;
                o.depth = -realViewPos.z; // linear view depth (positive into screen)

                return o;
            }
            ENDHLSL
        }
    }
}
