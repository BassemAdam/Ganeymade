   struct SimParams
   {
       float  dt;
       uint   particleCount;
       float  restDensity;
       float  gasStiffness;
       float  viscosity;
       float  smoothingRadius;
       float  sdfVoxelSize;    // SDF grid cell size (0 = no SDF)
       float  _pad1;
       float3 gravity;
       float  particleMass;
       float3 boundsMin;
       float  poly6Const;
       float3 boundsMax;
       float  spikyGradConst;
       uint3  sdfDims;        // SDF grid voxels per axis (0,0,0 = disabled)
       float  viscLapConst;
       float3 gridOrigin;     // world-space origin of grid (also SDF origin)
       uint   sdfEnabled;     // 1 = SDF collision active, 0 = off
       float  damping;        // per-frame velocity damping (e.g. 0.998)
       uint   bitonicK;       // bitonic sort stage  (power of 2)
       uint   bitonicJ;       // bitonic sort step   (power of 2)
       float  interactionStrength; // >0 attract, <0 repulse, 0 off
       float3 interactionPos;     // world-space interaction point
       float  interactionRadius;  // radius of effect
       float  thermalDiffusivity;
       float  ambientTemperature;
       float  coolingRate;
       float  boilingTemperature;   // temperature threshold for liquid→gas
       float  latentHeat;           // energy required for full phase transition
       float  gasRestDensity;         // rest density for gas phase (much lower than liquid)
       float  gasViscosity;           // viscosity for gas phase (lower → turbulent)
       float  gasBuoyancy;            // upward acceleration for gas particles
       float  pressureBoilingScale;   // how much local pressure raises the effective boiling point
       float  cohesionStrength;       // attractive force between nearby liquid particles (surface tension)
   };

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
       float  latentHeatAccum;   // energy absorbed/released during phase change
       float  _pad1;
   };

   struct HeatSource
    {
        float position[3];   // world-space center
        float radius;        // influence radius 
        float temperature;   // fixed temperature for particles inside
        uint active;     // 1 = active, 0 = inactive (pad to 24 bytes)
        uint _pad0;
        uint _pad1;
    };

   struct DrainZone
   {
       float position[3];   // world-space center
       float radius;        // sphere drain radius
       uint  active;        // 1 = active, 0 = inactive
       uint  _pad0;
       uint  _pad1;
       uint  _pad2;
   };

   // Spatial hash: large primes, unsigned XOR, mod tableSize
   uint hashCell(int3 cell, uint tableSize)
   {
       uint h = (uint(cell.x) * 73856093u)
              ^ (uint(cell.y) * 19349663u)
              ^ (uint(cell.z) * 83492791u);
       return h % tableSize;
   }

   int3 getCellCoord(float3 pos, float3 origin, float cellSize)
   {
       return int3(floor((pos - origin) / cellSize));
   }
