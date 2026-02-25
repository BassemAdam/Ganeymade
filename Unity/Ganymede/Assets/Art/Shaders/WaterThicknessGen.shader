Shader "Hidden/WaterThicknessGen"
{
    SubShader
    {
        // We use the Transparent queue, but the exact queue will be overridden by our C# Render Feature anyway
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            // THE MAGIC BLEND STATE: Additive Blending
            // This takes whatever the fragment shader outputs and adds it to the texture.
            Blend One One 
            
            // Turn off depth writing so voxels don't block each other
            ZWrite Off 
            
            // Always render both faces, even when behind opaque objects.
            // The min(fragDepth, sceneDepth) logic in the fragment shader
            // correctly caps the contribution to the opaque surface depth.
            ZTest Always 
            
            // THE CULLING TRICK: Render both Front and Back faces at the same time
            Cull Off 

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct MeshInput
            {
                float4 positionOS : POSITION;
            };

            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float4 screenPos : TEXCOORD0;
            };

            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.screenPos = ComputeScreenPos(OUT.positionHCS);
                return OUT;
            }

            // VFACE tells us if the GPU is currently drawing the outside (front) or inside (back) of the mesh
            float4 frag(Interpolators IN, float vface : VFACE) : SV_Target
            {
                // 1. Determine if this is a front face or back face.
                // In Unity, VFACE > 0 means Front Face. VFACE < 0 means Back Face.
                // Front faces subtract depth (-1). Back faces add depth (+1).
                float signMultiplier = (vface > 0.0) ? -1.0 : 1.0;

                // 2. Calculate the depth of THIS specific voxel face (in meters)
                float rawFragDepth = IN.positionHCS.z; 
                float fragLinearEyeDepth = LinearEyeDepth(rawFragDepth, _ZBufferParams);

                // 3. Sample the Opaque Scene Depth (the rocks, the floor)
                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;
                float rawSceneDepth = SampleSceneDepth(screenUV);
                float sceneLinearEyeDepth = LinearEyeDepth(rawSceneDepth, _ZBufferParams);

                // 4. THE ROCK COLLISION LOGIC
                // If the voxel face is behind a rock, we cap its depth to the rock's depth.
                float validDepth = min(fragLinearEyeDepth, sceneLinearEyeDepth);

                // 5. Apply the Add/Subtract rule
                float finalThickness = validDepth * signMultiplier;

                // Output the value. We output to all channels (RGBA) just to be safe, 
                return float4(finalThickness, finalThickness, finalThickness, 1.0);
            }
            ENDHLSL
        }
    }
}