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
        // get res will need it to dimension compute shader dispatch and buffer sizes
        Vector3Int res = volumeManager.gridResolution;

        // generate SDF grid
        sdfGenerator.GenerateSDF();
        float[,,] grid3D = sdfGenerator.GetSDFGrid();
        float[] flatGrid = flatten3DArray(grid3D); // compute buffers carry 1d array

    
        // allocate & set data to compute shader Buffers
        ComputeBuffer sdfBuffer = new ComputeBuffer(flatGrid.Length, sizeof(float));
        sdfBuffer.SetData(flatGrid);

        // allocate Output buffer for triangles
        // a cube can have maximum in worst case 5 triangles 
        // max triangles = number of voxels * 5
        // number of voxels determined by grid resolution - 1 in each dimension
        // also dont forget each traignle have 3 verticies each vertex is a Vector3 (3 floats)
        // so so 5*9 floats per voxel
        int maxTriangles = res.x * res.y * res.z * 5;
        ComputeBuffer triangleBuffer = new ComputeBuffer(maxTriangles, sizeof(float) * 9, ComputeBufferType.Append);
        triangleBuffer.SetCounterValue(0);

        // Load data to compute shader
        int kernel = marchingShader.FindKernel("March");
        marchingShader.SetBuffer(kernel, "SDFGrid", sdfBuffer); // input
        marchingShader.SetVector("GridSize", new Vector3(res.x, res.y, res.z)); // input
        marchingShader.SetVector("VoxelSize", volumeManager.GetVoxelSize()); // input
        marchingShader.SetFloat("isoLevel", isoLevel); // input
        marchingShader.SetBuffer(kernel, "OutputTriangles", triangleBuffer); // output

        // Dispatch compute shader
        int threadGroupsX = Mathf.CeilToInt(res.x / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(res.y / 8.0f);
        int threadGroupsZ = Mathf.CeilToInt(res.z / 8.0f);
        marchingShader.Dispatch(kernel, threadGroupsX, threadGroupsY, threadGroupsZ);

        // Retrieve triangle data from GPU
        // since its an append so we dont really know how many triangles were generated 
        ComputeBuffer argBuffer = new ComputeBuffer(4, sizeof(int), ComputeBufferType.IndirectArguments); 
        ComputeBuffer.CopyCount(triangleBuffer, argBuffer, 0); // copy the count of triangles generated to argBuffer
        int[] args = new int[4];
        argBuffer.GetData(args);
        int triangleCount = args[0]; // the count of triangles generated

        // now lets get or read the exact triangle data from the GPU
        Triangle[] gpuTriangles = new Triangle[triangleCount];
        triangleBuffer.GetData(gpuTriangles, 0, 0, triangleCount); 

        // BUILD THE UNITY MESH XD
        ConstructUnityMesh(gpuTriangles);

        // Clean up
        sdfBuffer.Release();
        triangleBuffer.Release();
        argBuffer.Release();
    }

    private void ConstructUnityMesh(Triangle[] gpuTriangles)
        {
            Vector3[] vertices = new Vector3[gpuTriangles.Length * 3];
            int[] triangles = new int[gpuTriangles.Length * 3];

            for (int i = 0; i < gpuTriangles.Length; i++)
            {
                // Extract the 3 points of the triangle
                vertices[i * 3 + 0] = gpuTriangles[i].vertexA;
                vertices[i * 3 + 1] = gpuTriangles[i].vertexB;
                vertices[i * 3 + 2] = gpuTriangles[i].vertexC;

                // Tell Unity what order to draw them in
                triangles[i * 3 + 0] = i * 3 + 0;
                triangles[i * 3 + 1] = i * 3 + 2;
                triangles[i * 3 + 2] = i * 3 + 1;
            }

            Mesh mesh = new Mesh();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32; // Allows for high-poly meshes
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals(); // Automatically calculates light reflections!

            meshFilter.mesh = mesh;

            // Set the tag and layer of the GameObject to "Water"
            gameObject.tag = "Water";
            gameObject.layer = LayerMask.NameToLayer("Water");
        }


    //helpers

    private float[] flatten3DArray(float[,,] array3D)
    {
        int xSize = array3D.GetLength(0);
        int ySize = array3D.GetLength(1);
        int zSize = array3D.GetLength(2);

        float[] flatArray = new float[xSize * ySize * zSize];
        int index = 0;

        for (int z = 0; z < zSize; z++)
        {
            for (int y = 0; y < ySize; y++)
            {
                for (int x = 0; x < xSize; x++)
                {
                    flatArray[index++] = array3D[x, y, z];
                }
            }
        }

        return flatArray;
    }
}