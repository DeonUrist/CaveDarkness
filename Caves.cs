using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace CaveDarkness
{
    internal class Part
    {
        public Renderer R;
        public float Open;          // sky openness 0..1
        public float Dist;          // metres from the cave entrance
        public bool Ours;           // a band renderer we made (destroyed on undo) vs a game renderer (restored on undo)
        public LightProbeUsage OrigUsage;
        public bool HadBlock;
    }

    internal class Cave
    {
        public GameObject Root;
        public string Name;
        public bool Ready, Baking, Failed;
        public Bounds Box;                          // whole structure
        public bool HasInner; public Bounds Inner;  // the dark inside (light < 0.5)
        public readonly List<Part> Parts = new List<Part>();
        public readonly List<Mesh> Made = new List<Mesh>();
        public readonly List<Material> MadeMats = new List<Material>();
        public string FloorInfo;
        public Texture2D Map;          // terrain light map (TerrainDark)
        public Vector4 MapRect;
        public readonly List<GameObject> MadeGos = new List<GameObject>();
        public readonly List<KeyValuePair<MeshFilter, Mesh>> Swapped = new List<KeyValuePair<MeshFilter, Mesh>>();
        public readonly List<KeyValuePair<Renderer, Transform>> Anchored = new List<KeyValuePair<Renderer, Transform>>();
        public readonly List<KeyValuePair<LODGroup, LOD[]>> Lods = new List<KeyValuePair<LODGroup, LOD[]>>();
        public Transform InAnchor, OutAnchor;
        public Transform MouthT;                    // the Cave_IN trigger (read live: MapMagic moves the structure after spawning it)
        public Vector3 Mouth { get { return MouthT != null ? MouthT.position : (Root != null ? Root.transform.position : Vector3.zero); } }
        public float Dist(Vector3 p) { return Vector3.Distance(p, Mouth); }
        public int AppliedVersion = -1;
        public readonly Dictionary<long, float> Grid = new Dictionary<long, float>();

        public float OpenAt(Vector3 p)
        {
            int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y), z = Mathf.FloorToInt(p.z);
            long key = ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(uint)(z & 0x1FFFFF);
            float o;
            if (Grid.TryGetValue(key, out o)) return o;
            bool bf = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            o = Sky.Point(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
            Physics.queriesHitBackfaces = bf;
            if (Grid.Count > 50000) Grid.Clear();
            Grid[key] = o;
            return o;
        }
    }

    internal static class Caves
    {
        public static readonly List<Cave> All = new List<Cave>();
        private static readonly HashSet<int> _known = new HashSet<int>();
        private static float _nextScan, _nextAmb, _bakeBeat;
        private static bool _baking;
        private static Cave _bakingCave;
        private static readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        private const int Bands = 16;
        private const float MaxEdge = 2f;            // subdivide rock meshes to triangles this small (m) before sorting them into bands
        private const double BudgetMs = 1.5;         // bake time per frame

        public static void Reset()
        {
            foreach (var c in All) Undo(c);
            TerrainDark.Restore();
            All.Clear(); _known.Clear(); _baking = false; _nextScan = 0f;
            Dyn.RestoreAll();
        }

        private static float _rebakeAt = -1f;

        // MaxDarkness / DarknessGradient decide which triangles go into which band, so the caves are baked again (about 0.1 s CPU each),
        // 1 s after the last change (the Mods-menu sliders fire many changes).
        public static void OnSettingsChanged()
        {
            if (!Plugin.Enabled.Value) { Reset(); return; }
            foreach (var c in All) c.AppliedVersion = -1;
            Dyn.Invalidate();
            _rebakeAt = Time.unscaledTime + 1f;
        }

        private static void Rebake()
        {
            _rebakeAt = -1f;
            if (_baking) { _rebakeAt = Time.unscaledTime + 0.5f; return; }
            foreach (var c in All)
            {
                if (!c.Ready) continue;
                Undo(c);
                c.HasInner = false; c.Failed = false; c.AppliedVersion = -1;
            }
            Plugin.Verbose("settings changed: caves are baked again");
        }

        public static void Tick()
        {
            float now = Time.unscaledTime;
            var cam = Camera.main;
            Vector3 cp = cam != null ? cam.transform.position : Vector3.zero;
            if (now >= _nextAmb)
            {
                _nextAmb = now + 0.5f;
                Sky.RefreshAmbient();
            }
            for (int i = All.Count - 1; i >= 0; i--)
            {
                var c = All[i];
                if (c.Root == null)
                {
                    foreach (var m in c.Made) if (m != null) Object.Destroy(m);
                    foreach (var m in c.MadeMats) if (m != null) Object.Destroy(m);
                    if (c.Map != null) Object.Destroy(c.Map);
                    All.RemoveAt(i); continue;
                }
                // the sky's ambient changes over the day: keep the caves near the player in step (far ones catch up when approached)
                if (c.Ready && c.AppliedVersion != Sky.AmbientVersion && c.Box.SqrDistance(cp) < 500f * 500f) Apply(c);
            }
            if (_rebakeAt > 0f && now >= _rebakeAt) Rebake();
            if (now >= _nextScan) { _nextScan = now + 2f; Scan(); }
            if (_baking && now - _bakeBeat > 15f)
            {
                Plugin.Log.LogWarning("bake of " + (_bakingCave != null ? _bakingCave.Name : "?") + " stopped responding - skipped");
                if (_bakingCave != null) { _bakingCave.Failed = true; _bakingCave.Baking = false; }
                _baking = false;
            }
            if (!_baking) StartNearest();
        }

        private static void Scan()
        {
            GameObject[] ins;
            try { ins = GameObject.FindGameObjectsWithTag("Cave_IN"); } catch { return; }
            foreach (var g in ins)
            {
                var root = RootOf(g.transform);
                if (root == null || !_known.Add(root.GetInstanceID())) continue;
                var c = new Cave { Root = root.gameObject, Name = root.name, MouthT = g.transform };
                All.Add(c);
                Plugin.Verbose("found " + Path(root));
            }
        }

        // MapMagic/Tile x,y/Objects/Cave_N(Clone)/CaveDarkness/CaveDarkness_IN -> Cave_N(Clone)
        private static Transform RootOf(Transform t)
        {
            var p = t.parent != null ? t.parent.parent : null;
            if (p != null && p.name.StartsWith("Cave")) return p;
            Transform cur = t;
            for (int i = 0; i < 8 && cur.parent != null; i++)
            {
                if (cur.parent.name == "Objects") return cur;
                cur = cur.parent;
            }
            return p;
        }

        private static string Path(Transform t)
        {
            string s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        private static void StartNearest()
        {
            var cam = Camera.main;
            Vector3 at = cam != null ? cam.transform.position : Vector3.zero;
            Cave best = null; float bd = float.MaxValue;
            foreach (var c in All)
            {
                if (c.Ready || c.Failed || c.Root == null || !c.Root.activeInHierarchy) continue;
                float d = (c.Root.transform.position - at).sqrMagnitude;
                if (d < bd) { bd = d; best = c; }
            }
            if (best == null || bd > 1500f * 1500f) return;
            _baking = true; _bakingCave = best; _bakeBeat = Time.unscaledTime;
            Runner.I.StartCoroutine(Bake(best));
        }

        // ---------------- bake ----------------
        private static Stopwatch _sw = new Stopwatch();
        private static bool _bf;
        private static void Begin() { _bakeBeat = Time.unscaledTime; _bf = Physics.queriesHitBackfaces; Physics.queriesHitBackfaces = true; _sw.Reset(); _sw.Start(); }
        private static void End() { Physics.queriesHitBackfaces = _bf; }
        internal static bool Over() { return _sw.Elapsed.TotalMilliseconds > BudgetMs; }

        private static IEnumerator Bake(Cave c)
        {
            c.Baking = true;
            var total = Stopwatch.StartNew();
            double cpu = 0; int frames = 0, rays0 = Sky.Rays;
            Sky.Init();
            Physics.SyncTransforms();
            var root = c.Root.transform;
            var rs = c.Root.GetComponentsInChildren<MeshRenderer>(true);
            bool first = true; Bounds box = new Bounds();
            foreach (var r in rs)
            {
                if (r == null) continue;
                if (first) { box = r.bounds; first = false; } else box.Encapsulate(r.bounds);
            }
            c.Box = box;
            c.OutAnchor = new GameObject("CaveDarkness.OutsideAnchor").transform;
            c.OutAnchor.SetParent(root, false);
            c.OutAnchor.position = new Vector3(box.center.x, box.max.y + 40f, box.center.z);
            c.MadeGos.Add(c.OutAnchor.gameObject);

            int split = 0, single = 0, tris = 0, bands = 0;
            Begin();
            foreach (var r in rs)
            {
                if (r == null || c.Root == null) break;
                var go = r.gameObject;
                if (go.layer == 2 || go.layer == 5 || go.layer == 22 || go.name.StartsWith("CaveDarkness")) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var data = MeshBank.Get(mf.sharedMesh);
                if (data != null)
                {
                    var e = Split(c, r, mf, data);
                    while (true)
                    {
                        bool more;
                        try { more = e.MoveNext(); } catch (System.Exception ex) { Plugin.Log.LogWarning("split " + r.name + ": " + ex.Message); break; }
                        if (!more) break;
                        cpu += _sw.Elapsed.TotalMilliseconds; frames++; End();
                        yield return null;
                        if (c.Root == null || !Plugin.Enabled.Value) { _baking = false; c.Baking = false; yield break; }
                        Begin();
                    }
                    split++; tris += _lastTris; bands += _lastBands;
                }
                else
                {
                    var p = r.bounds.center;
                    float o = Sky.Point(p);
                    float dd = c.Dist(p);
                    float l = Sky.Light(o, dd);
                    if (l < Sky.Outside)
                    {
                        c.Parts.Add(new Part { R = r, Open = o, Dist = dd, OrigUsage = r.lightProbeUsage, HadBlock = r.HasPropertyBlock() });
                        single++;
                        if (l < 0.5f) Inner(c, p);
                    }
                }
                if (Over())
                {
                    cpu += _sw.Elapsed.TotalMilliseconds; frames++; End();
                    yield return null;
                    if (c.Root == null || !Plugin.Enabled.Value) { _baking = false; c.Baking = false; yield break; }
                    Begin();
                }
            }
            cpu += _sw.Elapsed.TotalMilliseconds; End();

            // the terrain floor inside
            if (c.Root != null)
            {
                Begin();
                var fe = TerrainDark.Build(c);
                while (true)
                {
                    bool more;
                    try { more = fe.MoveNext(); } catch (System.Exception ex) { Plugin.Log.LogWarning("floor of " + c.Name + ": " + ex); c.FloorInfo = "failed"; break; }
                    if (!more) break;
                    cpu += _sw.Elapsed.TotalMilliseconds; frames++; End();
                    yield return null;
                    if (c.Root == null || !Plugin.Enabled.Value) { _baking = false; c.Baking = false; yield break; }
                    Begin();
                }
                cpu += _sw.Elapsed.TotalMilliseconds; End();
            }

            if (c.Root != null)
            {
                if (c.HasInner)
                {
                    var pg = new GameObject("CaveDarkness.Reflection");
                    pg.transform.SetParent(root, false);
                    pg.transform.position = c.Inner.center;
                    var ls = root.lossyScale;
                    pg.transform.localScale = new Vector3(1f / Mathf.Max(1e-3f, ls.x), 1f / Mathf.Max(1e-3f, ls.y), 1f / Mathf.Max(1e-3f, ls.z));
                    var rp = pg.AddComponent<ReflectionProbe>();
                    rp.mode = ReflectionProbeMode.Custom;
                    rp.customBakedTexture = Sky.Black;
                    rp.size = c.Inner.size + Vector3.one;
                    rp.center = Vector3.zero;
                    rp.importance = 100;
                    rp.blendDistance = 1f;
                    rp.boxProjection = false;
                    c.MadeGos.Add(pg);
                    c.InAnchor = new GameObject("CaveDarkness.InsideAnchor").transform;
                    c.InAnchor.SetParent(root, false);
                    c.InAnchor.position = c.Inner.center;
                    c.MadeGos.Add(c.InAnchor.gameObject);
                    foreach (var p in c.Parts) if (p.Ours && Sky.Light(p.Open, p.Dist) < 0.5f && p.R != null) p.R.probeAnchor = c.InAnchor;
                }
                c.Ready = true;
                Apply(c);
                float lmin = 1f, lmax = 0f;
                foreach (var p in c.Parts) { float l = Sky.Light(p.Open, p.Dist); if (l < lmin) lmin = l; if (l > lmax) lmax = l; }
                Plugin.Log.LogInfo("cave " + c.Name + ": " + split + " rock mesh(es) -> " + tris + " triangles in " + bands + " light bands, " + single +
                                   " other object(s) darkened, " + (Sky.Rays - rays0) + " rays, " + cpu.ToString("0") + " ms over " + frames + " frames (" +
                                   total.Elapsed.TotalSeconds.ToString("0.0") + " s)" + (c.HasInner ? "" : ", no dark inside found") +
                                   ", light " + lmin.ToString("0.00") + ".." + lmax.ToString("0.00") + ", deepest part " +
                                   DeepestDist(c).ToString("0") + " m from the entrance; floor: " + c.FloorInfo);
                if (_firstLog) { _firstLog = false; Plugin.Log.LogInfo(Sky.AmbientInfo()); }
            }
            c.Baking = false;
            _baking = false;
        }

        private static float DeepestDist(Cave c)
        {
            float m = 0f;
            foreach (var p in c.Parts) if (p.Dist > m) m = p.Dist;
            return m;
        }

        private static bool _firstLog = true;
        private static int _lastTris, _lastBands;

        private static void Inner(Cave c, Vector3 p)
        {
            if (!c.HasInner) { c.Inner = new Bounds(p, Vector3.zero); c.HasInner = true; }
            else c.Inner.Encapsulate(p);
        }

        private static IEnumerator Split(Cave c, MeshRenderer r, MeshFilter mf, MeshData data)
        {
            _lastTris = 0; _lastBands = 0;
            var tr = r.transform;
            var ls = tr.lossyScale;
            float scale = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
            var wm = new WorkMesh(data);
            wm.Subdivide(MaxEdge / Mathf.Max(0.01f, scale), 120000);
            int vc = wm.P.Count;
            var open = new float[vc];
            var wp = new Vector3[vc];
            var same = new Dictionary<long, float>();   // UV-seam duplicates share position and normal
            for (int i = 0; i < vc; i++)
            {
                wp[i] = tr.TransformPoint(wm.P[i]);
                var wn = tr.TransformDirection(wm.N[i]).normalized;
                long key = Key(wm.P[i], wm.N[i]);
                float o;
                if (!same.TryGetValue(key, out o)) { o = Sky.Surface(wp[i], wn); same[key] = o; }
                open[i] = o;
                if (Over()) yield return null;
            }
            int nt = wm.T.Count / 3;
            var outside = new List<int>();
            var band = new List<int>[Bands];
            var bandOpen = new float[Bands];
            var bandDist = new float[Bands];
            for (int t = 0; t < nt; t++)
            {
                int a = wm.T[t * 3], b = wm.T[t * 3 + 1], d = wm.T[t * 3 + 2];
                float o = (open[a] + open[b] + open[d]) / 3f;
                var ctr = (wp[a] + wp[b] + wp[d]) / 3f;
                float dd = c.Dist(ctr);
                float l = Sky.Light(o, dd);
                if (l >= Sky.Outside) { outside.Add(t * 3); continue; }
                int k = Mathf.Clamp((int)(l * Bands), 0, Bands - 1);
                if (band[k] == null) band[k] = new List<int>();
                band[k].Add(t * 3); bandOpen[k] += o; bandDist[k] += dd;
                if (l < 0.5f) Inner(c, ctr);
            }
            _lastTris = nt;
            if (outside.Count == nt) yield break;   // all open: leave it as it is

            var orig = mf.sharedMesh;
            var outMesh = wm.Build(outside, orig.name + " (CaveDarkness outside)");
            c.Made.Add(outMesh);
            c.Swapped.Add(new KeyValuePair<MeshFilter, Mesh>(mf, orig));
            mf.sharedMesh = outMesh;
            c.Anchored.Add(new KeyValuePair<Renderer, Transform>(r, r.probeAnchor));
            r.probeAnchor = c.OutAnchor;

            var made = new List<Renderer>();
            for (int k = 0; k < Bands; k++)
            {
                if (band[k] == null) continue;
                var g = new GameObject("CaveDarkness.band" + k);
                g.layer = r.gameObject.layer;
                g.transform.SetParent(tr, false);
                var m = wm.Build(band[k], orig.name + " (CaveDarkness band " + k + ")");
                c.Made.Add(m);
                g.AddComponent<MeshFilter>().sharedMesh = m;
                var br = g.AddComponent<MeshRenderer>();
                br.sharedMaterials = r.sharedMaterials;
                br.shadowCastingMode = r.shadowCastingMode;
                br.receiveShadows = r.receiveShadows;
                br.reflectionProbeUsage = r.reflectionProbeUsage;
                br.probeAnchor = c.OutAnchor;   // switched to the inside anchor once the inside is known (dark bands only)
                br.enabled = r.enabled;
                c.Parts.Add(new Part { R = br, Open = bandOpen[k] / band[k].Count, Dist = bandDist[k] / band[k].Count, Ours = true });
                made.Add(br);
                _lastBands++;
            }
            // keep LOD culling working for the new pieces
            var lg = r.GetComponentInParent<LODGroup>();
            if (lg != null)
            {
                var lods = lg.GetLODs();
                bool changed = false;
                var copy = new LOD[lods.Length];
                for (int i = 0; i < lods.Length; i++)
                {
                    copy[i] = lods[i];
                    var list = new List<Renderer>(lods[i].renderers ?? new Renderer[0]);
                    if (list.Contains(r)) { list.AddRange(made); lods[i].renderers = list.ToArray(); changed = true; }
                }
                if (changed) { c.Lods.Add(new KeyValuePair<LODGroup, LOD[]>(lg, copy)); lg.SetLODs(lods); }
            }
        }

        private static long Key(Vector3 p, Vector3 n)
        {
            long h = 1469598103934665603L;
            h = (h ^ Mathf.RoundToInt(p.x * 200f)) * 1099511628211L;
            h = (h ^ Mathf.RoundToInt(p.y * 200f)) * 1099511628211L;
            h = (h ^ Mathf.RoundToInt(p.z * 200f)) * 1099511628211L;
            h = (h ^ Mathf.RoundToInt(n.x * 20f)) * 1099511628211L;
            h = (h ^ Mathf.RoundToInt(n.y * 20f)) * 1099511628211L;
            h = (h ^ Mathf.RoundToInt(n.z * 20f)) * 1099511628211L;
            return h;
        }

        private static void Apply(Cave c)
        {
            c.AppliedVersion = Sky.AmbientVersion;
            foreach (var p in c.Parts)
                if (p.R != null) Sky.Apply(p.R, _mpb, Sky.Light(p.Open, p.Dist));
        }

        private static void Undo(Cave c)
        {
            foreach (var kv in c.Lods) if (kv.Key != null) kv.Key.SetLODs(kv.Value);
            foreach (var kv in c.Swapped) if (kv.Key != null) kv.Key.sharedMesh = kv.Value;
            foreach (var kv in c.Anchored) if (kv.Key != null) kv.Key.probeAnchor = kv.Value;
            foreach (var p in c.Parts)
            {
                if (p.Ours || p.R == null) continue;
                p.R.lightProbeUsage = p.OrigUsage;
                if (!p.HadBlock) p.R.SetPropertyBlock(null);
            }
            foreach (var g in c.MadeGos) if (g != null) Object.Destroy(g);
            foreach (var p in c.Parts) if (p.Ours && p.R != null) Object.Destroy(p.R.gameObject);
            foreach (var m in c.Made) if (m != null) Object.Destroy(m);
            foreach (var m in c.MadeMats) if (m != null) Object.Destroy(m);
            c.MadeMats.Clear();
            if (c.Map != null) { Object.Destroy(c.Map); c.Map = null; }
            c.Parts.Clear(); c.Made.Clear(); c.MadeGos.Clear(); c.Swapped.Clear(); c.Anchored.Clear(); c.Lods.Clear();
            c.Ready = false;
        }

        // The cave whose box holds p (the player's camera, an NPC ...), or null.
        public static Cave At(Vector3 p)
        {
            foreach (var c in All)
                if (c.Ready && c.Root != null && c.Box.SqrDistance(p) < 1f) return c;
            return null;
        }
    }
}
