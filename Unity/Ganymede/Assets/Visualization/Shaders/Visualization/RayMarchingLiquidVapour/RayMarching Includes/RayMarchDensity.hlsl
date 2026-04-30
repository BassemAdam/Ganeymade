#ifndef RAY_MARCH_DENSITY_INCLUDED
#define RAY_MARCH_DENSITY_INCLUDED

// R = liquid density (normalised 0..1), G = vapour density (normalised 0..1).
// Liquid has a sharp iso-surface → surface detection, normals, refraction.
// Vapour is purely volumetric → scattering/absorption only, never enters surface tests.
Texture3D<float2> _PhysicsDensityGrid;
SamplerState      sampler_PhysicsDensityGrid;

float4 _PhysicsVolumeDims;
float4 _PhysicsBoundsMinWS;
float4 _PhysicsBoundsMaxWS;

// World-space position → trilinear UVW inside the physics AABB.
float3 DensityGridUVW(float3 posWS)
{
    float3 sizeWS   = max(_PhysicsBoundsMaxWS.xyz - _PhysicsBoundsMinWS.xyz, 1e-5);
    float3 gridSize = max(_PhysicsVolumeDims.xyz, 1.0);
    return (posWS - _PhysicsBoundsMinWS.xyz) / sizeWS + 0.5 / gridSize;
}

// Raw two-channel sample. Use in the ray loop where both phases are needed.
float2 SampleDensityRG_WS(float3 posWS)
{
    return _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, DensityGridUVW(posWS), 0);
}

// Liquid channel only.
// Use for iso-surface detection, normal gradient estimation, and refraction.
// Vapour must never feed into surface tests — it has no sharp phase boundary.
float SampleLiquidDensityWS(float3 posWS)
{
    return SampleDensityRG_WS(posWS).r;
}

// Vapour channel only. Use for volumetric scattering and absorption accumulation.
float SampleVapourDensityWS(float3 posWS)
{
    return SampleDensityRG_WS(posWS).g;
}

// Pre-baked normal grid written once per frame by BakeNormals kernel (ParticlesToDensityGrid.compute).
// Used as a FALLBACK only — primary surface uses the per-fragment HQ normal below,
// which gives much smoother results because the stencil is set in WORLD SPACE
// (matched to the SPH smoothing radius) instead of voxel space.
Texture3D<float4> _PhysicsNormalGrid;
SamplerState      sampler_PhysicsNormalGrid;

// World-space stencil half-width for the per-fragment liquid normal. Set roughly
// to the SPH smoothing radius (≈ kernel support / 2). Larger = smoother but loses
// fine surface ripples; smaller = more detail but more SPH splat noise.
// Declared here (not in the .shader CBUFFER) because RayMarchDensity.hlsl is
// included BEFORE the CBUFFER block, so the function below would not see it.
float _LiquidNormalStencilWS;

