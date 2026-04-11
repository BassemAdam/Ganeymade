using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Generates a signed distance field on the CPU from FluidBoundary objects.
/// Outputs a flat float[] array indexed as sdf[z * dimY * dimX + y * dimX + x].
/// Negative values = inside solid.
/// </summary>
public static class SDFGenerator
{
    /// <summary>
    /// Compute a flat SDF grid from the given boundaries.
    /// </summary>
    /// <param name="boundaries">List of FluidBoundary objects in the scene.</param>
    /// <param name="origin">World-space origin (min corner) of the SDF grid.</param>
    /// <param name="voxelSize">Size of each voxel in world units.</param>
    /// <param name="dimX">Grid cells along X.</param>
    /// <param name="dimY">Grid cells along Y.</param>
    /// <param name="dimZ">Grid cells along Z.</param>
    /// <returns>Flat float array of SDF values. Positive = outside, negative = inside solid.</returns>
    public static float[] Generate(
        IReadOnlyList<FluidBoundary> boundaries,
        Vector3 origin, float voxelSize,
        int dimX, int dimY, int dimZ)
    {
        int total = dimX * dimY * dimZ;
        float[] sdf = new float[total];

        // Init to large positive (far from any surface)
        float farDist = dimX * voxelSize + dimY * voxelSize + dimZ * voxelSize;
        for (int i = 0; i < total; i++)
            sdf[i] = farDist;

        if (boundaries == null || boundaries.Count == 0)
            return sdf;

        // For each voxel, compute union (min) of all boundary SDFs
        for (int z = 0; z < dimZ; z++)
        {
            float wz = origin.z + (z + 0.5f) * voxelSize;
            for (int y = 0; y < dimY; y++)
            {
                float wy = origin.y + (y + 0.5f) * voxelSize;
                int zy = z * dimY * dimX + y * dimX;
                for (int x = 0; x < dimX; x++)
                {
                    float wx = origin.x + (x + 0.5f) * voxelSize;
                    Vector3 worldPos = new Vector3(wx, wy, wz);

                    float minDist = farDist;
                    for (int b = 0; b < boundaries.Count; b++)
                    {
                        float d = BoundarySDF(boundaries[b], worldPos);
                        if (d < minDist) minDist = d;
                    }
                    sdf[zy + x] = minDist;
                }
            }
        }

        return sdf;
    }

    /// <summary>
    /// Compute the signed distance from a world point to a single FluidBoundary.
    /// Negative = inside the solid.
    /// </summary>
    static float BoundarySDF(FluidBoundary boundary, Vector3 worldPos)
    {
        switch (boundary.shape)
        {
            case FluidBoundary.BoundaryShape.Box:
                return BoxSDF(worldPos, boundary.Center, boundary.GetWorldHalfExtents(), boundary.transform.rotation);
            case FluidBoundary.BoundaryShape.Sphere:
                return SphereSDF(worldPos, boundary.Center, boundary.GetWorldRadius());
            default:
                // Mesh shape — fall back to AABB approximation until Phase 4
                return BoxSDF(worldPos, boundary.Center, boundary.GetWorldHalfExtents(), boundary.transform.rotation);
        }
    }

    /// <summary>Exact SDF for an oriented box.</summary>
    static float BoxSDF(Vector3 worldPos, Vector3 center, Vector3 halfExtents, Quaternion rotation)
    {
        // Transform point into box-local space
        Vector3 local = Quaternion.Inverse(rotation) * (worldPos - center);
        // Absolute value for symmetry
        Vector3 q = new Vector3(
            Mathf.Abs(local.x) - halfExtents.x,
            Mathf.Abs(local.y) - halfExtents.y,
            Mathf.Abs(local.z) - halfExtents.z
        );
        // Exterior distance (positive part)
        float exterior = new Vector3(
            Mathf.Max(q.x, 0f),
            Mathf.Max(q.y, 0f),
            Mathf.Max(q.z, 0f)
        ).magnitude;
        // Interior distance (negative when inside)
        float interior = Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f);
        return exterior + interior;
    }

    /// <summary>Exact SDF for a sphere.</summary>
    static float SphereSDF(Vector3 worldPos, Vector3 center, float radius)
    {
        return (worldPos - center).magnitude - radius;
    }

    /// <summary>
    /// Compute grid dimensions that cover the given bounds at the specified voxel size.
    /// </summary>
    public static void ComputeGridDims(
        Vector3 boundsMin, Vector3 boundsMax, float voxelSize,
        out int dimX, out int dimY, out int dimZ)
    {
        Vector3 size = boundsMax - boundsMin;
        dimX = Mathf.Max(1, Mathf.CeilToInt(size.x / voxelSize));
        dimY = Mathf.Max(1, Mathf.CeilToInt(size.y / voxelSize));
        dimZ = Mathf.Max(1, Mathf.CeilToInt(size.z / voxelSize));
    }
}
