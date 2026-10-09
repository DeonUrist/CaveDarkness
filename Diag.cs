using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace CaveDarkness
{
    // Debug (TintTest or VerboseLog): when the camera enters a baked cave, and every 5 s while inside, log the render state and
    // what is under the crosshair, to find out what still lights a cave.
    internal static class Diag
    {
        private static float _next;
        private static bool _wasIn, _globalDone;

        public static void Tick()
        {
            if (!Plugin.TintTest.Value && !Plugin.VerboseLog.Value) { _wasIn = false; return; }
            float now = Time.unscaledTime;
            if (now < _next) return;
            var cam = Camera.main;
            if (cam == null) return;
            var cave = Caves.At(cam.transform.position);
            bool inside = cave != null && Dyn.CameraLight < 0.9f;
            if (!inside) { _wasIn = false; _next = now + 0.5f; return; }
            _next = now + 5f;
            var sb = new StringBuilder();
            if (!_wasIn || !_globalDone) { Global(sb, cam); _globalDone = true; }
            _wasIn = true;
            var cp = cam.transform.position;
            sb.Append("[diag] camera in ").Append(cave.Name).Append(", ").Append(cave.Dist(cp).ToString("0.0")).Append(" m from the entrance, open ")
              .Append(cave.OpenAt(cp).ToString("0.00")).Append(", camera light ").Append(Dyn.CameraLight.ToString("0.00")).Append('\n');
            RaycastHit hit;
            if (Physics.Raycast(cp, cam.transform.forward, out hit, 60f, ~((1 << 2) | (1 << 6)), QueryTriggerInteraction.Ignore))
            {
                sb.Append("[diag] crosshair: ").Append(Path(hit.collider.transform)).Append(" at ").Append(hit.distance.ToString("0.0")).Append(" m\n");
                var t = hit.collider.transform;
                var ter = hit.collider.GetComponent<Terrain>();
                if (ter != null)
                {
                    var mt = ter != null ? ter.materialTemplate : null;
                    sb.Append("   terrain ").Append(ter != null ? ter.name : "?").Append(" material ").Append(mt != null ? mt.name + " / " + mt.shader.name : "none")
                      .Append(", layers ").Append(ter != null && ter.terrainData != null ? ter.terrainData.terrainLayers.Length : 0)
                      .Append(", floor map ").Append(cave.FloorInfo ?? "none").Append('\n');
                }
                foreach (var r in t.GetComponentsInChildren<Renderer>(true)) Describe(sb, r, cave);
                if (t.parent != null) foreach (var r in t.parent.GetComponents<Renderer>()) Describe(sb, r, cave);
            }
            else sb.Append("[diag] crosshair: nothing within 60 m\n");
            Plugin.Log.LogInfo(sb.ToString().TrimEnd());
        }

        private static void Describe(StringBuilder sb, Renderer r, Cave cave)
        {
            if (r == null || r.GetType().Name.Contains("Particle")) return;
            var mats = r.sharedMaterials;
            sb.Append("   ").Append(r.name).Append(" [").Append(r.GetType().Name).Append(r.enabled ? "" : " DISABLED").Append(r.isVisible ? " visible" : "")
              .Append("] probes ").Append(r.lightProbeUsage).Append(", MPB ").Append(r.HasPropertyBlock() ? "yes" : "no")
              .Append(", refl ").Append(r.reflectionProbeUsage).Append(", anchor ").Append(r.probeAnchor != null ? r.probeAnchor.name : "-")
              .Append(", shadows cast ").Append(r.shadowCastingMode).Append(" recv ").Append(r.receiveShadows);
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) sb.Append(", mesh ").Append(mf.sharedMesh.name).Append(" (").Append(mf.sharedMesh.vertexCount).Append(" v)");
            foreach (var m in mats)
            {
                if (m == null) continue;
                sb.Append("\n      material ").Append(m.name).Append(" shader ").Append(m.shader != null ? m.shader.name : "?")
                  .Append(" queue ").Append(m.renderQueue).Append(" keywords [").Append(string.Join(" ", m.shaderKeywords)).Append(']');
                if (m.HasProperty("_EmissionColor")) sb.Append(" emission ").Append(m.GetColor("_EmissionColor"));
                if (m.HasProperty("_Color")) sb.Append(" color ").Append(m.GetColor("_Color"));
            }
            foreach (var p in cave.Parts)
                if (p.R == r) sb.Append("\n      cave part: open ").Append(p.Open.ToString("0.00")).Append(", ").Append(p.Dist.ToString("0.0"))
                                .Append(" m, light ").Append(Sky.Light(p.Open, p.Dist).ToString("0.00"));
            sb.Append('\n');
        }

        private static void Global(StringBuilder sb, Camera cam)
        {
            sb.Append("[diag] camera ").Append(cam.name).Append(" path ").Append(cam.actualRenderingPath).Append(", HDR ").Append(cam.allowHDR)
              .Append(", colour space ").Append(QualitySettings.activeColorSpace).Append(", quality ").Append(QualitySettings.names[QualitySettings.GetQualityLevel()])
              .Append(", shadows ").Append(QualitySettings.shadows).Append(" dist ").Append(QualitySettings.shadowDistance).Append(" cascades ").Append(QualitySettings.shadowCascades).Append('\n');
            sb.Append("[diag] ").Append(Sky.AmbientInfo()).Append(", reflection intensity ").Append(RenderSettings.reflectionIntensity)
              .Append(" mode ").Append(RenderSettings.defaultReflectionMode).Append(", fog ").Append(RenderSettings.fog).Append(' ').Append(RenderSettings.fogMode)
              .Append(" density ").Append(RenderSettings.fogDensity).Append('\n');
            foreach (var l in Object.FindObjectsOfType<Light>())
            {
                if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f) continue;
                if (l.type != LightType.Directional && (l.transform.position - cam.transform.position).sqrMagnitude > 40f * 40f) continue;
                sb.Append("[diag] light ").Append(Path(l.transform)).Append(' ').Append(l.type).Append(" int ").Append(l.intensity.ToString("0.00"))
                  .Append(" range ").Append(l.range.ToString("0.0")).Append(" shadows ").Append(l.shadows).Append(" mask ").Append(l.cullingMask)
                  .Append(" mode ").Append(l.renderMode).Append('\n');
            }
            foreach (var v in Object.FindObjectsOfType<PostProcessVolume>())
            {
                var prof = v != null ? v.sharedProfile : null;
                sb.Append("[diag] post volume ").Append(v.name).Append(v.isGlobal ? " global" : " local").Append(" weight ").Append(v.weight).Append(":");
                if (prof != null) foreach (var s in prof.settings) if (s != null) sb.Append(' ').Append(s.GetType().Name).Append(s.enabled.value ? "(on)" : "(off)");
                sb.Append('\n');
            }
            foreach (var pl in Object.FindObjectsOfType<PostProcessLayer>())
                sb.Append("[diag] post layer on ").Append(pl.name).Append(pl.enabled ? " enabled" : " disabled").Append(", volume mask ").Append(pl.volumeLayer.value).Append('\n');
            foreach (var mb in cam.GetComponents<MonoBehaviour>())
                if (mb != null) sb.Append("[diag] camera component ").Append(mb.GetType().FullName).Append(mb.enabled ? "" : " (off)").Append('\n');
        }

        private static string Path(Transform t)
        {
            string s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }
    }
}
