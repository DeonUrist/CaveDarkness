using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CaveDarkness
{
    // Cave floors are the TERRAIN (the camp_base rock lies just under it), and a terrain is one object per map tile, so it can't take a
    // per-object ambient like the rocks. Instead every terrain is drawn with "CaveDarkness/Terrain" (cavedarkness.bundle): Unity's own
    // Nature/Terrain/Standard whose AMBIENT (sky light + reflections) is multiplied by a small light map per cave. Sun, flashlight, fires
    // and lamps still light the floor. Each baked cave gets a 0.5 m map over its dark inside (+2 m); the 4 caves nearest the camera are
    // handed to the shader as globals.
    internal static class TerrainDark
    {
        private const float Step = 0.5f;
        private const int MaxCells = 256;

        private static Shader _shader;
        private static bool _loaded;
        private class Swap { public Terrain T; public Material Orig, Ours; }
        private static readonly List<Swap> _swaps = new List<Swap>();
        private static readonly HashSet<string> _skipped = new HashSet<string>();
        private static float _nextSwap, _nextGlobals;
        private static readonly int[] MapId = { Shader.PropertyToID("_CaveMap0"), Shader.PropertyToID("_CaveMap1"), Shader.PropertyToID("_CaveMap2"), Shader.PropertyToID("_CaveMap3") };
        private static readonly int[] RectId = { Shader.PropertyToID("_CaveRect0"), Shader.PropertyToID("_CaveRect1"), Shader.PropertyToID("_CaveRect2"), Shader.PropertyToID("_CaveRect3") };
        private static readonly int CountId = Shader.PropertyToID("_CaveCount");
        private static readonly List<Cave> _near = new List<Cave>();

        public static bool Available { get { Load(); return _shader != null; } }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            string path = System.IO.Path.Combine(Plugin.Folder, "cavedarkness.bundle");
            if (!System.IO.File.Exists(path)) { Plugin.Log.LogWarning("cavedarkness.bundle missing - cave floors (terrain) stay lit"); return; }
            var b = AssetBundle.LoadFromFile(path);
            if (b == null) { Plugin.Log.LogWarning("cavedarkness.bundle could not be loaded"); return; }
            foreach (var s in b.LoadAllAssets<Shader>())
                if (s != null && s.name == "CaveDarkness/Terrain") _shader = s;
            if (_shader == null || !_shader.isSupported) { Plugin.Log.LogWarning("CaveDarkness/Terrain shader " + (_shader == null ? "not in the bundle" : "not supported")); _shader = null; return; }
            Plugin.Log.LogInfo("terrain shader loaded: " + _shader.name);
        }

        public static void Tick()
        {
            float now = Time.unscaledTime;
            if (now >= _nextSwap) { _nextSwap = now + 2f; SwapTerrains(); }
            if (now >= _nextGlobals) { _nextGlobals = now + 0.25f; Globals(); }
        }

        private static void SwapTerrains()
        {
            if (!Available) return;
            for (int i = _swaps.Count - 1; i >= 0; i--)
                if (_swaps[i].T == null) { if (_swaps[i].Ours != null) Object.Destroy(_swaps[i].Ours); _swaps.RemoveAt(i); }
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null) continue;
                Swap s = null;
                foreach (var x in _swaps) if (x.T == t) { s = x; break; }
                if (s != null && t.materialTemplate == s.Ours) continue;
                var cur = t.materialTemplate;   // new tile, or the game replaced our material
                string sh = cur != null && cur.shader != null ? cur.shader.name : "(default)";
                if (cur != null && sh != "Nature/Terrain/Standard")
                {
                    if (_skipped.Add(sh)) Plugin.Log.LogWarning("terrain " + t.name + " uses " + sh + " - its cave floors stay lit");
                    continue;
                }
                var m = cur != null ? new Material(cur) : new Material(_shader);
                m.shader = _shader;
                m.name = "CaveDarkness terrain";
                if (s == null) { s = new Swap { T = t }; _swaps.Add(s); }
                else if (s.Ours != null) Object.Destroy(s.Ours);
                s.Orig = cur; s.Ours = m;
                t.materialTemplate = m;
                Plugin.Verbose("terrain " + t.name + ": " + sh + " -> " + _shader.name);
            }
        }

        public static void Restore()
        {
            foreach (var s in _swaps)
            {
                if (s.T != null && s.T.materialTemplate == s.Ours) s.T.materialTemplate = s.Orig;
                if (s.Ours != null) Object.Destroy(s.Ours);
            }
            _swaps.Clear();
            Shader.SetGlobalFloat(CountId, 0f);
        }

        // the 4 baked caves nearest the camera
        private static void Globals()
        {
            var cam = Camera.main;
            _near.Clear();
            if (cam != null)
            {
                var p = cam.transform.position;
                foreach (var c in Caves.All)
                    if (c.Ready && c.Map != null && c.Root != null && c.Box.SqrDistance(p) < 300f * 300f) _near.Add(c);
                _near.Sort((a, b) => a.Box.SqrDistance(p).CompareTo(b.Box.SqrDistance(p)));
            }
            int n = Mathf.Min(4, _near.Count);
            for (int i = 0; i < 4; i++)
            {
                if (i < n) { Shader.SetGlobalTexture(MapId[i], _near[i].Map); Shader.SetGlobalVector(RectId[i], _near[i].MapRect); }
                else { Shader.SetGlobalTexture(MapId[i], Texture2D.whiteTexture); Shader.SetGlobalVector(RectId[i], new Vector4(0, 0, 0, 0)); }
            }
            Shader.SetGlobalFloat(CountId, n);
        }

        // Bake step: the light map of the terrain inside this cave (light at the terrain surface, as for the rocks).
        public static IEnumerator Build(Cave c)
        {
            c.FloorInfo = "none";
            if (!Available) { c.FloorInfo = "no terrain shader (cavedarkness.bundle)"; yield break; }
            if (!c.HasInner) { c.FloorInfo = "no dark inside"; yield break; }
            var mouth = c.Mouth;
            Terrain ter = null;
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                var tp0 = t.transform.position; var s0 = t.terrainData.size;
                if (mouth.x >= tp0.x && mouth.x <= tp0.x + s0.x && mouth.z >= tp0.z && mouth.z <= tp0.z + s0.z) { ter = t; break; }
            }
            if (ter == null) { c.FloorInfo = "no terrain under the cave"; yield break; }
            var td = ter.terrainData; var tp = ter.transform.position; var size = td.size;

            var b = c.Inner;
            float x0 = b.min.x - 2f, x1 = b.max.x + 2f, z0 = b.min.z - 2f, z1 = b.max.z + 2f;
            float step = Mathf.Max(Step, Mathf.Max(x1 - x0, z1 - z0) / MaxCells);
            int w = Mathf.Max(2, Mathf.CeilToInt((x1 - x0) / step) + 1), h = Mathf.Max(2, Mathf.CeilToInt((z1 - z0) / step) + 1);
            var px = new Color32[w * h];
            int dark = 0;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    float x = x0 + i * step, z = z0 + j * step;
                    float l = 1f;
                    float u = (x - tp.x) / size.x, v = (z - tp.z) / size.z;
                    if (u >= 0f && u <= 1f && v >= 0f && v <= 1f)   // (a cave on a tile border: the part on the next tile stays lit)
                    {
                        var n = td.GetInterpolatedNormal(u, v);
                        var p = new Vector3(x, td.GetInterpolatedHeight(u, v) + tp.y + 0.05f, z);
                        l = Sky.Light(Sky.Surface(p, n), c.Dist(p));
                        if (l < Sky.Outside) dark++;
                        if (Caves.Over()) yield return null;
                    }
                    byte by = (byte)Mathf.Clamp(Mathf.RoundToInt(l * 255f), 0, 255);
                    px[j * w + i] = new Color32(by, by, by, 255);
                }
            if (dark == 0) { c.FloorInfo = "terrain inside is open to the sky"; yield break; }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            tex.name = "CaveDarkness map " + c.Name;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels32(px);
            tex.Apply(false, true);
            c.Map = tex;
            // texel centres sit on the grid points
            c.MapRect = new Vector4(x0 - step * 0.5f, z0 - step * 0.5f, 1f / (step * w), 1f / (step * h));
            c.FloorInfo = w + "x" + h + " map (" + step.ToString("0.00") + " m), " + dark + " dark points";
        }
    }
}
