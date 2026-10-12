using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Fits generated geometry onto the terrain:
    /// <list type="bullet">
    /// <item><see cref="Drape"/> – flat overlays (landuse, roads, water, markings): triangles are cut along the
    /// ground cells and their diagonals, then lifted to the faceted surface + their layer offset.</item>
    /// <item><see cref="Lift"/> – small objects (trees, props): every vertex moves up by the ground height below it.</item>
    /// </list>
    /// </summary>
    public static class TerrainDrape
    {
        const float FlatTolerance = 0.02f;

        public static void Lift(MeshSink.Batch b, TerrainField t)
        {
            if (t.IsFlat) return;
            for (int i = 0; i < b.vertices.Count; i++)
            {
                var v = b.vertices[i];
                v.y += t.Height(v.x, v.z);
                b.vertices[i] = v;
            }
        }

        /// <summary>Moves vertices [start, end) up by a constant (buildings sit on their footprint minimum).</summary>
        public static void Offset(MeshSink.Batch b, int start, float dy)
        {
            if (dy == 0f) return;
            for (int i = start; i < b.vertices.Count; i++)
            {
                var v = b.vertices[i];
                v.y += dy;
                b.vertices[i] = v;
            }
        }

        struct Vert
        {
            public Vector3 p;     // y = layer offset above ground
            public Color32 c;
            public Vector4 uv;
        }

        public static void Drape(MeshSink.Batch b, TerrainField t)
        {
            if (t.IsFlat) return;
            var outV = new List<Vector3>(b.vertices.Count);
            var outN = new List<Vector3>(b.vertices.Count);
            var outC = new List<Color32>(b.colors.Count);
            var outUv = new List<Vector4>(b.uvs.Count);
            var outT = new List<int>(b.triangles.Count);
            float cs = TerrainField.CellSize;

            void Emit(Vert a, Vert c1, Vert c2)
            {
                Vector3 pa = Ground(a.p, t), pb = Ground(c1.p, t), pc = Ground(c2.p, t);
                var nrm = Vector3.Cross(pb - pa, pc - pa);
                if (nrm.sqrMagnitude < 1e-10f) return;
                nrm.Normalize();
                if (nrm.y < 0) { (pb, pc) = (pc, pb); (c1, c2) = (c2, c1); nrm = -nrm; }
                int i = outV.Count;
                outV.Add(pa); outV.Add(pb); outV.Add(pc);
                outN.Add(nrm); outN.Add(nrm); outN.Add(nrm);
                outC.Add(a.c); outC.Add(c1.c); outC.Add(c2.c);
                outUv.Add(a.uv); outUv.Add(c1.uv); outUv.Add(c2.uv);
                outT.Add(i); outT.Add(i + 1); outT.Add(i + 2);
            }

            for (int k = 0; k < b.triangles.Count; k += 3)
            {
                var tri = new Vert[3];
                for (int j = 0; j < 3; j++)
                {
                    int vi = b.triangles[k + j];
                    tri[j] = new Vert { p = b.vertices[vi], c = b.colors[vi], uv = b.uvs[vi] };
                }
                float minX = Mathf.Min(tri[0].p.x, Mathf.Min(tri[1].p.x, tri[2].p.x));
                float maxX = Mathf.Max(tri[0].p.x, Mathf.Max(tri[1].p.x, tri[2].p.x));
                float minZ = Mathf.Min(tri[0].p.z, Mathf.Min(tri[1].p.z, tri[2].p.z));
                float maxZ = Mathf.Max(tri[0].p.z, Mathf.Max(tri[1].p.z, tri[2].p.z));

                if (IsFlat(t, minX, maxX, minZ, maxZ))
                {
                    Emit(tri[0], tri[1], tri[2]);
                    continue;
                }

                // Cut along the ground cells and their (x0,z0)-(x1,z1) diagonal.
                int cx0 = Mathf.FloorToInt(minX / cs), cx1 = Mathf.FloorToInt(maxX / cs);
                int cz0 = Mathf.FloorToInt(minZ / cs), cz1 = Mathf.FloorToInt(maxZ / cs);
                for (int cx = cx0; cx <= cx1; cx++)
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    float x0 = cx * cs, z0 = cz * cs;
                    var poly = new List<Vert>(tri);
                    poly = Clip(poly, v => v.p.x - x0);
                    poly = Clip(poly, v => x0 + cs - v.p.x);
                    poly = Clip(poly, v => v.p.z - z0);
                    poly = Clip(poly, v => z0 + cs - v.p.z);
                    if (poly.Count < 3) continue;
                    // Lower-right half (u >= v) and upper-left half (u <= v) of the cell.
                    foreach (var half in new[] { Clip(poly, v => (v.p.x - x0) - (v.p.z - z0)), Clip(poly, v => (v.p.z - z0) - (v.p.x - x0)) })
                    {
                        for (int f = 1; f + 1 < half.Count; f++) Emit(half[0], half[f], half[f + 1]);
                    }
                }
            }

            b.vertices.Clear(); b.vertices.AddRange(outV);
            b.normals.Clear(); b.normals.AddRange(outN);
            b.colors.Clear(); b.colors.AddRange(outC);
            b.uvs.Clear(); b.uvs.AddRange(outUv);
            b.triangles.Clear(); b.triangles.AddRange(outT);
        }

        static Vector3 Ground(Vector3 p, TerrainField t) => new Vector3(p.x, t.Height(p.x, p.z) + p.y, p.z);

        /// <summary>Terrain inside the box varies less than the tolerance → no need to cut.</summary>
        static bool IsFlat(TerrainField t, float minX, float maxX, float minZ, float maxZ)
        {
            float cs = TerrainField.CellSize;
            float lo = float.MaxValue, hi = float.MinValue;
            for (float x = Mathf.Floor(minX / cs) * cs; x <= maxX + cs; x += cs)
            for (float z = Mathf.Floor(minZ / cs) * cs; z <= maxZ + cs; z += cs)
            {
                float h = t.Raw(x, z);
                if (h < lo) lo = h;
                if (h > hi) hi = h;
                if (hi - lo > FlatTolerance) return false;
            }
            return true;
        }

        /// <summary>Sutherland–Hodgman against the half-plane f(v) >= 0, interpolating all attributes.</summary>
        static List<Vert> Clip(List<Vert> poly, System.Func<Vert, float> f)
        {
            var result = new List<Vert>(poly.Count + 2);
            if (poly.Count == 0) return result;
            Vert s = poly[poly.Count - 1];
            float fs = f(s);
            foreach (var e in poly)
            {
                float fe = f(e);
                if (fe >= 0)
                {
                    if (fs < 0) result.Add(Lerp(s, e, fs / (fs - fe)));
                    result.Add(e);
                }
                else if (fs >= 0) result.Add(Lerp(s, e, fs / (fs - fe)));
                s = e; fs = fe;
            }
            return result;
        }

        static Vert Lerp(Vert a, Vert b, float t) => new Vert
        {
            p = Vector3.Lerp(a.p, b.p, t),
            c = Color32.Lerp(a.c, b.c, t),
            uv = Vector4.Lerp(a.uv, b.uv, t),
        };
    }
}
