using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CaveDarkness
{
    // Caves become dark places instead of dimming the whole world.
    // Vanilla: the Player's PlayMaker FSM "CaveDarkness" eases Azure's environmentIntensity (= RenderSettings.ambientIntensity, global)
    // to 0.15 when the player passes a cave mouth's Cave_IN trigger and back at Cave_OUT. This mod skips that global change (the FSM keeps
    // running, so CaveDarkness_bool still works for other mods) and darkens the cave itself. Per cave, once (Field.cs): a voxel grid of rock
    // and air and each air voxel's distance through the air to open sky; light falls with that distance. Every renderer of the cave gets its
    // own ambient light from it (LightProbeUsage.CustomProvided + scaled ambient SH); the big rock shells are split into light bands
    // (readable copies of their meshes ship in cavemeshes.bin); the terrain floor uses a shader from cavedarkness.bundle with a light map;
    // a black reflection probe covers the inside; NPCs, items, cars and the player follow the light of their spot. Positions are kept in
    // MapMagic's frame (the game's floating origin shifts world coordinates every 1,000 m).
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.cavedarkness";
        public const string NAME = "CaveDarkness";
        public const string VERSION = "1.0.1";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled, VerboseLog, TintTest;
        internal static ConfigEntry<float> MaxDarkness, DarknessGradient;
        internal static string Folder;

        private static GameObject _runner;

        private void Awake()
        {
            Log = Logger;
            Folder = Path.GetDirectoryName(Info.Location);
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            Enabled = Config.Bind("General", "Enabled", true,
                "Caves are dark inside instead of the whole world dimming when you walk into one");
            MaxDarkness = Config.Bind("General", "MaxDarkness", 0.97f, new ConfigDescription(
                "How dark the deepest part of a cave gets (1 = pitch black, 0 = as bright as outside). Lamps and your flashlight still light it",
                new AcceptableValueRange<float>(0f, 1f)));
            DarknessGradient = Config.Bind("General", "DarknessGradient", 12f, new ConfigDescription(
                "Metres walked in from the cave mouth to the full darkness (smaller = it gets dark sooner)",
                new AcceptableValueRange<float>(1f, 60f)));
            VerboseLog = Config.Bind("Debug", "VerboseLog", false,
                "Debug: log every cave found and baked, and in a cave the render state and what is under the crosshair. Also needed for TintTest");
            TintTest = Config.Bind("Debug", "TintTest", false,
                "Debug (only with VerboseLog on): everything this mod darkens is lit flat red instead (bright red = full light, black = full darkness)");
            TintTest.SettingChanged += (s, e) => Caves.OnSettingsChanged();
            VerboseLog.SettingChanged += (s, e) => { if (TintTest.Value) Caves.OnSettingsChanged(); };
            Enabled.SettingChanged += (s, e) => Caves.OnSettingsChanged();
            MaxDarkness.SettingChanged += (s, e) => Caves.OnSettingsChanged();
            DarknessGradient.SettingChanged += (s, e) => Caves.OnSettingsChanged();
            DropOrphan("General", "Darkness");   // 0.1.0 setting, replaced by MaxDarkness

            var h = new Harmony(GUID);
            SaveLighting.Install(h);
            var setProp = AccessTools.TypeByName("HutongGames.PlayMaker.Actions.SetProperty");
            if (setProp != null)
            {
                var pre = new HarmonyMethod(typeof(Plugin), nameof(SkipVanillaDim));
                h.Patch(AccessTools.Method(setProp, "OnEnter"), prefix: pre);
                h.Patch(AccessTools.Method(setProp, "OnUpdate"), prefix: pre);
            }
            else Log.LogWarning("SetProperty action not found - the vanilla global dimming stays on");

            // [Debug] at the bottom of the file (BepInEx writes sections alphabetically)
            h.Patch(AccessTools.Method(typeof(ConfigFile), "Save"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterSave)));
            _cfg = Config;
            Config.Save();

            MeshBank.Load(Path.Combine(Folder, "cavemeshes.bin"));
            SceneManager.sceneLoaded += (s, m) => { EnsureRunner(); Caves.Reset(); };
            EnsureRunner();
            Verbose(NAME + " " + VERSION + " loaded");
        }

        internal static bool Tint { get { return TintTest.Value && VerboseLog.Value; } }

        private static ConfigFile _cfg;
        private static bool _saving;

        private static void AfterSave(ConfigFile __instance)
        {
            if (_saving || __instance != _cfg) return;
            _saving = true;
            try { SortSections(__instance); }
            catch (System.Exception e) { Log.LogWarning("Config section order: " + e.Message); }
            finally { _saving = false; }
        }

        // rewrite the file with the sections in bind order (General, then Debug)
        private static void SortSections(ConfigFile cfg)
        {
            string path = cfg.ConfigFilePath;
            if (!File.Exists(path)) return;
            string text = File.ReadAllText(path);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var header = new List<string>();
            var blocks = new List<KeyValuePair<string, List<string>>>();
            foreach (var line in lines)
            {
                string t = line.Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) { blocks.Add(new KeyValuePair<string, List<string>>(t.Substring(1, t.Length - 2), new List<string> { line })); continue; }
                if (blocks.Count == 0) header.Add(line); else blocks[blocks.Count - 1].Value.Add(line);
            }
            var order = new List<string>();
            foreach (var k in cfg.Keys) if (!order.Contains(k.Section)) order.Add(k.Section);
            var sorted = blocks.OrderBy(b => { int i = order.IndexOf(b.Key); return i < 0 ? int.MaxValue : i; }).ToList();
            var sb = new StringBuilder();
            foreach (var l in header) sb.Append(l).Append('\n');
            foreach (var b in sorted) foreach (var l in b.Value) sb.Append(l).Append('\n');
            string result = sb.ToString().TrimEnd('\n') + "\n";
            if (text.Contains("\r\n")) result = result.Replace("\n", "\r\n");
            if (result != text) File.WriteAllText(path, result, new UTF8Encoding(false));
        }

        private void DropOrphan(string section, string key)
        {
            try
            {
                var prop = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries");
                var dict = prop != null ? prop.GetValue(Config, null) as System.Collections.Generic.Dictionary<ConfigDefinition, string> : null;
                if (dict != null && dict.Remove(new ConfigDefinition(section, key))) Config.Save();
            }
            catch { }
        }

        // The Player's CaveDarkness FSM is the only user of SetProperty on Azure's environmentIntensity.
        private static bool SkipVanillaDim(HutongGames.PlayMaker.FsmStateAction __instance)
        {
            if (!Enabled.Value) return true;
            var f = __instance.Fsm;
            if (f != null && f.Name == "CaveDarkness") { __instance.Finish(); return false; }
            return true;
        }

        private static void EnsureRunner()
        {
            if (_runner != null) return;
            _runner = new GameObject("CaveDarkness.Runner");
            Object.DontDestroyOnLoad(_runner);
            _runner.hideFlags = HideFlags.HideAndDontSave;
            _runner.AddComponent<Runner>();
        }

        internal static void Verbose(string s) { if (VerboseLog.Value) Log.LogInfo(s); }
    }

    internal class Runner : MonoBehaviour
    {
        internal static Runner I;
        private void Awake() { I = this; }
        private void Update()
        {
            if (!Plugin.Enabled.Value) { Exposure.Restore(); return; }
            Caves.Tick();
            Dyn.Tick();
            Exposure.Tick();
            TerrainDark.Tick();
            SunCut.Tick();
            Diag.Tick();
        }
    }
}
