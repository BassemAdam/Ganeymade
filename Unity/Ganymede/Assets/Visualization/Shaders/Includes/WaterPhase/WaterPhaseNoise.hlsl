#ifndef WATER_PHASE_NOISE_INCLUDED
    #define WATER_PHASE_NOISE_INCLUDED

    #if defined(_PHYSICS_DENSITY_GRID)
        // ───────────────────────────────────────────────────────────────
        // Physics density grid (GPU) — set at runtime by PhysicsWaterPhaseBridge
        //
        // _PhysicsDensityGrid: fixed-point uint density values in a 3D grid
        // _PhysicsBoundsMinWS/_MaxWS: world-space simulation bounds
        // _PhysicsVolumeDims.xyz: grid dimensions (ints)
        // _PhysicsVolumeDims.w: invScale to convert fixed-point sum to [0..1]
        //
        // NOTE: This path is keyword-gated to avoid forcing extra Vulkan
        //       bindings for noise-only materials.
        StructuredBuffer<uint> _PhysicsDensityGrid;
        float4 _PhysicsBoundsMinWS;
        float4 _PhysicsBoundsMaxWS;
        float4 _PhysicsVolumeDims;

        int PhysicsGridIndex(int3 v, int3 dims)
        {
            return v.x + dims.x * (v.y + dims.y * v.z);
        }

        float SamplePhysicsDensityGrid(float3 worldPos)
        {
            int3 dims = (int3)_PhysicsVolumeDims.xyz;
            float invScale = _PhysicsVolumeDims.w;

            float3 minWS = _PhysicsBoundsMinWS.xyz;
            float3 maxWS = _PhysicsBoundsMaxWS.xyz;
            float3 sizeWS = maxWS - minWS;

            float3 uvw = (worldPos - minWS) / max(sizeWS, 1e-5);
            if (uvw.x < 0.0 || uvw.y < 0.0 || uvw.z < 0.0 || uvw.x > 1.0 || uvw.y > 1.0 || uvw.z > 1.0)
                return 0.0;

            float3 gridPos = uvw * (float3)(dims - 1);
            int3 base = (int3)floor(gridPos);
            float3 f = gridPos - base;
            int3 base1 = min(base + 1, dims - 1);

            // Fetch 8 corners and manually trilinear-interpolate.
            float d000 = (float)_PhysicsDensityGrid[PhysicsGridIndex(int3(base.x,  base.y,  base.z),  dims)] * invScale;
            float d100 = (float)_PhysicsDensityGrid[PhysicsGridIndex(int3(base1.x, base.y,  base.z),  dims)] * invScale;
            float d010 = (float)_PhysicsDensityGrid[PhysicsGridIndex(int3(base.x,  base1.y, base.z),  dims)] * invScale;
            float d110 = (float)_PhysicsDensityGrid[PhysicsGridIndex(int3(base1.x, base1.y, base.z),  dims)] * invScale;
            float d001 = (float)_PhysicsDensityGrid[PhysicsGridIndex(int3(base.x,  base.y,  base1.z), dims)] * invScale;
            float d101 = (float)_PhysicsDensityGrid[PhysicsGridIndex(int3(base1.x, base.y,  base1.z), dims)] * invScale;
            float d011 = (float)_PhysicsDensityGrid[PhysicsGridIndex(int3(base.x,  base1.y, base1.z), dims)] * invScale;
            float d111 = (float)_PhysicsDensityGrid[PhysicsGridIndex(int3(base1.x, base1.y, base1.z), dims)] * invScale;

            float d00 = lerp(d000, d100, f.x);
            float d10 = lerp(d010, d110, f.x);
            float d01 = lerp(d001, d101, f.x);
            float d11 = lerp(d011, d111, f.x);
            float d0  = lerp(d00, d10, f.y);
            float d1  = lerp(d01, d11, f.y);

            return saturate(lerp(d0, d1, f.z));
        }
    #else
        float SamplePhysicsDensityGrid(float3 worldPos) { return 0.0; }
    #endif

    float Hash3D(float3 p)
    {
        p = frac(p * float3(443.897, 441.423, 437.195));
        p += dot(p, p.yzx + 19.19);
        return frac((p.x + p.y) * p.z);
    }

    float ValueNoise3D(float3 p)
    {
        float3 i = floor(p);
        float3 f = frac(p);
        float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);

        float c000 = Hash3D(i + float3(0, 0, 0));
        float c100 = Hash3D(i + float3(1, 0, 0));
        float c010 = Hash3D(i + float3(0, 1, 0));
        float c110 = Hash3D(i + float3(1, 1, 0));
        float c001 = Hash3D(i + float3(0, 0, 1));
        float c101 = Hash3D(i + float3(1, 0, 1));
        float c011 = Hash3D(i + float3(0, 1, 1));
        float c111 = Hash3D(i + float3(1, 1, 1));

        float x0 = lerp(c000, c100, u.x);
        float x1 = lerp(c010, c110, u.x);
        float x2 = lerp(c001, c101, u.x);
        float x3 = lerp(c011, c111, u.x);

        float y0 = lerp(x0, x1, u.y);
        float y1 = lerp(x2, x3, u.y);

        return lerp(y0, y1, u.z);
    }

    float FBM(float3 p, int octaves, float lacunarity, float gain)
    {
        float value = 0.0;
        float amplitude = 0.5;
        float frequency = 1.0;
        float maxValue = 0.0;

        for (int i = 0; i < octaves; i++)
        {
            value += amplitude * ValueNoise3D(p * frequency);
            maxValue += amplitude;
            amplitude *= gain;
            frequency *= lacunarity;
        }

        return value / max(maxValue, 1e-5);
    }

    float SampleDensity(float3 worldPos, float time,
    float3 driftDir, float driftSpeed,
    float noiseScale, int octaves,
    float densityPower,
    float physicsDensity, float physicsBlend)
    {
        #if defined(_PHYSICS_DENSITY_GRID)
            int3 dims = (int3)_PhysicsVolumeDims.xyz;
            float invScale = _PhysicsVolumeDims.w;
            bool gridValid = (dims.x > 1 && dims.y > 1 && dims.z > 1 && invScale > 0.0);

            // Fast path: fully physics-driven density.
            if (physicsBlend >= 0.999 && gridValid)
            {
                float physicsGridDensity = SamplePhysicsDensityGrid(worldPos);
                return saturate(physicsGridDensity * physicsDensity);
            }
        #endif

        // Procedural noise density (shared for legacy mode and blend mode).
        float3 driftedPos = worldPos + driftDir * (time * driftSpeed);
        float3 p = driftedPos / max(noiseScale, 1e-5);

        float3 warpOffset = float3(
        ValueNoise3D(p * 0.7 + float3(1.72, 9.23, 5.41)),
        ValueNoise3D(p * 0.7 + float3(8.31, 2.84, 3.26)),
        ValueNoise3D(p * 0.7 + float3(4.17, 6.73, 1.92))
        ) * 2.0 - 1.0;

        float3 warpedP = p + warpOffset * 0.35;
        float rawNoise = FBM(warpedP, octaves, 2.0, 0.5);
        float noiseDensity = pow(saturate(rawNoise), densityPower);

        #if defined(_PHYSICS_DENSITY_GRID)
            // No physics contribution.
            if (physicsBlend <= 0.0001)
                return saturate(noiseDensity);

            // If the physics grid isn't configured, fall back to legacy behaviour
            // (noise density modulated by the "physics" scalar).
            if (!gridValid)
            {
                float physicsModulated = noiseDensity * physicsDensity;
                return saturate(lerp(noiseDensity, physicsModulated, physicsBlend));
            }

            float physicsGridDensity = SamplePhysicsDensityGrid(worldPos);
            float physicsFieldDensity = physicsGridDensity * physicsDensity;
            return saturate(lerp(noiseDensity, physicsFieldDensity, physicsBlend));
        #else
            // Legacy behaviour: "physics" is a scalar that modulates the procedural density.
            float physicsModulated = noiseDensity * physicsDensity;
            return saturate(lerp(noiseDensity, physicsModulated, physicsBlend));
        #endif
    }

#endif
