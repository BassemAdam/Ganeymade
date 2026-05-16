#ifndef SSF_LIGHT_DEPTH_INCLUDED
#define SSF_LIGHT_DEPTH_INCLUDED

struct LightDepthOut
{
    float4 color : SV_Target;
    float  depth : SV_Depth;
};

ParticleVaryings vertSSFLightDepth(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    return SSFParticleVertex(vid, iid);
}

LightDepthOut fragSSFLightDepth(ParticleVaryings i)
{
    SSFImpostorHit h = SSFEvaluateImpostor(i);
    if (!h.hit) discard;

    LightDepthOut o;
    o.depth = h.clipPos.z / h.clipPos.w;
    o.color = float4(-h.viewPos.z, 0, 0, 1);
    return o;
}

#endif