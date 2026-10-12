using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    public static class GeoUtil
    {
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
