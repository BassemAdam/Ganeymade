#ifndef WATER_PHASE_HELPERS_INCLUDED
#define WATER_PHASE_HELPERS_INCLUDED

float Hash3D(float3 p)
{
    p = frac(p * float3(443.897, 441.423, 437.195));
    p += dot(p, p.yzx + 19.19);
    return frac((p.x + p.y) * p.z);
}

float ValueNoise3D(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);

    float c000 = Hash3D(i + float3(0, 0, 0));
    float c100 = Hash3D(i + float3(1, 0, 0));
    float c010 = Hash3D(i + float3(0, 1, 0));
    float c110 = Hash3D(i + float3(1, 1, 0));
    float c001 = Hash3D(i + float3(0, 0, 1));
    float c101 = Hash3D(i + float3(1, 0, 1));
    float c011 = Hash3D(i + float3(0, 1, 1));
    float c111 = Hash3D(i + float3(1, 1, 1));

    float x0 = lerp(c000, c100, u.x);
    float x1 = lerp(c010, c110, u.x);
    float x2 = lerp(c001, c101, u.x);
    float x3 = lerp(c011, c111, u.x);

    float y0 = lerp(x0, x1, u.y);
    float y1 = lerp(x2, x3, u.y);

    return lerp(y0, y1, u.z);
}

float FBM(float3 p, int octaves, float lacunarity, float gain)
{
    float value = 0.0;
    float amplitude = 0.5;
    float frequency = 1.0;
    float maxValue = 0.0;

    for (int i = 0; i < octaves; i++)
    {
        value += amplitude * ValueNoise3D(p * frequency);
        maxValue += amplitude;
        amplitude *= gain;
        frequency *= lacunarity;
    }

    return value / max(maxValue, 1e-5);
}

float SampleDensity(float3 worldPos, float time,
                    float3 driftDir, float driftSpeed,
                    float noiseScale, int octaves,
                    float densityPower,
                    float physicsDensity, float physicsBlend)
{
    float3 driftedPos = worldPos + driftDir * (time * driftSpeed);
    float3 p = driftedPos / max(noiseScale, 1e-5);

    float3 warpOffset = float3(
        ValueNoise3D(p * 0.7 + float3(1.72, 9.23, 5.41)),
        ValueNoise3D(p * 0.7 + float3(8.31, 2.84, 3.26)),
        ValueNoise3D(p * 0.7 + float3(4.17, 6.73, 1.92))
    ) * 2.0 - 1.0;

    float3 warpedP = p + warpOffset * 0.35;
    float rawNoise = FBM(warpedP, octaves, 2.0, 0.5);
    float shaped = pow(saturate(rawNoise), densityPower);

    float physicsModulated = shaped * physicsDensity;
    float finalDensity = lerp(shaped, physicsModulated, physicsBlend);
    return saturate(finalDensity);
}

float HenyeyGreenstein(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    return (1.0 - g2) / pow(abs(denom), 1.5);
}

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

float FresnelEdge(float3 viewDir, float3 normal, float power)
{
    float cosTheta = saturate(dot(viewDir, normal));
    return pow(1.0 - cosTheta, power);
}

// ── Subsurface scattering: physically-motivated translucency approximation ──
// Based on GDC 2011 "Fast Subsurface Scattering" (Jimenez et al.)
// Models light transmitting through thin liquid edges with
// forward-scatter lobe distorted by the surface normal.
half3 ComputeSSS(float3 viewDir, float3 lightDir, float3 normal,
                 half3 lightColor, half3 sssColor,
                 float strength, float power, float distortion,
                 float ambient, float thickness)
{
    // Distort the light vector by the surface normal to simulate
    // subsurface light transport bending around the medium.
    float3 sssLightDir = normalize(lightDir + normal * distortion);

    // Forward-scatter: how much light arrives from behind the surface
    // toward the viewer through the translucent medium.
    float sssDot = saturate(dot(viewDir, -sssLightDir));
    float sssForward = pow(sssDot, power);

    // Beer-Lambert attenuation: thicker medium absorbs more light
    float attenuation = exp(-thickness);

    // Combine forward scatter with a small ambient term for
    // omnidirectional subsurface glow (scattered ambient light).
    float sss = (sssForward + ambient) * attenuation * strength;

    return sss * sssColor * lightColor;
}

float ComputeRadialFade(float radialDist)
{
    float radialFade = saturate(1.0 - radialDist);
    // Smoothstep-like shaping for a softer, rounder boundary.
    return radialFade * radialFade * (3.0 - 2.0 * radialFade);
}

