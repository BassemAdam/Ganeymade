import particle;

[[vk::push_constant]]
ConstantBuffer<SimParams> params;

[[vk::binding(0, 0)]]
StructuredBuffer<Particle> particlesIn;

[[vk::binding(2, 0)]]
RWStructuredBuffer<uint> hashKeys;

[[vk::binding(3, 0)]]
RWStructuredBuffer<uint> indices;

[shader("compute")]
[numthreads(256, 1, 1)]
void computeMain(uint3 threadId: SV_DispatchThreadID)
{
   uint i = threadId.x;
   if (i >= params.particleCount)
      return;

   float3 pos = particlesIn[i].position;
   int3 cell = getCellCoord(pos, params.gridOrigin, params.smoothingRadius);
   hashKeys[i] = hashCell(cell, params.particleCount);
   indices[i] = i;
}
