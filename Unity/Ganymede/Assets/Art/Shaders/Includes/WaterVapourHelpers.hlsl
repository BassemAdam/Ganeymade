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
//  SECTION 3 — VOLUMETRIC LIGHTING
// -----------------------------------------------------------------------------

// HenyeyGreenstein
//   The standard real-time approximation for Mie scattering — the dominant
//   scattering mode for particles the size of water droplets.
//
//   Physics: a particle scatters MORE light toward the direction light came FROM
//   (forward scatter) and LESS in the opposite direction. The parameter g controls
//   how strongly forward-biased this is.
//
//   Formula (unnormalized, 4π omitted since we tune brightness via other props):
//     p(cosθ, g) = (1 - g²) / (1 + g² - 2g·cosθ)^(3/2)
//
//   Results at g = 0.5:
//     cosθ =  1  (ray toward light, vapor backlit)  → ~6.0  (bright halo)
//     cosθ =  0  (perpendicular)                    → ~0.53 (medium)
//     cosθ = -1  (ray away from light)               → ~0.07 (dark)
//
//   Input  : cosTheta — dot(-rayDir, lightDir)  range [-1, 1]
//            g        — anisotropy: 0 = isotropic, 0.3–0.7 = typical vapor
//   Output : phase weight (unitless, use to scale scattered light)
float HenyeyGreenstein(float cosTheta, float g)
{
    float g2    = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    // abs() guards against numerical precision issues near denom ≈ 0
    return (1.0 - g2) / pow(abs(denom), 1.5);
}

// -----------------------------------------------------------------------------
//  SECTION 3b — SDF EDGE FADE HELPER  (needed by RaymarchVapour below)
// -----------------------------------------------------------------------------

// sdBox
//   Signed Distance Field for a box centred at the origin with half-extents b.
//   Returns a NEGATIVE value inside the box whose magnitude is the distance
//   to the nearest face, and a POSITIVE value outside.
//   This is the standard formula used to force density to exactly 0 at bounds.
float sdBox(float3 p, float3 b)
{
    float3 d = abs(p) - b;
    return min(max(d.x, max(d.y, d.z)), 0.0) + length(max(d, 0.0));
}

// ComputeEdgeFade
//   Uses sdBox to compute how far inward from the AABB surface the sample is,
//   then applies smoothstep to produce a [0,1] density multiplier:
//     - Exactly 0.0 at (and beyond) every face — density is mathematically
//       guaranteed to be zero at the bounding box boundary, so no hard edge.
//     - Rises to 1.0 once the sample is 'softness' world units inward.
//
//   Correct formula per the SDF approach:
//     Density_final = Density_noise * smoothstep(0, fadeDistance, -sdBox(p, extents))
//
//   Input  : posOS    — sample position in object space
//            boundsMin, boundsMax — AABB extents in object space
//            softness — inward fade band width in object-space units
//   Output : fade multiplier in [0, 1]
float ComputeEdgeFade(float3 posOS, float3 boundsMin, float3 boundsMax, float softness)
{
    float3 boundsCenter  = (boundsMin + boundsMax) * 0.5;
    float3 boundsExtents = (boundsMax - boundsMin) * 0.5;

    // sdBox is negative inside (distance to nearest wall, inward).
    // Negate it so we get a positive "how far from the wall am I" value.
    float distInward = -sdBox(posOS - boundsCenter, boundsExtents);

    // smoothstep: 0 exactly at the surface, 1 once 'softness' units inward.
    // This guarantees density = 0 at every face regardless of what the noise produces.
    return smoothstep(0.0, max(softness, 1e-5), distInward);
}

// -----------------------------------------------------------------------------
//  SECTION 4 — RAYMARCHING
// -----------------------------------------------------------------------------

