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
   p.density = max(p.density, 0.001);

   p.pressure = params.gasStiffness * (p.density - params.restDensity);
   p.pressure = max(p.pressure, 0.0);

   particlesOut[i] = p;
}
