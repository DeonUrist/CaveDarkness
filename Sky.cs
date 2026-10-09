using UnityEngine;
using UnityEngine.Rendering;

namespace CaveDarkness
{
    // How much of the sky a point / surface can see, and the ambient light that goes with it.
    internal static class Sky
    {
        public const float MaxDist = 60f;     // a ray that travels this far counts as open
        public const float OpenFull = 0.55f;  // seeing this share of the open directions already counts as fully lit
        public const float Gamma = 1.6f;      // >1: light falls off quickly once inside
        public const float Outside = 0.97f;   // light at or above this = leave the renderer alone

        private static Vector3[] _dirs;
        public static int Mask;
        public static int Rays;               // raycasts done (statistics)

        public static void Init()
        {
            if (_dirs == null)
            {
                const int n = 32;   // Fibonacci sphere
                _dirs = new Vector3[n];
                float ga = Mathf.PI * (3f - Mathf.Sqrt(5f));
                for (int i = 0; i < n; i++)
                {
                    float y = 1f - (i + 0.5f) * 2f / n, r = Mathf.Sqrt(1f - y * y), a = ga * i;
                    _dirs[i] = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
                }
            }
            // static world: Default, layer 11, the camp/cave base rock (14) and the terrain's layer
            Mask = (1 << 0) | (1 << 11) | (1 << 14);
            foreach (var t in Terrain.activeTerrains) if (t != null) Mask |= 1 << t.gameObject.layer;
        }

        private static bool Hit(Vector3 o, Vector3 d)
        {
            Rays++;
            return Physics.Raycast(o, d, MaxDist, Mask, QueryTriggerInteraction.Ignore);
        }

        // Rays escaping this low under a roof count as blocked: they sneak out under the foot of the walls / through gaps between rock
        // pieces, not to the sky (the mouth's own light comes from the depth term).
        private const float LowUnderRoof = 0.25f;

        // Roof: straight up or 3 of 4 rays tilted 25 deg from up hit something (one crack in the ceiling doesn't make it "open sky").
        private static bool Roof(Vector3 o)
        {
            if (Hit(o, Vector3.up)) return true;
            const float cu = 0.906f, su = 0.423f;
            int h = 0;
            if (Hit(o, new Vector3(su, cu, 0f))) h++;
            if (Hit(o, new Vector3(-su, cu, 0f))) h++;
            if (Hit(o, new Vector3(0f, cu, su))) h++;
            if (h == 0) return false;
            if (Hit(o, new Vector3(0f, cu, -su))) h++;
            return h >= 3;
        }

        // Enclosed = inside the cave (or inside the rock itself, between its outer and inner faces): under a roof and walled in -
        // 6 of 8 directions just above the horizon hit something within MaxDist. Enclosed spots get no sky light at all; their light is
        // the depth-from-the-entrance term only (bright near the mouth, MaxDarkness deeper in).
        private static readonly Vector3[] _ring = MakeRing();
        private static Vector3[] MakeRing()
        {
            var r = new Vector3[8];
            for (int i = 0; i < 8; i++) { float a = i * Mathf.PI / 4f; r[i] = new Vector3(Mathf.Cos(a), 0.17f, Mathf.Sin(a)).normalized; }
            return r;
        }
        internal static bool Walled(Vector3 o)
        {
            int miss = 0;
            for (int i = 0; i < 8; i++) if (!Hit(o, _ring[i]) && ++miss > 2) return false;
            return true;
        }

        // Surface at p with normal n: cosine-weighted share of its hemisphere that is open.
        public static float Surface(Vector3 p, Vector3 n)
        {
            Vector3 o = p + n * 0.06f;
            bool roof = Roof(o);
            if (roof && Walled(o)) return 0f;
            if (!roof && !Hit(o, n))
            {
                // quick out (an outer surface, most of a rock) - but only if 4 rays tilted 35 deg off the normal escape too:
                // one or two rays alone slip through cracks and lit whole interior ledges in 0.3.x
                Vector3 t1 = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized, t2 = Vector3.Cross(n, t1);
                const float ca = 0.819f, sa = 0.574f;
                if (!Hit(o, n * ca + t1 * sa) && !Hit(o, n * ca - t1 * sa) && !Hit(o, n * ca + t2 * sa) && !Hit(o, n * ca - t2 * sa)) return 1f;
            }
            float tot = 0f, esc = 0f;
            for (int i = 0; i < _dirs.Length; i++)
            {
                float c = Vector3.Dot(_dirs[i], n);
                if (c <= 0.05f) continue;
                tot += c;
                if (roof && _dirs[i].y < LowUnderRoof) continue;
                if (!Hit(o, _dirs[i])) esc += c;
            }
            return tot > 0f ? esc / tot : 1f;
        }