// IntersectRayAABBOS
//   Slab intersection in OBJECT space.
//   Returns entry/exit distances along the ray when a hit exists.
bool IntersectRayAABBOS(float3 rayOriginOS, float3 rayDirOS, float3 bmin, float3 bmax, out float tEnter, out float tExit)
{
    // Epsilon-protected reciprocal avoids division-by-zero on axis-aligned rays.
    float3 safeDir = sign(rayDirOS) * max(abs(rayDirOS), 1e-6);
    float3 invDir = 1.0 / safeDir;

    float3 t0 = (bmin - rayOriginOS) * invDir;
    float3 t1 = (bmax - rayOriginOS) * invDir;

    float3 tMin3 = min(t0, t1);
    float3 tMax3 = max(t0, t1);

    tEnter = max(max(tMin3.x, tMin3.y), tMin3.z);
    tExit  = min(min(tMax3.x, tMax3.y), tMax3.z);

    return tExit >= tEnter;
}

// ComputeVoxelRaySegmentWS
//   Computes the valid world-space ray segment through the voxel bounds:
//   entryWS -> exitWS, plus ray direction and segment length.
//   Works whether camera is outside OR inside the volume.
//   marchDistance covers the FULL segment — no artificial cap.
bool ComputeVoxelRaySegmentWS(float3 cameraWS, float3 sampleWS,
                              float3 boundsMinOS, float3 boundsMaxOS,
                              out float3 entryWS, out float3 rayDirWS, out float marchDistance)
{
    float3 viewRayWS = normalize(sampleWS - cameraWS);

    float3 rayOriginOS = TransformWorldToObject(cameraWS);
    float3 rayDirOS = normalize(TransformWorldToObjectDir(viewRayWS));

    float tEnter;
    float tExit;
    if (!IntersectRayAABBOS(rayOriginOS, rayDirOS, boundsMinOS, boundsMaxOS, tEnter, tExit))
    {
        entryWS = 0.0;
        rayDirWS = 0.0;
        marchDistance = 0.0;
        return false;
    }

    // If camera is inside, tEnter < 0. Start marching from camera.
    tEnter = max(tEnter, 0.0);

    float3 entryOS = rayOriginOS + rayDirOS * tEnter;
    float3 exitOS  = rayOriginOS + rayDirOS * tExit;

    entryWS = TransformObjectToWorld(entryOS);
    float3 exitWS = TransformObjectToWorld(exitOS);

    float segmentDistanceWS = distance(entryWS, exitWS);
    if (segmentDistanceWS <= 1e-5)
    {
        rayDirWS = 0.0;
        marchDistance = 0.0;
        return false;
    }

    rayDirWS = normalize(exitWS - entryWS);
    // Cover the full segment through the volume — no cap.
    // Absorption and march steps control the density budget.
    marchDistance = segmentDistanceWS;
    return true;
}

