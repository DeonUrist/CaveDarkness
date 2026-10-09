using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CaveDarkness
{
    // Things that move in and out of caves: NPCs, items, cars, the player (body, arms, held gear - whatever hangs under the camera's root).
    // A few times a second: everything with a collider on the Player/Car/Item/Actor layers inside a baked cave's box gets the ambient
    // light of its spot (sky openness sampled on a 1 m grid, cached per cave); leaving the cave fades it back and hands the renderers back.
    internal static class Dyn
    {
        private class Tracked
        {
            public Transform Root;
            public readonly List<Renderer> R = new List<Renderer>();
            public readonly List<LightProbeUsage> Orig = new List<LightProbeUsage>();
            public readonly List<bool> HadBlock = new List<bool>();
            public float Cur = 1f, Applied = -1f, NextRefresh;
            public int AppliedVersion = -1;
            public bool Seen, IsCamera;
            public Vector3 Sample;
            public Cave Cave;
        }

        private const float Period = 0.2f;
        private const float NearCave = 120f;
        private static readonly int DynMask = (1 << 6) | (1 << 8) | (1 << 9) | (1 << 10);
        private static readonly Dictionary<Transform, Tracked> _t = new Dictionary<Transform, Tracked>();
        private static readonly Collider[] _buf = new Collider[512];
        private static readonly List<Transform> _drop = new List<Transform>();
        private static readonly List<Renderer> _tmp = new List<Renderer>();
        private static readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        private static float _next, _nextExtra;
        private static readonly string[] ExtraNames = { "ApocaplayerBody", "ApocaplayerRagdoll" };
        private static readonly List<Transform> _extra = new List<Transform>();

        public static float CameraLight = 1f;   // ambient light at the camera (1 = outside)

        public static void Invalidate() { foreach (var t in _t.Values) t.Applied = -1f; }

        public static void RestoreAll()
        {
            foreach (var t in _t.Values) Release(t);
            _t.Clear();
        }

        public static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + Period;
            var cam = Camera.main;
            if (cam == null) { if (_t.Count > 0) RestoreAll(); return; }
            Vector3 cp = cam.transform.position;

            foreach (var t in _t.Values) t.Seen = false;
            foreach (var c in Caves.All)
            {
                if (!c.Ready || c.Root == null || c.Box.SqrDistance(cp) > NearCave * NearCave) continue;
                var b = c.Box;
                int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one, _buf, Quaternion.identity, DynMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var col = _buf[i];
                    if (col == null) continue;
                    var root = col.attachedRigidbody != null ? col.attachedRigidbody.transform : col.transform.root;
                    if (root == c.Root.transform) continue;
                    Mark(root, c, root.position + Vector3.up * 0.9f, false, now);
                }
            }
            // bodies that have no collider of their own: Apocaplayer's character (a scene root, follows the player) and its ragdoll
            if (now >= _nextExtra)
            {
                _nextExtra = now + 2f;
                _extra.Clear();
                foreach (var n in ExtraNames) { var g = GameObject.Find(n); if (g != null) _extra.Add(g.transform); }
            }
            foreach (var x in _extra)
            {
                if (x == null || !x.gameObject.activeInHierarchy) continue;
                var smr = x.GetComponentInChildren<SkinnedMeshRenderer>();
                var sp = smr != null ? smr.bounds.center : x.position + Vector3.up * 0.9f;   // (the body's root may not follow her)
                var xc = Caves.At(sp);
                if (xc != null) Mark(x, xc, sp, false, now);
            }
            // the player's view: arms, held items and the flashlight model hang under the camera's root
            CameraLight = 1f;
            var camCave = Caves.At(cp);
            if (camCave != null) Mark(cam.transform.root, camCave, cp, true, now);

            _drop.Clear();
            foreach (var kv in _t)
            {
                var t = kv.Value;
                if (t.Root == null) { _drop.Add(kv.Key); continue; }
                float target = 1f;
                if (t.Seen && t.Cave != null) target = Sky.Light(t.Cave.OpenAt(t.Sample), t.Cave.Dist(t.Sample));
                t.Cur += (target - t.Cur) * 0.45f;
                if (Mathf.Abs(target - t.Cur) < 0.01f) t.Cur = target;
                if (!t.Seen && t.Cur >= Sky.Outside) { Release(t); _drop.Add(kv.Key); continue; }
                if (t.Seen && now >= t.NextRefresh) Refresh(t, now);
                if (t.IsCamera && t.Seen) CameraLight = t.Cur;
                if (Mathf.Abs(t.Cur - t.Applied) > 0.005f || t.AppliedVersion != Sky.AmbientVersion)
                {
                    t.Applied = t.Cur; t.AppliedVersion = Sky.AmbientVersion;
                    for (int i = 0; i < t.R.Count; i++) if (t.R[i] != null) Sky.Apply(t.R[i], _mpb, t.Cur);
                }
            }
            foreach (var k in _drop) _t.Remove(k);
        }

        private static void Mark(Transform root, Cave c, Vector3 sample, bool camera, float now)
        {
            Tracked t;
            if (!_t.TryGetValue(root, out t))
            {
                // only start tracking once it really is in the dark
                if (Sky.Light(c.OpenAt(sample), c.Dist(sample)) >= Sky.Outside) return;
                t = new Tracked { Root = root };
                _t[root] = t;
                Refresh(t, now);
            }
            if (t.Seen && t.IsCamera && !camera) return;   // the camera's sample wins for the player's root
            t.Seen = true; t.Cave = c; t.Sample = sample; t.IsCamera = camera;
        }

        // re-read the renderers (weapons are swapped, items picked up / dropped); hand back the ones that left
        private static void Refresh(Tracked t, float now)
        {
            t.NextRefresh = now + 2f;
            _tmp.Clear();
            t.Root.GetComponentsInChildren(true, _tmp);
            var keep = new HashSet<Renderer>();
            foreach (var r in _tmp)
            {
                if (r == null || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                if (r.gameObject.name.StartsWith("CaveDarkness")) continue;
                keep.Add(r);
            }
            for (int i = t.R.Count - 1; i >= 0; i--)
            {
                var r = t.R[i];
                if (r != null && keep.Contains(r)) { keep.Remove(r); continue; }
                if (r != null) Give(r, t.Orig[i], t.HadBlock[i]);
                t.R.RemoveAt(i); t.Orig.RemoveAt(i); t.HadBlock.RemoveAt(i);
            }
            foreach (var r in keep)
            {
                if (r.lightProbeUsage == LightProbeUsage.CustomProvided) continue;   // someone else's (or a cave part's) custom lighting
                t.R.Add(r); t.Orig.Add(r.lightProbeUsage); t.HadBlock.Add(r.HasPropertyBlock());
            }
            t.Applied = -1f;
        }

        private static void Release(Tracked t)
        {
            for (int i = 0; i < t.R.Count; i++) if (t.R[i] != null) Give(t.R[i], t.Orig[i], t.HadBlock[i]);
            t.R.Clear(); t.Orig.Clear(); t.HadBlock.Clear();
        }

        private static void Give(Renderer r, LightProbeUsage orig, bool hadBlock)
        {
            r.lightProbeUsage = orig;
            if (!hadBlock) r.SetPropertyBlock(null);
        }
    }
}
