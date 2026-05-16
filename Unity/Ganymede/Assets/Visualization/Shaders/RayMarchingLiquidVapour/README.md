# WaterRaymarching Shader Notes

## Debug view quick map (material `_DebugViewMode`)

- `0`: Off
- `1`: Reflection Only (final reflection source after fallback/blending)
- `2`: Optical Normal Used
- `3`: Reflection Direction
- `4`: Reflection Weight (Fresnel)
- `5`: Reflection Contribution
- `6`: Refraction Contribution
- `7`: Background Mix
- `8`: View Transmittance
- `9`: Glossy Environment Raw
- `10`: SpecCube Raw
- `11`: Outward Surface Normal
- `14`: Scene Depth (near is white)
- `15`: Scene Normal (camera normal texture)
- `16`: SSR Hit Mask (red = depth hit)
- `17`: SSR Fetch Color (scene color sampled at hit UV)
- `18`: SSR Fade Factor (edge/thickness/backface attenuation)

## SSR implementation summary

SSR is evaluated only when a liquid surface hit exists. For each water reflection ray:

1. March along reflected world-space direction.
2. Reproject marched points into screen UV.
3. Compare marched ray distance vs scene distance reconstructed from camera depth.
4. Validate hit using thickness tolerance and backface check against scene normals.
5. Sample `_CameraOpaqueTexture` at hit UV for SSR color.
6. Blend SSR with environment reflection (fallback) using SSR fade and `_SSRStrength`.

This keeps reflections physically plausible for visible on-screen geometry while preserving stable fallback for misses/off-screen rays.
