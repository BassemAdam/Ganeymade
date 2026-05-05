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

   // Dormant particles skip all SPH computation.
   if (p.phase == -1)
   {
       particlesOut[i] = p;
       return;
   }

   // Boundary particles (phase=2): fixed density, zero pressure, never changes.
   if (p.phase == 2)
   {
       p.density = params.restDensity;
       p.pressure = 0.0;
       particlesOut[i] = p;
       return;
   }
   float h = params.smoothingRadius;
   float h2 = h * h;
   uint n = params.particleCount;

   int3 myCell = getCellCoord(p.position, params.gridOrigin, h);

   float densitySum = 0.0;
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
         // Skip boundary particles — they don't contribute to fluid density
         if (particlesIn[j].phase == 2) { idx++; continue; }
         float3 diff = p.position - particlesIn[j].position;
         float r2 = dot(diff, diff);
         if (r2 < h2)
         {
            float w = h2 - r2;
            densitySum += w * w * w;
         }
         idx++;
      }
   }
   p.density = params.particleMass * params.poly6Const * densitySum;
   p.density = max(p.density, params.restDensity * 0.01);

   // Phase-dependent rest density: gas uses much lower rest density → high expansion pressure
   float rd = (p.phase == 1) ? params.gasRestDensity : params.restDensity;
   p.pressure = params.gasStiffness * (p.density - rd);
   // Liquid: allow negative pressure scaled by tensileK for cohesive attraction.
   // tensileK=0 clamps to zero (old behavior). tensileK=1 allows full negative pressure.
   if (p.phase != 1)
       p.pressure = max(p.pressure, p.pressure * params.tensileK);

   particlesOut[i] = p;
}
