#ifndef WATER_PHASE_LIQUID_SHADING_INCLUDED
    #define WATER_PHASE_LIQUID_SHADING_INCLUDED

    // ── Depth-based liquid absorption tint (Beer-Lambert) ──
    // Shallow: mostly refracted scene tinted toward shallowColor.
    // Deep: scene is absorbed, trends toward deepColor.
    half3 CalculateLiquidDepthColor(half3 sceneColor, half3 shallowColor, half3 deepColor, float depth, float absorptionRate)
    {
        float absorption = exp(-depth * absorptionRate);
        half3 tintedScene = lerp(shallowColor, sceneColor, absorption);
        return lerp(deepColor, tintedScene, absorption);
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

#endif
