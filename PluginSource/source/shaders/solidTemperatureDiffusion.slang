struct SolidSimParams 
{
    float dt;
    uint gridWidth;
    uint gridHeight;
    uint gridDepth;
};

[[vk::push_constant]] ConstantBuffer<SolidSimParams> params;

[[vk::binding(0)]] 
StructuredBuffer<float> inputData;   
[[vk::binding(1)]] 
RWStructuredBuffer<float> outputData; 
[[vk::binding(2)]] 
StructuredBuffer<uint> maskData;   
[[vk::binding(3)]] 
StructuredBuffer<float> diffusivityData;
[[vk::binding(4)]] 
StructuredBuffer<float> heatSourcePins; 

[shader("compute")]
[numthreads(4, 4, 4)]  
void computeMain(uint3 threadId : SV_DispatchThreadID)
{
    uint x = threadId.x;
    uint y = threadId.y;
    uint z = threadId.z;

    // becuase the grid size may exceed the number of cells
    if (x >= params.gridWidth  || y >= params.gridHeight || z >= params.gridDepth) 
        return;

    // row-major 3D index
    uint sliceSize = params.gridWidth * params.gridHeight;
    uint idx = z * sliceSize + y * params.gridWidth + x;

    // if it's a non-solid cell , keep the temperature constant and skip diffusion 
    if (maskData[idx] == 0u) 
    {
        outputData[idx] = 0.0;
        return;
    }
    // pin heat source voxels such that the neighbors read the pinned value next frame
    float pin = heatSourcePins[idx];
    if (pin > 0.0)
    {
        outputData[idx] = pin;
        return;
    }

    // 6 neighbors, clamp to boundary (zero-flux / Neumann condition)
    uint xL = (x > 0) ? idx - 1 : idx;
    uint xR = (x < params.gridWidth - 1) ? idx + 1 : idx;
    uint yU = (y > 0) ? idx - params.gridWidth : idx;
    uint yD = (y < params.gridHeight - 1) ? idx + params.gridWidth : idx;
    uint zF = (z > 0) ? idx - sliceSize : idx;
    uint zB = (z < params.gridDepth - 1) ? idx + sliceSize : idx;

    float T = inputData[idx];

    // mask-guard each neighbour such that if the neighbour is non-solid, use T itself (zero flux)
    float TL = (maskData[xL] != 0u) ? inputData[xL] : T;
    float TR = (maskData[xR] != 0u) ? inputData[xR] : T;
    float TU = (maskData[yU] != 0u) ? inputData[yU] : T;
    float TD = (maskData[yD] != 0u) ? inputData[yD] : T;
    float TF = (maskData[zF] != 0u) ? inputData[zF] : T;
    float TB = (maskData[zB] != 0u) ? inputData[zB] : T;

    // clamp the diffusivity factor to ensure stability
    float coeff = min(diffusivityData[idx] * params.dt, 0.166666);

    // heat equation take the temp of the surrounding 6 neighbors 
    float laplacian = TL + TR + TU + TD + TF + TB - 6.0 * T;
    outputData[idx] = T + coeff * params.dt * laplacian;
}