struct SimParams {
    float dt;
    float alpha;         // temperature diffusivity factor
    uint  elementCount;
    uint  gridWidth;
    uint  gridHeight;
    uint  gridDepth;
    uint  _pad0;         // to conform to the 16-byte of vulkan
    uint  _pad1;
};

[[vk::push_constant]] ConstantBuffer<SimParams> params;

StructuredBuffer<float>   inputData;   // binding=0
RWStructuredBuffer<float> outputData;  // binding=1
StructuredBuffer<uint>     maskData;   // binding= 2, where 0 = normal cell and 1 = source cell (temperature doesn't change)

[shader("compute")]
[numthreads(4, 4, 4)]  // 4×4×4 = 64 threads per group
void computeMain(uint3 threadId : SV_DispatchThreadID)
{
    uint x = threadId.x;
    uint y = threadId.y;
    uint z = threadId.z;

    // becuase the grid size may exceed the number of cells
    if (x >= params.gridWidth  || y >= params.gridHeight || z >= params.gridDepth) 
        return;

    // Row-major 3D index: index = z*(height*width) + y*width + x
    uint sliceSize = params.gridWidth * params.gridHeight;
    uint idx = z * sliceSize + y * params.gridWidth + x;

    // if it's a souce cell , keep the temperature constant and skip diffusion entirely
    if (maskData[idx] == 1u) 
    {
        outputData[idx] = inputData[idx];
        return;
    }

    // 6 neighbors — clamp to boundary (zero-flux / Neumann condition)
    uint xL = (x > 0)                     ? idx - 1          : idx;
    uint xR = (x < params.gridWidth  - 1) ? idx + 1          : idx;
    uint yU = (y > 0)                     ? idx - params.gridWidth : idx;
    uint yD = (y < params.gridHeight - 1) ? idx + params.gridWidth : idx;
    uint zF = (z > 0)                     ? idx - sliceSize   : idx;
    uint zB = (z < params.gridDepth  - 1) ? idx + sliceSize   : idx;

    float T  = inputData[idx];
    float TL = inputData[xL];
    float TR = inputData[xR];
    float TU = inputData[yU];
    float TD = inputData[yD];
    float TF = inputData[zF];
    float TB = inputData[zB];

    // 3D heat equation — 6 neighbors so coefficient is 6, not 4
    float laplacian = TL + TR + TU + TD + TF + TB - 6.0 * T;
    outputData[idx] = T + params.alpha * params.dt * laplacian;
}