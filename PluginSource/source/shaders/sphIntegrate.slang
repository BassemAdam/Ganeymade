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
   // (e.g., sudden boundary changes packing many particles into one spot)
   float maxVel = 30.0;
   float vel2 = dot(p.velocity, p.velocity);
   if (vel2 > maxVel * maxVel)
      p.velocity *= maxVel / sqrt(vel2);

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
