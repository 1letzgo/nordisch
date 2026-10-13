using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Built-up part of a large area (Data/&lt;id&gt;.mask.png + .json, written by Tools/osm/make_mask.py): settlements and
    /// corridors along the roads between them. Everything outside is only terrain (<see cref="CoarseTerrain"/>), water
    /// and railways – no houses, trees, gardens, props or roads. Areas without a mask file are built everywhere
    /// (<see cref="Current"/> is null).
    /// Pixel values: 0 outside, 32 outside + forest, 64 outside + lake, 160 landscape band along a motorway (coarse terrain,
    /// but roads, water, barriers, LiDAR trees), 176 motorway corridor (same with detailed terrain), 192 road corridor,
    /// 255 settlement (≥ 128 = inside; houses, areas and mapped objects only ≥ 192 = <see cref="Detail"/>); top row = north.
    /// </summary>
    public class AreaMask
    {
        public static AreaMask Current { get; private set; }

        byte[] px;
        int w, h;
        float xMin, zMax, step;

        public static AreaMask Load(string dataDir, string id)
        {
            Current = null;
            string png = Path.Combine(dataDir, id + ".mask.png"), meta = Path.Combine(dataDir, id + ".mask.json");
            if (!File.Exists(png) || !File.Exists(meta)) return null;
            var m = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(meta));
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!tex.LoadImage(File.ReadAllBytes(png))) { Object.DestroyImmediate(tex); return null; }
            var cols = tex.GetPixels32(); // row 0 = south after LoadImage
            bool alpha = tex.format == TextureFormat.Alpha8;
            var mask = new AreaMask
            {
                w = tex.width, h = tex.height, step = System.Convert.ToSingle(m["step"]),
                xMin = System.Convert.ToSingle(m["xMin"]),
            };
            mask.zMax = System.Convert.ToSingle(m["zMin"]) + mask.h * mask.step;
            mask.px = new byte[mask.w * mask.h];
            for (int i = 0; i < cols.Length; i++)
            {
                int row = mask.h - 1 - i / mask.w; // store north-up like the file
                mask.px[row * mask.w + i % mask.w] = alpha ? cols[i].a : cols[i].r;
            }
            Object.DestroyImmediate(tex);
            return Current = mask;
        }

        byte Value(float x, float z)
        {
            int c = Mathf.FloorToInt((x - xMin) / step), r = Mathf.FloorToInt((zMax - z) / step);
            return c < 0 || r < 0 || c >= w || r >= h ? (byte)0 : px[r * w + c];
        }

        public bool Inside(float x, float z) => Value(x, z) >= 128;
        public bool Inside(Vector2 p) => Value(p.x, p.y) >= 128;
        /// <summary>Settlement or road corridor: houses, gardens, landuse areas, furniture (not in the motorway's landscape).</summary>
        public bool Detail(float x, float z) => Value(x, z) >= 192;
        public bool Detail(Vector2 p) => Value(p.x, p.y) >= 192;
        public const byte FineLevel = 176;
        public bool IsForest(float x, float z) => Value(x, z) == 32;
        public bool IsLake(float x, float z) => Value(x, z) == 64;

        /// <summary>No mask, or the point is inside: the builders may place things here.</summary>
        public static bool Allows(Vector2 p) => Current == null || Current.Inside(p);
        public static bool Allows(float x, float z) => Current == null || Current.Inside(x, z);
        /// <summary>No mask, or houses are built here (settlements and road corridors).</summary>
        public static bool AllowsDetail(float x, float z) => Current == null || Current.Detail(x, z);

        /// <summary>Generated street lamps: in settlements only – the country roads between the villages are unlit.</summary>
        public static bool LitStreet(Vector2 p) => Current == null || Current.Value(p.x, p.y) == 255;

        /// <summary>Any inside pixel touches the square [x0, x0+size]² grown by <paramref name="margin"/>.</summary>
        public bool AnyInside(float x0, float z0, float size, float margin, byte level = 128)
        {
            int c0 = Mathf.Max(0, Mathf.FloorToInt((x0 - margin - xMin) / step));
            int c1 = Mathf.Min(w - 1, Mathf.FloorToInt((x0 + size + margin - xMin) / step));
            int r0 = Mathf.Max(0, Mathf.FloorToInt((zMax - (z0 + size + margin)) / step));
            int r1 = Mathf.Min(h - 1, Mathf.FloorToInt((zMax - (z0 - margin)) / step));
            for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (px[r * w + c] >= level) return true;
            return false;
        }

        /// <summary>
        /// Terrain block that is built in full detail (<see cref="GroundBuilder.BuildBase"/>); all others are left to
        /// <see cref="CoarseTerrain"/>. 10 m margin so nothing draped inside the mask reaches coarse ground.
        /// </summary>
        public static bool FineTerrain(float x0, float z0, float size) => Current == null || Current.AnyInside(x0, z0, size, 10f, FineLevel);

        // ------------------------------------------------------------------ OSM filter

        public struct FilterStats { public int nodes, polygons, ways, pieces; }

        static bool IsWaterArea(Dictionary<string, string> t) =>
            t.Is("natural", "water") || t.Has("water") || t.Is("waterway", "riverbank") || t.Is("landuse", "basin") || t.Is("landuse", "reservoir");

        /// <summary>Lines kept outside the mask: rivers, canals, railways (streams and ditches only inside).</summary>
        static bool KeepEverywhere(Dictionary<string, string> t)
        {
            if (t.Is("waterway", "river") || t.Is("waterway", "canal")) return true;
            var rw = t.Get("railway");
            return rw == "rail" || rw == "light_rail" || rw == "narrow_gauge";
        }

        /// <summary>
        /// Removes everything outside the mask right after loading (the builders stay unchanged): nodes by position,
        /// buildings by centroid, other areas when their centroid and most of the outline are inside, lines are cut to
        /// the inside runs. Rivers, canals and railways stay everywhere, lakes when they touch the mask.
        /// </summary>
        public FilterStats Filter(OsmData data)
        {
            var st = new FilterStats();
            st.nodes = data.nodes.RemoveAll(n => !Detail(data.projection.ToLocal(n.lat, n.lon)));
            st.polygons = data.polygons.RemoveAll(p => !KeepPolygon(p));
            var ways = new List<OsmWay>(data.ways.Count);
            foreach (var way in data.ways)
            {
                if (KeepEverywhere(way.tags)) { ways.Add(way); continue; }
                var runs = InsideRuns(way.points);
                if (runs.Count == 1 && runs[0].Count == way.points.Count) { ways.Add(way); continue; }
                if (runs.Count == 0) { st.ways++; continue; }
                st.pieces += runs.Count;
                foreach (var run in runs) ways.Add(new OsmWay { id = way.id, tags = way.tags, points = run });
            }
            data.ways.Clear();
            data.ways.AddRange(ways);
            return st;
        }

        bool KeepPolygon(OsmPolygon p)
        {
            if (p.outer.Count == 0) return false;
            if (IsWaterArea(p.tags))
            {
                // Lakes completely outside are coloured into the coarse terrain (mask value 64) – draping them is costly.
                if (Inside(GeoUtil.Centroid(p.outer))) return true;
                foreach (var q in p.outer) if (Inside(q)) return true;
                return false;
            }
            if (!Detail(GeoUtil.Centroid(p.outer))) return false;
            if (p.tags.Has("building") || p.tags.Has("building:part")) return true;
            int inside = 0;
            foreach (var q in p.outer) if (Detail(q)) inside++;
            return inside * 2 >= p.outer.Count;
        }

        /// <summary>Pieces of a polyline inside the mask, sampled every ≤ 5 m (cut points snap to the samples).</summary>
        List<List<Vector2>> InsideRuns(List<Vector2> pts)
        {
            var runs = new List<List<Vector2>>();
            List<Vector2> cur = null;
            Vector2 lastInside = Vector2.zero;
            for (int i = 0; i < pts.Count; i++)
            {
                if (i == 0)
                {
                    if (Inside(pts[0])) { cur = new List<Vector2> { pts[0] }; lastInside = pts[0]; }
                    continue;
                }
                Vector2 a = pts[i - 1], b = pts[i];
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 5f));
                for (int k = 1; k <= n; k++)
                {
                    var p = k == n ? b : Vector2.Lerp(a, b, (float)k / n);
                    bool inside = Inside(p);
                    if (inside && cur == null) cur = new List<Vector2> { p };               // entering
                    else if (inside && k == n) cur.Add(p);                                   // original vertex
                    else if (!inside && cur != null)                                         // leaving
                    {
                        if (cur[cur.Count - 1] != lastInside) cur.Add(lastInside);
                        if (cur.Count >= 2) runs.Add(cur);
                        cur = null;
                    }
                    if (inside) lastInside = p;
                }
            }
            if (cur != null)
            {
                if (cur[cur.Count - 1] != lastInside) cur.Add(lastInside);
                if (cur.Count >= 2) runs.Add(cur);
            }
            return runs;
        }
    }
}
