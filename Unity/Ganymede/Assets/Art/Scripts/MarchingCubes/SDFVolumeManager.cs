using UnityEngine;

public class SDFVolumeManager : MonoBehaviour
{
    [Header("Volume Settings")]
    [Tooltip("The physical size of the simulation boundary in meters")]
    public Vector3 volumeSize = new Vector3(10f, 10f, 10f);

    [Tooltip("The resolution of the grid (number of voxels along each axis)")]
    public Vector3Int gridResolution = new Vector3Int(32, 32, 32);

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(transform.position, volumeSize);
    }

    public Vector3 GetVoxelSize()
    {
        return new Vector3(
            volumeSize.x / gridResolution.x,
            volumeSize.y / gridResolution.y,
            volumeSize.z / gridResolution.z
        );
    }
}