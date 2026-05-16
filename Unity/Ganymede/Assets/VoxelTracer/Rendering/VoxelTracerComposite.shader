Shader "Hidden/VoxelTracer/Composite"
{
    Properties
    {
        _MainTex   ("Scene",       2D) = "black" {}
        _VoxelTex  ("Voxel Color", 2D) = "black" {}
        _VoxelDepth("Voxel Depth", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_VoxelTex);
            SAMPLER(sampler_VoxelTex);
            TEXTURE2D(_VoxelDepth);
            SAMPLER(sampler_VoxelDepth);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv  = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 scene  = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                half4 voxel  = SAMPLE_TEXTURE2D(_VoxelTex, sampler_VoxelTex, IN.uv);
                float vDepth = SAMPLE_TEXTURE2D(_VoxelDepth, sampler_VoxelDepth, IN.uv).r;

                // Scene depth (linear eye-space)
                float rawDepth   = SampleSceneDepth(IN.uv);
                float sceneDepth = LinearEyeDepth(rawDepth, _ZBufferParams);

                // If voxel hit (depth < 1e9) and closer than scene geometry
                if (vDepth > 0.0 && vDepth < 1e9 && vDepth < sceneDepth)
                    return voxel;

                return scene;
            }
            ENDHLSL
        }
    }
}
