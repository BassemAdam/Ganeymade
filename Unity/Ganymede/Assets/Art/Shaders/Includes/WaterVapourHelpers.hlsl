#ifndef WATER_VAPOUR_HELPERS_INCLUDED
#define WATER_VAPOUR_HELPERS_INCLUDED

// =============================================================================
//  WaterVapourHelpers.hlsl
//  All pure-function helpers for the WaterVapour shader.
//  The main shader only calls these — no math logic lives there.
// =============================================================================

// -----------------------------------------------------------------------------
//  SECTION 1 — NOISE
// -----------------------------------------------------------------------------

// Hash3D
//   Maps any 3D world-space point to a pseudo-random float in [0, 1].
//   Uses a cheap dot-product scramble followed by frac — fast on GPU,
//   no texture lookups required.
//   Input  : p — any world-space position
//   Output : pseudo-random scalar in [0, 1]
float Hash3D(float3 p)
{
    // Scatter the input across three prime-ish frequencies to break
    // all axis-aligned patterns, then mix x+y and z together.
    p = frac(p * float3(443.897, 441.423, 437.195));
    p += dot(p, p.yzx + 19.19);
    return frac((p.x + p.y) * p.z);
}

// ValueNoise3D
//   Smooth value noise — hashes the 8 corners of the unit cube that
//   surrounds p and trilinearly interpolates with a quintic ease curve
//   so derivatives are continuous (no visible grid lines).
//   Input  : p — world-space sample position (scale before calling)
//   Output : smooth noise in [0, 1]
float ValueNoise3D(float3 p)
{
    float3 i = floor(p);          // integer cell origin
    float3 f = frac(p);           // position within cell [0,1]^3

    // Quintic ease curve: 6t^5 - 15t^4 + 10t^3
    // Gives zero first AND second derivative at cell boundaries —
    // result looks much smoother than plain lerp.
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);

    // Hash all 8 corners of the unit cube
    float c000 = Hash3D(i + float3(0,0,0));
    float c100 = Hash3D(i + float3(1,0,0));
    float c010 = Hash3D(i + float3(0,1,0));
    float c110 = Hash3D(i + float3(1,1,0));
    float c001 = Hash3D(i + float3(0,0,1));
    float c101 = Hash3D(i + float3(1,0,1));
    float c011 = Hash3D(i + float3(0,1,1));
    float c111 = Hash3D(i + float3(1,1,1));

    // Trilinear interpolation: lerp along X, then Y, then Z
    float x0 = lerp(c000, c100, u.x);
    float x1 = lerp(c010, c110, u.x);
    float x2 = lerp(c001, c101, u.x);
    float x3 = lerp(c011, c111, u.x);

    float y0 = lerp(x0, x1, u.y);
    float y1 = lerp(x2, x3, u.y);

    return lerp(y0, y1, u.z);
}

// FBM  (Fractal Brownian Motion)
//   Stacks multiple octaves of ValueNoise3D at increasing frequencies
//   (lacunarity) and decreasing amplitudes (gain) to produce natural,
//   detail-rich shapes — large billows from low octaves, fine wisps
//   from high octaves.
//   Result is normalized to [0, 1] regardless of octave count.
//   Input  : p          — world-space sample position
//            octaves    — number of noise layers  (1=smooth blob, 6=wispy)
//            lacunarity — frequency multiplier per octave  (typically 2.0)
//            gain       — amplitude multiplier per octave  (typically 0.5)
//   Output : normalized noise in [0, 1]
float FBM(float3 p, int octaves, float lacunarity, float gain)
{
    float value     = 0.0;
    float amplitude = 0.5;   // first octave contributes 50% of the total
    float frequency = 1.0;
    float maxValue  = 0.0;   // track sum of amplitudes for normalization

    for (int i = 0; i < octaves; i++)
    {
        value    += amplitude * ValueNoise3D(p * frequency);
        maxValue += amplitude;
        amplitude *= gain;       // each octave is quieter
        frequency *= lacunarity; // each octave is finer
    }

    // Divide by maxValue so result stays in [0, 1] no matter the octave count
    return value / maxValue;
}

// -----------------------------------------------------------------------------
//  SECTION 2 — DENSITY FIELD
// -----------------------------------------------------------------------------

