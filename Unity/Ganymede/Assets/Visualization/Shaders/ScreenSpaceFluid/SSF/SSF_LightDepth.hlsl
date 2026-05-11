#ifndef SSF_LIGHT_DEPTH_INCLUDED
#define SSF_LIGHT_DEPTH_INCLUDED

// ============================================================
// Pass: ScreenSpaceFluidLightDepth  (Simon Green Step 7 — shadows)
//
// Renders sphere impostors AGAIN, this time using a light-space
// view/projection bound by C# via _SSFViewMatrix / _SSFProjMatrix
// (with _SSFUseOverrideMatrices = 1). Output is positive
// "light eye-depth" in metres — the distance from the light along
// its forward axis to the front of the closest particle.
//
// This map is later sampled by the composite & caustics passes to
// figure out whether a world-space point is in shadow of the fluid.
// ============================================================

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
    o.color = float4(-h.viewPos.z, 0, 0, 1); // light-space eye depth
    return o;
}

#endif // SSF_LIGHT_DEPTH_INCLUDED
