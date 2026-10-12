using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Ear-clipping triangulation for simple polygons with optional holes (holes are
    /// bridged into the outer ring). Works on the XZ plane via Vector2(x, z).
    /// </summary>
    public static class PolygonTriangulator
    {
        /// <summary>Signed area; positive = counter-clockwise (x right, y up).</summary>
        public static float SignedArea(IList<Vector2> ring)
        {
            float a = 0f;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                a += ring[j].x * ring[i].y - ring[i].x * ring[j].y;
            return a * 0.5f;
        }

        /// <summary>Removes closing duplicate and consecutive duplicates.</summary>
        public static List<Vector2> Clean(IList<Vector2> ring)
        {
            var result = new List<Vector2>(ring.Count);
            foreach (var p in ring)
                if (result.Count == 0 || (result[result.Count - 1] - p).sqrMagnitude > 1e-6f)
                    result.Add(p);
            if (result.Count > 1 && (result[0] - result[result.Count - 1]).sqrMagnitude <= 1e-6f)
                result.RemoveAt(result.Count - 1);
            return result;
        }

        public static void EnsureWinding(List<Vector2> ring, bool ccw)
        {
            if ((SignedArea(ring) > 0f) != ccw) ring.Reverse();
        }

        /// <summary>
        /// Triangulates. Returns vertex list (merged outer + holes) and CCW triangle indices.
        /// Returns false if the polygon is degenerate.
        /// </summary>
        public static bool Triangulate(List<Vector2> outer, List<List<Vector2>> holes,
            out List<Vector2> vertices, out List<int> triangles)
        {
            vertices = null;
            triangles = null;
            outer = Clean(outer);
            if (outer.Count < 3) return false;
            EnsureWinding(outer, true);

            var poly = new List<Vector2>(outer);
            if (holes != null && holes.Count > 0)
            {
                var cleanHoles = new List<List<Vector2>>();
                foreach (var h in holes)
                {
                    var ch = Clean(h);
                    if (ch.Count < 3) continue;
                    EnsureWinding(ch, false);
                    cleanHoles.Add(ch);
                }
                // Bridge holes in order of max x (rightmost first) for robustness.
                cleanHoles.Sort((a, b) => MaxX(b).CompareTo(MaxX(a)));
                foreach (var h in cleanHoles) poly = BridgeHole(poly, h);
            }

            vertices = poly;
            triangles = EarClip(poly);
            return triangles.Count >= 3;
        }

        static float MaxX(List<Vector2> ring)
        {
            float m = float.MinValue;
            foreach (var p in ring) if (p.x > m) m = p.x;
            return m;
        }

        static List<Vector2> BridgeHole(List<Vector2> poly, List<Vector2> hole)
        {
            int hi = 0;
            for (int i = 1; i < hole.Count; i++) if (hole[i].x > hole[hi].x) hi = i;
            Vector2 hp = hole[hi];

            // Nearest polygon vertex whose bridge segment does not cross any polygon edge.
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < poly.Count; i++)
            {
                float d = (poly[i] - hp).sqrMagnitude;
                if (d >= bestDist) continue;
                if (SegmentCrossesRing(hp, poly[i], poly) || SegmentCrossesRing(hp, poly[i], hole)) continue;
                best = i;
                bestDist = d;
            }
            if (best < 0)
            {
                for (int i = 0; i < poly.Count; i++)
                {
                    float d = (poly[i] - hp).sqrMagnitude;
                    if (d < bestDist) { best = i; bestDist = d; }
                }
            }

            var result = new List<Vector2>(poly.Count + hole.Count + 2);
            for (int i = 0; i <= best; i++) result.Add(poly[i]);
            for (int k = 0; k <= hole.Count; k++) result.Add(hole[(hi + k) % hole.Count]);
            result.Add(poly[best]);
            for (int i = best + 1; i < poly.Count; i++) result.Add(poly[i]);
            return result;
        }

        static bool SegmentCrossesRing(Vector2 a, Vector2 b, List<Vector2> ring)
        {
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                Vector2 c = ring[j], d = ring[i];
                if (c == a || c == b || d == a || d == b) continue;
                if (SegmentsIntersect(a, b, c, d)) return true;
            }
            return false;
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            float d1 = Cross(p3, p4, p1), d2 = Cross(p3, p4, p2);
            float d3 = Cross(p1, p2, p3), d4 = Cross(p1, p2, p4);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        static List<int> EarClip(List<Vector2> pts)
        {
            var tris = new List<int>((pts.Count - 2) * 3);
            var idx = new List<int>(pts.Count);
            for (int i = 0; i < pts.Count; i++) idx.Add(i);

            int guard = 0;
            int maxIter = pts.Count * pts.Count + 10;
            while (idx.Count > 3 && guard++ < maxIter)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int ip = idx[(i - 1 + idx.Count) % idx.Count];
                    int ic = idx[i];
                    int inx = idx[(i + 1) % idx.Count];
                    Vector2 a = pts[ip], b = pts[ic], c = pts[inx];
                    if (Cross(a, b, c) <= 1e-7f) continue; // reflex or degenerate
                    if (AnyPointInside(pts, idx, ip, ic, inx, a, b, c)) continue;
                    tris.Add(ip); tris.Add(ic); tris.Add(inx);
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped)
                {
                    // Self-intersecting / degenerate input: drop the flattest vertex and continue.
                    int worst = 0;
                    float worstArea = float.MaxValue;
                    for (int i = 0; i < idx.Count; i++)
                    {
                        float ar = Mathf.Abs(Cross(pts[idx[(i - 1 + idx.Count) % idx.Count]], pts[idx[i]], pts[idx[(i + 1) % idx.Count]]));
                        if (ar < worstArea) { worstArea = ar; worst = i; }
                    }
                    idx.RemoveAt(worst);
                }
            }
            if (idx.Count == 3 && Cross(pts[idx[0]], pts[idx[1]], pts[idx[2]]) > 0f)
            {
                tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]);
            }
            return tris;
        }

        static bool AnyPointInside(List<Vector2> pts, List<int> idx, int ia, int ib, int ic, Vector2 a, Vector2 b, Vector2 c)
        {
            foreach (int k in idx)
            {
                if (k == ia || k == ib || k == ic) continue;
                Vector2 p = pts[k];
                if (p == a || p == b || p == c) continue; // bridge duplicates
                if (Cross(a, b, p) >= 0 && Cross(b, c, p) >= 0 && Cross(c, a, p) >= 0) return true;
            }
            return false;
        }
    }
}