// 12-tap world-space normal: a 6-tap central difference along each axis combined with
// a 6-tap "diagonal" central difference. This is mathematically a wider 3D Sobel
// evaluated entirely on the GPU's hardware-trilinear filter, so the gradient is
// computed on the SMOOTHED density field instead of on raw voxel values.
//
// Why this is what SPH literature calls the "color-field gradient":
//   The discrete SPH color-field gradient is  n_a = Σ_b m_b/ρ_b ∇W_ab.
//   The density grid is exactly that color field discretised at voxel centers via
//   the splat kernel; ∇C of that grid (with a stencil matched to the kernel radius)
//   is the same quantity in continuous form. The benefit of doing it on the grid is
//   we get hardware trilinear interpolation for free, and we don't have to evaluate
//   a neighbor sum per shaded pixel.
//
// Why per-fragment beats the pre-baked grid for the primary surface:
//   - The bake's gradient stencil is fixed at ±1 voxel (or a 3x3 Sobel), so its
//     spatial scale is tied to grid resolution.
//   - The per-fragment stencil is set in METRES, so a 128^3 grid and a 64^3 grid
//     produce identical normal smoothness — and we can match it to the kernel
//     radius regardless of grid size.
//   - We can sample at sub-voxel positions, so the normal is computed AT the
//     refined surface-hit position (not snapped to a voxel centre).
float3 ComputeLiquidNormalHQ(float3 posWS)
{
    float s = max(_LiquidNormalStencilWS, 1e-4);

    // Axis-aligned 6-tap central difference (the classic gradient).
    float dxA = SampleLiquidDensityWS(posWS + float3( s, 0, 0)) - SampleLiquidDensityWS(posWS + float3(-s, 0, 0));
    float dyA = SampleLiquidDensityWS(posWS + float3( 0, s, 0)) - SampleLiquidDensityWS(posWS + float3( 0,-s, 0));
    float dzA = SampleLiquidDensityWS(posWS + float3( 0, 0, s)) - SampleLiquidDensityWS(posWS + float3( 0, 0,-s));

    // Diagonal 6-tap central differences. Each diagonal sample contributes to TWO
    // axes, so this adds 6 more taps but doubles the effective stencil coverage,
    // which low-passes high-frequency density noise without losing surface direction.
    float d = s * 0.7071068;  // 1/sqrt(2): keeps the diagonal stencil at the same world-space radius
    float pxy = SampleLiquidDensityWS(posWS + float3( d, d, 0)) - SampleLiquidDensityWS(posWS + float3(-d,-d, 0));
    float nxy = SampleLiquidDensityWS(posWS + float3( d,-d, 0)) - SampleLiquidDensityWS(posWS + float3(-d, d, 0));
    float pxz = SampleLiquidDensityWS(posWS + float3( d, 0, d)) - SampleLiquidDensityWS(posWS + float3(-d, 0,-d));
    float nxz = SampleLiquidDensityWS(posWS + float3( d, 0,-d)) - SampleLiquidDensityWS(posWS + float3(-d, 0, d));
    float pyz = SampleLiquidDensityWS(posWS + float3( 0, d, d)) - SampleLiquidDensityWS(posWS + float3( 0,-d,-d));
    float nyz = SampleLiquidDensityWS(posWS + float3( 0, d,-d)) - SampleLiquidDensityWS(posWS + float3( 0,-d, d));

    // Recombine the diagonals back onto each axis. Coefficients are chosen so the
    // total weight on each axis matches the axis-aligned tap (no DC bias).
    float dxD = (pxy + nxy + pxz + nxz) * 0.7071068;
    float dyD = (pxy - nxy + pyz + nyz) * 0.7071068;
    float dzD = (pxz - nxz + pyz - nyz) * 0.7071068;

    // 50/50 mix of axis-aligned and diagonal stencils. This is the same recipe as
    // a 3D Sobel kernel, but with the smoothing happening at the trilinear-sample
    // level rather than at the voxel level → no aliasing from the grid.
    float3 grad = 0.5 * float3(dxA + dxD, dyA + dyD, dzA + dzD);

    float len = length(grad);
    // Outward normal points AWAY from increasing density (away from fluid).
    return (len >= 1e-5) ? (-grad / len) : float3(0.0, 0.0, 0.0);
}

// Surface normal selector — uses per-fragment HQ gradient and falls back to the
// pre-baked grid only when the HQ result is below the noise floor (typically the
// far interior of the liquid where ∇C ≈ 0). The fallback prevents the surface
// from disappearing in patches when a primary ray happens to hit the bulk.
float3 GetSurfaceNormalWS(float3 posWS, float3 rayDir)
{
    float3 nHQ = ComputeLiquidNormalHQ(posWS);
    if (dot(nHQ, nHQ) > 1e-6)
        return nHQ;

    // Fallback: pre-baked normal (read with the same half-voxel offset as the density).
    float3 nBaked = _PhysicsNormalGrid.SampleLevel(sampler_PhysicsNormalGrid, DensityGridUVW(posWS), 0).xyz;
    float  len    = length(nBaked);
    return (len >= 1e-4) ? (nBaked / len) : float3(0.0, 0.0, 0.0);
}

#endif