// SampleDensity
//   Full density pipeline in one call:
//     1. Drift  — scroll world position along driftDir over time (vapor rising/moving)
//     2. Warp   — domain warping displaces the sample position using cheap single-octave
//                 noise, breaking any grid regularity and creating organic turbulence
//     3. FBM    — sample the warped, drifted position for detailed wispy structure
//     4. Shape  — remap and apply a power curve (>1 sharpens wisps, <1 softens them)
//     5. Bridge — blend between pure noise and physics-scaled result
//
//   Input  : worldPos       — world-space position to evaluate
//            time           — _Time.y from the shader
//            driftDir       — direction the vapor scrolls (e.g. float3(0,1,0) = rising)
//            driftSpeed     — scroll speed in world units per second
//            noiseScale     — divides worldPos before noise — larger = bigger blobs
//            octaves        — FBM octave count
//            densityPower   — power curve exponent applied after FBM
//            physicsDensity — scalar [0,1] from physics engine (1.0 = full vapor)
//            physicsBlend   — 0 = pure noise preview, 1 = physics modulates noise
//   Output : final density in [0, 1]
float SampleDensity(float3 worldPos, float time,
                    float3 driftDir,  float driftSpeed,
                    float  noiseScale, int octaves,
                    float  densityPower,
                    float  physicsDensity, float physicsBlend)
{
    // --- 1. Drift -----------------------------------------------------------
    // Offset the sample position along driftDir at driftSpeed units/sec.
    // This makes the entire noise field scroll smoothly — vapor rises, drifts.
    float3 driftedPos = worldPos + driftDir * (time * driftSpeed);

    // Scale into noise space. Larger noiseScale = zoomed-in noise = bigger features.
    float3 p = driftedPos / noiseScale;

    // --- 2. Domain Warp (Turbulence) ----------------------------------------
    // Sample three cheap single-octave noise values at offset seeds to get
    // a 3D displacement vector. Add it to p before the main FBM sample.
    // This bends the noise field on itself — creating swirling, organic turbulence
    // without any explicit simulation. Strength is 1/3 of noiseScale so it
    // distorts features but doesn’t completely destroy them.
    float3 warpOffset = float3(
        ValueNoise3D(p * 0.7 + float3(1.72, 9.23, 5.41)),
        ValueNoise3D(p * 0.7 + float3(8.31, 2.84, 3.26)),
        ValueNoise3D(p * 0.7 + float3(4.17, 6.73, 1.92))
    ) * 2.0 - 1.0;  // remap [0,1] -> [-1,1] so warp pushes in all directions

    float3 warpedP = p + warpOffset * 0.35;

    // --- 3. FBM -------------------------------------------------------------
    // Sample the fractal noise at the warped position.
    // lacunarity=2 (each octave is 2x finer), gain=0.5 (each octave is half as loud).
    float rawNoise = FBM(warpedP, octaves, 2.0, 0.5);

    // --- 4. Power Curve & Remap ---------------------------------------------
    // FBM output already in [0,1]. Apply power to reshape the distribution:
    //   densityPower > 1  → pushes midtones darker, creates sharp wispy edges
    //   densityPower < 1  →  brightens midtones, creates soft puffy clouds
    float shaped = pow(saturate(rawNoise), densityPower);

    // --- 5. Physics Engine Bridge -------------------------------------------
    // When physicsBlend = 0 : result is pure noise (design/preview mode)
    // When physicsBlend = 1 : physicsDensity scales the noise
    //   (e.g. physicsDensity=0 means no vapor — noise fades to black)
    float physicsModulated = shaped * physicsDensity;
    float finalDensity = lerp(shaped, physicsModulated, physicsBlend);

    return saturate(finalDensity);
}

// -----------------------------------------------------------------------------
//  SECTION 3 — VOLUMETRIC LIGHTING  (implemented in Step 6)
// -----------------------------------------------------------------------------

// HenyeyGreenstein
//   Models how a particle scatters light at a given angle.
//   This is the Mie scattering approximation used for vapor and clouds.
//   Input  : cosTheta — dot(viewDir, lightDir)  [-1, 1]
//            g        — anisotropy factor: 0 = isotropic, +1 = full forward scatter
//                        vapor is typically 0.3 – 0.7
//   Output : phase weight — higher means more light reaches the viewer
float HenyeyGreenstein(float cosTheta, float g)
{
    // TODO: Step 6
    return 1.0;
}

// -----------------------------------------------------------------------------
//  SECTION 4 — RAYMARCHING  (implemented in Step 5 + 6 combined)
// -----------------------------------------------------------------------------

// RaymarchVapour
//   Steps a ray through the voxel volume accumulating density and scattered light.
//   Uses Beer-Lambert law for transmittance and Henyey-Greenstein at each step.
//   Input  : rayOrigin      — world-space start of the ray (fragment position)
//            rayDir         — normalized direction from fragment toward camera
//            lightDir       — normalized direction toward the main light
//            lightColor     — main light color
//            marchSteps     — number of steps (quality vs perf tradeoff)
//            marchDistance  — total distance to march (tie to voxel size)
//            g              — Henyey-Greenstein anisotropy
//            absorptionCoeff — how strongly the medium absorbs light (Beer-Lambert)
//            [density field params forwarded to SampleDensity]
//   Output : float4 where .rgb = accumulated in-scattered light color
//                          .a   = accumulated opacity  (1 - transmittance)
float4 RaymarchVapour(float3 rayOrigin, float3 rayDir,
                      float3 lightDir,  half3 lightColor,
                      int    marchSteps, float marchDistance,
                      float  g, float absorptionCoeff,
                      // density field params
                      float time,
                      float3 driftDir,   float driftSpeed,
                      float  noiseScale, int octaves,
                      float  densityPower,
                      float  physicsDensity, float physicsBlend)
{
    // TODO: Steps 5 & 6
    return float4(0, 0, 0, 0);
}

// -----------------------------------------------------------------------------
//  SECTION 5 — FRESNEL  (implemented in Step 7)
// -----------------------------------------------------------------------------

// FresnelEdge
//   Approximates Schlick Fresnel using the density gradient as the surface normal.
//   Returns a value that is HIGH at silhouette edges and LOW face-on.
//   Used to brighten and soften vapor edges.
//   Input  : viewDir  — normalized world-space view direction
//            normal   — surface-like normal (estimated from density gradient)
//            power    — exponent controlling how tight the edge glow is
//   Output : Fresnel weight in [0, 1]
float FresnelEdge(float3 viewDir, float3 normal, float power)
{
    // TODO: Step 7
    return 0.0;
}

#endif // WATER_VAPOUR_HELPERS_INCLUDED
