   struct SimParams
   {
       float  dt;
       uint   particleCount;
       float  restDensity;
       float  gasStiffness;
       float  viscosity;
       float  smoothingRadius;
       float3 gravity;
       float  particleMass;
       float3 boundsMin;
       float  poly6Const;
       float3 boundsMax;
       float  spikyGradConst;
       uint3  gridDims;       // cells per axis
       float  viscLapConst;
       float3 gridOrigin;     // world-space origin of grid
       uint   maxCells;       // gridDims.x * gridDims.y * gridDims.z
       float  damping;        // per-frame velocity damping (e.g. 0.998)
       uint   bitonicK;       // bitonic sort stage  (power of 2)
       uint   bitonicJ;       // bitonic sort step   (power of 2)
       float  interactionStrength; // >0 attract, <0 repulse, 0 off
       float3 interactionPos;     // world-space interaction point
       float  interactionRadius;  // radius of effect
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
       float  _pad0;
       float  _pad1;
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
