using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;


// One-shot editor tool: converts all Blacksmith Pack materials to URP Lit with explicit texture mapping 
// Run via menu: Tools -> Fix Blacksmith Materials.

public static class FixBlacksmithMaterials
{
    // Material name -> texture base name (before _Diffuse/_Normal/_Roughness/_Metallic)
    // Only entries where material name (minus _MAT) != texture base name
    static readonly Dictionary<string, string> ExplicitMap = new Dictionary<string, string>
    {
        // Hammers
        { "BallPeen_MAT", "Ball-Peen" },
        { "StraightPeen_MAT", "Straight-Peen" },
        { "LumpHammer_MAT", "Lump" },
        // Ingots  (material has "Ingot", texture doesn't)
        { "CopperIngot_MAT", "Copper" },
        { "BronzeIngot_MAT", "Bronze" },
        { "GoldIngot_MAT", "Gold" },
        { "SteelIngot_MAT","Steel" },
        { "SilverIngot_MAT","Silver" },
        { "PlatinumIngot_MAT","Platinum" },
        // Tools
        { "Bolster_MAT", "CuppingBolster" },
        // Light sources
        { "CandelabraHanging_MAT", "Candelabra" },
        { "LanternEmission_MAT","Lantern" },
        // Floors 
        { "Floor1M_MAT", "1Mfloor" },
        { "Floor2M_MAT", "2Mfloor" },
        // Thin walls
        { "1MeterWall_MAT", "1Mwall" },
        { "2MeterWall_MAT", "Tall2Mwall" },
        { "2MeterWallShort_MAT","Short2Mwall" },
        { "45DegreeWall_MAT", "1Wall45" },
        { "Reverse45Wall_MAT","Reverse1Wall45" },
        // Thick walls
        { "2MShortThick_MAT", "2MShortThickWall" },
        { "45Thick_MAT", "45WallThick" },
        { "Reverse45Thick_MAT", "Reverse45ThickWall" },
        // Roofs 
        { "RoofWooden_MAT", "45Roof" },
        { "RoofTiled_MAT", "Tile" },
        // Pillars (1M)
        { "1MeterPillarVar1_MAT", "Pillar1" },
        { "1MeterPillarVar2_MAT", "Pillar1VAR2" },
        { "1MeterPillarVar3_MAT", "Pillar1VAR3" },
        // Pillars (2M)
        { "2MeterPillarVAR1_MAT", "Pillar2VAR1" },
        { "2MeterPillarVar2_MAT", "Pillar2VAR2" },
        { "2MeterPillarVar3_MAT", "Pillar2VAR3" },
        { "2MeterPillarVar4_MAT", "Pillar2VAR4" },
        { "IronPillarClasp_MAT",  "PillarClasp" },
        // Forge 
        { "Forge_MAT2", "Forge" },
        // Chain 
        { "Chain_MAT'", "Chain" },
    };

    // Materials that should have smoothness = 0.5 (metals that are polished/reflective)
    static readonly HashSet<string> SmoothMetals = new HashSet<string>
    {
        "CopperIngot_MAT","BronzeIngot_MAT","GoldIngot_MAT","SteelIngot_MAT","SilverIngot_MAT","PlatinumIngot_MAT",
    };

    // Materials whose metallic map should be cleared (non-metal surfaces)
    static readonly HashSet<string> NoMetallicSet = new HashSet<string>
    {
        // Floors
        "Floor1M_MAT","Floor2M_MAT",
        // Roofs
        "RoofWooden_MAT","RoofTiled_MAT",
        // Thin walls
        "1MeterWall_MAT","2MeterWall_MAT","2MeterWallShort_MAT","45DegreeWall_MAT","Reverse45Wall_MAT",
        // Thick walls
        "2MShortThick_MAT","45Thick_MAT","Reverse45Thick_MAT",
        // Forge
        "Forge_MAT","Forge_MAT2",
        // Grindstone wood
        "GrindstoneWood_MAT",
        // Furniture 
        "Crate_MAT","StoneTable_MAT","StoneChair_MAT","WeaponsRack_MAT",
        // Specific pillars
        "1MeterPillarVar1_MAT","2MeterPillarVAR1_MAT",
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

        // Build lookup: lowercase texture name (no extension) -> asset path
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
            if (mat == null) 
                continue;

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

            // Diffuse -> _BaseMap
            Texture2D diffuse = FindTex(texLookup, texBaseLower, "_diffuse");
            if (diffuse != null)
            {
                mat.SetTexture("_BaseMap", diffuse);
                mat.SetTexture("_MainTex", diffuse);
            }

            // Normal -> _BumpMap
            Texture2D normal = FindTex(texLookup, texBaseLower, "_normal");
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.SetFloat("_BumpScale", 1f);
                mat.EnableKeyword("_NORMALMAP");
            }

            // Metallic -> _MetallicGlossMap (skip for non-metal surfaces)
            bool noMetallic = NoMetallicSet.Contains(mat.name);
            Texture2D metallic = null;
            if (!noMetallic)
            {
                metallic = FindTex(texLookup, texBaseLower, "_metallic");
                if (metallic != null)
                {
                    mat.SetTexture("_MetallicGlossMap", metallic);
                    mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                }
            }
            else
            {
                // Explicitly clear metallic map
                mat.SetTexture("_MetallicGlossMap", null);
                mat.SetFloat("_Metallic", 0f);
                mat.DisableKeyword("_METALLICSPECGLOSSMAP");
            }

