# Needed Edits: Use Physics 3D Textures Instead of Procedural Noise

## Goal
Replace procedural noise-driven density in the water shader with physics engine volume data so water vapour, liquid regions, and motion match simulation results.

## What We Need From The Physics Engine

### Required 3D textures (same resolution, same voxel grid, same bounds)
1. Density volume
- Suggested name: _DensityTex3D
- Channels: R
- Format: R16 or R32_FLOAT
- Expected range: normalized [0, 1] or documented physical range mapped to [0, 1]
- Used for: absorption, opacity, phase split baseline

2. Temperature volume
- Suggested name: _TemperatureTex3D
- Channels: R
- Format: R16 or R32_FLOAT
- Expected range: either normalized [0, 1] or real units (Kelvin/Celsius) with provided min/max
- Used for: vapour/liquid transition bias, warm/cool tint, emission shaping

3. Velocity volume
- Suggested name: _VelocityTex3D
- Channels: RGB
- Format: RGBHalf or RGBAHalf
- Expected range: velocity in world space units per second (or sim-local units with conversion documented)
- Used for: advection (movement of rendered features), directional flow realism

### Strongly recommended extra scalar field (the "something else")
Use one of these as a 4th optional field:
1. Phase fraction (best option)
- Suggested name: _PhaseTex3D
- Range: 0 = vapour, 1 = liquid
- Benefit: physically direct phase rendering without heuristic threshold guessing

2. Pressure (alternative)
- Suggested name: _PressureTex3D
- Benefit: denser compression regions, better cloud/liquid body shaping

3. Vorticity magnitude (alternative)
- Suggested name: _VorticityTex3D
- Benefit: turbulence enhancement for highlights and edge activity

## Metadata Required From Physics Engine
Provide these values every update:
1. Simulation bounds in world space
- _SimBoundsMinWS (xyz)
- _SimBoundsMaxWS (xyz)

2. Grid information
- Resolution (Nx, Ny, Nz)
- Cell size

3. Timing
- Simulation delta time (for advection)

4. Data contract
- Confirm if textures are linear (not sRGB)
- Confirm coordinate convention and handedness
- Confirm velocity space (world or local)

## Realism Requirements
To get visually realistic water/vapour and movement:
1. All three textures must be spatially aligned (same bounds and resolution).
2. Values must be temporally stable (avoid random remapping each frame).
3. Trilinear filtering should be enabled for smooth sampling.
4. Clamped addressing is preferred to avoid wrapping artifacts.
5. Temperature and density ranges must be documented and consistent.

