using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(SDFVolumeManager))]
public class SDFGenerator : MonoBehaviour
{
    private SDFVolumeManager volumeManager;
    
    [Header("Targeting")]
    [Tooltip("Put all your water voxels in this list for now to test.")]
    public List<Transform> waterObjects;

    [Header("Debug Gizmos")]
    [SerializeField, Min(0f)] public float GizmoRadius = 0.1f;

    // This 3D array will hold our mathematical distances
    private float[,,] sdfGrid;

    void Start()
    {
        volumeManager = GetComponent<SDFVolumeManager>();
        GenerateSDF();
    }

    // You can also link this to a button or call it in Update if voxels move
    [ContextMenu("Generate SDF")] 
    public void GenerateSDF()
    {
        if (volumeManager == null) volumeManager = GetComponent<SDFVolumeManager>();

        Vector3Int res = volumeManager.gridResolution;
        sdfGrid = new float[res.x, res.y, res.z];
        
        Vector3 spacing = volumeManager.GetVoxelSize();
        Vector3 startPos = transform.position - (volumeManager.volumeSize / 2f) + (spacing / 2f);

        // Loop through every X, Y, and Z point in our grid
        for (int x = 0; x < res.x; x++)
        {
            for (int y = 0; y < res.y; y++)
            {
                for (int z = 0; z < res.z; z++)
                {
                    // Calculate exactly where this point is in the Unity world
                    Vector3 worldPos = startPos + new Vector3(x * spacing.x, y * spacing.y, z * spacing.z);
                    
                    // Find the SDF value for this specific point
                    sdfGrid[x, y, z] = CalculateDistanceToWater(worldPos);
                }
            }
        }
        
        Debug.Log("SDF Generation Complete!");
    }

    private float CalculateDistanceToWater(Vector3 worldPoint)
    {
        // Start with an infinitely huge distance
        float minDistance = float.MaxValue;

        foreach (Transform waterObj in waterObjects)
        {
            // 1. Convert the world point to the local space of the box. 
            Vector3 localPoint = waterObj.InverseTransformPoint(worldPoint);
            
            // 2. The Box SDF Equation (half-extents is exactly 0.5 for a standard Unity Cube)
            Vector3 halfExtents = Vector3.one * 0.5f; 
            
            Vector3 q = new Vector3(Mathf.Abs(localPoint.x) - halfExtents.x, 
                                    Mathf.Abs(localPoint.y) - halfExtents.y, 
                                    Mathf.Abs(localPoint.z) - halfExtents.z);

            float outsideDist = Vector3.Max(q, Vector3.zero).magnitude;
            float insideDist = Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0.0f);
            
            // 3. Final distance for this specific box
            float boxDistance = outsideDist + insideDist;

            // 4. Union operation: Keep the smallest distance (closest to water)
            minDistance = Mathf.Min(minDistance, boxDistance);
        }

        return minDistance;
    }

    public float[,,] GetSDFGrid()
    {
        return sdfGrid;
    }

    // VISUALIZATION: Let's see the math!
    private void OnDrawGizmosSelected()
    {
        if (sdfGrid == null || volumeManager == null) return;

        Vector3 spacing = volumeManager.GetVoxelSize();
        Vector3 startPos = transform.position - (volumeManager.volumeSize / 2f) + (spacing / 2f);

        for (int x = 0; x < sdfGrid.GetLength(0); x++)
        {
            for (int y = 0; y < sdfGrid.GetLength(1); y++)
            {
                for (int z = 0; z < sdfGrid.GetLength(2); z++)
                {
                    Vector3 worldPos = startPos + new Vector3(x * spacing.x, y * spacing.y, z * spacing.z);
                    float distance = sdfGrid[x, y, z];

                    // If distance is negative (inside water), draw a BLUE sphere
                    // If distance is positive (outside air), draw a small RED sphere
                    if (distance <= 0f)
                    {
                        Gizmos.color = new Color(0, 0.5f, 1f, 0.8f); 
                        Gizmos.DrawSphere(worldPos, GizmoRadius);
                    }
                    else
                    {
                        Gizmos.color = new Color(1f, 0, 0, 0.2f); 
                        Gizmos.DrawSphere(worldPos, GizmoRadius);
                    }
                }
            }
        }
    }
}