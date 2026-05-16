using UnityEditor;
using UnityEngine;

public sealed class WaterRaymarchingShaderGUI : ShaderGUI
{
    static bool s_SharedVolume = true;
    static bool s_Debug = true;
    static bool s_BlueNoise = false;
    static bool s_Liquid = true;
    static bool s_LiquidVolume = true;
    static bool s_SurfaceOptics = false;
    static bool s_SSR = true;
    static bool s_SurfaceNormals = false;
    static bool s_Vapour = true;
    static bool s_VapourRendering = true;
    static bool s_VapourGate = false;
    static bool s_VapourStructure = true;
    static bool s_Advanced = false;

    MaterialEditor _editor;
    MaterialProperty[] _properties;

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        _editor = materialEditor;
        _properties = properties;

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Water Raymarching", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Liquid and vapour are rendered in one shared raymarch. The foldouts below only organize material settings; they do not change shader behavior.",
            MessageType.Info);

        DrawSharedVolumeSection();
        DrawLiquidSection();
        DrawVapourSection();
        DrawAdvancedSection(materialEditor);
    }

    void DrawSharedVolumeSection()
    {
        DrawSection("Shared Volume / Raymarch", ref s_SharedVolume, () =>
        {
            Draw("_StepSize");
            Draw("_IsoLevel");

            DrawSubSection("Debug", ref s_Debug, () =>
            {
                DrawDebugViewMode();
                EditorGUILayout.HelpBox(
                    "Reflection Only shows the sampled reflected environment with fallback. Optical Normal Used visualizes the exact view-facing normal used by Fresnel/reflection/refraction. Outward Surface Normal visualizes the raw density/bounds normal before the optical flip. Reflection Direction visualizes the reflect vector. Reflection Weight shows Fresnel reflectance. Reflection Contribution and Refraction Contribution show the weighted terms after the water background composition logic. Background Mix shows their combined result. View Transmittance shows how much refracted background survives the volume raymarch. Glossy Environment Raw and SpecCube Raw split the actual water pass reflection sources. Scene Depth and Scene Normal expose SSR inputs. SSR Hit Mask (red), SSR Fetch Color, and SSR Fade Factor are the core SSR debugging steps. Magenta = no liquid surface hit. Yellow = surface hit exists but both reflection sources are black.",
                    MessageType.Info);
            });

            DrawSubSection("Blue Noise Jitter", ref s_BlueNoise, () =>
            {
                DrawTexture("_BlueNoiseTex");
                Draw("_BlueNoiseScale");
                Draw("_BlueNoiseStrength");
                Draw("_BlueNoiseTimeSpeed");
            });
        });
    }

    void DrawLiquidSection()
    {
        DrawSection("Liquid", ref s_Liquid, () =>
        {
            DrawSubSection("Density / Scattering", ref s_LiquidVolume, () =>
            {
                Draw("_ScatteringCoefficients");
                Draw("_LiquidScatterColor");
                Draw("_DensityMultiplier");
                Draw("_DensityOffset");
                Draw("_LightStepSize");
            });

            DrawSubSection("Surface Optics", ref s_SurfaceOptics, () =>
            {
                Draw("_RefractionStrength");
                Draw("_ReflectionStrength");
                Draw("_ReflectionVisibilityBoost");
                Draw("_ReflectionVisibilityFloor");
                Draw("_TIRSoftness");
                EditorGUILayout.HelpBox(
                    "Reflection and refraction use exact unpolarized Fresnel weights with greedy single-path selection. Refraction samples the URP scene texture at the refracted UV; reflection samples the URP environment probe in the reflected direction. Strength is the physical multiplier. Visibility Boost/Floor are art controls applied only to the final reflected contribution so the normal view can show reflections without changing the raw reflection debug modes.",
                    MessageType.Info);

                DrawSubSection("Screen Space Reflections (SSR)", ref s_SSR, () =>
                {
                    Draw("_SSRStrength");
                    Draw("_SSRColorBoost");
                    Draw("_SSRMinBlend");
                    Draw("_SSRUseSceneNormals");
                    Draw("_SSRStepSize");
                    Draw("_SSRMaxDistance");
                    Draw("_SSRMaxSteps");
                    Draw("_SSRThickness");
                    Draw("_SSREdgeFadeWidth");
                    Draw("_SSRBackfaceThreshold");
                    EditorGUILayout.HelpBox(
                        "SSR traces reflection rays in screen space against camera depth. Strength controls fade-weighted blend, Color Boost amplifies fetched SSR color, and Minimum Hit Blend enforces a floor when a valid hit exists. Enable Scene-Normal Backface Check only when your pipeline provides a compatible camera normals texture; if you see MSAA bindMS warnings, keep this toggle off. If a ray misses, runs off-screen, or fails checks, reflection falls back to the environment probe. Use Scene Depth/Scene Normal + SSR Hit/Fetch/Fade debug modes to tune quality.",
                        MessageType.None);
                });
            });

            DrawSubSection("Surface Detection / Normals", ref s_SurfaceNormals, () =>
            {
                Draw("_SurfaceDetectionMargin");
                Draw("_SurfaceRefineIterations");
                Draw("_NormalSampleRadiusVoxels");
                Draw("_BakedNormalBlend");
                Draw("_BoundaryNormalBlendDistance");
                Draw("_BoundaryNormalUpBiasPower");
            });
        });
    }

    void DrawVapourSection()
    {
        DrawSection("Vapour", ref s_Vapour, () =>
        {
            DrawSubSection("Rendering / Lighting", ref s_VapourRendering, () =>
            {
                Draw("_VapourBaseColor");
                Draw("_VapourAbsorption");
                Draw("_VapourGodRayStrength");
                Draw("_VapourShadowFloor");
                Draw("_VapourScatterG");
                Draw("_VapourBackscatter");
            });

            DrawSubSection("Physics Gate", ref s_VapourGate, () =>
            {
                Draw("_VapourPresenceThreshold");
                Draw("_VapourFullDensity");
                Draw("_VapourDensityMultiplier");
            });

            DrawSubSection("Structure", ref s_VapourStructure, () =>
            {
                Draw("_NoiseScale");
                Draw("_NoiseDriftDir");
                Draw("_NoiseDriftSpeed");
                Draw("_VelocityInfluence");
                Draw("_NoiseOctaves");
                Draw("_DensityPower");
                Draw("_VapourWarpStrength");
                Draw("_VapourErosionScale");
                Draw("_VapourErosionStrength");
                Draw("_VapourCutoff");
                Draw("_VapourSoftness");
                Draw("_VapourVerticalStretch");
                Draw("_VapourHeightDissipation");
                Draw("_EdgeSoftness");
            });
        });
    }

    void DrawAdvancedSection(MaterialEditor materialEditor)
    {
        DrawSection("Advanced", ref s_Advanced, () =>
        {
            materialEditor.RenderQueueField();
            materialEditor.EnableInstancingField();
            materialEditor.DoubleSidedGIField();
        });
    }

    void DrawSection(string title, ref bool expanded, System.Action drawContents)
    {
        EditorGUILayout.Space(6);
        expanded = EditorGUILayout.Foldout(expanded, title, true, EditorStyles.foldoutHeader);
        if (!expanded)
            return;

        EditorGUI.indentLevel++;
        drawContents?.Invoke();
        EditorGUI.indentLevel--;
    }

    void DrawSubSection(string title, ref bool expanded, System.Action drawContents)
    {
        EditorGUILayout.Space(2);
        expanded = EditorGUILayout.Foldout(expanded, title, true);
        if (!expanded)
            return;

        EditorGUI.indentLevel++;
        drawContents?.Invoke();
        EditorGUI.indentLevel--;
    }

    static readonly GUIContent[] s_DebugViewNames =
    {
        new GUIContent("Off"),
        new GUIContent("Reflection Only"),
        new GUIContent("Optical Normal Used"),
        new GUIContent("Reflection Direction"),
        new GUIContent("Reflection Weight"),
        new GUIContent("Reflection Contribution"),
        new GUIContent("Refraction Contribution"),
        new GUIContent("Background Mix"),
        new GUIContent("View Transmittance"),
        new GUIContent("Glossy Environment Raw"),
        new GUIContent("SpecCube Raw"),
        new GUIContent("Outward Surface Normal"),
        new GUIContent("[Debug] IOR Direction (Green=Enter/Red=Leave)"),
        new GUIContent("[Debug] Total Internal Reflection"),
        new GUIContent("Scene Depth (Near=White)"),
        new GUIContent("Scene Normal"),
        new GUIContent("SSR Hit Mask (Red=Hit)"),
        new GUIContent("SSR Fetch Color"),
        new GUIContent("SSR Fade Factor"),
    };

    static readonly int[] s_DebugViewValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 };

    void DrawDebugViewMode()
    {
        MaterialProperty prop = Find("_DebugViewMode");
        if (prop == null) return;

        EditorGUI.BeginChangeCheck();
        int current = (int)prop.floatValue;
        int next = EditorGUILayout.IntPopup(
            new GUIContent("Debug View"),
            current,
            s_DebugViewNames,
            s_DebugViewValues);
        if (EditorGUI.EndChangeCheck())
            prop.floatValue = next;
    }

    void Draw(string propertyName)
    {
        MaterialProperty property = Find(propertyName);
        if (property == null)
            return;

        _editor.ShaderProperty(property, property.displayName);
    }

    void DrawTexture(string propertyName)
    {
        MaterialProperty property = Find(propertyName);
        if (property == null)
            return;

        _editor.TexturePropertySingleLine(new GUIContent(property.displayName), property);
    }

    MaterialProperty Find(string propertyName)
    {
        return FindProperty(propertyName, _properties, false);
    }
}
