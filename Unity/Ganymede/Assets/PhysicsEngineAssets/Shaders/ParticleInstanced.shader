Shader "Custom/ParticleInstanced"
{
    Properties
    {
        _Size ("Particle Size", Float) = 0.05
        _GasScale ("Gas Particle Scale Multiplier", Float) = 1.5
        _GasAlpha ("Gas Particle Opacity", Range(0, 1)) = 0.4
        _MinTemperature ("Min Temperature (for gradient)", Float) = 20.0
        _MaxTemperature ("Max Temperature (for gradient)", Float) = 100.0
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
                float _pad1;
            };

            StructuredBuffer<Particle> _ParticleBuffer;
            float _Size;
            float _GasScale;
            float _GasAlpha;
            float _MinTemperature;
            float _MaxTemperature;
            sampler2D _GradientTex;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float temperature : TEXCOORD1;
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
                    dead.pos = float4(0, 0, -2, 1); // behind near plane -> clipped
                    dead.normal= float3(0, 1, 0);
                    dead.temperature = 0;
                    dead.isGas = 0;
                    return dead;
                }
                float scale = (p.phase == 1) ? _Size * _GasScale : _Size;
                float3 worldPos = p.position + v.vertex.xyz * scale;

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.normal = v.normal;
                o.temperature = p.temperature;
                o.isGas = (p.phase == 1) ? 1.0 : 0.0;
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
