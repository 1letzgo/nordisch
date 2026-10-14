using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.World
{
    /// <summary>
    /// Named streets of an area (OSM highway=* with name; motorways by their ref, e.g. "A 28") as 2D segments, filled by
    /// the importer. <see cref="Nearest"/> answers "which street am I on" for the HUD, <see cref="OnFastRoad"/> whether
    /// that is a motorway (cars may go faster there).
    /// </summary>
    public class StreetIndex : ScriptableObject
    {
        public List<string> names = new List<string>();
        /// <summary>(ax, az, bx, bz) per segment.</summary>
        public List<Vector4> segments = new List<Vector4>();
        /// <summary>Index into <see cref="names"/> per segment.</summary>
        public List<int> nameOf = new List<int>();
        /// <summary>Indices into <see cref="names"/> of motorways and their ramps (empty in older maps).</summary>
        public List<int> fast = new List<int>();

        const float Cell = 25f;
        Dictionary<Vector2Int, List<int>> grid;

        void BuildGrid()
        {
            grid = new Dictionary<Vector2Int, List<int>>();
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                int x0 = Mathf.FloorToInt(Mathf.Min(s.x, s.z) / Cell), x1 = Mathf.FloorToInt(Mathf.Max(s.x, s.z) / Cell);
                int z0 = Mathf.FloorToInt(Mathf.Min(s.y, s.w) / Cell), z1 = Mathf.FloorToInt(Mathf.Max(s.y, s.w) / Cell);
                for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    var k = new Vector2Int(x, z);
                    if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                    l.Add(i);
                }
            }
        }

        /// <summary>
        /// Name of the closest street within <paramref name="maxDistance"/> metres, or null. The street in
        /// <paramref name="current"/> wins ties within <paramref name="stickiness"/> metres (no flicker at junctions).
        /// </summary>
        public string Nearest(Vector3 position, float maxDistance, string current = null, float stickiness = 4f)
        {
            if (grid == null) BuildGrid();
            var p = new Vector2(position.x, position.z);
            int r = Mathf.CeilToInt(maxDistance / Cell);
            var c = new Vector2Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell));
            float best = float.MaxValue, currentBest = float.MaxValue;
            int bestName = -1;
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (!grid.TryGetValue(new Vector2Int(c.x + dx, c.y + dz), out var list)) continue;
                foreach (int i in list)
                {
                    var s = segments[i];
                    Vector2 a = new Vector2(s.x, s.y), ab = new Vector2(s.z, s.w) - a;
                    float t = ab.sqrMagnitude < 1e-4f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                    float d = Vector2.Distance(p, a + ab * t);
                    if (d < best) { best = d; bestName = nameOf[i]; }
                    if (current != null && names[nameOf[i]] == current) currentBest = Mathf.Min(currentBest, d);
                }
            }
            if (current != null && currentBest <= maxDistance && currentBest <= best + stickiness) return current;
            return bestName >= 0 && best <= maxDistance ? names[bestName] : null;
        }

        /// <summary>A motorway segment (centre line of one carriageway) lies within <paramref name="maxDistance"/> metres.</summary>
        public bool OnFastRoad(Vector3 position, float maxDistance)
        {
            if (fast == null || fast.Count == 0) return false;
            if (grid == null) BuildGrid();
            var p = new Vector2(position.x, position.z);
            int r = Mathf.CeilToInt(maxDistance / Cell);
            var c = new Vector2Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell));
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (!grid.TryGetValue(new Vector2Int(c.x + dx, c.y + dz), out var list)) continue;
                foreach (int i in list)
                {
                    if (!fast.Contains(nameOf[i])) continue;
                    var s = segments[i];
                    Vector2 a = new Vector2(s.x, s.y), ab = new Vector2(s.z, s.w) - a;
                    float t = ab.sqrMagnitude < 1e-4f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                    if (Vector2.Distance(p, a + ab * t) <= maxDistance) return true;
                }
            }
            return false;
        }
    }
}
