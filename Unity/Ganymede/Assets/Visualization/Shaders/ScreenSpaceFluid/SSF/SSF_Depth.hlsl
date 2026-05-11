#ifndef SSF_DEPTH_INCLUDED
#define SSF_DEPTH_INCLUDED

// ============================================================
// Pass 0 — ScreenSpaceFluidDepth
//   Renders each particle as a sphere impostor.
//   Color output (RHalf): positive linear eye-depth in metres.
//   Depth output (SV_Depth): correct hardware depth for Z-testing
//   between overlapping particles (closest wins).
//
// Pass 1 — ScreenSpaceFluidThickness
//   Additive blend of sphere chord length (2*sqrt(r²-d²)).
//   No depth test — all particles along a ray contribute.
// ============================================================

// ---- Pass 0: Depth ------------------------------------------
struct DepthOut
{
    float4 color : SV_Target;  // R = eye-depth in metres
    float depth : SV_Depth;    // hardware Z
};

ParticleVaryings vertDepth(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    return ParticleVertex(vid, iid);
}

DepthOut fragDepth(ParticleVaryings i)
{
    if (i.active < 0.5) discard;
    float3 surfVS; float4 surfCS;
    if (!SphereSurface(i, surfVS, surfCS)) discard;

    DepthOut o;
    o.depth = surfCS.z / surfCS.w;          // NDC depth → hardware buffer
    o.color = float4(-surfVS.z, 0, 0, 1);  // positive linear eye-depth
    return o;
}

// ---- Pass 1: Thickness --------------------------------------
ParticleVaryings vertThick(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    return ParticleVertex(vid, iid);
}

half4 fragThick(ParticleVaryings i) : SV_Target
{
    if (i.active < 0.5) discard;
    float rsq = dot(i.quadCoord, i.quadCoord);
    if (rsq > 1.0) discard;
    float chord = 2.0 * sqrt(max(0.0, 1.0 - rsq)) * i.radius;
    return half4(chord, 0, 0, 0);
}

#endif // SSF_DEPTH_INCLUDED
