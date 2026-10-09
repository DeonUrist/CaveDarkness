using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CaveDarkness
{
    // Readable copies of the cave rock meshes. The game's own meshes are not CPU-readable, so they were exported from the game's
    // assets (Rock1..5_Cave, cave_entrance, camp_base) into cavemeshes.bin:
    // "CDM1", int count, then per mesh: int nameLen, name (UTF-8), int vertexCount, int indexCount,
    // float3 positions[vc], float3 normals[vc], float2 uv[vc], int indices[ic] (submesh 0, the game's own winding).
    internal class MeshData
    {
        public string Name;
        public Vector3[] P, N;
        public Vector2[] U;
        public int[] T;
    }

    internal static class MeshBank
    {
        private static readonly Dictionary<string, MeshData> _meshes = new Dictionary<string, MeshData>();

        public static void Load(string path)
        {
            _meshes.Clear();
            if (!File.Exists(path)) { Plugin.Log.LogWarning("cavemeshes.bin missing (" + path + ") - cave rocks are darkened as whole objects"); return; }
            try
            {
                using (var br = new BinaryReader(File.OpenRead(path)))
                {
                    if (new string(br.ReadChars(4)) != "CDM1") throw new Exception("bad header");
                    int n = br.ReadInt32();
                    for (int m = 0; m < n; m++)
                    {
                        var d = new MeshData();
                        d.Name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(br.ReadInt32()));
                        int vc = br.ReadInt32(), ic = br.ReadInt32();
                        d.P = new Vector3[vc]; d.N = new Vector3[vc]; d.U = new Vector2[vc]; d.T = new int[ic];
                        for (int i = 0; i < vc; i++) d.P[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        for (int i = 0; i < vc; i++) d.N[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        for (int i = 0; i < vc; i++) d.U[i] = new Vector2(br.ReadSingle(), br.ReadSingle());
                        for (int i = 0; i < ic; i++) d.T[i] = br.ReadInt32();
                        _meshes[d.Name] = d;
                    }
                }
                Plugin.Log.LogInfo("cave meshes: " + string.Join(", ", new List<string>(_meshes.Keys).ToArray()));
            }
            catch (Exception e) { _meshes.Clear(); Plugin.Log.LogWarning("cavemeshes.bin unreadable: " + e.Message); }
        }

        public static MeshData Get(Mesh m)
        {
            if (m == null) return null;
            MeshData d;
            string n = m.name;
            if (n.EndsWith(" Instance")) n = n.Substring(0, n.Length - 9);
            if (!_meshes.TryGetValue(n, out d)) return null;
            return d.P.Length == m.vertexCount ? d : null;   // a different mesh with the same name: don't touch it
        }
    }

    // Working copy of a mesh for subdividing and splitting.
    internal class WorkMesh
    {
        public List<Vector3> P, N;
        public List<Vector2> U;
        public List<int> T;

        public WorkMesh(MeshData d)
        {
            P = new List<Vector3>(d.P); N = new List<Vector3>(d.N); U = new List<Vector2>(d.U); T = new List<int>(d.T);
        }

        // Splits every edge longer than maxLen (decided per edge, so both triangles of an edge agree and no cracks appear) until none is left.
        public void Subdivide(float maxLen, int maxTris)
        {
            float m2 = maxLen * maxLen;
            for (int pass = 0; pass < 10; pass++)
            {
                var mids = new Dictionary<long, int>();
                var nt = new List<int>(T.Count * 2);
                bool any = false;
                for (int i = 0; i + 2 < T.Count; i += 3)
                {
                    int a = T[i], b = T[i + 1], c = T[i + 2];
                    int ab = Mid(a, b, m2, mids), bc = Mid(b, c, m2, mids), ca = Mid(c, a, m2, mids);
                    int k = (ab >= 0 ? 1 : 0) + (bc >= 0 ? 1 : 0) + (ca >= 0 ? 1 : 0);
                    if (k == 0) { nt.Add(a); nt.Add(b); nt.Add(c); continue; }
                    any = true;
                    if (k == 3)
                    {
                        Tri(nt, a, ab, ca); Tri(nt, ab, b, bc); Tri(nt, ca, bc, c); Tri(nt, ab, bc, ca);
                        continue;
                    }
                    // rotate so the pattern is canonical: one split -> on edge a-b; two splits -> on a-b and b-c
                    if (k == 1)
                    {
                        if (bc >= 0) { Rot(ref a, ref b, ref c, ref ab, ref bc, ref ca); }
                        else if (ca >= 0) { Rot(ref a, ref b, ref c, ref ab, ref bc, ref ca); Rot(ref a, ref b, ref c, ref ab, ref bc, ref ca); }
                        Tri(nt, a, ab, c); Tri(nt, ab, b, c);
                    }
                    else
                    {
                        if (ab < 0) { Rot(ref a, ref b, ref c, ref ab, ref bc, ref ca); }       // missing a-b -> becomes missing c-a
                        else if (bc < 0) { Rot(ref a, ref b, ref c, ref ab, ref bc, ref ca); Rot(ref a, ref b, ref c, ref ab, ref bc, ref ca); }
                        Tri(nt, ab, b, bc); Tri(nt, a, ab, bc); Tri(nt, a, bc, c);
                    }
                }
                T = nt;
                if (!any || T.Count / 3 > maxTris) break;
            }
        }

        // (a,b,c) -> (b,c,a) with the edge midpoints following
        private static void Rot(ref int a, ref int b, ref int c, ref int ab, ref int bc, ref int ca)
        {
            int t = a; a = b; b = c; c = t;
            int e = ab; ab = bc; bc = ca; ca = e;
        }

        private static void Tri(List<int> l, int a, int b, int c) { l.Add(a); l.Add(b); l.Add(c); }

        private int Mid(int a, int b, float m2, Dictionary<long, int> mids)
        {
            if ((P[a] - P[b]).sqrMagnitude <= m2) return -1;
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int m;
            if (mids.TryGetValue(key, out m)) return m;
            m = P.Count;
            P.Add((P[a] + P[b]) * 0.5f);
            N.Add((N[a] + N[b]).normalized);
            U.Add((U[a] + U[b]) * 0.5f);
            mids[key] = m;
            return m;
        }

        // A new mesh holding only the given triangles (indices into T, step 3), vertices compacted.
        public Mesh Build(List<int> triStarts, string name)
        {
            var map = new Dictionary<int, int>();
            var p = new List<Vector3>(); var n = new List<Vector3>(); var u = new List<Vector2>(); var t = new List<int>(triStarts.Count * 3);
            for (int i = 0; i < triStarts.Count; i++)
                for (int j = 0; j < 3; j++)
                {
                    int v = T[triStarts[i] + j], w;
                    if (!map.TryGetValue(v, out w)) { w = p.Count; map[v] = w; p.Add(P[v]); n.Add(N[v]); u.Add(U[v]); }
                    t.Add(w);
                }
            var m = new Mesh();
            m.name = name;
            if (p.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(p); m.SetNormals(n); m.SetUVs(0, u); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            if (t.Count > 0) m.RecalculateTangents();
            m.UploadMeshData(true);   // GPU only; the bake keeps its own lists
            return m;
        }
    }
}
