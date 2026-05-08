Shader "Hidden/SSF_Thickness"
{
    // Pass 2: Accumulate fluid thickness using additive blending.
    // Each particle contributes sqrt(1-r²) thickness.
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend One One // Additive

        Pass
        {
            Name "SSF_ThicknessPass"

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
            };

            VertexOutput vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                VertexOutput o;

                static const float2 corners[6] = {
                    float2( 0.5,  0.5),
                    float2( 0.5, -0.5),
                    float2(-0.5, -0.5),
                    float2( 0.5,  0.5),
                    float2(-0.5, -0.5),
                    float2(-0.5,  0.5)
                };

                Particle p = _ParticleBuffer[instanceID];

                // Temporarily disabled phase check for debugging
                // if (p.phase != 0)
                // {
                //     o.positionCS = float4(0, 0, 0, 0);
                //     o.uv = 0;
                //     return o;
                // }

                float nFactor = saturate(p.neighborCount / max(_SprayThreshold * 2.0, 1.0));
                float radius = _ParticleRadius * lerp(0.4, 1.0, nFactor);

                float3 viewPos = mul(_SSF_ViewMatrix, float4(p.position, 1.0)).xyz;
                float2 corner = corners[vertexID] * radius * 2.0;

                float4 billboardPos = float4(viewPos + float3(corner, 0.0), 1.0);
                o.positionCS = mul(_SSF_ProjMatrix, billboardPos);
                o.uv = corners[vertexID] + 0.5;

                return o;
            }

            float4 frag(VertexOutput i) : SV_Target
            {
                float2 nxy = i.uv * 2.0 - 1.0;
                float r2 = dot(nxy, nxy);
                if (r2 > 1.0)
                    discard;

                float thickness = sqrt(1.0 - r2);
                float alpha = 0.08; // per-particle contribution weight
                return float4(alpha * thickness, 0, 0, 1);
            }
            ENDHLSL
        }
    }
}
