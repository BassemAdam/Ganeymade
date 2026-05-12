#ifndef SSF_THICKNESS_INCLUDED
#define SSF_THICKNESS_INCLUDED

// ============================================================
// Pass: ScreenSpaceFluidThickness  (Simon Green Step 5)
//   Renders particles a second time as smooth Gaussian splats
//   with additive blending and NO depth testing. Every particle
//   along the ray contributes; total accumulated value is
//   proportional to the chord length through the fluid volume.
// ============================================================

ParticleVaryings vertSSFThickness(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    return SSFParticleVertex(vid, iid);
}

half4 fragSSFThickness(ParticleVaryings i) : SV_Target
{
    if (i.active < 0.5) discard;
    float r2 = dot(i.quadCoord, i.quadCoord);
    if (r2 > 1.0) discard;

    // Chord length through a sphere of radius R at offset r from the axis:
    //   chord = 2 * R * sqrt(1 - r²)
    // No per-splat Gaussian fall-off — matches Splash-main (thicknessMap.wgsl):
    //   thickness = particle_alpha * sqrt(1 - r²)
    // Post-splat Gaussian blur (SSF_ThicknessBlur) provides the smoothing.
    float chord = 2.0 * i.radius * sqrt(saturate(1.0 - r2));
    return half4(chord, 0, 0, 0);
}

#endif // SSF_THICKNESS_INCLUDED
