using UnityEngine;
using System;

/// <summary>
/// this script is used to create a game object that works as a heat source 
/// The ThermalReceiver on the receiving object will detect this
/// and mark the overlapping grid cells as fixed-temperature sources.
/// </summary>
public class HeatSourceObj : MonoBehaviour
{
    [Range(0f, 1000f)]
    public float temperature = 100f;

    // Called by ThermalReceiver to check if a world point is adjacent to this source
    public bool IsAdjacentToSource(Vector3 worldPoint, float threshold)
    {
        Collider col = GetComponent<Collider>();
        if (col == null) 
            return false;

        Vector3 closest = col.ClosestPoint(worldPoint);
        return (Vector3.Distance(closest, worldPoint) <= threshold);
    }
    public float GetTemperature()
    {
        return temperature;
    }
}