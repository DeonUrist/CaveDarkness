using System.IO;
using UnityEditor;
using UnityEngine;

// Menu "CaveDarkness/Build shader bundle" (or batchmode: -executeMethod CaveDarknessBuild.Build) -> Bundle/cavedarkness.bundle,
// copied into the game's BepInEx/plugins/CaveDarkness when that folder exists. Unity 2020.3.49f1, Windows x64.
public static class CaveDarknessBuild
{
    const string Game = @"E:\SteamLibrary\steamapps\common\Apocalypter\BepInEx\plugins\CaveDarkness";

    [MenuItem("CaveDarkness/Build shader bundle")]
    public static void Build()
    {
        KeepAllVariants();
        Directory.CreateDirectory("Bundle");
        var b = new AssetBundleBuild
        {
            assetBundleName = "cavedarkness.bundle",
            assetNames = new[] { "Assets/CaveDarkness/CaveTerrain.shader", "Assets/CaveDarkness/CaveTerrain-AddPass.shader" }
        };
        var m = BuildPipeline.BuildAssetBundles("Bundle", new[] { b }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (m == null) { Debug.LogError("CaveDarkness: bundle build failed (see the shader errors above)"); return; }
        string src = Path.Combine("Bundle", "cavedarkness.bundle");
        if (Directory.Exists(Game)) { File.Copy(src, Path.Combine(Game, "cavedarkness.bundle"), true); Debug.Log("CaveDarkness: bundle copied to " + Game); }
        else Debug.Log("CaveDarkness: bundle built at " + Path.GetFullPath(src));
    }

    // An empty project has no scenes, so Unity would strip the fog / shadow variants the game needs.
    static void KeepAllVariants()
    {
        var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (gs == null || gs.Length == 0) return;
        var so = new SerializedObject(gs[0]);
        Set(so, "m_FogStripping", 1);
        Set(so, "m_FogKeepLinear", 1); Set(so, "m_FogKeepExp", 1); Set(so, "m_FogKeepExp2", 1);
        Set(so, "m_LightmapStripping", 1);
        Set(so, "m_LightmapKeepPlain", 1); Set(so, "m_LightmapKeepDirCombined", 1); Set(so, "m_LightmapKeepDynamicPlain", 1);
        Set(so, "m_LightmapKeepDynamicDirCombined", 1); Set(so, "m_LightmapKeepShadowMask", 1); Set(so, "m_LightmapKeepSubtractive", 1);
        Set(so, "m_InstancingStripping", 2);
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void Set(SerializedObject so, string name, int v)
    {
        var p = so.FindProperty(name);
        if (p == null) return;
        if (p.propertyType == SerializedPropertyType.Boolean) p.boolValue = v != 0; else p.intValue = v;
    }
}