float ComputeShapeMaskOS(float3 sampleOS,
                         float3 boundsMinOS, float3 boundsMaxOS,
                         float3 boundsCenterOS, float3 boundsExtentsOS,
                         float edgeSoftness)
{
    float axialFade = ComputeEdgeFade(sampleOS, boundsMinOS, boundsMaxOS, edgeSoftness);
    float3 normPos = (sampleOS - boundsCenterOS) / max(boundsExtentsOS, 1e-6);
    float radialDist = length(normPos);
    float radialFade = ComputeRadialFade(radialDist);
    return axialFade * radialFade;
}

float SampleShapeMaskWS(float3 sampleWS,
                        float3 boundsMinOS, float3 boundsMaxOS,
                        float3 boundsCenterOS, float3 boundsExtentsOS,
                        float edgeSoftness)
{
    float3 sampleOS = TransformWorldToObject(sampleWS);
    return ComputeShapeMaskOS(sampleOS, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);
}

float3 ComputeShapeNormalWS(float3 sampleWS,
                            float3 boundsMinOS, float3 boundsMaxOS,
                            float3 boundsCenterOS, float3 boundsExtentsOS,
                            float edgeSoftness,
                            float epsWS)
{
    float e = max(epsWS, 1e-4);
    float3 dx = float3(e, 0.0, 0.0);
    float3 dy = float3(0.0, e, 0.0);
    float3 dz = float3(0.0, 0.0, e);

    float mx1 = SampleShapeMaskWS(sampleWS + dx, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);
    float mx0 = SampleShapeMaskWS(sampleWS - dx, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);
    float my1 = SampleShapeMaskWS(sampleWS + dy, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);
    float my0 = SampleShapeMaskWS(sampleWS - dy, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);
    float mz1 = SampleShapeMaskWS(sampleWS + dz, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);
    float mz0 = SampleShapeMaskWS(sampleWS - dz, boundsMinOS, boundsMaxOS, boundsCenterOS, boundsExtentsOS, edgeSoftness);

    float3 grad = float3(mx1 - mx0, my1 - my0, mz1 - mz0);
    float gradLen2 = dot(grad, grad);
    if (gradLen2 < 1e-10)
        return float3(0.0, 1.0, 0.0);

    // Shape mask is ~1 inside and ~0 outside, so gradient generally points inward.
    return normalize(-grad);
}

// ── Caustics: dual-layer chromatic aberration sampling ──
// Approximates light refraction patterns on underwater surfaces.
// Two scrolling layers at different speeds create interference.
// Chromatic split samples R/G/B at slight UV offsets to simulate
// wavelength-dependent refraction (dispersion).
half3 SampleCaustics(TEXTURE2D_PARAM(causticsTex, causticsSampler),
                     float3 worldPos, float3 lightDir,
                     float time, float scale, float speed,
                     float chromaticSplit)
{
    // Project world position along light direction onto the XZ plane
    // for physically-based light-aligned caustic projection.
    float2 projUV = worldPos.xz / max(scale, 0.01);

    // Two layers scrolling at different speeds and angles for interference
    float2 scroll1 = float2(0.7, 0.3) * speed * time;
    float2 scroll2 = float2(-0.4, 0.6) * speed * time * 0.8;

    float2 uv1 = projUV + scroll1;
    float2 uv2 = projUV * 1.3 + scroll2;

    // Chromatic aberration: offset each channel slightly
    float2 splitR = float2(chromaticSplit, 0.0);
    float2 splitB = float2(-chromaticSplit, chromaticSplit);

    // Layer 1
    float c1r = SAMPLE_TEXTURE2D(causticsTex, causticsSampler, uv1 + splitR).r;
    float c1g = SAMPLE_TEXTURE2D(causticsTex, causticsSampler, uv1).g;
    float c1b = SAMPLE_TEXTURE2D(causticsTex, causticsSampler, uv1 + splitB).b;

    // Layer 2
    float c2r = SAMPLE_TEXTURE2D(causticsTex, causticsSampler, uv2 + splitR).r;
    float c2g = SAMPLE_TEXTURE2D(causticsTex, causticsSampler, uv2).g;
    float c2b = SAMPLE_TEXTURE2D(causticsTex, causticsSampler, uv2 + splitB).b;

    // min blending creates the sharp bright intersection patterns
    // characteristic of real water caustics (constructive interference)
    half3 caustics = half3(
        min(c1r, c2r),
        min(c1g, c2g),
        min(c1b, c2b)
    );

    return caustics;
}

