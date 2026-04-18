Shader "Hidden/WaterThicknessGen"
{
    SubShader
    {
        // We use the Transparent queue, but the exact queue will be overridden by our C# Render Feature anyway
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "WaterThicknessGen"
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
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile _ _MARCHING_CUBES_PROCEDURAL

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

#if defined(_MARCHING_CUBES_PROCEDURAL)
            // Marching-cubes procedural vertex stream.
            struct MCVertex {
                float4 position;
                float4 normal;
            };
            StructuredBuffer<MCVertex> _MCVertices;
#endif

            struct MeshInput
            {
#if defined(_MARCHING_CUBES_PROCEDURAL)
                uint vertexID : SV_VertexID;
#else
                float4 positionOS : POSITION;
#endif
            };

            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float4 screenPos : TEXCOORD0;
            };

            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
#if defined(_MARCHING_CUBES_PROCEDURAL)
                // procedural vertices are already mostly in World Space, though we usually just 
                // use TransformWorldToHClip. Let's make sure:
                float3 posWS = _MCVertices[IN.vertexID].position.xyz;
                OUT.positionHCS = TransformWorldToHClip(posWS);
#else
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
#endif
                OUT.screenPos = ComputeScreenPos(OUT.positionHCS);
                return OUT;
            }

            // SV_IsFrontFace is a bool and safely cross-compiles to Vulkan, DX, Metal
            float4 frag(Interpolators IN, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                // Front faces subtract depth (-1). Back faces add depth (+1).
                float signMultiplier = isFrontFace ? -1.0 : 1.0;

                // 2. Calculate the depth of THIS specific voxel face (in meters)
                // IN.screenPos.w natively contains the exact linear eye depth derived from the vertex positionHCS.w!
                float fragLinearEyeDepth = IN.screenPos.w; 

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