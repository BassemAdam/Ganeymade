Shader "temp_texture_shader"
{
    Properties
    {
        _TempTex ("Temperature Volume", 3D) = "black" {}
        _MaxTemp ("Max Temperature", Float) = 100.0
        _BaseColor ("Base Color", Color) = (0.2, 0.2, 0.2, 1)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler3D _TempTex;
            float  _MaxTemp;
            float4 _BaseColor;

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 localPos : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos  = UnityObjectToClipPos(v.vertex);
                // Local position is in [-0.5, 0.5] — shift to [0, 1] for UV
                o.localPos = v.vertex.xyz + 0.5;
                return o;
            }

            // Maps a normalized temperature [0,1] to a heat color
            // black -> red -> orange -> yellow -> white
            float4 HeatColor(float t)
            {
                t = saturate(t);
                float4 col;
                col.a = 1;
                col.r = saturate(t * 3.0);
                col.g = saturate(t * 3.0 - 1.0);
                col.b = saturate(t * 3.0 - 2.0);
                return col;
            }

            float4 frag(v2f i) : SV_Target
            {
                float temp = tex3D(_TempTex, i.localPos).r;
                float normalized = saturate(temp / _MaxTemp);
                float4 heat = HeatColor(normalized);
                // Blend heat color over base color based on temperature
                return lerp(_BaseColor, heat, normalized);
            }
            ENDCG
        }
    }
}