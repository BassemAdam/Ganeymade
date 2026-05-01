#ifndef RAY_MARCH_SURFACE_INCLUDED
#define RAY_MARCH_SURFACE_INCLUDED

#define IOR_AIR   1.0003
#define IOR_WATER 1.3330

// Exact unpolarized Fresnel reflectance, matching the reference raymarcher:
// average of perpendicular and parallel polarization reflectance.
float CalculateReflectance(float3 inDir, float3 normal, float iorA, float iorB)
{
    float refractRatio = iorA / iorB;
    float cosAngleIn = saturate(-dot(inDir, normal));
    float sinSqrAngleOfRefraction = refractRatio * refractRatio * (1.0 - cosAngleIn * cosAngleIn);
    if (sinSqrAngleOfRefraction >= 1.0)
        return 1.0;

    float cosAngleOfRefraction = sqrt(max(0.0, 1.0 - sinSqrAngleOfRefraction));

    float rPerpendicular = (iorA * cosAngleIn - iorB * cosAngleOfRefraction)
                         / max(iorA * cosAngleIn + iorB * cosAngleOfRefraction, 1e-6);
    rPerpendicular *= rPerpendicular;

    float rParallel = (iorB * cosAngleIn - iorA * cosAngleOfRefraction)
                    / max(iorB * cosAngleIn + iorA * cosAngleOfRefraction, 1e-6);
    rParallel *= rParallel;

    return saturate((rPerpendicular + rParallel) * 0.5);
}

float3 RefractExact(float3 inDir, float3 normal, float iorA, float iorB)
{
    float refractRatio = iorA / iorB;
    float cosAngleIn = saturate(-dot(inDir, normal));
    float sinSqrAngleOfRefraction = refractRatio * refractRatio * (1.0 - cosAngleIn * cosAngleIn);
    if (sinSqrAngleOfRefraction > 1.0)
        return float3(0.0, 0.0, 0.0);

    return refractRatio * inDir
         + (refractRatio * cosAngleIn - sqrt(max(0.0, 1.0 - sinSqrAngleOfRefraction))) * normal;
}

float3 ReflectExact(float3 inDir, float3 normal)
{
    return inDir - 2.0 * dot(inDir, normal) * normal;
}

struct SurfaceHit
{
    bool   hit;
    float3 posWS;
    float3 normal;        // View-facing optical normal used by Fresnel/refraction.
    float3 outwardNormal; // Raw density/bounds normal, stable for debugging surface shape.
    float3 reflectDir;
    float3 refractDir;
    float  reflectWeight;
    float  refractWeight;
    bool   totalInternalReflection;
};

SurfaceHit NoSurfaceHit()
{
    SurfaceHit s;
    s.hit                     = false;
    s.posWS                   = float3(0, 0, 0);
    s.normal                  = float3(0, 0, 0);
    s.outwardNormal           = float3(0, 0, 0);
    s.reflectDir              = float3(0, 0, 0);
    s.refractDir              = float3(0, 0, 0);
    s.reflectWeight           = 0.0;
    s.refractWeight           = 1.0;
    s.totalInternalReflection = false;
    return s;
}

SurfaceHit MakeSurfaceHit(float3 posWS, float3 rayDir, bool enteringWater)
{
    SurfaceHit s;
    s.hit   = true;
    s.posWS = posWS;

    float3 n = GetSurfaceNormalWS(posWS, rayDir);
    // A zero normal means the local density gradient was below threshold — this
    // crossing is not a real surface (e.g. noise, interior bulk, or thin wisp).
    // Discard it so the ray loop can continue searching for a genuine surface.
    if (dot(n, n) < 1e-8)
        return NoSurfaceHit();

    s.outwardNormal = n;

    // The outward normal may still point into the liquid from the viewer's side.
    // Flip it if needed so Fresnel/reflection/refraction are correct.
    if (dot(n, rayDir) > 0.0)
        n = -n;
    s.normal = n;

    float iorIncident = enteringWater ? IOR_AIR   : IOR_WATER;
    float iorTransmit = enteringWater ? IOR_WATER : IOR_AIR;

    s.reflectWeight = CalculateReflectance(rayDir, n, iorIncident, iorTransmit);
    s.refractWeight = 1.0 - s.reflectWeight;
    s.reflectDir    = ReflectExact(rayDir, n);

    float3 rawRefracted       = RefractExact(rayDir, n, iorIncident, iorTransmit);
    s.totalInternalReflection = (dot(rawRefracted, rawRefracted) < 0.001);
    if (s.totalInternalReflection)
    {
        s.reflectWeight = 1.0;
        s.refractWeight = 0.0;
        s.refractDir    = s.reflectDir;
    }
    else
    {
        s.refractDir = normalize(rawRefracted);
    }

    return s;
}

#endif
