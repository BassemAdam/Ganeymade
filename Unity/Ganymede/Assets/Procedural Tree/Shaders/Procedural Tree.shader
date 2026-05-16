Shader "Nature/Procedural Tree"
{
    Properties
    {
        _BarkMap("Bark Texture", 2D) = "white" {}
        _BarkColor("Bark Color", Color) = (1,1,1,1)
        _BarkSmoothness("Bark Smoothness", Range(0,1)) = 0.12

        _LeafMap("Leaf Texture", 2D) = "white" {}
        _LeafColor("Leaf Color", Color) = (1,1,1,1)
        _LeafBottomTint("Leaf Bottom Tint", Color) = (0.24,0.42,0.20,1)
        _LeafTopTint("Leaf Top Tint", Color) = (0.58,0.82,0.34,1)
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.4

        _LeafTranslucency("Leaf Translucency", Range(0,2)) = 0.4
        _LeafSmoothness("Leaf Smoothness", Range(0,1)) = 0.05

        _WindStrength("Wind Strength", Range(0,0.5)) = 0.025
        _WindSpeed("Wind Speed", Range(0,10)) = 1.6
        _WindScale("Wind Scale", Range(0.1,10)) = 1.8
    }

    SubShader
    {
        Tags
        {
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
            "IgnoreProjector" = "True"
        }

        LOD 250
        Cull Off

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows alphatest:_Cutoff addshadow vertex:vert
        #pragma target 3.0

        sampler2D _BarkMap;
        sampler2D _LeafMap;

        fixed4 _BarkColor;
        fixed4 _LeafColor;
        fixed4 _LeafBottomTint;
        fixed4 _LeafTopTint;

        half _BarkSmoothness;
        half _LeafSmoothness;
        half _LeafTranslucency;

        half _WindStrength;
        half _WindSpeed;
        half _WindScale;

        struct Input
        {
            float2 uv_BarkMap;
            float2 uv_LeafMap;
            float4 color : COLOR;
            float3 worldNormal;
            float3 worldPos;
            float facing : VFACE;
        };

        void vert(inout appdata_full v)
        {
            half leafMask = saturate(v.color.a);
            half inheritedWind = saturate(v.color.b);
            half leafFlutter = saturate(v.texcoord.y) * leafMask;

            float timeValue = _Time.y * _WindSpeed;
            float waveA = sin((v.vertex.x + v.vertex.z) * _WindScale + timeValue);
            float waveB = cos(v.vertex.z * (_WindScale * 1.37) + timeValue * 1.19);
            float waveC = sin(v.vertex.y * (_WindScale * 0.73) + timeValue * 0.81);
            float sway = (waveA + waveB + waveC) * 0.33333334;

            v.vertex.x += sway * (_WindStrength * 0.55) * inheritedWind;
            v.vertex.z += waveA * (_WindStrength * 0.28) * inheritedWind;
            v.vertex.y += waveB * (_WindStrength * 0.08) * inheritedWind;

            v.vertex.x += sway * _WindStrength * leafFlutter;
            v.vertex.z += waveA * (_WindStrength * 0.45) * leafFlutter;
            v.vertex.y += waveB * (_WindStrength * 0.18) * leafFlutter;
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            half leafMask = saturate(IN.color.a);

            fixed4 barkSample = tex2D(_BarkMap, IN.uv_BarkMap) * _BarkColor;
            fixed4 leafSample = tex2D(_LeafMap, IN.uv_LeafMap) * _LeafColor;

            fixed3 leafGradient = lerp(_LeafBottomTint.rgb, _LeafTopTint.rgb, saturate(IN.uv_LeafMap.y));
            fixed leafVariation = lerp(0.9h, 1.1h, saturate(IN.color.r));

            fixed3 barkAlbedo = barkSample.rgb;
            fixed3 leafAlbedo = leafSample.rgb * leafGradient * leafVariation;

            half faceSign = IN.facing >= 0.0f ? 1.0h : -1.0h;
            half3 normalWS = normalize(IN.worldNormal) * faceSign;
            half3 lightDir = normalize(UnityWorldSpaceLightDir(IN.worldPos));
            half backLighting = saturate(dot(-normalWS, lightDir));

            o.Albedo = lerp(barkAlbedo, leafAlbedo, leafMask);
            o.Metallic = 0.0h;
            o.Smoothness = lerp(_BarkSmoothness, _LeafSmoothness, leafMask);
            o.Occlusion = 1.0h;
            o.Emission = leafAlbedo * backLighting * _LeafTranslucency * leafMask;
            o.Alpha = lerp(1.0h, leafSample.a, leafMask);
        }
        ENDCG
    }

    FallBack "Legacy Shaders/Transparent/Cutout/Diffuse"
}