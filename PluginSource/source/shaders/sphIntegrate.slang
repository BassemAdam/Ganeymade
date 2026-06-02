import particle;

[[vk::push_constant]]
ConstantBuffer<SimParams> params;

[[vk::binding(0, 0)]]
StructuredBuffer<Particle> particlesIn;
[[vk::binding(1, 0)]]
RWStructuredBuffer<Particle> particlesOut;
[[vk::binding(2, 0)]]
StructuredBuffer<uint> hashKeys;
[[vk::binding(3, 0)]]
StructuredBuffer<uint> indices;
[[vk::binding(4, 0)]]
StructuredBuffer<uint> startIndices;
[[vk::binding(5, 0)]]
StructuredBuffer<float> sdfGrid;
[[vk::binding(6, 0)]]
StructuredBuffer<DrainZone> drains;

// SDF sampling helpers
// Buffer layout: [0]=originX [1]=originY [2]=originZ [3]=pad, then SDF data at index 4+

static const int SDF_HEADER = 4; // floats before actual SDF data

float sampleSDF(float3 worldPos)
{
    float3 origin = float3(sdfGrid[0], sdfGrid[1], sdfGrid[2]);
    float3 local = (worldPos - origin) / params.sdfVoxelSize;
    int3 dims = int3(params.sdfDims);

    // Floor index and fractional part for trilinear interpolation
    int3 i0 = int3(floor(local));
    float3 f = local - float3(i0);

    // Clamp to valid range
    int3 i1 = i0 + int3(1, 1, 1);
    i0 = clamp(i0, int3(0, 0, 0), dims - int3(1, 1, 1));
    i1 = clamp(i1, int3(0, 0, 0), dims - int3(1, 1, 1));

    int dX = 1;
    int dY = dims.x;
    int dZ = dims.x * dims.y;

    // 8 corner samples (offset by SDF_HEADER)
    float c000 = sdfGrid[SDF_HEADER + i0.z * dZ + i0.y * dY + i0.x * dX];
    float c100 = sdfGrid[SDF_HEADER + i0.z * dZ + i0.y * dY + i1.x * dX];
    float c010 = sdfGrid[SDF_HEADER + i0.z * dZ + i1.y * dY + i0.x * dX];
    float c110 = sdfGrid[SDF_HEADER + i0.z * dZ + i1.y * dY + i1.x * dX];
    float c001 = sdfGrid[SDF_HEADER + i1.z * dZ + i0.y * dY + i0.x * dX];
    float c101 = sdfGrid[SDF_HEADER + i1.z * dZ + i0.y * dY + i1.x * dX];
    float c011 = sdfGrid[SDF_HEADER + i1.z * dZ + i1.y * dY + i0.x * dX];
    float c111 = sdfGrid[SDF_HEADER + i1.z * dZ + i1.y * dY + i1.x * dX];

    // Trilinear blend
    float c00 = lerp(c000, c100, f.x);
    float c10 = lerp(c010, c110, f.x);
    float c01 = lerp(c001, c101, f.x);
    float c11 = lerp(c011, c111, f.x);
    float c0 = lerp(c00, c10, f.y);
    float c1 = lerp(c01, c11, f.y);
    return lerp(c0, c1, f.z);
}

float3 sdfGradient(float3 worldPos)
{
    float eps = params.sdfVoxelSize;
    float3 grad = float3(
        sampleSDF(worldPos + float3(eps, 0, 0)) - sampleSDF(worldPos - float3(eps, 0, 0)),
        sampleSDF(worldPos + float3(0, eps, 0)) - sampleSDF(worldPos - float3(0, eps, 0)),
        sampleSDF(worldPos + float3(0, 0, eps)) - sampleSDF(worldPos - float3(0, 0, eps))
    );
    float len = length(grad);
    return len > 1e-8 ? grad / len : float3(0, 1, 0);
}

// Cohesion kernel (Akinci 2013, clamped)
// Peaks at r ≈ h/2, zero at r=0 and r=h, models mid-range liquid attraction.
// Clamped to >= 0: pressure handles close-range repulsion; cohesion is attraction only.
float cohesionKernel(float r, float h)
{
    float h3 = h * h * h;
    float h9 = h3 * h3 * h3;
    float coeff = 32.0 / (3.14159265 * h9);
    float hr3 = (h - r) * (h - r) * (h - r);
    float r3  = r * r * r;
    float val;
    if (r <= 0.5 * h)
        val = coeff * (2.0 * hr3 * r3 - h9 / 64.0);
    else
        val = coeff * hr3 * r3;
    return max(0.0, val); // clamp: no repulsive cohesion at very close range
}

// Main compute kernel