// ── Surface texture: triplanar mapping for arbitrary shapes ──
// Samples a texture projected along X/Y/Z and blends by the surface normal.
// This avoids stretching and works for cubes, spheres, and deformed volumes.
half3 SampleSurfaceTextureTriplanar(TEXTURE2D_PARAM(surfaceTex, surfaceSampler),
                                   float3 worldPos, float3 normalWS,
                                   float time, float scale, float scrollSpeed,
                                   float blendSharpness)
{
    float3 n = normalize(normalWS);
    float3 w = abs(n);

    float sharp = max(blendSharpness, 1e-3);
    w = pow(w, sharp);
    w /= max(w.x + w.y + w.z, 1e-5);

    float invScale = 1.0 / max(scale, 1e-3);
    float3 p = worldPos * invScale;

    float t = time * scrollSpeed;
    float2 uvX = p.zy + float2(t, t * 0.77); // project along +X (YZ plane)
    float2 uvY = p.xz + float2(t * 0.63, t); // project along +Y (XZ plane)
    float2 uvZ = p.xy + float2(t * 0.91, t * 0.58); // project along +Z (XY plane)

    half3 sx = SAMPLE_TEXTURE2D(surfaceTex, surfaceSampler, uvX).rgb;
    half3 sy = SAMPLE_TEXTURE2D(surfaceTex, surfaceSampler, uvY).rgb;
    half3 sz = SAMPLE_TEXTURE2D(surfaceTex, surfaceSampler, uvZ).rgb;

    return sx * w.x + sy * w.y + sz * w.z;
}

struct WaterPhaseMarchResult
{
    float3 vapourScatter;
    float vapourAlpha;
    float liquidAlpha;
    float liquidDepth;
    float vapourLitness;

    // Liquid surface hit (front-most) for surface texturing / shading.
    float3 liquidSurfaceWS;
    float3 liquidSurfaceNormalWS;
    float  liquidSurfaceFound;
};

