#ifndef SSF_COMMON_INCLUDED
#define SSF_COMMON_INCLUDED

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

float4x4 _SSFViewMatrix;
float4x4 _SSFProjMatrix;
int      _SSFUseOverrideMatrices;

float4x4 SSFViewMatrix() { return (_SSFUseOverrideMatrices != 0) ? _SSFViewMatrix : UNITY_MATRIX_V; }
float4x4 SSFProjMatrix() { return (_SSFUseOverrideMatrices != 0) ? _SSFProjMatrix : UNITY_MATRIX_P; }

float3 SSFTransformWorldToView(float3 posWS)
{
    return mul(SSFViewMatrix(), float4(posWS, 1.0)).xyz;
}

struct ParticleVaryings
{
    float4 positionCS : SV_POSITION;
    float3 centerVS   : TEXCOORD0;
    float2 quadCoord  : TEXCOORD1;
    float  radius     : TEXCOORD2;
    float  active     : TEXCOORD3;
};

float2 SSFQuadCoord(uint vid)
{
    const float2 COORDS[6] =
    {
        float2(-1, -1), float2(-1,  1), float2( 1,  1),
        float2(-1, -1), float2( 1,  1), float2( 1, -1)
    };
    return COORDS[vid % 6];
}

ParticleVaryings SSFParticleVertex(uint vid, uint iid)
{
    ParticleVaryings o;
    o.quadCoord  = SSFQuadCoord(vid);
    o.radius     = max(_ParticleRadius, 1e-4);
    o.active     = 0.0;
    o.centerVS   = 0.0;
    o.positionCS = float4(-2, -2, 1, 1);

    if (iid >= (uint)_ParticleCount) return o;
    Particle p = _ParticleBuffer[iid];
    if (p.phase != _RenderPhase)    return o;

    o.active     = 1.0;
    o.centerVS   = SSFTransformWorldToView(p.position);
    float3 vs    = o.centerVS + float3(o.quadCoord * o.radius, 0.0);
    o.positionCS = mul(SSFProjMatrix(), float4(vs, 1.0));
    return o;
}

struct SSFImpostorHit
{
    float3 viewPos;
    float3 viewNormal;
    float4 clipPos;
    bool   hit;
};

SSFImpostorHit SSFEvaluateImpostor(ParticleVaryings i)
{
    SSFImpostorHit h;
    float r2 = dot(i.quadCoord, i.quadCoord);
    h.hit = (r2 <= 1.0) && (i.active > 0.5);
    if (!h.hit)
    {
        h.viewPos    = 0.0;
        h.viewNormal = float3(0, 0, 1);
        h.clipPos    = float4(0, 0, 0, 1);
        return h;
    }

    float  z   = sqrt(saturate(1.0 - r2));
    float3 nVS = float3(i.quadCoord, z);
    h.viewPos    = float3(i.centerVS.xy + i.quadCoord * i.radius,
                          i.centerVS.z   + z * i.radius);
    h.viewNormal = nVS;
    h.clipPos    = mul(SSFProjMatrix(), float4(h.viewPos, 1.0));
    return h;
}

float3 SSFViewPosFromEyeDepth(float2 uv, float eyeDepth)
{
    float2 ndc = uv * 2.0 - 1.0;
    return float3(
        ndc.x * eyeDepth / UNITY_MATRIX_P[0][0],
        ndc.y * eyeDepth / UNITY_MATRIX_P[1][1],
        -eyeDepth);
}

float SSFEyeDepthToHWDepth(float eyeDepth)
{
    float4 cp = mul(UNITY_MATRIX_P, float4(0, 0, -eyeDepth, 1));
    return cp.z / cp.w;
}

float SSFHWDepthToEye(float rawDepth)
{
    return LinearEyeDepth(rawDepth, _ZBufferParams);
}

#endif
