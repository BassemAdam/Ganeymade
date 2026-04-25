#ifndef RAY_MARCH_SURFACE_INCLUDED
#define RAY_MARCH_SURFACE_INCLUDED

#define IOR_AIR   1.0003
#define IOR_WATER 1.3330

// Fresnel-Schlick: returns [0=refract, 1=reflect] weight.
float FresnelSchlick(float cosTheta, float n1, float n2)
{
    float r0 = (n1 - n2) / (n1 + n2);
    r0 *= r0;
    return r0 + (1.0 - r0) * pow(1.0 - cosTheta, 5.0);
}

struct SurfaceHit
{
    bool   hit;
    float3 posWS;
    float3 normal;
    float3 reflectDir;
    float3 refractDir;
    float  fresnel;
    bool   totalInternalReflection;
};

SurfaceHit NoSurfaceHit()
{
    SurfaceHit s;
    s.hit                     = false;
    s.posWS                   = float3(0, 0, 0);
    s.normal                  = float3(0, 0, 0);
    s.reflectDir              = float3(0, 0, 0);
    s.refractDir              = float3(0, 0, 0);
    s.fresnel                 = 0.0;
    s.totalInternalReflection = false;
    return s;
}

SurfaceHit MakeSurfaceHit(float3 posWS, float3 rayDir, bool enteringWater)
{
    SurfaceHit s;
    s.hit   = true;
    s.posWS = posWS;

    float3 n = GetSurfaceNormalWS(posWS, rayDir);
    // A zero normal means the baked gradient was below threshold — this density
    // crossing is not a real surface (e.g. noise, interior bulk, or thin wisp).
    // Discard it so the ray loop can continue searching for a genuine surface.
    if (dot(n, n) < 1e-8)
        return NoSurfaceHit();

    // The pre-baked outward normal may still point into the liquid from the
    // viewer's side. Flip it if needed so Fresnel/reflection/refraction are correct.
    if (dot(n, rayDir) > 0.0)
        n = -n;
    s.normal = n;

    float iorIncident = enteringWater ? IOR_AIR   : IOR_WATER;
    float iorTransmit = enteringWater ? IOR_WATER : IOR_AIR;

    float cosI    = saturate(dot(-rayDir, n));
    s.fresnel     = FresnelSchlick(cosI, iorIncident, iorTransmit);
    s.reflectDir  = reflect(rayDir, n);

    float  iorRatio           = iorIncident / iorTransmit;
    float3 rawRefracted       = refract(rayDir, n, iorRatio);
    s.totalInternalReflection = (dot(rawRefracted, rawRefracted) < 0.001);
    s.refractDir              = s.totalInternalReflection ? s.reflectDir : rawRefracted;

    return s;
}

#endif
