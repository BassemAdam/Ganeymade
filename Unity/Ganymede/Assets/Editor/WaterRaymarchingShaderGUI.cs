using UnityEditor;
using UnityEngine;

public sealed class WaterRaymarchingShaderGUI : ShaderGUI
{
    static bool s_SharedVolume = true;
    static bool s_BlueNoise = false;
    static bool s_Liquid = true;
    static bool s_LiquidVolume = true;
    static bool s_SurfaceOptics = false;
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
                EditorGUILayout.HelpBox(
                    "Reflection and refraction use exact unpolarized Fresnel weights with greedy single-path selection. Refraction samples the URP scene texture at the refracted UV; reflection samples the URP environment probe in the reflected direction. Strength sliders are physical multipliers (1.0 = full physical contribution).",
                    MessageType.Info);
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
