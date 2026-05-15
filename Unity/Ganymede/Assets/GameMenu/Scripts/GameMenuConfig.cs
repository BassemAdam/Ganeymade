using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Holds the list of scenes available in the main menu.
/// Create via Assets ▸ Create ▸ Ganymede ▸ Game Menu Config.
/// Toggle individual SceneEntry.enabled to include/exclude before build.
/// </summary>
[CreateAssetMenu(fileName = "GameMenuConfig", menuName = "Ganymede/Game Menu Config")]
public class GameMenuConfig : ScriptableObject
{
    [Tooltip("Title shown at the top of the menu.")]
    public string gameTitle = "GANYMEDE";

    [Tooltip("Ordered list of scene entries. Disabled entries are hidden from the menu.")]
    public List<SceneEntry> scenes = new List<SceneEntry>();

    /// <summary>Returns only enabled scenes with valid scene names.</summary>
    public List<SceneEntry> GetEnabledScenes()
    {
        var result = new List<SceneEntry>();
        foreach (var entry in scenes)
        {
            if (entry != null && entry.enabled && !string.IsNullOrWhiteSpace(entry.sceneName))
                result.Add(entry);
        }
        return result;
    }
}
