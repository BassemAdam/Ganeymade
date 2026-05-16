#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Batch tool: enables Read/Write on every model asset (and standalone Mesh asset)
/// in the project so that Mesh.GetVertices / Mesh.GetIndices work in builds.
/// Required by VoxelTracerSystem.AppendMesh — see Tools/Voxel/Enable Read-Write On All Meshes.
/// </summary>
public static class EnableMeshReadWriteBatch
{
    private const string MENU_ALL       = "Tools/Voxel/Enable Read-Write On All Meshes";
    private const string MENU_SELECTION = "Tools/Voxel/Enable Read-Write On Selected Meshes";
    private const string MENU_REPORT    = "Tools/Voxel/Report Non-Readable Meshes";

    [MenuItem(MENU_ALL)]
    public static void EnableAll()
    {
        if (!EditorUtility.DisplayDialog(
            "Enable Read/Write on ALL meshes",
            "This will modify the import settings of every model asset in the project " +
            "and reimport them. This can take a while and will increase build RAM usage.\n\n" +
            "Continue?",
            "Yes, enable all", "Cancel"))
        {
            return;
        }

        Process(GetAllModelGuids(), GetAllStandaloneMeshGuids());
    }

    [MenuItem(MENU_SELECTION)]
    public static void EnableSelection()
    {
        var modelGuids = new List<string>();
        var meshGuids  = new List<string>();

        foreach (var obj in Selection.objects)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) continue;

            var importer = AssetImporter.GetAtPath(path);
            string guid  = AssetDatabase.AssetPathToGUID(path);

            if (importer is ModelImporter)
            {
                modelGuids.Add(guid);
            }
            else if (obj is Mesh)
            {
                meshGuids.Add(guid);
            }
        }

        if (modelGuids.Count == 0 && meshGuids.Count == 0)
        {
            EditorUtility.DisplayDialog("Nothing to do",
                "Select one or more model/mesh assets in the Project window first.", "OK");
            return;
        }

        Process(modelGuids, meshGuids);
    }

    [MenuItem(MENU_REPORT)]
    public static void Report()
    {
        int totalModels = 0, nonReadableModels = 0;
        var offenders = new List<string>();

        foreach (string guid in GetAllModelGuids())
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null) continue;
            totalModels++;
            if (!mi.isReadable)
            {
                nonReadableModels++;
                offenders.Add(path);
            }
        }

        Debug.Log($"[Voxel R/W Report] {nonReadableModels}/{totalModels} model assets are NOT readable.");
        for (int i = 0; i < offenders.Count; i++)
        {
            Debug.Log($"  [{i + 1}] {offenders[i]}");
        }

        EditorUtility.DisplayDialog("Read/Write Report",
            $"{nonReadableModels} / {totalModels} model assets have Read/Write DISABLED.\n\n" +
            "See Console for the full list.", "OK");
    }

    // ---------------------------------------------------------------------

    private static void Process(IList<string> modelGuids, IList<string> meshGuids)
    {
        int changed = 0, skipped = 0, total = modelGuids.Count + meshGuids.Count;
        if (total == 0)
        {
            EditorUtility.DisplayDialog("Nothing to do", "No model or mesh assets found.", "OK");
            return;
        }

        try
        {
            AssetDatabase.StartAssetEditing();

            int i = 0;
            // ----- Model assets (FBX / OBJ / DAE / ...) -----
            foreach (string guid in modelGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Enabling mesh Read/Write",
                        $"({++i}/{total}) {path}",
                        (float)i / total))
                {
                    break;
                }

                var mi = AssetImporter.GetAtPath(path) as ModelImporter;
                if (mi == null) { skipped++; continue; }
                if (mi.isReadable) { skipped++; continue; }

                mi.isReadable = true;
                mi.SaveAndReimport();
                changed++;
            }

            // ----- Standalone .asset Meshes -----
            foreach (string guid in meshGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Enabling mesh Read/Write",
                        $"({++i}/{total}) {path}",
                        (float)i / total))
                {
                    break;
                }

                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null) { skipped++; continue; }
                if (mesh.isReadable) { skipped++; continue; }

                // Standalone Mesh assets: flip the flag and re-save.
                mesh.UploadMeshData(false); // false = keep CPU-side copy
                EditorUtility.SetDirty(mesh);
                changed++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.StopAssetEditing();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        Debug.Log($"[Voxel R/W] Done. Updated {changed} asset(s), skipped {skipped} (already readable or invalid).");
        EditorUtility.DisplayDialog("Done",
            $"Updated {changed} asset(s).\nSkipped {skipped} (already readable).", "OK");
    }

    private static string[] GetAllModelGuids()
    {
        // Catches FBX, OBJ, DAE, 3DS, BLEND, SKP, etc. — anything ModelImporter handles.
        return AssetDatabase.FindAssets("t:Model");
    }

    private static string[] GetAllStandaloneMeshGuids()
    {
        // Standalone Mesh assets saved as .asset (not the sub-meshes inside a Model).
        return AssetDatabase.FindAssets("t:Mesh");
    }
}
#endif
