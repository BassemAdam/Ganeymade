using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

#if UNITY_EDITOR
/// <summary>
/// Editor utility that syncs EditorBuildSettings.scenes from the GameMenuConfig
/// so you don't have to manually drag scenes into Build Settings.
/// Access via menu: Ganymede ▸ Sync Build Scenes From Config.
/// </summary>
public static class MenuAutoBuilder
{
    [MenuItem("Ganymede/Sync Build Scenes From Config")]
    public static void SyncBuildScenes()
    {
        // Find the config asset
        string[] guids = AssetDatabase.FindAssets("t:GameMenuConfig");
        if (guids.Length == 0)
        {
            Debug.LogError("[MenuAutoBuilder] No GameMenuConfig asset found. Create one first.");
            return;
        }

        string configPath = AssetDatabase.GUIDToAssetPath(guids[0]);
        var config = AssetDatabase.LoadAssetAtPath<GameMenuConfig>(configPath);
        if (config == null)
        {
            Debug.LogError("[MenuAutoBuilder] Failed to load GameMenuConfig.");
            return;
        }

        var buildScenes = new List<EditorBuildSettingsScene>();

        // MainMenu scene must be first (index 0)
        string menuScenePath = FindScenePath("MainMenu");
        if (string.IsNullOrEmpty(menuScenePath))
        {
            Debug.LogError("[MenuAutoBuilder] Could not find a scene named 'MainMenu'. " +
                           "Create a scene called 'MainMenu' with the GameMenu component.");
            return;
        }
        buildScenes.Add(new EditorBuildSettingsScene(menuScenePath, true));

        // Add enabled game scenes
        foreach (var entry in config.GetEnabledScenes())
        {
            string path = FindScenePath(entry.sceneName);
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning($"[MenuAutoBuilder] Scene '{entry.sceneName}' not found in project. Skipping.");
                continue;
            }
            buildScenes.Add(new EditorBuildSettingsScene(path, true));
        }

        EditorBuildSettings.scenes = buildScenes.ToArray();
        Debug.Log($"[MenuAutoBuilder] Build Settings updated: {buildScenes.Count} scene(s) configured.");
    }

    static string FindScenePath(string sceneName)
    {
        string[] guids = AssetDatabase.FindAssets(sceneName + " t:Scene");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            if (fileName == sceneName)
                return path;
        }
        return null;
    }
}
#endif
