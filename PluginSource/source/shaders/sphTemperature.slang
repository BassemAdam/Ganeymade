import particle;

[[vk::push_constant]]
ConstantBuffer<SimParams> params;

[[vk::binding(0, 0)]]
StructuredBuffer<Particle> particlesIn;
[[vk::binding(1, 0)]]
RWStructuredBuffer<Particle> particlesOut;
[[vk::binding(2, 0)]]
StructuredBuffer<uint> hashKeys;
[[vk::binding(3, 0)]]
StructuredBuffer<uint> indices;
[[vk::binding(4, 0)]]
StructuredBuffer<uint> startIndices;
[[vk::binding(5, 0)]]
StructuredBuffer<HeatSource> heatSources;

// SPH kernel for heat conduction, same Poly6 weight used for density
float heatKernel(float r2, float h2)
{
    float w = h2 - r2;
    return w * w * w;
}

[shader("compute")]
[numthreads(256, 1, 1)]
void computeMain(uint3 threadId: SV_DispatchThreadID)
{
    uint i = threadId.x;
    if (i >= params.particleCount)
        return;

    Particle p = particlesIn[i];
    float h  = params.smoothingRadius;
    float h2 = h * h;
    uint  n  = params.particleCount;

    int3 myCell = getCellCoord(p.position, params.gridOrigin, h);

    // Accumulate heat exchange with all neighbors
    float dT = 0.0;
    float weightSum = 0.0;

    // Check if this particle is inside any heat source
    // Heat sources act as temperature boundary conditions in both directions:
    // they heat cold particles AND cool hot particles toward the source temperature.
    bool insideHeatSource = false;
    bool nearHeatSource   = false;
    for (int s = 0; s < 16; s++)
    {
        if (heatSources[s].active == 0u) continue;
        float3 hsPos  = float3(heatSources[s].position[0], heatSources[s].position[1], heatSources[s].position[2]);
        float3 diff   = p.position - hsPos;
        float  dist2  = dot(diff, diff);
        float  r2     = heatSources[s].radius * heatSources[s].radius;
        if (dist2 <= r2)
        {
            // Bidirectional: lerp toward source temperature each frame for smooth response
            float rate = clamp(params.thermalDiffusivity * params.dt * 10.0, 0.0, 1.0);
            p.temperature = lerp(p.temperature, heatSources[s].temperature, rate);
            insideHeatSource = true;
            nearHeatSource   = true;
            break;
        }
        if (dist2 <= r2 * 4.0)
            nearHeatSource = true;
    }

    float deltaT = 0.0;
    int liquidAbove = 0; // count liquid neighbors above this particle

    if (!insideHeatSource)
    {
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            uint key = hashCell(myCell + int3(dx, dy, dz), n);
            uint idx = startIndices[key];
            if (idx == 0xFFFFFFFF) continue;

            while (idx < n && hashKeys[idx] == key)
            {
                uint j = indices[idx];
                if (j != i)
                {
                    Particle pj = particlesIn[j];
                    float3 diff = p.position - pj.position;
                    float r2 = dot(diff, diff);

                    if (r2 < h2 && r2 > 1e-12)
                    {
                        float w = heatKernel(r2, h2);
                        dT += w * (pj.temperature - p.temperature) / pj.density;
                        weightSum += w;

                        // Count liquid neighbors above (for surface detection)
                        if (pj.phase == 0 && pj.position.y > p.position.y + h * 0.2)
                            liquidAbove++;
                    }
                }
                idx++;
            }
        }

        // Apply conduction
        float conduction = params.thermalDiffusivity * params.particleMass * params.poly6Const * dT * params.dt;

        // Apply ambient cooling (Newton's law of cooling)
        float cooling = params.coolingRate * (p.temperature - params.ambientTemperature) * params.dt;

        deltaT = conduction - cooling;
    }

    // ── Latent heat model: absorb/release energy at boiling point ──
    // Per-particle random factor for condensation variation (stable per particle index)
    float randHash = frac(sin(float(i) * 12.9898 + 78.233) * 43758.5453);
    float randScale = 0.5 + randHash; // range [0.5, 1.5]

    // Effective boiling point rises with local pressure (Clausius-Clapeyron approximation):
    // deep/high-pressure particles are harder to boil than surface/low-pressure particles.
    float effectiveBoilingTemp = params.boilingTemperature
                               + max(p.pressure, 0.0) * params.pressureBoilingScale;

    if (p.phase == 0 && p.temperature + deltaT > effectiveBoilingTemp)
    {
        if (liquidAbove > 0)
        {
            // Buried particle: clamp at effective boiling, no evaporation.
            p.temperature = effectiveBoilingTemp;
        }
        else
        {
            // Surface particle: stochastic nucleation driven by superheat.
            float superheat = (p.temperature + deltaT) - effectiveBoilingTemp;

            // Nucleation probability scales with superheat relative to boiling temp.
            // A particle far above boiling snaps to gas quickly; one just at the threshold waits.
            float nucleationChance = clamp(superheat / max(effectiveBoilingTemp, 1e-6), 0.0, 1.0)
                                   * params.dt * 10.0;

            // Time-varying per-particle random seed for stochastic nucleation
            float seed = float(i) * 12.9898 + params.dt * float(i % 256u) * 43.758 + 1.618;
            float randNuc = frac(sin(seed) * 43758.5453);

            p.temperature = effectiveBoilingTemp;
            p.latentHeatAccum += superheat;

            // Nucleation fires when: random roll passes AND at least half the latent heat is stored.
            if (randNuc < nucleationChance && p.latentHeatAccum >= params.latentHeat * 0.5)
            {
                p.phase = 1;
                p.latentHeatAccum = params.latentHeat;
            }
            else if (p.latentHeatAccum >= params.latentHeat)
            {
                // Deterministic fallback: always evaporate once fully charged.
                p.phase = 1;
                p.latentHeatAccum = params.latentHeat;
            }
        }
    }
    else if (p.phase == 1)
    {
        // Apply temperature change if not held by a heat source
        if (!insideHeatSource)
            p.temperature += deltaT;

        // Base drain from ambient cooling
        float coolingDrain = params.coolingRate
                           * (effectiveBoilingTemp - params.ambientTemperature)
                           * params.dt
                           * randScale;

        // Cold source accelerates condensation proportional to how far below boiling it pulls the particle
        if (insideHeatSource && p.temperature < effectiveBoilingTemp)
        {
            float coldDelta = effectiveBoilingTemp - p.temperature;
            coolingDrain += coldDelta * params.dt * 5.0; // fast drain when actively chilled
        }

        // Drain latent heat. Only clamp temperature when NOT being actively chilled by a source.
        if (p.temperature <= effectiveBoilingTemp)
        {
            p.latentHeatAccum -= coolingDrain;
            if (!insideHeatSource)
                p.temperature = effectiveBoilingTemp; // hold at boiling during natural condensation
        }

        if (p.latentHeatAccum <= 0.0)
        {
            p.phase = 0;
            p.latentHeatAccum = 0.0;
        }
    }
    else
    {
        p.temperature += deltaT;
    }

    // Clamp to physical bounds
    p.temperature = max(p.temperature, 0.0);

    particlesOut[i] = p;
}