import particle;

[[vk::push_constant]]
ConstantBuffer<SimParams> params;

[[vk::binding(0, 0)]]
StructuredBuffer<Particle> particlesIn;
[[vk::binding(1, 0)]]
RWStructuredBuffer<Particle> particlesOut;
[[vk::binding(3, 0)]]
RWStructuredBuffer<uint> indices;

// Reorder particles so memory order matches sorted (hash) order.
// After this pass, particlesOut[i] = particlesIn[indices[i]] and indices[i] = i.
// This makes subsequent neighbor lookups (particlesIn[indices[idx]]) sequential
// instead of random, dramatically improving GPU cache coherence.
[shader("compute")]
[numthreads(256, 1, 1)]
void computeMain(uint3 threadId: SV_DispatchThreadID)
{
   uint i = threadId.x;
   if (i >= params.particleCount)
      return;

   particlesOut[i] = particlesIn[indices[i]];
   indices[i] = i;
}
