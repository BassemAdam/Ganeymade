import particle;

[[vk::push_constant]]
ConstantBuffer<SimParams> params;

[[vk::binding(2, 0)]]
StructuredBuffer<uint> hashKeys;

[[vk::binding(4, 0)]]
RWStructuredBuffer<uint> startIndices;

// Assumes startIndices was cleared to 0xFFFFFFFF by vkCmdFillBuffer before this dispatch.
[shader("compute")]
[numthreads(256, 1, 1)]
void computeMain(uint3 threadId: SV_DispatchThreadID)
{
   uint i = threadId.x;
   if (i >= params.particleCount)
      return;

   uint key = hashKeys[i];

   // First particle or key differs from previous -> this is a bucket start
   if (i == 0 || key != hashKeys[i - 1])
      startIndices[key] = i;
}
