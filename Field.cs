using System.Collections;
using UnityEngine;

namespace CaveDarkness
{
    // 0.5.0: ONE darkness field per cave instead of per-spot sky rays.
    // A voxel grid over the cave structure (+4 m) is split into rock (any surface of the static world passing through a voxel, found by rays
    // along all three axes + everything under the terrain, then grown by one voxel to seal cracks and the gap under the walls) and air.
    // Every air voxel gets its distance THROUGH THE AIR to open sky (Dijkstra; sources = air voxels with nothing above them, and the grid's
    // sides). Light comes in where the sky is and fades with that distance: the cave mouth is bright, the inside darkens the further you walk
    // in, ground under an outer overhang is only a few metres from the sky (stays lit), and a crack thinner than a voxel leads nowhere.
    // Rocks, floor, objects and the player all read the same field, so there are no seams between them.
    internal class Field
    {
        public Transform Anchor;      // the cave's top-level object (MapMagic): moves with every floating-origin jump
        public Vector3 OriginLocal;   // corner of voxel (0,0,0) in the anchor's frame
        public Vector3 Origin { get { return OriginLocal + (Anchor != null ? Anchor.position : Vector3.zero); } }   // world, now
        public float V;               // voxel size
        public int NX, NY, NZ;
        public byte[] Q;              // 255 = rock, 254 = air not reachable from the sky, else distance in Unit metres
        public const float Unit = 0.25f;
        public int Rock, Air, Unreached;

        private int Idx(int x, int y, int z) { return (y * NZ + z) * NX + x; }

        // distance through the air to open sky at p (m); 0 outside the grid, 1000 where no air connects to the sky
        public float Sample(Vector3 p)
        {
            Vector3 g = (p - Origin) / V - new Vector3(0.5f, 0.5f, 0.5f);
            int x0 = Mathf.FloorToInt(g.x), y0 = Mathf.FloorToInt(g.y), z0 = Mathf.FloorToInt(g.z);
            if (x0 < -1 || y0 < -1 || z0 < -1 || x0 >= NX || y0 >= NY || z0 >= NZ) return 0f;
            float fx = g.x - x0, fy = g.y - y0, fz = g.z - z0;
            float sum = 0f, sw = 0f;
            for (int k = 0; k < 8; k++)
            {
                int x = x0 + (k & 1), y = y0 + ((k >> 1) & 1), z = z0 + ((k >> 2) & 1);
                if (x < 0 || y < 0 || z < 0 || x >= NX || y >= NY || z >= NZ) { float wo = W(k, fx, fy, fz); sum += 0f; sw += wo; continue; }
                byte q = Q[Idx(x, y, z)];
                if (q == 255) continue;
                float w = W(k, fx, fy, fz);
                sum += w * (q == 254 ? 1000f : q * Unit);
                sw += w;
            }
            if (sw > 1e-4f) return sum / sw;
            return Nearest(Mathf.RoundToInt(g.x), Mathf.RoundToInt(g.y), Mathf.RoundToInt(g.z));
        }

        // a surface point: look a little way out along its normal (the voxels on the surface itself are rock)
        public float SampleSurface(Vector3 p, Vector3 n)
        {
            for (int s = 1; s <= 3; s++)
            {
                var q = p + n * (V * (0.25f + 0.5f * s));
                if (!IsRock(q)) return Sample(q);
            }
            return Sample(p + n * V);
        }

        public bool IsRock(Vector3 p)
        {
            Vector3 g = (p - Origin) / V;
            int x = Mathf.FloorToInt(g.x), y = Mathf.FloorToInt(g.y), z = Mathf.FloorToInt(g.z);
            if (x < 0 || y < 0 || z < 0 || x >= NX || y >= NY || z >= NZ) return false;
            return Q[Idx(x, y, z)] == 255;
        }

        private static float W(int k, float fx, float fy, float fz)
        {
            return ((k & 1) != 0 ? fx : 1f - fx) * (((k >> 1) & 1) != 0 ? fy : 1f - fy) * (((k >> 2) & 1) != 0 ? fz : 1f - fz);
        }

        private float Nearest(int cx, int cy, int cz)
        {
            float best = 1000f, bd = float.MaxValue;
            for (int r = 1; r <= 2; r++)
                for (int y = cy - r; y <= cy + r; y++)
                    for (int z = cz - r; z <= cz + r; z++)
                        for (int x = cx - r; x <= cx + r; x++)
                        {
                            if (x < 0 || y < 0 || z < 0 || x >= NX || y >= NY || z >= NZ) continue;
                            byte q = Q[Idx(x, y, z)];
                            if (q == 255) continue;
                            float d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy) + (z - cz) * (z - cz);
                            if (d2 < bd) { bd = d2; best = q == 254 ? 1000f : q * Unit; }
                        }
            return best;
        }

