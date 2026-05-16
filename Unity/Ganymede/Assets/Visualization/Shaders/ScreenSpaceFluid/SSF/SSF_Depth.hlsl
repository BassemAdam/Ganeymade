#ifndef SSF_DEPTH_INCLUDED
#define SSF_DEPTH_INCLUDED

struct DepthOut
{
    float4 color : SV_Target;
    float  depth : SV_Depth;
};

ParticleVaryings vertSSFDepth(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    return SSFParticleVertex(vid, iid);
}

DepthOut fragSSFDepth(ParticleVaryings i)
{
    SSFImpostorHit h = SSFEvaluateImpostor(i);
    if (!h.hit) discard;

    DepthOut o;
    o.depth = h.clipPos.z / h.clipPos.w;
    o.color = float4(-h.viewPos.z, 0, 0, 1);
    return o;
}

#endif
