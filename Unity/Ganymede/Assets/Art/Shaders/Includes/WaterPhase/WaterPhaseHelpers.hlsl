#ifndef WATER_PHASE_HELPERS_INCLUDED
    #define WATER_PHASE_HELPERS_INCLUDED

    // Keep this include path stable.
    // This file is now an aggregator that groups helpers by purpose.
    //
    // Dependency order matters:
    // - Geometry defines ComputeEdgeFade used by Shape
    // - Shape + Noise + Lighting are required by Raymarch
    #include "WaterPhaseGeometry.hlsl"
    #include "WaterPhaseShape.hlsl"
    #include "WaterPhaseNoise.hlsl"
    #include "WaterPhaseLighting.hlsl"
    #include "WaterPhaseLiquidShading.hlsl"
    #include "WaterPhaseRaymarch.hlsl"

#endif
