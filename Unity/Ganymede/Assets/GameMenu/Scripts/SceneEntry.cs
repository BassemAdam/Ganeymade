using UnityEngine;

/// <summary>
/// Defines a single playable scene that can appear in the main menu.
/// Create instances via Assets ▸ Create ▸ Ganymede ▸ Scene Entry.
/// </summary>
[CreateAssetMenu(fileName = "NewSceneEntry", menuName = "Ganymede/Scene Entry")]
public class SceneEntry : ScriptableObject
{
    [Tooltip("Display name shown on the menu button.")]
    public string displayName = "Untitled Scene";

    [Tooltip("Short description shown below the button.")]
    [TextArea(1, 3)]
    public string description = "";

    [Tooltip("Scene name exactly as it appears in Build Settings (without .unity).")]
    public string sceneName = "";

    [Tooltip("Uncheck to hide this scene from the menu without deleting it.")]
    public bool enabled = true;
}