        // A point in space (object centre): share of the directions from just below the horizon upward that are open.
        public static float Point(Vector3 p)
        {
            bool roof = Roof(p);
            if (roof && Walled(p)) return 0f;
            float tot = 0f, esc = 0f;
            for (int i = 0; i < _dirs.Length; i++)
            {
                if (_dirs[i].y < -0.15f) continue;
                tot += 1f;
                if (roof && _dirs[i].y < LowUnderRoof) continue;
                if (!Hit(p, _dirs[i])) esc += 1f;
            }
            return tot > 0f ? esc / tot : 1f;
        }

        // dist = metres THROUGH THE AIR to open sky (the cave's Field). The first Margin metres stay fully lit (mouth, overhangs), then the
        // light falls to MaxDarkness over DarknessGradient metres, quickly at first. (open: unused since 0.5.0)
        public const float Margin = 2f;
        public static float Light(float open, float dist)
        {
            float floor = 1f - Plugin.MaxDarkness.Value;
            float t = Mathf.Clamp01((dist - Margin) / Mathf.Max(0.5f, Plugin.DarknessGradient.Value));
            float u = 1f - t;
            return floor + (1f - floor) * u * u;
        }

        // ---- ambient SH ----
        private static SphericalHarmonicsL2 _amb;
        private static float _ambKey = -1f;
        public static int AmbientVersion;
        private static readonly SphericalHarmonicsL2[] _one = new SphericalHarmonicsL2[1];

        // Called a few times a second: Azure changes the sky ambient over the day.
        public static void RefreshAmbient()
        {
            var a = RenderSettings.ambientProbe;
            float k = RenderSettings.ambientIntensity;
            a *= k;   // the probe the shaders get is the stored one times ambientIntensity
            float key = a[0, 0] * 7f + a[1, 0] * 13f + a[2, 0] * 17f + a[1, 1] + a[1, 2] + a[1, 3];
            if (Mathf.Abs(key - _ambKey) > 0.002f + Mathf.Abs(_ambKey) * 0.01f)
            {
                _amb = a; _ambKey = key; AmbientVersion++;
            }
        }

        public static string AmbientInfo()
        {
            var a = RenderSettings.ambientProbe;
            return "ambientMode " + RenderSettings.ambientMode + ", intensity " + RenderSettings.ambientIntensity.ToString("0.00") +
                   ", probe DC rgb " + a[0, 0].ToString("0.000") + "/" + a[1, 0].ToString("0.000") + "/" + a[2, 0].ToString("0.000") +
                   ", light probes " + (LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count : 0);
        }

        public static void Apply(Renderer r, MaterialPropertyBlock mpb, float light)
        {
            if (Plugin.Tint)
            {
                // debug: flat ambient, red = darkened by this mod (bright red = light 1, black = light 0)
                var sh = new SphericalHarmonicsL2();
                sh.AddAmbientLight(new Color(light * 2f, 0f, 0f));
                _one[0] = sh;
            }
            else _one[0] = _amb * light;
            r.GetPropertyBlock(mpb);
            mpb.CopySHCoefficientArraysFrom(_one);
            r.SetPropertyBlock(mpb);
            if (r.lightProbeUsage != LightProbeUsage.CustomProvided) r.lightProbeUsage = LightProbeUsage.CustomProvided;
        }

        private static Cubemap _black;
        public static Cubemap Black
        {
            get
            {
                if (_black == null)
                {
                    _black = new Cubemap(8, TextureFormat.RGBA32, false);
                    var px = new Color[64];
                    for (int i = 0; i < px.Length; i++) px[i] = Color.black;
                    for (int f = 0; f < 6; f++) _black.SetPixels(px, (CubemapFace)f);
                    _black.Apply(false, true);
                    _black.hideFlags = HideFlags.HideAndDontSave;
                }
                return _black;
            }
        }
    }
}
