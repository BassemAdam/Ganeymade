Shader "Hidden/VoxelComposite"
{
    Properties
    {
        _VoxTex ("Voxels", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_VoxTex);
            SAMPLER(sampler_VoxTex);

            half4 frag(Varyings IN) : SV_Target
            {
                half4 scene = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, IN.texcoord);
                half4 voxel = SAMPLE_TEXTURE2D(_VoxTex, sampler_VoxTex, IN.texcoord);
                // Alpha-over: where voxel hit (a=1), show voxel; where miss (a=0), show scene
                return lerp(scene, half4(voxel.rgb, 1.0), voxel.a);
            }
            ENDHLSL
        }
    }
}
