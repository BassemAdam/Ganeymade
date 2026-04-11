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
StructuredBuffer<float> sdfGrid;
[[vk::binding(6, 0)]]
StructuredBuffer<DrainZone> drains;

// ── SDF sampling helpers ────────────────────────────────────────────────
// Buffer layout: [0]=originX [1]=originY [2]=originZ [3]=pad, then SDF data at index 4+

static const int SDF_HEADER = 4; // floats before actual SDF data

float sampleSDF(float3 worldPos)
{
    float3 origin = float3(sdfGrid[0], sdfGrid[1], sdfGrid[2]);
    float3 local = (worldPos - origin) / params.sdfVoxelSize;
    int3 dims = int3(params.sdfDims);

    // Floor index and fractional part for trilinear interpolation
    int3 i0 = int3(floor(local));
    float3 f = local - float3(i0);

    // Clamp to valid range
    int3 i1 = i0 + int3(1, 1, 1);
    i0 = clamp(i0, int3(0, 0, 0), dims - int3(1, 1, 1));
    i1 = clamp(i1, int3(0, 0, 0), dims - int3(1, 1, 1));

    int dX = 1;
    int dY = dims.x;
    int dZ = dims.x * dims.y;

    // 8 corner samples (offset by SDF_HEADER)
    float c000 = sdfGrid[SDF_HEADER + i0.z * dZ + i0.y * dY + i0.x * dX];
    float c100 = sdfGrid[SDF_HEADER + i0.z * dZ + i0.y * dY + i1.x * dX];
    float c010 = sdfGrid[SDF_HEADER + i0.z * dZ + i1.y * dY + i0.x * dX];
    float c110 = sdfGrid[SDF_HEADER + i0.z * dZ + i1.y * dY + i1.x * dX];
    float c001 = sdfGrid[SDF_HEADER + i1.z * dZ + i0.y * dY + i0.x * dX];
    float c101 = sdfGrid[SDF_HEADER + i1.z * dZ + i0.y * dY + i1.x * dX];
    float c011 = sdfGrid[SDF_HEADER + i1.z * dZ + i1.y * dY + i0.x * dX];
    float c111 = sdfGrid[SDF_HEADER + i1.z * dZ + i1.y * dY + i1.x * dX];

    // Trilinear blend
    float c00 = lerp(c000, c100, f.x);
    float c10 = lerp(c010, c110, f.x);
    float c01 = lerp(c001, c101, f.x);
    float c11 = lerp(c011, c111, f.x);
    float c0 = lerp(c00, c10, f.y);
    float c1 = lerp(c01, c11, f.y);
    return lerp(c0, c1, f.z);
}

float3 sdfGradient(float3 worldPos)
{
    float eps = params.sdfVoxelSize;
    float3 grad = float3(
        sampleSDF(worldPos + float3(eps, 0, 0)) - sampleSDF(worldPos - float3(eps, 0, 0)),
        sampleSDF(worldPos + float3(0, eps, 0)) - sampleSDF(worldPos - float3(0, eps, 0)),
        sampleSDF(worldPos + float3(0, 0, eps)) - sampleSDF(worldPos - float3(0, 0, eps))
    );
    float len = length(grad);
    return len > 1e-8 ? grad / len : float3(0, 1, 0);
}

// ── Main compute kernel ─────────────────────────────────────────────────

[shader("compute")]
[numthreads(256, 1, 1)]
void computeMain(uint3 threadId: SV_DispatchThreadID)
{
   uint i = threadId.x;
   if (i >= params.particleCount)
      return;

   Particle p = particlesIn[i];
   float h = params.smoothingRadius;
   float h2 = h * h;
   uint n = params.particleCount;

   int3 myCell = getCellCoord(p.position, params.gridOrigin, h);

   float3 fPressure = float3(0.0);
   float3 fViscosity = float3(0.0);

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
               float r = sqrt(r2);
               float3 dir = diff / r;

               float hr = h - r;
               float3 spikyGrad = params.spikyGradConst * (hr * hr) * dir;
               fPressure += -params.particleMass * (p.pressure + pj.pressure) / (2.0 * pj.density) * spikyGrad;

               float viscLap = params.viscLapConst * hr;
               fViscosity += params.particleMass * params.viscosity * (pj.velocity - p.velocity) / pj.density * viscLap;
            }
         }
         idx++;
      }
   }

   float3 sphAccel = (fPressure + fViscosity) / p.density;
   p.acceleration = sphAccel;

   // Interaction force: attract or repulse particles toward/away from a point
   float3 interactAccel = float3(0.0);
   if (params.interactionStrength != 0.0)
   {
      float3 toTarget = params.interactionPos - p.position;
      float dist = length(toTarget);
      if (dist < params.interactionRadius && dist > 1e-6)
      {
         float3 dir = toTarget / dist;
         float falloff = 1.0 - (dist / params.interactionRadius);
         interactAccel = dir * params.interactionStrength * falloff;
      }
   }

   p.velocity += (sphAccel + params.gravity + interactAccel) * params.dt;
   p.velocity *= params.damping;
   p.position += p.velocity * params.dt;

   // Velocity magnitude clamp — prevents explosions from density spikes
   float maxVel = 30.0;
   float vel2 = dot(p.velocity, p.velocity);
   if (vel2 > maxVel * maxVel)
      p.velocity *= maxVel / sqrt(vel2);

   // ── SDF collision: push out of solids along gradient normal ──────
   if (params.sdfEnabled != 0 && params.sdfVoxelSize > 0.0)
   {
      float dist = sampleSDF(p.position);
      if (dist < 0.0)
      {
         float3 normal = sdfGradient(p.position);
         p.position -= normal * dist;  // push to surface
         float vn = dot(p.velocity, normal);
         if (vn < 0.0)
            p.velocity -= (1.0 + 0.05) * vn * normal;  // reflect with slight damping
      }
   }

   // ── Drain zones: deactivate particles inside any active drain ────
   for (uint d = 0; d < 16; d++)
   {
      if (drains[d].active == 0) continue;
      float3 drainPos = float3(drains[d].position[0], drains[d].position[1], drains[d].position[2]);
      float drainDist = length(p.position - drainPos);
      if (drainDist < drains[d].radius)
      {
         p.position = float3(1e10, 1e10, 1e10);
         p.velocity = float3(0, 0, 0);
         p.phase = -1;
         break;
      }
   }

   // Boundary collision: clamp position and zero out velocity component.
   // Zero restitution is appropriate for fluid against walls — prevents
   // chain-reaction bounces when many particles pile at a boundary.
   if (p.position.x < params.boundsMin.x) { p.position.x = params.boundsMin.x; if (p.velocity.x < 0) p.velocity.x = 0; }
   if (p.position.y < params.boundsMin.y) { p.position.y = params.boundsMin.y; if (p.velocity.y < 0) p.velocity.y = 0; }
   if (p.position.z < params.boundsMin.z) { p.position.z = params.boundsMin.z; if (p.velocity.z < 0) p.velocity.z = 0; }
   if (p.position.x > params.boundsMax.x) { p.position.x = params.boundsMax.x; if (p.velocity.x > 0) p.velocity.x = 0; }
   if (p.position.y > params.boundsMax.y) { p.position.y = params.boundsMax.y; if (p.velocity.y > 0) p.velocity.y = 0; }
   if (p.position.z > params.boundsMax.z) { p.position.z = params.boundsMax.z; if (p.velocity.z > 0) p.velocity.z = 0; }

   particlesOut[i] = p;
}
