import particle;

[[vk::push_constant]]
ConstantBuffer<SimParams> params;

[[vk::binding(2, 0)]]
RWStructuredBuffer<uint> hashKeys;

[[vk::binding(3, 0)]]
RWStructuredBuffer<uint> indices;

// One compare-and-swap step of bitonic sort.
// params.bitonicK = stage size (power of 2), params.bitonicJ = step size (power of 2).
// Called log2(n)*(log2(n)+1)/2 times per frame with different k,j values.
[shader("compute")]
[numthreads(256, 1, 1)]
void computeMain(uint3 threadId: SV_DispatchThreadID)
{
   uint i = threadId.x;
   if (i >= params.particleCount)
      return;

   uint k = params.bitonicK;
   uint j = params.bitonicJ;

   uint partner = i ^ j;
   if (partner <= i || partner >= params.particleCount)
      return;

   uint keyI = hashKeys[i];
   uint keyP = hashKeys[partner];

   // Ascending if bit k is 0 in index i
   bool ascending = ((i & k) == 0);

   if ((keyI > keyP) == ascending)
   {
      hashKeys[i] = keyP;
      hashKeys[partner] = keyI;

      uint idxI = indices[i];
      uint idxP = indices[partner];
      indices[i] = idxP;
      indices[partner] = idxI;
   }
}
