#ifndef SSF_THICKNESS_INCLUDED
#define SSF_THICKNESS_INCLUDED

ParticleVaryings vertSSFThickness(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    return SSFParticleVertex(vid, iid);
}

half4 fragSSFThickness(ParticleVaryings i) : SV_Target
{
    if (i.active < 0.5) discard;
    float r2 = dot(i.quadCoord, i.quadCoord);
    if (r2 > 1.0) discard;

    float chord = 2.0 * i.radius * sqrt(saturate(1.0 - r2));
    return half4(chord, 0, 0, 0);
}

#endif