[shader("compute")]
[numthreads(256, 1, 1)]
void computeMain(uint3 threadId: SV_DispatchThreadID)
{
   uint i = threadId.x;
   if (i >= params.particleCount)
      return;

   Particle p = particlesIn[i];
   if (p.phase == -1)
   {
      particlesOut[i] = p;
      return;
   }

   // Boundary particles: immovable, never integrate forces.
   if (p.phase == 2)
   {
      p.velocity = float3(0.0);
      p.acceleration = float3(0.0);
      particlesOut[i] = p;
      return;
   }
   float h = params.smoothingRadius;
   float h2 = h * h;
   uint n = params.particleCount;

   int3 myCell = getCellCoord(p.position, params.gridOrigin, h);

   float3 fPressure = float3(0.0);
   float3 fViscosity = float3(0.0);
   float3 fCohesion  = float3(0.0);
   float3 fBoundary  = float3(0.0);

   for (int dz = -1; dz <= 1; dz++)
   for (int dy = -1; dy <= 1; dy++)
   for (int dx = -1; dx <= 1; dx++)
   {
      uint key = hashCell(myCell + int3(dx, dy, dz), n);
      uint idx = startIndices[key];
      if (idx == 0xFFFFFFFF) continue;
      while (idx < n && hashKeys[idx] == key)
      {
         uint j = indices[idx];
         if (j != i)
         {
            Particle pj = particlesIn[j];
            float3 diff = p.position - pj.position;
            float r2 = dot(diff, diff);
            if (r2 < h2 && r2 > 1e-12)
            {
               float r = sqrt(r2);
               float3 dir = diff / r;

               // Boundary particle: repulsive penalty force
               if (pj.phase == 2)
               {
                  float penetration = 1.0 - r / h; // 1 at center, 0 at edge
                  fBoundary += dir * params.gasStiffness * penetration * penetration;
                  idx++;
                  continue;
               }

               float hr = h - r;
               float3 spikyGrad = params.spikyGradConst * (hr * hr) * dir;

               // Monaghan tensile instability correction: adds small repulsion
               // when both particles have negative pressure to prevent clumping.
               float tensileCorr = 0.0;
               if (params.tensileK > 0.0 && p.pressure < 0.0 && pj.pressure < 0.0)
               {
                  float wRef = h2 - (0.5 * h) * (0.5 * h); // poly6 at r=h/2
                  float wCur = h2 - r2;
                  float sRatio = wCur / max(wRef, 1e-10);
                  tensileCorr = params.tensileK * 0.1 * (sRatio * sRatio * sRatio * sRatio);
               }

               fPressure += -params.particleMass * (p.pressure + pj.pressure + tensileCorr) / (2.0 * pj.density) * spikyGrad;

               float viscLap = params.viscLapConst * hr;
               // Phase-dependent viscosity: gas uses lower viscosity for turbulent motion
               float visc = (p.phase == 1) ? params.gasViscosity : params.viscosity;
               fViscosity += params.particleMass * visc * (pj.velocity - p.velocity) / pj.density * viscLap;

               // Cohesion: liquid-liquid only. attracts particles toward each other at mid-range
               if (p.phase == 0 && pj.phase == 0 && params.cohesionStrength > 0.0)
               {
                  float ck = cohesionKernel(r, h);
                  fCohesion += -params.cohesionStrength * params.particleMass * ck * dir;
               }
            }
         }
         idx++;
      }
   }

   float3 sphAccel = (fPressure + fViscosity + fCohesion) / p.density + fBoundary;
   p.acceleration = sphAccel;

   // Buoyancy: gas particles always rise at full strength
   float3 buoyancy = float3(0.0);
   if (p.phase == 1)
   {
       buoyancy = float3(0.0, params.gasBuoyancy, 0.0);
   }

   // Boundary repulsion: compensates for SPH kernel truncation at walls
   // Without this, wall-adjacent particles lose ~half their kernel support,
   // underestimating density and producing a pressure gradient that pushes
   // particles into the wall instead of away from it.
   float3 boundaryForce = float3(0.0);
   float boundaryStiff = params.gasStiffness / h;
   float3 dLo = p.position - params.boundsMin;
   float3 dHi = params.boundsMax - p.position;
   if (dLo.x > 0.0 && dLo.x < h) { float q = 1.0 - dLo.x / h; boundaryForce.x += boundaryStiff * q * q; }
   if (dLo.y > 0.0 && dLo.y < h) { float q = 1.0 - dLo.y / h; boundaryForce.y += boundaryStiff * q * q; }
   if (dLo.z > 0.0 && dLo.z < h) { float q = 1.0 - dLo.z / h; boundaryForce.z += boundaryStiff * q * q; }
   if (dHi.x > 0.0 && dHi.x < h) { float q = 1.0 - dHi.x / h; boundaryForce.x -= boundaryStiff * q * q; }
   if (dHi.y > 0.0 && dHi.y < h) { float q = 1.0 - dHi.y / h; boundaryForce.y -= boundaryStiff * q * q; }
   if (dHi.z > 0.0 && dHi.z < h) { float q = 1.0 - dHi.z / h; boundaryForce.z -= boundaryStiff * q * q; }

   // Interaction force: attract or repulse particles toward/away from a point
   float3 interactAccel = float3(0.0);
   if (params.interactionStrength != 0.0)
   {
      float3 toTarget = params.interactionPos - p.position;
      float dist = length(toTarget);
      if (dist < params.interactionRadius && dist > 1e-6)
      {
         float3 dir = toTarget / dist;
         float falloff = 1.0 - (dist / params.interactionRadius);
         interactAccel = dir * params.interactionStrength * falloff;
      }
   }

   p.velocity += (sphAccel + params.gravity + interactAccel + boundaryForce + buoyancy) * params.dt;
   // Phase-dependent damping: gas retains more momentum
   float damp = (p.phase == 1) ? min(params.damping + 0.001, 1.0) : params.damping;
   p.velocity *= damp;

   // Velocity magnitude clamp before position update. otherwise an unclamped
   // velocity moves the particle too far in a single frame.
   float maxVel = 30.0;
   float vel2 = dot(p.velocity, p.velocity);
   if (vel2 > maxVel * maxVel)
      p.velocity *= maxVel / sqrt(vel2);

   p.position += p.velocity * params.dt;

   // SDF collision
   // Skin offset is baked into the SDF data on CPU (subtracted before upload),
   // so dist=0 is the desired keep-out surface. dist < 0 = penetrated.
   // Multiple iterations handle deep penetrations (e.g. enlarged surfaces).
   if (params.sdfEnabled != 0 && params.sdfVoxelSize > 0.0)
   {
      // Push-out iterations
      for (int sdfIter = 0; sdfIter < 4; sdfIter++)
      {
         float dist = sampleSDF(p.position);
         if (dist >= 0.0)
            break;
         float3 normal = sdfGradient(p.position);
         p.position -= normal * dist;
      }

      // Velocity correction + friction for particles near the surface
      float distFinal = sampleSDF(p.position);
      float dampZone = params.sdfVoxelSize * 2.0;
      if (distFinal < dampZone)
      {
         float3 normal = sdfGradient(p.position);
         float proximity = 1.0 - saturate(distFinal / dampZone);

         // Only zero normal velocity if moving INTO the surface (prevents oscillation)
         float vn = dot(p.velocity, normal);
         if (vn < 0.0)
            p.velocity -= vn * normal;

         // Pre-compensate gravity toward surface
         float gn = dot(params.gravity, normal);
         if (gn < 0.0)
            p.velocity -= gn * params.dt * normal;

         // Configurable surface friction (tangential damping)
         if (params.sdfFriction > 0.0)
         {
            float3 tangentVel = p.velocity - dot(p.velocity, normal) * normal;
            float frictionStrength = params.sdfFriction * 0.5 * proximity;
            p.velocity -= tangentVel * frictionStrength;
         }
      }
   }

   // DISABLED Drain zones: deactivate particles inside any active drain
   for (uint d = 0; d < 16; d++)
   {
      if (drains[d].active == 0) continue;
      float3 drainPos = float3(drains[d].position[0], drains[d].position[1], drains[d].position[2]);
      float drainDist = length(p.position - drainPos);
      if (drainDist < drains[d].radius)
      {
         p.position = float3(1e10, 1e10, 1e10);
         p.velocity = float3(0, 0, 0);
         p.phase = -1;
         break;
      }
   }

   // AABB boundary collision with gravity pre-compensation and surface friction.
   float eps = h * 0.01;
   float aabbFriction = 0.3;
   float3 bMin = params.boundsMin + float3(eps, eps, eps);
   float3 bMax = params.boundsMax - float3(eps, eps, eps);
   if (p.position.x < bMin.x) { p.position.x = bMin.x; p.velocity.x = 0; if (params.gravity.x < 0) p.velocity.x -= params.gravity.x * params.dt; p.velocity.yz *= (1.0 - aabbFriction); }
   if (p.position.y < bMin.y) { p.position.y = bMin.y; p.velocity.y = 0; if (params.gravity.y < 0) p.velocity.y -= params.gravity.y * params.dt; p.velocity.xz *= (1.0 - aabbFriction); }
   if (p.position.z < bMin.z) { p.position.z = bMin.z; p.velocity.z = 0; if (params.gravity.z < 0) p.velocity.z -= params.gravity.z * params.dt; p.velocity.xy *= (1.0 - aabbFriction); }
   if (p.position.x > bMax.x) { p.position.x = bMax.x; p.velocity.x = 0; if (params.gravity.x > 0) p.velocity.x -= params.gravity.x * params.dt; p.velocity.yz *= (1.0 - aabbFriction); }
   if (p.position.y > bMax.y) { p.position.y = bMax.y; p.velocity.y = 0; if (params.gravity.y > 0) p.velocity.y -= params.gravity.y * params.dt; p.velocity.xz *= (1.0 - aabbFriction); }
   if (p.position.z > bMax.z) { p.position.z = bMax.z; p.velocity.z = 0; if (params.gravity.z > 0) p.velocity.z -= params.gravity.z * params.dt; p.velocity.xy *= (1.0 - aabbFriction); }

   particlesOut[i] = p;
}
