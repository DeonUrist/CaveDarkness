using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CaveDarkness
{
    // Cave floors are the TERRAIN (the camp_base rock lies ~0.2 m under it), and a terrain can't take a per-renderer ambient like the rocks.
    // So inside each cave a thin copy of the terrain surface is laid 3 cm above it: a 0.5 m grid sampled from the TerrainData, drawn with a
    // copy of the terrain's own material (same splat map and layers, uv = the terrain's 0..1 coordinates) so it looks identical, split into
    // light bands like the rocks. The real terrain stays underneath for collisions and everything else.
    internal static class Floor
    {
        private const float Step = 0.5f, Lift = 0.03f;
        private const int Bands = 16, MaxCells = 200;

        public static IEnumerator Build(Cave c)
        {
            c.FloorInfo = "none";
            if (!c.HasInner) { c.FloorInfo = "no dark inside"; yield break; }
            var mouth = c.Mouth;
            Terrain ter = null;
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                var p = t.transform.position; var s = t.terrainData.size;
                if (mouth.x >= p.x && mouth.x <= p.x + s.x && mouth.z >= p.z && mouth.z <= p.z + s.z) { ter = t; break; }
            }
            if (ter == null) { c.FloorInfo = "no terrain under the cave"; yield break; }
            var td = ter.terrainData; var tp = ter.transform.position; var size = td.size;

            var src = ter.materialTemplate;
            var sh = src != null ? src.shader : Shader.Find("Nature/Terrain/Standard");
            if (sh == null) { c.FloorInfo = "no terrain shader"; yield break; }
            var mat = src != null ? new Material(src) : new Material(sh);
            if (!mat.HasProperty("_Control") || !mat.HasProperty("_Splat0"))
            {
                c.FloorInfo = "terrain shader " + sh.name + " not supported";
                Object.Destroy(mat);
                yield break;
            }
            mat.name = "CaveDarkness floor (" + sh.name + ")";
            mat.enableInstancing = false;
            mat.DisableKeyword("_TERRAIN_INSTANCED_PERPIXEL_NORMAL");
            mat.DisableKeyword("_ALPHATEST_ON");
            var al = td.alphamapTextures;
            if (al.Length > 0) mat.SetTexture("_Control", al[0]);
            var layers = td.terrainLayers;
            bool normals = false;
            for (int i = 0; i < 4 && i < layers.Length; i++)
            {
                var L = layers[i];
                if (L == null) continue;
                string n = "_Splat" + i;
                if (!mat.HasProperty(n)) continue;
                mat.SetTexture(n, L.diffuseTexture);
                mat.SetTextureScale(n, new Vector2(size.x / L.tileSize.x, size.z / L.tileSize.y));
                mat.SetTextureOffset(n, new Vector2(L.tileOffset.x / L.tileSize.x, L.tileOffset.y / L.tileSize.y));
                if (L.normalMapTexture != null && mat.HasProperty("_Normal" + i))
                {
                    mat.SetTexture("_Normal" + i, L.normalMapTexture);
                    if (mat.HasProperty("_NormalScale" + i)) mat.SetFloat("_NormalScale" + i, L.normalScale);
                    normals = true;
                }
                if (mat.HasProperty("_Metallic" + i)) mat.SetFloat("_Metallic" + i, L.metallic);
                if (mat.HasProperty("_Smoothness" + i)) mat.SetFloat("_Smoothness" + i, L.smoothness);
            }
            if (normals) mat.EnableKeyword("_NORMALMAP");
            c.MadeMats.Add(mat);

            // grid over the dark inside (+2 m), clipped to the terrain
            var b = c.Inner;
            float x0 = Mathf.Max(tp.x, b.min.x - 2f), x1 = Mathf.Min(tp.x + size.x, b.max.x + 2f);
            float z0 = Mathf.Max(tp.z, b.min.z - 2f), z1 = Mathf.Min(tp.z + size.z, b.max.z + 2f);
            float step = Mathf.Max(Step, Mathf.Max(x1 - x0, z1 - z0) / MaxCells);
            int nx = Mathf.Max(1, Mathf.CeilToInt((x1 - x0) / step)), nz = Mathf.Max(1, Mathf.CeilToInt((z1 - z0) / step));
            int w = nx + 1;
            var pos = new Vector3[w * (nz + 1)];
            var nor = new Vector3[pos.Length];
            var uv = new Vector2[pos.Length];
            var open = new float[pos.Length];
            var dist = new float[pos.Length];
            var light = new float[pos.Length];
            for (int j = 0; j <= nz; j++)
                for (int i = 0; i <= nx; i++)
                {
                    int k = j * w + i;
                    float x = Mathf.Min(x0 + i * step, x1), z = Mathf.Min(z0 + j * step, z1);
                    float u = (x - tp.x) / size.x, v = (z - tp.z) / size.z;
                    var n = td.GetInterpolatedNormal(u, v);
                    var p = new Vector3(x, td.GetInterpolatedHeight(u, v) + tp.y + Lift, z);
                    pos[k] = p; nor[k] = n; uv[k] = new Vector2(u, v);
                    open[k] = Sky.Surface(p, n); dist[k] = c.Dist(p);
                    light[k] = Sky.Light(open[k], dist[k]);
                    if (Caves.Over()) yield return null;
                }

            var band = new List<int>[Bands];
            var bOpen = new float[Bands]; var bDist = new float[Bands];
            int quads = 0;
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    int a = j * w + i, bq = a + w, cq = a + w + 1, d = a + 1;
                    if (light[a] >= Sky.Outside && light[bq] >= Sky.Outside && light[cq] >= Sky.Outside && light[d] >= Sky.Outside) continue;
                    float l = (light[a] + light[bq] + light[cq] + light[d]) * 0.25f;
                    int k = Mathf.Clamp((int)(l * Bands), 0, Bands - 1);
                    if (band[k] == null) band[k] = new List<int>();
                    band[k].Add(a);
                    bOpen[k] += (open[a] + open[bq] + open[cq] + open[d]) * 0.25f;
                    bDist[k] += (dist[a] + dist[bq] + dist[cq] + dist[d]) * 0.25f;
                    quads++;
                }
            if (quads == 0) { c.FloorInfo = "terrain inside is open to the sky"; yield break; }

            var go = new GameObject("CaveDarkness.Floor");
            go.transform.SetParent(c.Root.transform, false);
            go.transform.position = Vector3.zero; go.transform.rotation = Quaternion.identity;
            var ls = go.transform.lossyScale;
            go.transform.localScale = new Vector3(go.transform.localScale.x / ls.x, go.transform.localScale.y / ls.y, go.transform.localScale.z / ls.z);
            c.MadeGos.Add(go);
            int made = 0;
            for (int k = 0; k < Bands; k++)
            {
                if (band[k] == null) continue;
                var map = new Dictionary<int, int>();
                var p = new List<Vector3>(); var n = new List<Vector3>(); var u = new List<Vector2>(); var t = new List<int>();
                foreach (int a in band[k])
                {
                    int bq = a + w, cq = a + w + 1, d = a + 1;
                    int ia = V(a, map, p, n, u, pos, nor, uv), ib = V(bq, map, p, n, u, pos, nor, uv);
                    int ic = V(cq, map, p, n, u, pos, nor, uv), id = V(d, map, p, n, u, pos, nor, uv);
                    t.Add(ia); t.Add(ib); t.Add(ic);   // clockwise seen from above
                    t.Add(ia); t.Add(ic); t.Add(id);
                }
                var m = new Mesh { name = "CaveDarkness floor band " + k };
                if (p.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m.SetVertices(p); m.SetNormals(n); m.SetUVs(0, u); m.SetTriangles(t, 0);
                m.RecalculateBounds();
                m.UploadMeshData(true);
                c.Made.Add(m);
                var g = new GameObject("CaveDarkness.floorband" + k);
                g.layer = ter.gameObject.layer;
                g.transform.SetParent(go.transform, false);
                g.AddComponent<MeshFilter>().sharedMesh = m;
                var r = g.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = true;
                c.Parts.Add(new Part { R = r, Open = bOpen[k] / band[k].Count, Dist = bDist[k] / band[k].Count, Ours = true });
                made++;
            }
            int extra = 0;
            if (layers.Length > 4) extra = layers.Length - 4;
            c.FloorInfo = quads + " cells in " + made + " bands, step " + step.ToString("0.00") + " m, shader " + sh.name + ", " + layers.Length + " terrain layers" +
                          (extra > 0 ? " (only the first 4 are drawn on the floor copy)" : "");
        }

        private static int V(int k, Dictionary<int, int> map, List<Vector3> p, List<Vector3> n, List<Vector2> u, Vector3[] pos, Vector3[] nor, Vector2[] uv)
        {
            int i;
            if (map.TryGetValue(k, out i)) return i;
            i = p.Count; map[k] = i; p.Add(pos[k]); n.Add(nor[k]); u.Add(uv[k]);
            return i;
        }
    }
}
