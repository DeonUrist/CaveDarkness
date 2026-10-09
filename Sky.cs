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

        // Surface at p with normal n: cosine-weighted share of its hemisphere that is open.
        public static float Surface(Vector3 p, Vector3 n)
        {
            Vector3 o = p + n * 0.06f;
            // quick out: straight along the normal and straight up both reach the sky -> an outer surface (most of a rock)
            Rays += 2;
            if (!Physics.Raycast(o, n, MaxDist, Mask, QueryTriggerInteraction.Ignore) &&
                (n.y < -0.3f || !Physics.Raycast(o, Vector3.up, MaxDist, Mask, QueryTriggerInteraction.Ignore))) return 1f;
            float tot = 0f, esc = 0f;
            for (int i = 0; i < _dirs.Length; i++)
            {
                float c = Vector3.Dot(_dirs[i], n);
                if (c <= 0.05f) continue;
                tot += c; Rays++;
                if (!Physics.Raycast(o, _dirs[i], MaxDist, Mask, QueryTriggerInteraction.Ignore)) esc += c;
            }
            return tot > 0f ? esc / tot : 1f;
        }

        // A point in space (object centre): share of the directions from just below the horizon upward that are open.
        public static float Point(Vector3 p)
        {
            float tot = 0f, esc = 0f;
            for (int i = 0; i < _dirs.Length; i++)
            {
                if (_dirs[i].y < -0.15f) continue;
                tot += 1f; Rays++;
                if (!Physics.Raycast(p, _dirs[i], MaxDist, Mask, QueryTriggerInteraction.Ignore)) esc += 1f;
            }
            return tot > 0f ? esc / tot : 1f;
        }

        // dist = metres from the cave entrance. Depth sets the darkness (MaxDarkness reached at DarknessGradient metres, quick fall-off
        // near the mouth); sky openness lifts it back up where the sky is visible (outside of the rock, mouth area).
        public static float Light(float open, float dist)
        {
            float floor = 1f - Plugin.MaxDarkness.Value;
            float t = Mathf.Clamp01(dist / Mathf.Max(0.5f, Plugin.DarknessGradient.Value));
            float u = 1f - t;
            float depth = floor + (1f - floor) * u * u;
            float sky = Mathf.Pow(Mathf.Clamp01(open / OpenFull), Gamma);
            return Mathf.Lerp(depth, 1f, sky);
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
            if (Plugin.TintTest.Value)
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
