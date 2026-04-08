Shader "Custom/ParticleInstanced"
{
    Properties
    {
        _Size ("Particle Size", Float) = 0.05
        _MinTemperature ("Min Temperature (for gradient)", Float) = 20.0
        _MaxTemperature ("Max Temperature (for gradient)", Float) = 100.0
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
            float _MinTemperature;
            float _MaxTemperature;
            sampler2D _GradientTex;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float temperature : TEXCOORD1;
            };

            v2f vert(appdata_base v, uint instanceID : SV_InstanceID)
            {
                Particle p = _ParticleBuffer[instanceID];
                float3 worldPos = p.position + v.vertex.xyz * _Size;

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.normal = v.normal;
                o.temperature = p.temperature;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 lightDir = normalize(float3(0.5, 1.0, 0.3));
                float ndl = saturate(dot(i.normal, lightDir)) * 0.6 + 0.4;

                // Map temperature to gradient: 0 = minTemp, 1 = maxTemp
                float t = saturate((i.temperature - _MinTemperature) / (_MaxTemperature - _MinTemperature));
                
                // Direct color mapping (BLUE → RED) without texture
                float3 col;
                if (t < 0.25f)
                {
                    // Blue to Cyan
                    col = lerp(float3(0, 0, 1), float3(0, 1, 1), t / 0.25f);
                }
                else if (t < 0.5f)
                {
                    // Cyan to Yellow
                    col = lerp(float3(0, 1, 1), float3(1, 1, 0), (t - 0.25f) / 0.25f);
                }
                else if (t < 0.75f)
                {
                    // Yellow to Orange
                    col = lerp(float3(1, 1, 0), float3(1, 0.5, 0), (t - 0.5f) / 0.25f);
                }
                else
                {
                    // Orange to Red
                    col = lerp(float3(1, 0.5, 0), float3(1, 0, 0), (t - 0.75f) / 0.25f);
                }
                
                return float4(col * ndl, 1.0);
            }
            ENDCG
        }
    }
}