        // ---------------------------------------------------------------- build
        public static IEnumerator Build(Cave c, Field f)
        {
            var b = c.Box;
            b.Expand(8f);   // 4 m each side
            float vol = b.size.x * b.size.y * b.size.z;
            f.V = Mathf.Max(0.5f, Mathf.Pow(vol / 500000f, 1f / 3f));
            f.Anchor = c.Anchor;
            f.OriginLocal = b.min - c.AnchorPos;
            f.NX = Mathf.Max(2, Mathf.CeilToInt(b.size.x / f.V));
            f.NY = Mathf.Max(2, Mathf.CeilToInt(b.size.y / f.V));
            f.NZ = Mathf.Max(2, Mathf.CeilToInt(b.size.z / f.V));
            int n = f.NX * f.NY * f.NZ;
            var rock = new bool[n];
            float v = f.V;
            int mask = Sky.Mask;

            // 1. a 1.5 m layer under the ground is rock (else the air "leaks" under the walls and along slopes; terrain holes stay open).
            //    Deeper down stays air: underground caves keep their space, and air down there is only reachable through real openings.
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                var td = t.terrainData; var tp = t.transform.position; var sz = td.size;
                for (int z = 0; z < f.NZ; z++)
                    for (int x = 0; x < f.NX; x++)
                    {
                        float wx = f.Origin.x + (x + 0.5f) * v, wz = f.Origin.z + (z + 0.5f) * v;
                        float uu = (wx - tp.x) / sz.x, vv = (wz - tp.z) / sz.z;
                        if (uu < 0f || uu > 1f || vv < 0f || vv > 1f) continue;
                        float hgt = td.GetInterpolatedHeight(uu, vv) + tp.y;
                        int hr = td.holesResolution;
                        if (hr > 0 && td.IsHole(Mathf.Clamp((int)(uu * hr), 0, hr - 1), Mathf.Clamp((int)(vv * hr), 0, hr - 1))) continue;
                        int top = Mathf.Min(f.NY - 1, Mathf.FloorToInt((hgt + v * 0.25f - f.Origin.y) / v));
                        int bot = Mathf.Max(0, Mathf.FloorToInt((hgt - 1.5f - f.Origin.y) / v));
                        for (int y = bot; y <= top; y++) rock[f.Idx(x, y, z)] = true;
                    }
                if (Caves.Over()) yield return null;
            }
            // 2. surfaces: rays through the voxel centres along X, Y and Z, every crossing marks its voxel
            for (int axis = 0; axis < 3; axis++)
            {
                int na = axis == 0 ? f.NX : axis == 1 ? f.NY : f.NZ;
                int nu = axis == 0 ? f.NY : f.NX, nw = axis == 2 ? f.NY : f.NZ;
                Vector3 dir = axis == 0 ? Vector3.right : axis == 1 ? Vector3.down : Vector3.forward;
                float len = na * v;
                for (int u = 0; u < nu; u++)
                {
                    for (int w = 0; w < nw; w++)
                    {
                        Vector3 o;
                        if (axis == 0) o = f.Origin + new Vector3(0f, (u + 0.5f) * v, (w + 0.5f) * v);
                        else if (axis == 1) o = f.Origin + new Vector3((u + 0.5f) * v, len, (w + 0.5f) * v);
                        else o = f.Origin + new Vector3((u + 0.5f) * v, (w + 0.5f) * v, 0f);
                        float left = len;
                        for (int guard = 0; guard < 64 && left > 0.001f; guard++)
                        {
                            RaycastHit h;
                            if (!Physics.Raycast(o, dir, out h, left, mask, QueryTriggerInteraction.Ignore)) break;
                            Sky.Rays++;
                            Mark(f, rock, h.point);
                            float adv = h.distance + 0.01f;
                            o += dir * adv; left -= adv;
                        }
                    }
                    if (Caves.Over()) yield return null;
                }
            }
            // 3. grow the rock by one voxel: seals cracks and the gap under the walls (the mouth is metres wide, it stays open)
            var grown = (bool[])rock.Clone();
            for (int y = 0; y < f.NY; y++)
            {
                for (int z = 0; z < f.NZ; z++)
                    for (int x = 0; x < f.NX; x++)
                    {
                        int i = f.Idx(x, y, z);
                        if (!rock[i]) continue;
                        if (x > 0) grown[i - 1] = true;
                        if (x < f.NX - 1) grown[i + 1] = true;
                        if (z > 0) grown[i - f.NX] = true;
                        if (z < f.NZ - 1) grown[i + f.NX] = true;
                        if (y > 0) grown[i - f.NX * f.NZ] = true;
                        if (y < f.NY - 1) grown[i + f.NX * f.NZ] = true;
                    }
                if (Caves.Over()) yield return null;
            }
            rock = grown;

