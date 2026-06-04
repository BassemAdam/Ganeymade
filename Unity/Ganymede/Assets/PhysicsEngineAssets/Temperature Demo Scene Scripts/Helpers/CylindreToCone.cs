using UnityEngine;

[ExecuteInEditMode]
public class CylinderToCone : MonoBehaviour
{
    void Start()
    {
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null) 
            return;

        Mesh mesh = Instantiate(meshFilter.sharedMesh);
        meshFilter.mesh = mesh;

        Vector3[] vertices = mesh.vertices;

        // In Unity's default cylinder, vertices with a Y value of 1.0f form the top cap
        for (int i = 0; i < vertices.Length; i++)
        {
            if (Mathf.Approximately(vertices[i].y, 1.0f))
            {
                // Collapse all top vertices into the center point line
                vertices[i].x = 0;
                vertices[i].z = 0;
            }
        }

        mesh.vertices = vertices;
        mesh.RecalculateNormals(); 
        mesh.RecalculateBounds();
    }
}