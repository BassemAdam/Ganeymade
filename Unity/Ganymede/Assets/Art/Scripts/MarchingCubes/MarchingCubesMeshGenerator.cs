using UnityEngine;

[RequireComponent(typeof(SDFVolumeManager))]
[RequireComponent(typeof(SDFGenerator))]
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class MarchingCubesMeshGenerator : MonoBehaviour
{
    [Header("References")]
    public ComputeShader marchingShader;
    public Material waterMaterial;

    [Header("Settings")]
    public float isoLevel = 0.0f; // surface level for the marching cubes algorithm

    private SDFVolumeManager volumeManager;
    private SDFGenerator sdfGenerator;
    private MeshFilter meshFilter;

    // Match GPU Data Structures
    struct Triangle
    {
        public Vector3 vertexA;
        public Vector3 vertexB;
        public Vector3 vertexC;
    }

    void Start()
    {
        volumeManager = GetComponent<SDFVolumeManager>();
        sdfGenerator = GetComponent<SDFGenerator>();
        meshFilter = GetComponent<MeshFilter>();
        GetComponent<MeshRenderer>().material = waterMaterial;
    }

    [ContextMenu("Generate Water Mesh")]
    public void GeneraterMesh()
    {

    }

    private void ConstructUnityMesh(Triangle[] triangles)
    {
        
    }

}