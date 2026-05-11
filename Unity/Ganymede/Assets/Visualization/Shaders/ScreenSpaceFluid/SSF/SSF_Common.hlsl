#ifndef SSF_COMMON_INCLUDED
#define SSF_COMMON_INCLUDED

// ============================================================
// Shared types and helpers for all Screen-Space Fluid passes.
// This file is included via HLSLINCLUDE, so Core.hlsl and
// Blit.hlsl are already available.
// ============================================================

// ----- Particle struct (must match C# Particle, 80 bytes) ----
struct Particle
{
    float3 position;
    float  density;
    float3 velocity;
    float  pressure;
    float3 acceleration;
    float  mass;
    float  temperature;
    int    phase;
    float  latentHeatAccum;
    int    fixedId;
    float  neighborCount;
    float  pad0, pad1, pad2;
};

StructuredBuffer<Particle> _ParticleBuffer;
float _ParticleRadius;
int   _ParticleCount;
int   _RenderPhase;

// ----- Per-particle billboard varyings -----------------------
struct ParticleVaryings
{
    float4 positionCS : SV_POSITION;
    float3 centerVS   : TEXCOORD0;   // sphere centre in view-space
    float2 quadCoord  : TEXCOORD1;   // [-1..1] in quad space
    float  radius     : TEXCOORD2;
    float  active     : TEXCOORD3;   // 0 = culled/inactive
};

// Two-triangle quad corner coordinates (CCW)
float2 QuadCoord(uint vid)
{
    const float2 COORDS[6] =
    {
        float2(-1,-1), float2(-1, 1), float2( 1, 1),
        float2(-1,-1), float2( 1, 1), float2( 1,-1)
    };
    return COORDS[vid % 6];
}

// Build billboard vertex for a particle.
// Returns a sentinel with active=0 when the particle should be culled.
ParticleVaryings ParticleVertex(uint vid, uint iid)
{
    ParticleVaryings o;
    o.quadCoord  = QuadCoord(vid);
    o.radius     = max(_ParticleRadius, 1e-4);
    o.active     = 0.0;
    o.centerVS   = 0.0;
    o.positionCS = float4(-2, -2, 1, 1); // guaranteed off-screen

    if (iid >= (uint)_ParticleCount) return o;
    Particle p = _ParticleBuffer[iid];
    if (p.phase != _RenderPhase)    return o;

    o.active    = 1.0;
    o.centerVS  = TransformWorldToView(p.position);
    float3 vs   = o.centerVS + float3(o.quadCoord * o.radius, 0.0);
    o.positionCS = mul(UNITY_MATRIX_P, float4(vs, 1.0));
    return o;
}

// Compute the sphere-impostor surface point for the current fragment.
// Returns false when the ray misses the sphere (outside unit disc).
bool SphereSurface(ParticleVaryings i, out float3 surfaceVS, out float4 surfaceCS)
{
    float rsq = dot(i.quadCoord, i.quadCoord);
    surfaceVS = 0.0;
    surfaceCS = 0.0;
    if (rsq > 1.0) return false;
    float z   = sqrt(saturate(1.0 - rsq)) * i.radius;
    surfaceVS = float3(i.centerVS.xy + i.quadCoord * i.radius, i.centerVS.z + z);
    surfaceCS = mul(UNITY_MATRIX_P, float4(surfaceVS, 1.0));
    return true;
}

#endif // SSF_COMMON_INCLUDED
