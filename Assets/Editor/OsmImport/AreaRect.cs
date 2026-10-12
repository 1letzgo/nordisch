using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Playable area in local metres (x = east, z = north; Vector2.y = z). Areas may be rectangles that
    /// are not centred on the projection origin (e.g. Westerstede extends far to the west).
    /// </summary>
    public readonly struct AreaRect
    {
        public readonly float xMin, xMax, zMin, zMax;

        public AreaRect(float xMin, float xMax, float zMin, float zMax)
        {
            this.xMin = xMin; this.xMax = xMax; this.zMin = zMin; this.zMax = zMax;
        }

        public static AreaRect Square(float half) => new AreaRect(-half, half, -half, half);

        public float Width => xMax - xMin;
        public float Height => zMax - zMin;
        public Vector2 Center => new Vector2((xMin + xMax) * 0.5f, (zMin + zMax) * 0.5f);

        /// <summary>Grown (positive) or shrunk (negative) on every side.</summary>
        public AreaRect Expand(float d) => new AreaRect(xMin - d, xMax + d, zMin - d, zMax + d);

        /// <summary>Inside, keeping <paramref name="margin"/> metres from the edge.</summary>
        public bool Contains(Vector2 p, float margin = 0f) =>
            p.x >= xMin + margin && p.x <= xMax - margin && p.y >= zMin + margin && p.y <= zMax - margin;

        public bool Contains(Vector3 p, float margin = 0f) => Contains(new Vector2(p.x, p.z), margin);

        public Rect ToRect() => Rect.MinMaxRect(xMin, zMin, xMax, zMax);

        public bool Overlaps(List<Vector2> pts, float grow = 0f) =>
            GeoUtil.Bounds(pts).Overlaps(Expand(grow).ToRect());

        /// <summary>Sutherland–Hodgman clip of a ring against this rectangle.</summary>
        public List<Vector2> Clip(List<Vector2> ring)
        {
            var output = ring;
            for (int edge = 0; edge < 4; edge++)
            {
                var input = output;
                output = new List<Vector2>(input.Count + 4);
                if (input.Count == 0) break;
                Vector2 s = input[input.Count - 1];
                foreach (var e in input)
                {
                    bool eIn = Inside(e, edge), sIn = Inside(s, edge);
                    if (eIn)
                    {
                        if (!sIn) output.Add(Intersect(s, e, edge));
                        output.Add(e);
                    }
                    else if (sIn) output.Add(Intersect(s, e, edge));
                    s = e;
                }
            }
            return output;
        }

        bool Inside(Vector2 p, int edge)
        {
            switch (edge)
            {
                case 0: return p.x >= xMin;
                case 1: return p.x <= xMax;
                case 2: return p.y >= zMin;
                default: return p.y <= zMax;
            }
        }

        Vector2 Intersect(Vector2 a, Vector2 b, int edge)
        {
            float t;
            switch (edge)
            {
                case 0: t = (xMin - a.x) / (b.x - a.x); break;
                case 1: t = (xMax - a.x) / (b.x - a.x); break;
                case 2: t = (zMin - a.y) / (b.y - a.y); break;
                default: t = (zMax - a.y) / (b.y - a.y); break;
            }
            return Vector2.Lerp(a, b, t);
        }

        /// <summary>Liang–Barsky clip of segment a-b; false if it lies completely outside.</summary>
        public bool ClipSegment(ref Vector2 a, ref Vector2 b)
        {
            float t0 = 0f, t1 = 1f;
            Vector2 d = b - a;
            float[] p = { -d.x, d.x, -d.y, d.y };
            float[] q = { a.x - xMin, xMax - a.x, a.y - zMin, zMax - a.y };
            for (int k = 0; k < 4; k++)
            {
                if (Mathf.Approximately(p[k], 0f))
                {
                    if (q[k] < 0f) return false;
                    continue;
                }
                float r = q[k] / p[k];
                if (p[k] < 0f) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
            }
            Vector2 start = a;
            a = start + d * t0;
            b = start + d * t1;
            return true;
        }
    }
}
