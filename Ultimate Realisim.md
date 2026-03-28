# Ultimate Realisim

## Objective
Make water and water vapour look significantly more realistic by moving from the current single-scatter raymarch to physically based volumetrics, stable temporal rendering, and optional true hardware ray tracing.

## Current Baseline (What You Already Have)
- Voxel-bounded volumetric raymarch through density.
- Procedural FBM density with phase split (vapour vs liquid).
- Basic HG scattering and liquid opacity accumulation.
- Refraction and environment reflection blending.

This is a strong foundation. The biggest realism gains now come from better light transport, temporal stability, and physically grounded parameters.

## Path Decision First (Important)
Choose one of these two paths before deep implementation:

1. URP Advanced Volumetric Path
- Keep current URP shader architecture.
- Add physically based upgrades and temporal accumulation.
- Fastest route with large visual improvements.

2. HDRP DXR True Ray Tracing Path
- Migrate renderer to HDRP and enable DXR.
- Implement ray-traced volumetric integration, reflection, and refraction.
- Highest realism ceiling, highest complexity and hardware cost.

## Files To Edit Now
- Unity/Ganymede/Assets/Art/Shaders/Includes/WaterPhaseHelpers.hlsl
- Unity/Ganymede/Assets/Art/Shaders/Visualization/WaterPhase.shader

## Files To Add
- Unity/Ganymede/Assets/Art/Shaders/Includes/WaterOptics.hlsl
- Unity/Ganymede/Assets/Art/Shaders/Includes/WaterTemporal.hlsl
- Unity/Ganymede/Assets/Art/Shaders/Textures/BlueNoise256.png
- Unity/Ganymede/Assets/Scripts/Rendering/WaterPhaseTemporalPass.cs
- Unity/Ganymede/Assets/Scripts/Rendering/WaterPhaseQualityController.cs

If you choose HDRP DXR path, also add:
- Unity/Ganymede/Assets/Art/Shaders/RayTracing/WaterVolume.raytrace
- Unity/Ganymede/Assets/Scripts/Rendering/WaterRayTracingPass.cs

## Phase 1: Physically Based Volumetric Lighting (High Impact)
Goal: Better light behavior in vapour and liquid without changing pipeline yet.

### 1. Replace single HG with dual-lobe phase function
Edit in WaterPhaseHelpers.hlsl:
- Add a dual-lobe phase function to model strong forward scattering plus subtle backward haze.
- Formula:
  - phase = lerp(HG(cosTheta, g1), HG(cosTheta, g2), blend)

Recommended starting values:
- g1 = 0.75
- g2 = -0.2
- blend = 0.2

### 2. Add light-space transmittance raymarch (self-shadowing)
Edit in WaterPhaseHelpers.hlsl:
- For each camera march sample, cast a short secondary march toward main light.
- Integrate extinction to compute light visibility.
- Multiply local scattering by this visibility.

Result:
- Better cloud-like depth and internal shadowing.

### 3. Move to RGB extinction (wavelength-dependent absorption)
Edit in WaterPhase.shader and helpers:
- Replace scalar absorption with float3 extinction coefficients.
- Apply Beer-Lambert per channel:
  - T_rgb = exp(-sigma_rgb * density * distance)

Result:
- More natural blue-green depth shift in liquid.

## Phase 2: Temporal Stability and Detail Recovery
Goal: Remove flicker/noise and unlock more apparent detail at same step budget.

### 1. Blue-noise jitter sequence
Edit in WaterPhase.shader:
- Sample BlueNoise256 texture using pixel coords and frame index.
- Replace static jitter with blue-noise temporal jitter.

### 2. Temporal accumulation with history clamping
Add WaterPhaseTemporalPass.cs and WaterTemporal.hlsl:
- Reproject previous frame using motion vectors.
- Blend current and history color/transmittance.
- Use neighborhood clamping to avoid ghost trails.

Recommended start:
- historyBlend = 0.9
- clampStrength = 1.0

### 3. Adaptive step size + empty-space skipping
Edit in WaterPhaseHelpers.hlsl:
- Use larger steps in low-density regions.
- Use smaller steps near density gradients and liquid boundary.

Result:
- Higher quality for similar GPU cost.

## Phase 3: Better Liquid Interface Realism
Goal: Make water body read as real liquid, not just dense fog.

### 1. Surface normal from density gradient
Edit in WaterPhaseHelpers.hlsl:
- Compute gradient using central differences around sample position.
- Use normalized gradient as pseudo-surface normal near phase boundary.

### 2. Physically based Fresnel and IOR refraction
Edit in WaterPhase.shader:
- Add IOR parameter (water about 1.333).
- Use Schlick Fresnel with F0 from IOR.
- Use boundary normal for refraction offset direction.

### 3. Thin boundary sheen
Edit in WaterPhase.shader:
- Add boundary mask around phase transition.
- Add controlled specular boost only on boundary.