WaterPhaseMarchResult RaymarchWaterPhase(
    float3 rayOrigin, float3 rayDir,
    float3 lightDir, half3 lightColor,
    int marchSteps, float marchDistance,
    float vapourG, float vapourAbsorption,
    float liquidOpacityCoeff,
    float phaseThreshold, float phaseWidth,
    float time,
    float3 driftDir, float driftSpeed,
    float noiseScale, int octaves,
    float densityPower,
    float physicsDensity, float physicsBlend,
    float sceneLinearDepth,
    float3 boundsMinOS, float3 boundsMaxOS,
    float edgeSoftness,
    float2 screenUV,
    float2 blueNoiseRG,
    float blueNoiseStrength)
{
    WaterPhaseMarchResult result;
    result.vapourScatter = 0.0;
    result.vapourAlpha = 0.0;
    result.liquidAlpha = 0.0;
    result.liquidDepth = 0.0;
    result.vapourLitness = 0.0;

    result.liquidSurfaceWS = 0.0;
    result.liquidSurfaceNormalWS = float3(0.0, 1.0, 0.0);
    result.liquidSurfaceFound = 0.0;

    float stepSize = marchDistance / max((float)marchSteps, 1.0);
    float vapourTransmit = 1.0;

    float cosTheta = dot(-rayDir, lightDir);

    float3 boundsCenter = (boundsMinOS + boundsMaxOS) * 0.5;
    float3 boundsExtents = (boundsMaxOS - boundsMinOS) * 0.5;

    float3 boundsMinWS = TransformObjectToWorld(boundsMinOS);
    float3 boundsMaxWS = TransformObjectToWorld(boundsMaxOS);
    float boundsDiagWS = max(distance(boundsMinWS, boundsMaxWS), 1e-4);
    float normalEpsWS = clamp(boundsDiagWS * 0.005, 0.001, 0.05);

    float accumulatedLitAlpha = 0.0;
    float accumulatedTotalAlpha = 0.0;

    float2 pixelCoords = screenUV * _ScreenParams.xy;
    float ignJitter = frac(52.9829189 * frac(dot(pixelCoords, float2(0.06711056, 0.00583715))));
    float blueNoiseWeight = saturate(blueNoiseStrength);

    // Blue-noise seed rotates over time to reduce static pattern lock while
    // keeping deterministic distribution per-pixel within a frame.
    float blueSeed = frac(blueNoiseRG.x + blueNoiseRG.y * 0.754877666 + time * 0.61803398875);
    float blueStride = 0.61803398875 + blueNoiseRG.y * 0.14589803;

    for (int i = 0; i < marchSteps; i++)
    {
        float blueJitter = frac(blueSeed + (i + 1.0) * blueStride);
        float jitter = lerp(ignJitter, blueJitter, blueNoiseWeight);
        float3 samplePos = rayOrigin + rayDir * (stepSize * (i + jitter));

        float sampleEyeDepth = -mul(UNITY_MATRIX_V, float4(samplePos, 1.0)).z;
        if (sampleEyeDepth >= sceneLinearDepth)
            break;

        float3 sampleOS = TransformWorldToObject(samplePos);
        float shapeMask = ComputeShapeMaskOS(sampleOS, boundsMinOS, boundsMaxOS, boundsCenter, boundsExtents, edgeSoftness);

        float density = SampleDensity(
            samplePos, time,
            driftDir, driftSpeed,
            noiseScale, octaves,
            densityPower,
            physicsDensity, physicsBlend
        ) * shapeMask;

        if (density <= 0.001)
            continue;

        float liquidPhase = smoothstep(phaseThreshold - phaseWidth, phaseThreshold + phaseWidth, density);
        float vapourPhase = 1.0 - liquidPhase;

        float vapourDensity = density * vapourPhase;
        float liquidDensity = density * liquidPhase;

        if (vapourDensity > 0.0001)
        {
            float absorption = vapourDensity * vapourAbsorption * stepSize;
            float stepTransmit = exp(-absorption);
            float stepAlpha = 1.0 - stepTransmit;
            float phase = HenyeyGreenstein(cosTheta, vapourG);

            half shadowAtten = 1.0;
#if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
            float4 shadowCoord = TransformWorldToShadowCoord(samplePos);
            shadowAtten = MainLightRealtimeShadow(shadowCoord);
#endif

            result.vapourScatter += vapourTransmit * vapourDensity * stepSize * phase * (lightColor * shadowAtten);
            
            accumulatedLitAlpha += vapourTransmit * stepAlpha * shadowAtten;
            accumulatedTotalAlpha += vapourTransmit * stepAlpha;

            vapourTransmit *= stepTransmit;
        }

        if (liquidDensity > 0.0001)
        {
            if (result.liquidSurfaceFound < 0.5)
            {
                result.liquidSurfaceWS = samplePos;
                result.liquidSurfaceNormalWS = ComputeShapeNormalWS(
                    samplePos,
                    boundsMinOS, boundsMaxOS,
                    boundsCenter, boundsExtents,
                    edgeSoftness,
                    normalEpsWS
                );
                result.liquidSurfaceFound = 1.0;
            }

            float stepAlpha = saturate(liquidDensity * liquidOpacityCoeff * stepSize);
            result.liquidAlpha += (1.0 - result.liquidAlpha) * stepAlpha;
            result.liquidDepth += liquidDensity * stepSize;
        }

        if (vapourTransmit < 0.01 && result.liquidAlpha > 0.99)
            break;
    }

    result.vapourAlpha = 1.0 - vapourTransmit;
    result.vapourLitness = accumulatedTotalAlpha > 0.0001 ? (accumulatedLitAlpha / accumulatedTotalAlpha) : 0.0;
    return result;
}

