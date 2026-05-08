#ifndef WATER_PHASE_GEOMETRY_INCLUDED
    #define WATER_PHASE_GEOMETRY_INCLUDED

    float sdBox(float3 p, float3 b)
    {
        float3 d = abs(p) - b;
        return min(max(d.x, max(d.y, d.z)), 0.0) + length(max(d, 0.0));
    }

    float ComputeEdgeFade(float3 posOS, float3 boundsMin, float3 boundsMax, float softness)
    {
        float3 boundsCenter = (boundsMin + boundsMax) * 0.5;
        float3 boundsExtents = (boundsMax - boundsMin) * 0.5;
        float distInward = -sdBox(posOS - boundsCenter, boundsExtents);
        return smoothstep(0.0, max(softness, 1e-5), distInward);
    }

    bool IntersectRayAABBOS(float3 rayOriginOS, float3 rayDirOS, float3 bmin, float3 bmax, out float tEnter, out float tExit)
    {
        float3 safeDir = sign(rayDirOS) * max(abs(rayDirOS), 1e-6);
        float3 invDir = 1.0 / safeDir;

        float3 t0 = (bmin - rayOriginOS) * invDir;
        float3 t1 = (bmax - rayOriginOS) * invDir;

        float3 tMin3 = min(t0, t1);
        float3 tMax3 = max(t0, t1);

        tEnter = max(max(tMin3.x, tMin3.y), tMin3.z);
        tExit = min(min(tMax3.x, tMax3.y), tMax3.z);

        return tExit >= tEnter;
    }

    bool ComputeVoxelRaySegmentWS(float3 cameraWS, float3 sampleWS,
    float3 boundsMinOS, float3 boundsMaxOS,
    out float3 entryWS, out float3 rayDirWS, out float marchDistance)
    {
        float3 viewRayWS = normalize(sampleWS - cameraWS);
        float3 rayOriginOS = TransformWorldToObject(cameraWS);
        float3 rayDirOS = normalize(TransformWorldToObjectDir(viewRayWS));

        float tEnter;
        float tExit;
        if (!IntersectRayAABBOS(rayOriginOS, rayDirOS, boundsMinOS, boundsMaxOS, tEnter, tExit))
        {
            entryWS = 0.0;
            rayDirWS = 0.0;
            marchDistance = 0.0;
            return false;
        }

        tEnter = max(tEnter, 0.0);

        float3 entryOS = rayOriginOS + rayDirOS * tEnter;
        float3 exitOS = rayOriginOS + rayDirOS * tExit;

        entryWS = TransformObjectToWorld(entryOS);
        float3 exitWS = TransformObjectToWorld(exitOS);

        float segmentDistanceWS = distance(entryWS, exitWS);
        if (segmentDistanceWS <= 1e-5)
        {
            rayDirWS = 0.0;
            marchDistance = 0.0;
            return false;
        }

        rayDirWS = normalize(exitWS - entryWS);
        marchDistance = segmentDistanceWS;
        return true;
    }

#endif
