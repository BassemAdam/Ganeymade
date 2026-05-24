import particle;

[[vk::push_constant]]
ConstantBuffer<SimParams> params;

[[vk::binding(2, 0)]]
RWStructuredBuffer<uint> hashKeys;

[[vk::binding(3, 0)]]
RWStructuredBuffer<uint> indices;

// Local bitonic sort: performs ALL steps where j <= BLOCK_SIZE/2 in shared memory.
// params.bitonicK = current stage size. This shader handles steps j = BLOCK_SIZE/2 down to 1.
// Reduces ~8 global dispatches+barriers into 1 dispatch per stage.

static const uint BLOCK_SIZE = 256;

groupshared uint sharedKeys[BLOCK_SIZE];
groupshared uint sharedIdxs[BLOCK_SIZE];

[shader("compute")]
[numthreads(BLOCK_SIZE, 1, 1)]
void computeMain(uint3 threadId: SV_DispatchThreadID, uint3 localId: SV_GroupThreadID)
{
   uint gid = threadId.x;
   uint lid = localId.x;

   // Load into shared memory
   if (gid < params.particleCount)
   {
      sharedKeys[lid] = hashKeys[gid];
      sharedIdxs[lid] = indices[gid];
   }
   else
   {
      sharedKeys[lid] = 0xFFFFFFFF; // sentinel for out-of-bounds
      sharedIdxs[lid] = 0xFFFFFFFF;
   }
   GroupMemoryBarrierWithGroupSync();

   uint k = params.bitonicK;

   // Perform steps j = BLOCK_SIZE/2 down to 1 in shared memory
   for (uint j = BLOCK_SIZE >> 1; j > 0; j >>= 1)
   {
      uint partner = lid ^ j;
      if (partner > lid && partner < BLOCK_SIZE)
      {
         // Direction is based on global index and stage k
         bool ascending = ((gid & k) == 0);
         uint keyI = sharedKeys[lid];
         uint keyP = sharedKeys[partner];

         if ((keyI > keyP) == ascending)
         {
            sharedKeys[lid] = keyP;
            sharedKeys[partner] = keyI;
            uint idxI = sharedIdxs[lid];
            uint idxP = sharedIdxs[partner];
            sharedIdxs[lid] = idxP;
            sharedIdxs[partner] = idxI;
         }
      }
      GroupMemoryBarrierWithGroupSync();
   }

   // Write back to global memory
   if (gid < params.particleCount)
   {
      hashKeys[gid] = sharedKeys[lid];
      indices[gid] = sharedIdxs[lid];
   }
}
