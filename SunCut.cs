using System.Collections.Generic;
using UnityEngine;

namespace CaveDarkness
{
    // The dark rock pieces (band renderers with light < 0.5, ours, no colliders) go on the unused layer 23, which the sun (directional
    // lights) doesn't light: shadow-map bias and cracks between rock pieces let thin streaks of sun onto the inside walls and ledges.
    // Every other light (flashlight, fires, lamps) and the cameras get the layer added, so they treat those pieces as before.
    internal static class SunCut
    {
        public const int Layer = 23;
        private const int Bit = 1 << Layer;
        private static float _next;
        private static readonly List<Light> _cut = new List<Light>();

        public static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 3f;
            foreach (var l in Object.FindObjectsOfType<Light>())
            {
                if (l == null) continue;
                if (l.type == LightType.Directional)
                {
                    if ((l.cullingMask & Bit) != 0) { l.cullingMask &= ~Bit; if (!_cut.Contains(l)) _cut.Add(l); }
                }
                else if ((l.cullingMask & Bit) == 0 && l.cullingMask != 0) l.cullingMask |= Bit;
            }
            foreach (var cam in Camera.allCameras)
                if (cam != null && (cam.cullingMask & ~Bit) != 0 && (cam.cullingMask & (1 << 0)) != 0) cam.cullingMask |= Bit;
        }

        public static void Restore()
        {
            foreach (var l in _cut) if (l != null) l.cullingMask |= Bit;
            _cut.Clear();
        }
    }
}