## Files To Change
1. Unity/Ganymede/Assets/Art/Shaders/Includes/WaterPhaseHelpers.hlsl
2. Unity/Ganymede/Assets/Art/Shaders/Visualization/WaterPhase.shader
3. Optional bridge script for binding textures each frame (C#)

## Shader Changes In Detail

### 1) Add new shader properties in WaterPhase.shader
Add these properties:

```hlsl
_DensityTex3D ("Density 3D", 3D) = "" {}
_TemperatureTex3D ("Temperature 3D", 3D) = "" {}
_VelocityTex3D ("Velocity 3D", 3D) = "" {}
_PhaseTex3D ("Phase 3D (Optional)", 3D) = "" {}

_SimBoundsMinWS ("Sim Bounds Min WS", Vector) = (0,0,0,0)
_SimBoundsMaxWS ("Sim Bounds Max WS", Vector) = (1,1,1,0)
_SimDeltaTime ("Sim Delta Time", Float) = 0.0167
_VelocityAdvection ("Velocity Advection", Range(0,2)) = 1.0

_TemperatureMin ("Temperature Min", Float) = 0.0
_TemperatureMax ("Temperature Max", Float) = 1.0
_ColdPhaseBias ("Cold Phase Bias", Float) = 0.10
_HotPhaseBias ("Hot Phase Bias", Float) = 0.10
_UsePhaseTex ("Use Phase Texture", Range(0,1)) = 0
```

Declare texture samplers in HLSLPROGRAM:

```hlsl
TEXTURE3D(_DensityTex3D);      SAMPLER(sampler_DensityTex3D);
TEXTURE3D(_TemperatureTex3D);  SAMPLER(sampler_TemperatureTex3D);
TEXTURE3D(_VelocityTex3D);     SAMPLER(sampler_VelocityTex3D);
TEXTURE3D(_PhaseTex3D);        SAMPLER(sampler_PhaseTex3D);
```

Add matching CBUFFER variables.

### 2) Replace noise-based sampling in WaterPhaseHelpers.hlsl
Current path uses:
- ValueNoise3D
- FBM
- SampleDensity(worldPos, ...)

New path should sample physics textures directly.

Add helper conversion:

```hlsl
float3 WorldToSimUVW(float3 worldPos, float3 simMinWS, float3 simMaxWS)
{
    float3 sizeWS = max(simMaxWS - simMinWS, 1e-5);
    return saturate((worldPos - simMinWS) / sizeWS);
}
```

Add sampling helpers:

```hlsl
float SampleDensityTex(float3 uvw)
{
    return SAMPLE_TEXTURE3D(_DensityTex3D, sampler_DensityTex3D, uvw).r;
}

float SampleTemperatureTex(float3 uvw)
{
    return SAMPLE_TEXTURE3D(_TemperatureTex3D, sampler_TemperatureTex3D, uvw).r;
}

float3 SampleVelocityTex(float3 uvw)
{
    return SAMPLE_TEXTURE3D(_VelocityTex3D, sampler_VelocityTex3D, uvw).xyz;
}

float SamplePhaseTex(float3 uvw)
{
    return SAMPLE_TEXTURE3D(_PhaseTex3D, sampler_PhaseTex3D, uvw).r;
}
```

### 3) Update raymarch logic to use sampled fields
Inside RaymarchWaterPhase loop, replace procedural density block with:

```hlsl
float3 uvw = WorldToSimUVW(samplePos, _SimBoundsMinWS.xyz, _SimBoundsMaxWS.xyz);
float3 velocityWS = SampleVelocityTex(uvw);

float3 advectedPos = samplePos - velocityWS * _SimDeltaTime * _VelocityAdvection;
float3 advUVW = WorldToSimUVW(advectedPos, _SimBoundsMinWS.xyz, _SimBoundsMaxWS.xyz);

float density = SampleDensityTex(advUVW) * shapeMask;
float temperature = SampleTemperatureTex(advUVW);

float temp01 = saturate((temperature - _TemperatureMin) / max(_TemperatureMax - _TemperatureMin, 1e-5));
float tempShiftedThreshold = _DensityPhaseThreshold + lerp(_ColdPhaseBias, -_HotPhaseBias, temp01);

float liquidPhase = (_UsePhaseTex > 0.5)
    ? saturate(SamplePhaseTex(advUVW))
    : smoothstep(tempShiftedThreshold - _PhaseTransitionWidth,
                 tempShiftedThreshold + _PhaseTransitionWidth,
                 density);

float vapourPhase = 1.0 - liquidPhase;
```

This keeps your existing scattering/absorption math but drives it by physics fields.

### 4) Extend march result to carry useful averages
Add to WaterPhaseMarchResult:
- avgTemperature
- avgVelocityMagnitude

Accumulate weighted averages during marching. Use these in frag to improve:
1. Warm/cool vapour tint from avgTemperature
2. Emission strength from hot regions
3. Optional turbulence tint/intensity from velocity magnitude

### 5) Keep old noise path as fallback (recommended)
Add a keyword toggle:

```hlsl
#pragma shader_feature_local _USE_PHYSICS_VOLUME
```

If keyword disabled, keep old FBM path. This helps debugging and comparison.

## Fragment Shader Side (WaterPhase.shader)
1. Keep existing ray setup and depth intersection.
2. Call updated RaymarchWaterPhase.
3. Use result.avgTemperature for color blend:
- cold = more _VapourCoolColor
- hot = more _VapourWarmColor and emission
4. Keep liquid reflection/refraction path, but liquid alpha now follows physics density/phase.

## C# Binding Layer (Physics -> Material)
Each frame (or simulation step), set:

```csharp
material.SetTexture("_DensityTex3D", densityTex);
material.SetTexture("_TemperatureTex3D", temperatureTex);
material.SetTexture("_VelocityTex3D", velocityTex);
material.SetTexture("_PhaseTex3D", phaseTexOptional);

material.SetVector("_SimBoundsMinWS", boundsMinWS);
material.SetVector("_SimBoundsMaxWS", boundsMaxWS);
material.SetFloat("_SimDeltaTime", simDeltaTime);
material.SetFloat("_UsePhaseTex", phaseTexOptional != null ? 1f : 0f);
```

Important import settings for 3D textures:
1. sRGB off (linear)
2. Wrap mode clamp
3. Filter mode trilinear
4. Precision high enough for stable density gradients

## Visual Mapping Recommendation
1. Density
- Controls extinction, vapour alpha growth, liquid alpha growth

2. Temperature
- Controls phase threshold bias
- Controls warm/cool tint and emission

3. Velocity
- Controls advection direction and speed
- Optional secondary modulation for detail intensity

4. Optional phase fraction
- Directly controls vapour vs liquid split

## Validation Checklist
1. Features move in flow direction from velocity field.
2. Hot regions stay more vapour-like, cold dense regions condense.
3. No visible seams at sim bounds edges.
4. Turning simulation textures off reproduces old procedural look (fallback path).
5. Camera motion does not cause swimming artifacts (advection stable in world space).

## Implementation Order (Recommended)
1. Add texture properties and uniforms.
2. Add world-to-volume UVW helper and direct texture sampling.
3. Replace noise density in raymarch loop.
4. Integrate temperature-based phase bias.
5. Add optional phase texture override.
6. Bind textures and bounds from C#.
7. Tune thresholds, absorption, and advection strength with test scenes.
