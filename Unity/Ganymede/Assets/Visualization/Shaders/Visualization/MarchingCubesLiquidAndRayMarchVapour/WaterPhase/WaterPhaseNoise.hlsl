#ifndef WATER_PHASE_NOISE_INCLUDED
    #define WATER_PHASE_NOISE_INCLUDED

    // Physics density grid (GPU) — set at runtime by PhysicsWaterPhaseBridge.
    // Always declared unconditionally — no keyword guard needed.
    // R = liquid density, G = vapour density (both normalised 0..1).
    Texture3D<float2> _PhysicsDensityGrid;
    SamplerState sampler_PhysicsDensityGrid;
    float4 _PhysicsBoundsMinWS;
    float4 _PhysicsBoundsMaxWS;
    float4 _PhysicsVolumeDims;
    // Set to 1 by the vapour-only box renderer (MC mode). 0 = liquid (R), 1 = vapour (G).
    float _PhysicsUseVapourChannel;

    float SamplePhysicsDensityGrid(float3 worldPos)
    {
        int3 dims = (int3)_PhysicsVolumeDims.xyz;

        float3 minWS = _PhysicsBoundsMinWS.xyz;
        float3 maxWS = _PhysicsBoundsMaxWS.xyz;
        float3 sizeWS = maxWS - minWS;

        float3 uvw = (worldPos - minWS) / max(sizeWS, 1e-5);
        if (uvw.x < 0.0 || uvw.y < 0.0 || uvw.z < 0.0 || uvw.x > 1.0 || uvw.y > 1.0 || uvw.z > 1.0)
            return 0.0;

        float2 rg = _PhysicsDensityGrid.SampleLevel(sampler_PhysicsDensityGrid, uvw, 0);
        return (_PhysicsUseVapourChannel > 0.5) ? rg.g : rg.r;
    }

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
    float physicsDensity, float physicsBlend,
    float noiseDetailStrength = 0.0)
    {
        int3 dims = (int3)_PhysicsVolumeDims.xyz;
        bool gridValid = (dims.x > 1 && dims.y > 1 && dims.z > 1);

        // Fast path: fully physics-driven density.
        if (physicsBlend >= 0.999 && gridValid)
        {
            float physicsGridDensity = SamplePhysicsDensityGrid(worldPos);
            float base = physicsGridDensity * physicsDensity;

            // Noise detail: FBM shape variation gated strictly by physics presence.
            // When physics density is zero, zero is returned — noise never bleeds into empty space.
            if (noiseDetailStrength > 0.0001 && base > 0.0001)
            {
                float3 driftedPos = worldPos + driftDir * (time * driftSpeed);
                float3 p = driftedPos / max(noiseScale, 1e-5);
                float3 warpOffset = float3(
                    ValueNoise3D(p * 0.7 + float3(1.72, 9.23, 5.41)),
                    ValueNoise3D(p * 0.7 + float3(8.31, 2.84, 3.26)),
                    ValueNoise3D(p * 0.7 + float3(4.17, 6.73, 1.92))
                ) * 2.0 - 1.0;
                float3 warpedP = p + warpOffset * 0.35;
                float rawNoise = FBM(warpedP, octaves, 2.0, 0.5);
                // Centred noise [-0.5, 0.5] multiplied by physics density: sculpts only
                // where physics says there is vapour, never creates phantom density.
                base = base + base * (rawNoise - 0.5) * noiseDetailStrength;
            }
            return saturate(base);
        }

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

        // No physics contribution.
        if (physicsBlend <= 0.0001)
            return saturate(noiseDensity);

        // If the physics grid isn't configured, fall back to noise modulated by physics scalar.
        if (!gridValid)
        {
            float physicsModulated = noiseDensity * physicsDensity;
            return saturate(lerp(noiseDensity, physicsModulated, physicsBlend));
        }

        float physicsGridDensity = SamplePhysicsDensityGrid(worldPos);
        float physicsFieldDensity = physicsGridDensity * physicsDensity;
        return saturate(lerp(noiseDensity, physicsFieldDensity, physicsBlend));
    }

#endif
