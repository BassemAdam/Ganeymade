Shader "Custom/DebugPhysicsDensity"
{
    Properties
    {
        _DensityMultiplier ("Density Visualization Multiplier", Range(0.1, 50.0)) = 1.0
        _SliceZ ("Slice Position Z (Normalized)", Range(0.0, 1.0)) = 0.5
        
        [Header(Voxel Bounds Object Space)]
        _VoxelBoundsMin ("Bounds Min", Vector) = (-0.5, -0.5, -0.5, 0)
        _VoxelBoundsMax ("Bounds Max", Vector) = ( 0.5,  0.5,  0.5, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+100" "RenderPipeline" = "UniversalPipeline" }

        ZWrite Off
        Cull Front
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            
            // Force the physics density grid functions to be active even if the material doesn't toggle keywords.
            // The PhysicsWaterPhaseBridge script will assign _PhysicsDensityGrid via MaterialPropertyBlock automatically.
            #define _PHYSICS_DENSITY_GRID 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "../MarchingCubesLiquidAndRayMarchVapour/WaterPhase/WaterPhaseHelpers.hlsl"

            struct MeshInput
            {
                float4 positionOS : POSITION;
            };

            struct Interpolators
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
                float  _DensityMultiplier;
                float  _SliceZ;
                float4 _VoxelBoundsMin;
                float4 _VoxelBoundsMax;
            CBUFFER_END

            Interpolators vert(MeshInput IN)
            {
                Interpolators OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                OUT.viewDirWS = GetWorldSpaceViewDir(OUT.positionWS);
                return OUT;
            }

            half4 frag(Interpolators IN) : SV_Target
            {
                float3 cameraWS = _WorldSpaceCameraPos.xyz;
                float3 viewRayWS = normalize(IN.positionWS - cameraWS);
                
                float3 rayOriginOS = TransformWorldToObject(cameraWS);
                float3 rayDirOS = normalize(TransformWorldToObjectDir(viewRayWS));
                
                float3 boundsMinOS = _VoxelBoundsMin.xyz;
                float3 boundsMaxOS = _VoxelBoundsMax.xyz;

                float tEnter, tExit;
                if (!IntersectRayAABBOS(rayOriginOS, rayDirOS, boundsMinOS, boundsMaxOS, tEnter, tExit))
                {
                    return half4(0, 0, 0, 0); 
                }

                // Determine the local Z coordinate of our slice
                float targetZ = lerp(boundsMinOS.z, boundsMaxOS.z, _SliceZ);
                
                // Find where the ray hits this local Z plane
                if (abs(rayDirOS.z) < 1e-5)
                {
                    return half4(0, 0, 0, 0); // Ray is parallel to the slice plane
                }

                float tSlice = (targetZ - rayOriginOS.z) / rayDirOS.z;
                
                // If the intersection is outside the box bounds, or behind the camera, discard
                if (tSlice < tEnter || tSlice > tExit || tSlice < 0.0)
                {
                    return half4(0, 0, 0, 0);
                }

                // Get exactly where the ray hit the slice in world space
                float3 sliceHitOS = rayOriginOS + rayDirOS * tSlice;
                float3 sliceHitWS = TransformObjectToWorld(sliceHitOS);

                // Sample single exact physical value
                float gridDensity = SamplePhysicsDensityGrid(sliceHitWS);

                // Expose raw density scaled by multiplier as a grayscale value with full opacity
                float visualVal = saturate(gridDensity * _DensityMultiplier);

                return half4(visualVal, visualVal, visualVal, 1.0);
            }
            ENDHLSL
        }
    }
}