// RaymarchVapour
//   Steps a ray through the voxel volume accumulating density and scattered light.
//
//   How it works:
//     - Ray starts at the fragment (front face of the voxel) and marches INTO the
//       volume away from the camera, one step at a time.
//     - At each step we sample the density field.
//     - Beer-Lambert law: each step attenuates a "transmittance" value, modelling
//       how much light can still pass through remaining medium.
//         transmittance *= exp(-density * absorption * stepSize)
//     - The light scattered toward the viewer at each step is:
//         scatter += transmittance * density * stepSize
//       (HenyeyGreenstein weighting added in Step 6)
//     - Early exit: once transmittance falls below 0.01 the ray contributes
//       less than 1% more — no point continuing.
//
//   Output : .rgb = accumulated scattered light (flat white until Step 6 adds HG)
//            .a   = accumulated opacity = 1 - final transmittance
float4 RaymarchVapour(float3 rayOrigin, float3 rayDir,
                      float3 lightDir,  half3 lightColor,
                      int    marchSteps, float marchDistance,
                      float  g, float absorptionCoeff,
                      float  time,
                      float3 driftDir,   float driftSpeed,
                      float  noiseScale, int octaves,
                      float  densityPower,
                      float  physicsDensity, float physicsBlend,
                      float  sceneLinearDepth,
                      float3 boundsMinOS, float3 boundsMaxOS,
                      float  edgeSoftness,
                      float2 screenUV)
{
    float stepSize    = marchDistance / (float)marchSteps;
    float transmit    = 1.0;
    float3 scatter    = 0.0;

    float cosTheta = dot(-rayDir, lightDir);

    // Precompute ellipsoid center and inverse extents for the radial blob mask.
    float3 boundsCenter  = (boundsMinOS + boundsMaxOS) * 0.5;
    float3 boundsExtents = (boundsMaxOS - boundsMinOS) * 0.5;

    // Interleaved Gradient Noise (IGN) for dithering the start position
    // This perfectly breaks the planar alignment of samples that causes the
    // vapour to look like a solid box when the camera is outside.
    float2 pixelCoords = screenUV * _ScreenParams.xy;
    float jitter = frac(52.9829189 * frac(dot(pixelCoords, float2(0.06711056, 0.00583715))));

    for (int i = 0; i < marchSteps; i++)
    {
        // Use jitter instead of 0.5 to randomly offset the sample plane per-pixel
        float3 samplePos = rayOrigin + rayDir * (stepSize * (i + jitter));

        // Depth termination against opaque scene geometry
        float sampleEyeDepth = -mul(UNITY_MATRIX_V, float4(samplePos, 1.0)).z;
        if (sampleEyeDepth >= sceneLinearDepth)
            break;

        // --- Per-step shape masking (Step 7) ---
        // Transform to object space to measure AABB distance.
        float3 sampleOS = TransformWorldToObject(samplePos);

        // 1. Axis-aligned edge fade: density tapers to 0 near each face.
        //    Removes the hard box cutoff at every AABB boundary.
        float axialFade = ComputeEdgeFade(sampleOS, boundsMinOS, boundsMaxOS, edgeSoftness);

        // 2. Radial ellipsoid mask: density falls off at the corners of the
        //    ellipsoid that fits the box, breaking the rectangular silhouette
        //    and producing an organic, amorphous blob shape.
        float3 normPos    = (sampleOS - boundsCenter) / max(boundsExtents, 1e-6);
        float  radialDist = length(normPos);
        float  radialFade = saturate(1.0 - radialDist);
        radialFade = radialFade * radialFade * (3.0 - 2.0 * radialFade); // smoothstep

        float shapeMask = axialFade * radialFade;

        float density = SampleDensity(
            samplePos, time,
            driftDir, driftSpeed,
            noiseScale, octaves,
            densityPower,
            physicsDensity, physicsBlend
        );

        // Shape mask tapers density to 0 at AABB walls and outside the ellipsoid
        density *= shapeMask;

        if (density > 0.001)
        {
            float absorption   = density * absorptionCoeff * stepSize;
            float stepTransmit = exp(-absorption);

            float phase = HenyeyGreenstein(cosTheta, g);
            scatter += transmit * density * stepSize * phase * lightColor;

            transmit *= stepTransmit;
        }

        if (transmit < 0.01)
            break;
    }

    float opacity = 1.0 - transmit;
    return float4(scatter, opacity);
}

// -----------------------------------------------------------------------------
//  SECTION 5 — FRESNEL & EDGE SOFTNESS  (Step 7)
// -----------------------------------------------------------------------------

// FresnelEdge
//   Schlick Fresnel approximation — returns a weight that is HIGH at silhouette
//   edges (grazing angle) and LOW when looking straight at the vapor.
//
//   Normal: we approximate the vapor "surface" normal as the view ray direction
//   itself, since the volume has no real geometric normal. This makes every
//   fragment respond to how oblique the view angle is relative to incidence.
//
//   Physics: at a grazing angle (dot(viewDir, normal) ≈ 0), light exits the
//   surface boundary more readily — vapor edges appear brighter and denser.
//
//   Input  : viewDir — normalized world-space view direction (toward camera)
//            normal  — surface-like outward normal (use -rayDir from march)
//            power   — exponent: higher = tighter, sharper edge glow (2–8 typical)
//   Output : Fresnel weight in [0, 1]; 0 = face-on, 1 = grazing
float FresnelEdge(float3 viewDir, float3 normal, float power)
{
    // cosTheta = dot of view direction and outward normal.
    // At the silhouette edge this approaches 0, giving Fresnel → 1.
    float cosTheta = saturate(dot(viewDir, normal));
    // Schlick: F = (1 - cosTheta)^power
    return pow(1.0 - cosTheta, power);
}

// ComputeEdgeFade — defined in Section 3b above (must precede RaymarchVapour)

#endif // WATER_VAPOUR_HELPERS_INCLUDED
