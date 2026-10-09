using System.IO;
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
    // running, so CaveDarkness_bool still works for other mods) and darkens the cave itself: every renderer of a cave structure gets its own
    // ambient light (LightProbeUsage.CustomProvided + scaled ambient SH) from how much sky it can see (raycasts, done once per cave);
    // the big rock shells are split into ambient bands (readable copies of their meshes ship in cavemeshes.bin); a black reflection
    // probe covers the inside; NPCs, items, cars and the player's own view get the same treatment while they are in a cave.
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.cavedarkness";
        public const string NAME = "CaveDarkness";
        public const string VERSION = "0.5.1";

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
                "Metres from the cave entrance to the full darkness (smaller = it gets dark sooner)",
                new AcceptableValueRange<float>(1f, 60f)));
            VerboseLog = Config.Bind("Debug", "VerboseLog", false, "Log every cave that gets baked and what was darkened");
            TintTest = Config.Bind("Debug", "TintTest", false,
                "Debug: everything this mod darkens is lit flat red instead (bright red = full light, black = full darkness), to see what the mod reaches. " +
                "While on, entering a cave also logs what is under the crosshair and the render settings");
            TintTest.SettingChanged += (s, e) => Caves.OnSettingsChanged();
            Enabled.SettingChanged += (s, e) => Caves.OnSettingsChanged();
            MaxDarkness.SettingChanged += (s, e) => Caves.OnSettingsChanged();
            DarknessGradient.SettingChanged += (s, e) => Caves.OnSettingsChanged();
            DropOrphan("General", "Darkness");   // 0.1.0 setting, replaced by MaxDarkness

            var h = new Harmony(GUID);
            var setProp = AccessTools.TypeByName("HutongGames.PlayMaker.Actions.SetProperty");
            if (setProp != null)
            {
                var pre = new HarmonyMethod(typeof(Plugin), nameof(SkipVanillaDim));
                h.Patch(AccessTools.Method(setProp, "OnEnter"), prefix: pre);
                h.Patch(AccessTools.Method(setProp, "OnUpdate"), prefix: pre);
            }
            else Log.LogWarning("SetProperty action not found - the vanilla global dimming stays on");

            MeshBank.Load(Path.Combine(Folder, "cavemeshes.bin"));
            SceneManager.sceneLoaded += (s, m) => { EnsureRunner(); Caves.Reset(); };
            EnsureRunner();
            Log.LogInfo(NAME + " " + VERSION + " loaded");
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