            // 4. distance through the air from open sky (Dijkstra, 26 neighbours)
            var dist = new float[n];
            for (int i = 0; i < n; i++) dist[i] = float.MaxValue;
            var heap = new Heap(n / 4 + 64);
            for (int z = 0; z < f.NZ; z++)
                for (int x = 0; x < f.NX; x++)
                {
                    // open sky: the air in each column above its first rock voxel; the grid's sides: air above the ground (the first rock
                    // from the top) - not the air deep under the ground
                    for (int y = f.NY - 1; y >= 0; y--)
                    {
                        int i = f.Idx(x, y, z);
                        if (rock[i]) break;
                        dist[i] = 0f; heap.Push(0f, i);
                    }
                }
            int pops = 0;
            int sxy = f.NX * f.NZ;
            while (heap.Count > 0)
            {
                float d; int i;
                heap.Pop(out d, out i);
                if (d > dist[i]) continue;
                int y = i / sxy, rem = i - y * sxy, z = rem / f.NX, x = rem - z * f.NX;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy; if (yy < 0 || yy >= f.NY) continue;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int zz = z + dz; if (zz < 0 || zz >= f.NZ) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx; if (xx < 0 || xx >= f.NX) continue;
                            int k = (dx != 0 ? 1 : 0) + (dy != 0 ? 1 : 0) + (dz != 0 ? 1 : 0);
                            if (k == 0) continue;
                            int j = (yy * f.NZ + zz) * f.NX + xx;
                            if (rock[j]) continue;
                            // no diagonal squeezing between two rock voxels
                            if (k > 1 && (rock[(y * f.NZ + zz) * f.NX + xx] && rock[(yy * f.NZ + z) * f.NX + x] && rock[(yy * f.NZ + zz) * f.NX + x])) continue;
                            float nd = d + v * (k == 1 ? 1f : k == 2 ? 1.41421f : 1.73205f);
                            if (nd < dist[j]) { dist[j] = nd; heap.Push(nd, j); }
                        }
                    }
                }
                if ((++pops & 1023) == 0 && Caves.Over()) yield return null;
            }

            // 5. pack
            f.Q = new byte[n];
            for (int i = 0; i < n; i++)
            {
                if (rock[i]) { f.Q[i] = 255; f.Rock++; }
                else if (dist[i] == float.MaxValue) { f.Q[i] = 254; f.Unreached++; }
                else { f.Q[i] = (byte)Mathf.Min(253, Mathf.RoundToInt(dist[i] / Unit)); f.Air++; }
            }
        }

        private static void Mark(Field f, bool[] rock, Vector3 p)
        {
            Vector3 g = (p - f.Origin) / f.V;
            int x = Mathf.FloorToInt(g.x), y = Mathf.FloorToInt(g.y), z = Mathf.FloorToInt(g.z);
            if (x < 0 || y < 0 || z < 0 || x >= f.NX || y >= f.NY || z >= f.NZ) return;
            rock[f.Idx(x, y, z)] = true;
        }

        // binary min-heap of (float key, int value)
        private class Heap
        {
            private float[] _k; private int[] _v; public int Count;
            public Heap(int cap) { _k = new float[cap]; _v = new int[cap]; }
            public void Push(float k, int v)
            {
                if (Count == _k.Length) { System.Array.Resize(ref _k, Count * 2); System.Array.Resize(ref _v, Count * 2); }
                int i = Count++;
                while (i > 0) { int p = (i - 1) >> 1; if (_k[p] <= k) break; _k[i] = _k[p]; _v[i] = _v[p]; i = p; }
                _k[i] = k; _v[i] = v;
            }
            public void Pop(out float k, out int v)
            {
                k = _k[0]; v = _v[0];
                float lk = _k[--Count]; int lv = _v[Count];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1; if (l >= Count) break;
                    int r = l + 1, m = (r < Count && _k[r] < _k[l]) ? r : l;
                    if (_k[m] >= lk) break;
                    _k[i] = _k[m]; _v[i] = _v[m]; i = m;
                }
                _k[i] = lk; _v[i] = lv;
            }
        }
    }
}
