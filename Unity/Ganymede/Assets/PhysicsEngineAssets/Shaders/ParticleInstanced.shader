Shader "Custom/ParticleInstanced"
{
    Properties
    {
        _Size ("Particle Size", Float) = 0.05
        _GasScale ("Gas Particle Scale Multiplier", Float) = 1.5
        _GasAlpha ("Gas Particle Opacity", Range(0, 1)) = 0.4
        _MinValue ("Min Value (for gradient)", Float) = 20.0
        _MaxValue ("Max Value (for gradient)", Float) = 100.0
        _GradientTex ("Gradient Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite On
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
                float latentHeatAccum;
                float _fixedId;
            };

            StructuredBuffer<Particle> _ParticleBuffer;
            float _Size;
            float _GasScale;
            float _GasAlpha;
            int _VisualizedField;
            float _MinValue;
            float _MaxValue;
            sampler2D _GradientTex;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float value : TEXCOORD1;
                float isGas : TEXCOORD2;
            };

            v2f vert(appdata_base v, uint instanceID : SV_InstanceID)
            {
                Particle p = _ParticleBuffer[instanceID];
                // Dormant particles (phase == -1): collapse to a single degenerate
                // point well outside the clip volume so no fragments are produced.
                if (p.phase < 0)
                {
                    v2f dead;
                    dead.pos = float4(0, 0, 1, 0); // point at infinity
                    dead.normal= float3(0, 1, 0);
                    dead.value = 0;
                    dead.isGas = 0;
                    return dead;
                }
                float scale = (p.phase == 1) ? _Size * _GasScale : _Size;
                float3 worldPos = p.position + v.vertex.xyz * scale;
                float visualizedValue = p.temperature;

                if (_VisualizedField == 1)
                    visualizedValue = p.pressure;
                else if (_VisualizedField == 2)
                    visualizedValue = p.density;
                else if (_VisualizedField == 3)
                    visualizedValue = length(p.velocity);

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.normal = v.normal;
                o.value = visualizedValue;
                o.isGas = (p.phase == 1) ? 1.0 : 0.0;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 lightDir = normalize(float3(0.5, 1.0, 0.3));
                float ndl = saturate(dot(i.normal, lightDir)) * 0.6 + 0.4;

                // Map selected scalar to gradient: 0 = minValue, 1 = maxValue
                float range = max(_MaxValue - _MinValue, 1e-5);
                float t = saturate((i.value - _MinValue) / range);
                float3 col = tex2D(_GradientTex, float2(t, 0.5)).rgb;
                
                // Gas particles: blend toward white (steam-like) and reduce opacity
                float alpha = 1.0;
                if (i.isGas > 0.5)
                {
                    col = lerp(col, float3(1, 1, 1), 0.5);
                    alpha = _GasAlpha;
                }
                
                return float4(col * ndl, alpha);
            }
            ENDCG
        }
    }
}