            // Roughness
            Texture2D roughness = FindTex(texLookup, texBaseLower, "_roughness");
            if (roughness != null && metallic == null && !noMetallic)
                mat.SetTexture("_MetallicGlossMap", roughness);
            float smoothness = SmoothMetals.Contains(mat.name) ? 0.5f : 0f;
            mat.SetFloat("_Smoothness", smoothness);

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

            // Force emission on for ingot materials for visualization
            if (SmoothMetals.Contains(mat.name))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", Color.black);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            }

            mat.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(mat);
            count++;

            bool hasAny = diffuse || normal || metallic || roughness;
            if (hasAny) 
                matched++;

            string log = $"Fix {mat.name} (texBase={texBase}, smoothness={smoothness}):";
            if (diffuse) 
                log += $" diffuse={diffuse.name}";
            if (normal) 
                log += $" normal={normal.name}";
            if (metallic) 
                log += $" metallic={metallic.name}";
            if (roughness) 
                log += $" roughness={roughness.name}";
            if (!hasAny) 
                log += " NO TEXTURES MATCHED";
            Debug.Log(log);
        }

        Debug.Log($"FixBlacksmithMaterials Done: {count} materials processed, {matched} had textures, {count - matched} unmatched.");

        RestoreBlackGap();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
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

    static void RestoreBlackGap()
    {
        string[] sourcePathList = 
        {
            "Assets/Blacksmith Pack/Components/Mesh/Structuring/Walls/Thin Planked/1Mwall.fbx",
            "Assets/Blacksmith Pack/Components/Mesh/Structuring/Walls/Thick Planked/Reverse45Thick.fbx",
            "Assets/Blacksmith Pack/Components/Mesh/Structuring/Walls/Thick Planked/1MThickWall.fbx",
            "Assets/Blacksmith Pack/Components/Mesh/Structuring/Walls/Thick Planked/2MThickWall.fbx",
            "Assets/Blacksmith Pack/Components/Mesh/Structuring/Walls/Thick Planked/2MThickShortWall.fbx",
            "Assets/Blacksmith Pack/Components/Mesh/Structuring/Walls/Thick Planked/45WallThick.fbx",
            "Assets/Blacksmith Pack/Components/Mesh/Structuring/Roof/45Roof.fbx",
        };

        foreach (string sourcePath in sourcePathList)
        {
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(sourcePath))
            {
                if (obj is Material m && m.name == "Black Gap")
                {
                    m.SetColor("_BaseColor", Color.black);
                    m.SetColor("_Color", Color.black);
                    m.SetFloat("_Smoothness", 0.5f);
                    EditorUtility.SetDirty(m);
                    Debug.Log("[RestoreBlackGap] Restored to black.");
                }
            }
        }
        AssetDatabase.SaveAssets();
    }

    [MenuItem("Tools/Add Box Colliders")]
    static void AddBoxColliders()
    {
        AddBoxCollider("Pliers", center: new Vector3(-0.12f, 0.2f,  0f), size: new Vector3( 0.3f,  0.45f, 0.07f));

        var ingots = new (string name, float diffusivity)[]
        {
            ("Silver Ingot", 30f),
            ("Gold Ingot", 23f),
            ("Copper Ingot", 16f),
            ("Platinum Ingot", 4.5f),
            ("Steel Ingot", 2.2f),
            ("Bronze Ingot", 0.8f),
        };

        foreach (var (name, diffusivity) in ingots)
        {
            string path = FindPrefabPath(name);
            if (path == null) 
                continue;

            using (var scope = new PrefabUtility.EditPrefabContentsScope(path))
            {
                GameObject root = scope.prefabContentsRoot;
                BoxCollider existing = root.GetComponent<BoxCollider>();
                if (existing != null) 
                    Object.DestroyImmediate(existing);
                BoxCollider col = root.AddComponent<BoxCollider>();
                col.center = new Vector3(0f, 0.04f, 0f);
                col.size = new Vector3(0.4f, 0.1f, 0.2f);

                VoxelSolidMaterial vsm = root.GetComponent<VoxelSolidMaterial>() ?? root.AddComponent<VoxelSolidMaterial>();
                vsm.thermalDiffusivity = diffusivity;
                vsm.temperature = 25f; 

                Transform ingotMesh = root.transform.Find("Ingot"); 
                if (ingotMesh == null)
                {
                    Debug.LogWarning($"[Ingot] Could not find child 'ingot' on {name}");
                }
                else
                {
                    if (ingotMesh.GetComponent<VoxelDynamic>() == null)
                        ingotMesh.gameObject.AddComponent<VoxelDynamic>();
                }
            }

            Debug.Log($"[Ingot] Collider + VoxelSolidMaterial applied to: {path}");
        }
    }

    static void AddBoxCollider(string prefabName, Vector3 center, Vector3 size)
    {
        string path = FindPrefabPath(prefabName);
        if (path == null) 
            return;

        using (var scope = new PrefabUtility.EditPrefabContentsScope(path))
        {
            GameObject root = scope.prefabContentsRoot;
            BoxCollider existing = root.GetComponent<BoxCollider>();
            if (existing != null) 
                Object.DestroyImmediate(existing);
            BoxCollider col = root.AddComponent<BoxCollider>();
            col.center = center;
            col.size = size;
        }

        Debug.Log($"[BoxCollider] Added to: {path}");
    }

    static string FindPrefabPath(string prefabName)
    {
        string[] guids = AssetDatabase.FindAssets($"{prefabName} t:Prefab");
        if (guids.Length == 0)
        {
            Debug.LogWarning($"[BoxCollider] Could not find prefab: '{prefabName}'");
            return null;
        }
        return AssetDatabase.GUIDToAssetPath(guids[0]);
    }
}