// ── Liquid-optimised raymarch: IGN jitter only, no blue noise, no per-step shadows ──
WaterPhaseMarchResult RaymarchWaterPhaseLiquid(
    float3 rayOrigin, float3 rayDir,
    float3 lightDir, half3 lightColor,
    int marchSteps, float marchDistance,
    float vapourG, float vapourAbsorption,
    float liquidOpacityCoeff,
    float phaseThreshold, float phaseWidth,
    float time,
    float3 driftDir, float driftSpeed,
    float noiseScale, int octaves,
    float densityPower,
    float physicsDensity, float physicsBlend,
    float sceneLinearDepth,
    float3 boundsMinOS, float3 boundsMaxOS,
    float edgeSoftness,
    float2 screenUV)
{
    WaterPhaseMarchResult result;
    result.vapourScatter = 0.0;
    result.vapourAlpha = 0.0;
    result.liquidAlpha = 0.0;
    result.liquidDepth = 0.0;
    result.vapourLitness = 0.0;

    result.liquidSurfaceWS = 0.0;
    result.liquidSurfaceNormalWS = float3(0.0, 1.0, 0.0);
    result.liquidSurfaceFound = 0.0;

    float stepSize = marchDistance / max((float)marchSteps, 1.0);
    float vapourTransmit = 1.0;

    float cosTheta = dot(-rayDir, lightDir);

    float3 boundsCenter = (boundsMinOS + boundsMaxOS) * 0.5;
    float3 boundsExtents = (boundsMaxOS - boundsMinOS) * 0.5;

    float3 boundsMinWS = TransformObjectToWorld(boundsMinOS);
    float3 boundsMaxWS = TransformObjectToWorld(boundsMaxOS);
    float boundsDiagWS = max(distance(boundsMinWS, boundsMaxWS), 1e-4);
    float normalEpsWS = clamp(boundsDiagWS * 0.005, 0.001, 0.05);

    float2 pixelCoords = screenUV * _ScreenParams.xy;
    float jitter = frac(52.9829189 * frac(dot(pixelCoords, float2(0.06711056, 0.00583715))));

    for (int i = 0; i < marchSteps; i++)
    {
        float3 samplePos = rayOrigin + rayDir * (stepSize * (i + jitter));

        float sampleEyeDepth = -mul(UNITY_MATRIX_V, float4(samplePos, 1.0)).z;
        if (sampleEyeDepth >= sceneLinearDepth)
            break;

        float3 sampleOS = TransformWorldToObject(samplePos);
        float shapeMask = ComputeShapeMaskOS(sampleOS, boundsMinOS, boundsMaxOS, boundsCenter, boundsExtents, edgeSoftness);

        float density = SampleDensity(
            samplePos, time,
            driftDir, driftSpeed,
            noiseScale, octaves,
            densityPower,
            physicsDensity, physicsBlend
        ) * shapeMask;

        if (density <= 0.001)
            continue;

        float liquidPhase = smoothstep(phaseThreshold - phaseWidth, phaseThreshold + phaseWidth, density);
        float vapourPhase = 1.0 - liquidPhase;

        float vapourDensity = density * vapourPhase;
        float liquidDensity = density * liquidPhase;

        if (vapourDensity > 0.0001)
        {
            float absorption = vapourDensity * vapourAbsorption * stepSize;
            float stepTransmit = exp(-absorption);
            float phase = HenyeyGreenstein(cosTheta, vapourG);

            result.vapourScatter += vapourTransmit * vapourDensity * stepSize * phase * lightColor;
            vapourTransmit *= stepTransmit;
        }

        if (liquidDensity > 0.0001)
        {
            if (result.liquidSurfaceFound < 0.5)
            {
                result.liquidSurfaceWS = samplePos;
                result.liquidSurfaceNormalWS = ComputeShapeNormalWS(
                    samplePos,
                    boundsMinOS, boundsMaxOS,
                    boundsCenter, boundsExtents,
                    edgeSoftness,
                    normalEpsWS
                );
                result.liquidSurfaceFound = 1.0;
            }

            float stepAlpha = saturate(liquidDensity * liquidOpacityCoeff * stepSize);
            result.liquidAlpha += (1.0 - result.liquidAlpha) * stepAlpha;
            result.liquidDepth += liquidDensity * stepSize;
        }

        if (vapourTransmit < 0.01 && result.liquidAlpha > 0.99)
            break;
    }

    result.vapourAlpha = 1.0 - vapourTransmit;
    return result;
}

// 24-parameter overload for backward compatibility
WaterPhaseMarchResult RaymarchWaterPhase(
    float3 rayOrigin, float3 rayDir,
    float3 lightDir, half3 lightColor,
    int marchSteps, float marchDistance,
    float vapourG, float vapourAbsorption,
    float liquidOpacityCoeff,
    float phaseThreshold, float phaseWidth,
    float time,
    float3 driftDir, float driftSpeed,
    float noiseScale, int octaves,
    float densityPower,
    float physicsDensity, float physicsBlend,
    float sceneLinearDepth,
    float3 boundsMinOS, float3 boundsMaxOS,
    float edgeSoftness,
    float2 screenUV)
{
    return RaymarchWaterPhaseLiquid(
        rayOrigin, rayDir, lightDir, lightColor,
        marchSteps, marchDistance,
        vapourG, vapourAbsorption, liquidOpacityCoeff,
        phaseThreshold, phaseWidth,
        time, driftDir, driftSpeed,
        noiseScale, octaves, densityPower,
        physicsDensity, physicsBlend,
        sceneLinearDepth, boundsMinOS, boundsMaxOS,
        edgeSoftness, screenUV);
}

#endif
