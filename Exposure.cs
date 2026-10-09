using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace CaveDarkness
{
    // Eye adaptation (post-processing AutoExposure) brightens a dark picture back up, which flattens a dark cave into "uniformly lit".
    // While the camera is in a baked cave, the lowest luminance the adaptation reacts to is raised to EV 0 (or the profile's max, if lower),
    // so it stops boosting the dark; restored on the way out. Only touches profiles that have AutoExposure switched on.
    internal static class Exposure
    {
        private class Saved { public AutoExposure Ae; public float Min; }
        private static readonly List<Saved> _ae = new List<Saved>();
        private static float _nextFind, _next;
        private static bool _clamped, _logged;

        public static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 0.25f;
            if (now >= _nextFind && !_clamped) { _nextFind = now + 10f; Find(); }
            var cam = Camera.main;
            bool inCave = cam != null && Caves.At(cam.transform.position) != null && Dyn.CameraLight < 0.6f;
            if (inCave && !_clamped && _ae.Count > 0)
            {
                foreach (var s in _ae)
                    if (s.Ae != null) s.Ae.minLuminance.value = Mathf.Max(s.Min, Mathf.Min(0f, s.Ae.maxLuminance.value));
                _clamped = true;
                Plugin.Verbose("eye adaptation held while in the cave");
            }
            else if (!inCave && _clamped) Restore();
        }

        public static void Restore()
        {
            if (!_clamped) return;
            foreach (var s in _ae) if (s.Ae != null) s.Ae.minLuminance.value = s.Min;
            _clamped = false;
        }

        private static void Find()
        {
            _ae.Clear();
            foreach (var v in Object.FindObjectsOfType<PostProcessVolume>())
            {
                var prof = v != null ? v.sharedProfile : null;
                AutoExposure ae;
                if (prof == null || !prof.TryGetSettings(out ae) || ae == null) continue;
                if (!_logged)
                    Plugin.Verbose("post-processing " + v.name + ": AutoExposure " + (ae.enabled.value ? "on" : "off") + ", min " + ae.minLuminance.value +
                                       " EV, max " + ae.maxLuminance.value + " EV, key " + ae.keyValue.value + ", mode " + ae.eyeAdaptation.value);
                if (!ae.enabled.value || !ae.active) continue;
                bool dup = false;
                foreach (var s in _ae) if (s.Ae == ae) dup = true;
                if (!dup) _ae.Add(new Saved { Ae = ae, Min = ae.minLuminance.value });
            }
            _logged = true;
        }
    }
}
