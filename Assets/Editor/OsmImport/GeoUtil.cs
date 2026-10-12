using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    public static class GeoUtil
    {
        /// <summary>Sutherland–Hodgman clip of a ring against an axis-aligned square [-half, half]².</summary>
        public static List<Vector2> ClipToSquare(List<Vector2> ring, float half)
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
                    bool eIn = Inside(e, edge, half), sIn = Inside(s, edge, half);
                    if (eIn)
                    {
                        if (!sIn) output.Add(Intersect(s, e, edge, half));
                        output.Add(e);
                    }
                    else if (sIn) output.Add(Intersect(s, e, edge, half));
                    s = e;
                }
            }
            return output;
        }

        static bool Inside(Vector2 p, int edge, float h)
        {
            switch (edge)
            {
                case 0: return p.x >= -h;
                case 1: return p.x <= h;
                case 2: return p.y >= -h;
                default: return p.y <= h;
            }
        }

        static Vector2 Intersect(Vector2 a, Vector2 b, int edge, float h)
        {
            float t;
            switch (edge)
            {
                case 0: t = (-h - a.x) / (b.x - a.x); break;
                case 1: t = (h - a.x) / (b.x - a.x); break;
                case 2: t = (-h - a.y) / (b.y - a.y); break;
                default: t = (h - a.y) / (b.y - a.y); break;
            }
            return Vector2.Lerp(a, b, t);
        }

        public static Rect Bounds(List<Vector2> pts)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in pts)
            {
                if (p.x < minX) minX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.x > maxX) maxX = p.x;
                if (p.y > maxY) maxY = p.y;
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        public static bool Overlaps(List<Vector2> pts, float half) =>
            Bounds(pts).Overlaps(Rect.MinMaxRect(-half, -half, half, half));

        public static Vector2 Centroid(List<Vector2> ring)
        {
            float a = 0, cx = 0, cy = 0;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                float f = ring[j].x * ring[i].y - ring[i].x * ring[j].y;
                a += f;
                cx += (ring[j].x + ring[i].x) * f;
                cy += (ring[j].y + ring[i].y) * f;
            }
            if (Mathf.Abs(a) < 1e-5f)
            {
                Vector2 sum = Vector2.zero;
                foreach (var p in ring) sum += p;
                return sum / Mathf.Max(1, ring.Count);
            }
            return new Vector2(cx / (3f * a), cy / (3f * a));
        }

        /// <summary>Parses OSM length values like "12", "12.5 m", "40'" (feet). Returns null if unparsable.</summary>
        public static float? ParseMeters(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var v = value.Trim().Replace(',', '.');
            float factor = 1f;
            if (v.EndsWith("ft") || v.EndsWith("'")) { factor = 0.3048f; v = v.TrimEnd('f', 't', '\'', ' '); }
            else if (v.EndsWith("m")) v = v.TrimEnd('m', ' ');
            int sep = v.IndexOf(';');
            if (sep >= 0) v = v.Substring(0, sep);
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f * factor : (float?)null;
        }

        public static bool IsConvex(List<Vector2> ring)
        {
            int n = ring.Count;
            if (n < 3) return false;
            bool? sign = null;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = ring[i], b = ring[(i + 1) % n], c = ring[(i + 2) % n];
                float cross = (b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x);
                if (Mathf.Abs(cross) < 1e-4f) continue;
                bool s = cross > 0;
                if (sign == null) sign = s;
                else if (sign != s) return false;
            }
            return true;
        }
    }
}
