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
    bool insideHeatSource = false;
    for (int s = 0; s < 16; s++)
    {
        if (heatSources[s].active == 0u) continue;
        float3 diff = p.position - float3(heatSources[s].position[0], heatSources[s].position[1], heatSources[s].position[2]);
        if (dot(diff, diff) <= heatSources[s].radius * heatSources[s].radius)
        {
            p.temperature = heatSources[s].temperature;
            insideHeatSource = true;
            break;
        }
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

    if (p.phase == 0 && p.temperature + deltaT > params.boilingTemperature)
    {
        // Surface factor: only surface particles accumulate latent heat
        // 0 above = full rate, 1 = reduced, 2+ = blocked (no pre-charging)
        float surfaceFactor = max(1.0 - float(liquidAbove) * 0.5, 0.0);

        // Liquid at or above boiling: excess energy goes into latent heat accumulator
        float excess = (p.temperature + deltaT) - params.boilingTemperature;
        p.temperature = params.boilingTemperature;
        p.latentHeatAccum += excess * surfaceFactor;
        if (p.latentHeatAccum >= params.latentHeat)
        {
            p.phase = 1; // transition to gas
            p.latentHeatAccum = params.latentHeat; // cap at max
        }
    }
    else if (p.phase == 1)
    {
        // Apply temperature change if not held by a heat source
        if (!insideHeatSource)
            p.temperature += deltaT;

        // Drain latent heat when at or below boiling (condensation)
        if (p.temperature <= params.boilingTemperature)
        {
            float coolingDrain = params.coolingRate
                               * (params.boilingTemperature - params.ambientTemperature)
                               * params.dt
                               * randScale; // per-particle variation
            // For cold heat sources, the large temperature gap drains faster
            float deficit = params.boilingTemperature - p.temperature;
            p.latentHeatAccum -= max(coolingDrain, deficit);
            p.temperature = params.boilingTemperature;
        }

        if (p.latentHeatAccum <= 0.0)
        {
            p.phase = 0; // condense back to liquid
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