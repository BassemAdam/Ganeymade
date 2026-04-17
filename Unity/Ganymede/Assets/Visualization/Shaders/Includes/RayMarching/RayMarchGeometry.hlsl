#ifndef RAY_MARCH_GEOMETRY_INCLUDED
#define RAY_MARCH_GEOMETRY_INCLUDED

// Returns float2(dstToBox, dstInsideBox) for a ray vs AABB test in world space.
float2 RayBoxDst(float3 rayOriginWS, float3 rayDirWS, float3 bminWS, float3 bmaxWS)
{
    float3 invDir = 1.0 / max(abs(rayDirWS), 1e-6) * sign(rayDirWS);
    float3 t0 = (bminWS - rayOriginWS) * invDir;
    float3 t1 = (bmaxWS - rayOriginWS) * invDir;

    float3 tmin = min(t0, t1);
    float3 tmax = max(t0, t1);

    float dstA = max(max(tmin.x, tmin.y), tmin.z);
    float dstB = min(min(tmax.x, tmax.y), tmax.z);

    float dstToBox    = max(0.0, dstA);
    float dstInsideBox = max(0.0, dstB - dstToBox);

    return float2(dstToBox, dstInsideBox);
}

#endif