Result:
- Convincing wet glossy interface and stronger material separation.

## Phase 4: Multi-Scattering Approximation (Major Realism Jump)
Goal: Vapour should feel volumetric and soft, not only single-scatter sharp.

### 1. Add cheap multiple scattering term
Edit in WaterPhaseHelpers.hlsl:
- Approximate multi-scatter as boosted ambient in high optical depth.
- Drive by density integral and anisotropy.

### 2. Energy compensation
Edit in WaterPhase.shader:
- Clamp/renormalize to avoid overbright fog.
- Keep color in physically plausible range before tone mapping.

Result:
- Fuller, cinematic vapour body with realistic light wrapping.

## Phase 5: Scene Interaction Enhancements
Goal: Better integration with environment and camera.

### 1. Volumetric shadows from scene occluders
- Sample shadow map during light transmittance march.
- Modulate in-volume lighting by shadow term.

### 2. Screen-space reflections and fallback probes
- Add SSR for strong near reflections.
- Fallback to reflection probes where SSR misses.

### 3. Caustics contribution
- Project animated caustics onto nearby receiving surfaces.
- Intensity scaled by liquid thickness and light angle.

## Phase 6: Physics-Driven Fields (Instead of Pure Procedural FBM)
Goal: Tie visuals to simulation values for believable behavior.

### 1. Replace procedural density with 3D textures from simulation
- Density volume texture.
- Temperature volume texture.
- Velocity/advection texture.

### 2. Physically motivated phase transition
- Compute liquid/vapour ratio from density + temperature field.
- Keep procedural FBM only as detail modulation.

Result:
- Phase patterns follow dynamics, not random noise only.

## Optional Phase 7: True Hardware Ray Tracing (HDRP DXR)
Goal: Maximum visual realism if target hardware supports RT.

### Preconditions
- Switch project to HDRP.
- Enable DX12 and ray tracing support.
- Validate GPU/driver support.

### Implementation
1. Build acceleration structures for vessel geometry and relevant scene meshes.
2. In WaterVolume.raytrace, trace primary camera rays through water volume bounds.
3. Use stochastic volumetric integration (ratio tracking or delta tracking).
4. Shoot shadow rays for next-event estimation.
5. Trace reflection/refraction rays at dense liquid boundary.
6. Accumulate across frames with denoising.

### Denoising strategy
- Spatial bilateral pass + temporal accumulation.
- Optional integration with advanced denoisers if available.

## Recommended Property Additions
Add these material properties in WaterPhase.shader:
- _ExtinctionRGB (Color)
- _DualLobeG1 (Range -0.95 to 0.95)
- _DualLobeG2 (Range -0.95 to 0.95)
- _DualLobeBlend (Range 0 to 1)
- _ShadowMarchSteps (Range 4 to 48)
- _ShadowMarchDistance (Range 0.1 to 10)
- _TemporalBlend (Range 0 to 0.99)
- _HistoryClamp (Range 0 to 3)
- _IOR (Range 1.0 to 2.0)
- _BoundarySpecBoost (Range 0 to 4)

## Quality Tiers (Performance Safety)
Define 3 quality levels in WaterPhaseQualityController.cs:

1. Low
- March steps: 24
- Shadow steps: 6
- No temporal accumulation
- No multi-scatter

2. Medium
- March steps: 40
- Shadow steps: 12
- Temporal accumulation on
- Approx multi-scatter on

3. Ultra
- March steps: 64+
- Shadow steps: 20+
- Temporal + multi-scatter + SSR + caustics
- Optional DXR path

## Validation Checklist (Use Every Milestone)
1. Still frame realism in bright daylight.
2. Backlit shot realism (sun behind vapour).
3. Fast camera motion stability (flicker/ghosting check).
4. Night lighting response with emissive contribution.
5. Performance target by platform.
6. A/B compare against real-world reference clips.

## Suggested Build Order (Practical)
1. Phase 1 (lighting physics)
2. Phase 2 (temporal stability)
3. Phase 3 (liquid interface)
4. Phase 4 (multi-scattering)
5. Phase 5 (scene interaction)
6. Phase 6 (physics-driven fields)
7. Phase 7 (HDRP DXR, optional)

## Definition Of Done For "Super Realistic"
You are close to target when all are true:
- Vapour has believable depth, shadowing, and soft light wrapping.
- Liquid and vapour are clearly distinguishable with natural transition.
- Reflections/refractions feel anchored to scene geometry.
- Motion is temporally stable with minimal shimmer.
- Visual quality holds up in both daylight and night scenes.
- Performance remains inside your frame budget.

## Notes
- Keep the current shader as baseline branch before major changes.
- Introduce one realism feature at a time and capture visual comparisons.
- Prioritize temporal stability early; unstable realism looks fake even with complex shading.
