Shader "Custom/TerrainEdgeFog"
{
    // Drop this on a large inverted cylinder (built by TerrainEdgeFogRing) placed
    // around the terrain. Two effects combine to fake distance fog:
    //   1. Radial fade — pixels near the terrain (small horizontal distance from
    //      _FogCenter) are clear; pixels near the outer rim are fully fogged.
    //      A smoothstep avoids a visible inner edge.
    //   2. Exponential density on camera-to-pixel distance — the wall looks like
    //      thick haze on the horizon and disappears as the camera gets near it.
    Properties
    {
        _FogColor       ("Fog Color", Color)                       = (0.62, 0.70, 0.78, 1)
        _FogCenter      ("Fog Center (XZ used)", Vector)           = (0, 0, 0, 0)
        _InnerRadius    ("Inner Radius (start)", Float)            = 80
        _OuterRadius    ("Outer Radius (full)", Float)             = 160
        _Density        ("Density (per 100 m)", Range(0, 5))       = 1.5
        _HeightFalloff  ("Height Falloff (0 = disabled)", Float)   = 0
        _HeightCenter   ("Height Center", Float)                   = 0
        _MaxAlpha       ("Max Alpha", Range(0, 1))                 = 1
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+100" "IgnoreProjector"="True" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "TerrainEdgeFog"
            Tags { "LightMode"="UniversalForward" }

            Cull Front          // we render the inside of an inverted cylinder
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            CBUFFER_START(UnityPerMaterial)
                float4 _FogColor;
                float4 _FogCenter;
                float  _InnerRadius;
                float  _OuterRadius;
                float  _Density;
                float  _HeightFalloff;
                float  _HeightCenter;
                float  _MaxAlpha;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // 1) Radial fade — smoothstep so there is no visible "ring edge".
                float2 d   = IN.positionWS.xz - _FogCenter.xz;
                float  dist = length(d);
                float  radial = smoothstep(_InnerRadius, max(_OuterRadius, _InnerRadius + 0.001), dist);

                // 2) Exponential-squared distance fog (atmospheric look).
                //    _Density is per 100 m, so the curve feels intuitive in the inspector.
                float camDist = distance(IN.positionWS, _WorldSpaceCameraPos);
                float k = _Density * 0.01;          // per-meter density
                float fogFactor = 1.0 - exp(-(k * camDist) * (k * camDist));

                // 3) Optional vertical falloff so the dome doesn't tint sky pixels.
                float vertical = 1.0;
                if (_HeightFalloff > 0.0)
                {
                    float h = abs(IN.positionWS.y - _HeightCenter);
                    vertical = saturate(1.0 - h / _HeightFalloff);
                }

                float a = saturate(radial * fogFactor * vertical) * _MaxAlpha;
                return half4(_FogColor.rgb, a);
            }
            ENDHLSL
        }
    }

    // Built-in pipeline fallback (same logic, no URP includes).
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+100" "IgnoreProjector"="True" }
        Pass
        {
            Cull Front
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f     { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; };

            float4 _FogColor;
            float4 _FogCenter;
            float  _InnerRadius;
            float  _OuterRadius;
            float  _Density;
            float  _HeightFalloff;
            float  _HeightCenter;
            float  _MaxAlpha;

            v2f vert(appdata v)
            {
                v2f o;
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos  = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 d   = i.wpos.xz - _FogCenter.xz;
                float  dist = length(d);
                float  radial = smoothstep(_InnerRadius, max(_OuterRadius, _InnerRadius + 0.001), dist);

                float camDist = distance(i.wpos, _WorldSpaceCameraPos);
                float k = _Density * 0.01;
                float fogFactor = 1.0 - exp(-(k * camDist) * (k * camDist));

                float vertical = 1.0;
                if (_HeightFalloff > 0.0)
                {
                    float h = abs(i.wpos.y - _HeightCenter);
                    vertical = saturate(1.0 - h / _HeightFalloff);
                }

                float a = saturate(radial * fogFactor * vertical) * _MaxAlpha;
                return fixed4(_FogColor.rgb, a);
            }
            ENDCG
        }
    }
}
