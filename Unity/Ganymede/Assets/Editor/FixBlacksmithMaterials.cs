using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

/// <summary>
/// One-shot editor tool: converts all Blacksmith Pack materials to URP Lit
/// with explicit texture mapping (the pack's naming is very inconsistent).
/// Run via menu: Tools → Fix Blacksmith Materials.
/// Delete this script after use.
/// </summary>
public static class FixBlacksmithMaterials
{
    // Material name → texture base name (before _Diffuse/_Normal/_Roughness/_Metallic)
    // Only entries where material name (minus _MAT) != texture base name
    static readonly Dictionary<string, string> ExplicitMap = new Dictionary<string, string>
    {
        // Hammers
        { "BallPeen_MAT",          "Ball-Peen" },
        { "StraightPeen_MAT",      "Straight-Peen" },
        { "LumpHammer_MAT",        "Lump" },
        // Ingots  (material has "Ingot", texture doesn't)
        { "CopperIngot_MAT",       "Copper" },
        { "BronzeIngot_MAT",       "Bronze" },
        { "GoldIngot_MAT",         "Gold" },
        { "SteelIngot_MAT",        "Steel" },
        { "SilverIngot_MAT",       "Silver" },
        { "PlatinumIngot_MAT",     "Platinum" },
        // Tools
        { "Bolster_MAT",           "CuppingBolster" },
        // Light sources
        { "CandelabraHanging_MAT", "Candelabra" },
        { "LanternEmission_MAT",   "Lantern" },
        // Floors (swapped word order)
        { "Floor1M_MAT",           "1Mfloor" },
        { "Floor2M_MAT",           "2Mfloor" },
        // Thin walls
        { "1MeterWall_MAT",        "1Mwall" },
        { "2MeterWall_MAT",        "Tall2Mwall" },
        { "2MeterWallShort_MAT",   "Short2Mwall" },
        { "45DegreeWall_MAT",      "1Wall45" },
        { "Reverse45Wall_MAT",     "Reverse1Wall45" },
        // Thick walls
        { "2MShortThick_MAT",      "2MShortThickWall" },
        { "45Thick_MAT",           "45WallThick" },
        { "Reverse45Thick_MAT",    "Reverse45ThickWall" },
        // Roofs (both use 45Roof texture)
        { "RoofWooden_MAT",        "45Roof" },
        { "RoofTiled_MAT",         "Tile" },
        // Pillars (1M)
        { "1MeterPillarVar1_MAT",  "Pillar1" },
        { "1MeterPillarVar2_MAT",  "Pillar1VAR2" },
        { "1MeterPillarVar3_MAT",  "Pillar1VAR3" },
        // Pillars (2M)
        { "2MeterPillarVAR1_MAT",  "Pillar2VAR1" },
        { "2MeterPillarVar2_MAT",  "Pillar2VAR2" },
        { "2MeterPillarVar3_MAT",  "Pillar2VAR3" },
        { "2MeterPillarVar4_MAT",  "Pillar2VAR4" },
        { "IronPillarClasp_MAT",   "PillarClasp" },
        // Forge variant uses same texture
        { "Forge_MAT2",            "Forge" },
        // Chain has apostrophe in filename
        { "Chain_MAT'",            "Chain" },
    };

    [MenuItem("Tools/Fix Blacksmith Materials")]
    static void Fix()
    {
        string folder = "Assets/Blacksmith Pack";
        string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { folder });
        string[] texGuids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });

        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        if (urpLit == null)
        {
            Debug.LogError("Could not find 'Universal Render Pipeline/Lit' shader.");
            return;
        }

        // Build lookup: lowercase texture name (no extension) → asset path
        var texLookup = new Dictionary<string, string>();
        foreach (string guid in texGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            texLookup[name] = path;
        }

        int count = 0;
        int matched = 0;
        foreach (string guid in matGuids)
        {
            string matPath = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) continue;

            // Skip auto-generated materials inside Texture Maps/Diffuse/Materials
            if (matPath.Contains("Texture Maps/Diffuse/Materials"))
                continue;

            // Resolve texture base name
            string texBase;
            if (ExplicitMap.TryGetValue(mat.name, out string mapped))
            {
                texBase = mapped;
            }
            else
            {
                // Default: strip _MAT suffix
                texBase = mat.name;
                foreach (string suffix in new[] { "_MAT'", "_MAT", "_mat", "_Material" })
                {
                    if (texBase.EndsWith(suffix))
                    {
                        texBase = texBase.Substring(0, texBase.Length - suffix.Length);
                        break;
                    }
                }
            }

            string texBaseLower = texBase.ToLowerInvariant();

            // Switch to URP Lit
            mat.shader = urpLit;

            // Diffuse → _BaseMap
            Texture2D diffuse = FindTex(texLookup, texBaseLower, "_diffuse");
            if (diffuse != null)
            {
                mat.SetTexture("_BaseMap", diffuse);
                mat.SetTexture("_MainTex", diffuse);
            }

            // Normal → _BumpMap
            Texture2D normal = FindTex(texLookup, texBaseLower, "_normal");
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.SetFloat("_BumpScale", 1f);
                mat.EnableKeyword("_NORMALMAP");
            }

            // Metallic → _MetallicGlossMap
            Texture2D metallic = FindTex(texLookup, texBaseLower, "_metallic");
            if (metallic != null)
            {
                mat.SetTexture("_MetallicGlossMap", metallic);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            }

            // Roughness
            Texture2D roughness = FindTex(texLookup, texBaseLower, "_roughness");
            if (roughness != null && metallic == null)
                mat.SetTexture("_MetallicGlossMap", roughness);
            mat.SetFloat("_Smoothness", roughness != null ? 0.3f : 0.5f);

            // Occlusion
            Texture2D occlusion = FindTex(texLookup, texBaseLower, "_occlusion", "_ao");
            if (occlusion != null)
                mat.SetTexture("_OcclusionMap", occlusion);

            // Emission
            Texture2D emission = FindTex(texLookup, texBaseLower, "_emission", "_emissive");
            if (emission != null)
            {
                mat.SetTexture("_EmissionMap", emission);
                mat.SetColor("_EmissionColor", Color.white);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            mat.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(mat);
            count++;

            bool hasAny = diffuse || normal || metallic || roughness;
            if (hasAny) matched++;

            string log = $"[Fix] {mat.name} (texBase={texBase}):";
            if (diffuse) log += $" diffuse={diffuse.name}";
            if (normal) log += $" normal={normal.name}";
            if (metallic) log += $" metallic={metallic.name}";
            if (roughness) log += $" roughness={roughness.name}";
            if (!hasAny) log += " ⚠ NO TEXTURES MATCHED";
            Debug.Log(log);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[FixBlacksmithMaterials] Done: {count} materials processed, {matched} had textures, {count - matched} unmatched.");
    }

    static Texture2D FindTex(Dictionary<string, string> lookup, string baseLower, params string[] suffixes)
    {
        foreach (string suffix in suffixes)
        {
            string key = baseLower + suffix;
            if (lookup.TryGetValue(key, out string path))
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        return null;
    }
}
