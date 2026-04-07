Shader "Custom/ParticleInstanced"
{
    Properties
    {
        _Size ("Particle Size", Float) = 0.05
        _MaxSpeed ("Max Speed (for gradient)", Float) = 10.0
        _GradientTex ("Gradient Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct Particle
            {
                float3 position;
                float density;
                float3 velocity;
                float pressure;
                float3 acceleration;
                float mass;
                float temperature;
                int phase;
                float _pad0;
                float _pad1;
            };

            StructuredBuffer<Particle> _ParticleBuffer;
            float _Size;
            float _MaxSpeed;
            sampler2D _GradientTex;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float speed : TEXCOORD1;
            };

            v2f vert(appdata_base v, uint instanceID : SV_InstanceID)
            {
                Particle p = _ParticleBuffer[instanceID];
                float3 worldPos = p.position + v.vertex.xyz * _Size;

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.normal = v.normal;
                o.speed = length(p.velocity);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 lightDir = normalize(float3(0.5, 1.0, 0.3));
                float ndl = saturate(dot(i.normal, lightDir)) * 0.6 + 0.4;

                float t = saturate(i.speed / _MaxSpeed);
                float3 col = tex2D(_GradientTex, float2(t, 0.5)).rgb;
                return float4(col * ndl, 1.0);
            }
            ENDCG
        }
    }
}
