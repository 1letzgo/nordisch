using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>Flat ground layers: landuse/leisure/natural areas, water, roads, paths, rails.</summary>
    public static class GroundBuilder
    {
        struct AreaStyle
        {
            public Color32 color;
            public float y;
            public string mat;
        }

        public static void BuildAreas(OsmData data, MeshSink sink, float half)
        {
            foreach (var poly in data.polygons)
            {
                if (poly.tags.Has("building") || poly.tags.Has("building:part")) continue;
                if (!TryAreaStyle(poly.tags, out var style)) continue;
                if (!GeoUtil.Overlaps(poly.outer, half)) continue;

                var outer = GeoUtil.ClipToSquare(PolygonTriangulator.Clean(poly.outer), half);
                if (outer.Count < 3) continue;
                var holes = new List<List<Vector2>>();
                foreach (var h in poly.holes)
                {
                    var ch = GeoUtil.ClipToSquare(PolygonTriangulator.Clean(h), half);
                    if (ch.Count >= 3) holes.Add(ch);
                }
                if (!PolygonTriangulator.Triangulate(outer, holes, out var verts, out var tris)) continue;
                MeshSink.FlatPolygon(sink.Get(style.mat, GeoUtil.Centroid(outer)), verts, tris, style.y, style.color);
            }
        }

        static bool TryAreaStyle(Dictionary<string, string> t, out AreaStyle s)
        {
            s = new AreaStyle { mat = WorldStyle.MatGround, y = WorldStyle.YLanduse };
            if (t.Is("natural", "water") || t.Has("water") || t.Is("waterway", "riverbank") || t.Is("landuse", "basin") || t.Is("leisure", "swimming_pool"))
            {
                s.color = WorldStyle.Water; s.y = WorldStyle.YWater; s.mat = WorldStyle.MatWater; return true;
            }
            if (t.Is("highway", "pedestrian") && t.Is("area", "yes") || t.Is("place", "square") || t.Is("amenity", "marketplace"))
            {
                s.color = WorldStyle.Pedestrian; s.y = WorldStyle.YRoadMinor - 0.01f; return true;
            }
            if (t.Is("amenity", "parking"))
            {
                s.color = WorldStyle.Parking; s.y = WorldStyle.YLeisure; return true;
            }
            switch (t.Get("natural"))
            {
                case "beach": case "sand": s.color = WorldStyle.Sand; s.y = WorldStyle.YLeisure; return true;
                case "dune": s.color = WorldStyle.Dune; s.y = WorldStyle.YLeisure; return true;
                case "wood": case "scrub": case "heath": s.color = WorldStyle.Forest; return true;
                case "grassland": case "wetland": s.color = WorldStyle.Grass; return true;
            }
            switch (t.Get("leisure"))
            {
                case "park": case "garden": case "playground": case "pitch": case "golf_course": case "dog_park":
                    s.color = WorldStyle.Tint(WorldStyle.Grass, new System.Random(1), 0.04f); s.y = WorldStyle.YLeisure; return true;
            }
            switch (t.Get("landuse"))
            {
                case "forest": s.color = WorldStyle.Forest; return true;
                case "grass": case "meadow": case "recreation_ground": case "village_green": case "cemetery":
                case "allotments": case "farmland": case "orchard":
                    s.color = WorldStyle.Grass; return true;
                case "residential": s.color = WorldStyle.Residential; s.y = WorldStyle.YBase + 0.01f; return true;
                case "commercial": case "retail": case "industrial": case "railway": case "construction":
                    s.color = WorldStyle.Commercial; s.y = WorldStyle.YBase + 0.01f; return true;
            }
            s = default;
            return false;
        }

        struct LineStyle
        {
            public float width;
            public Color32 color;
            public float y;
            public string mat;
            public bool marking;
        }

        public static int BuildLines(OsmData data, MeshSink sink, float half)
        {
            int count = 0;
            foreach (var way in data.ways)
            {
                if (way.tags.Is("area", "yes") || way.tags.Is("tunnel", "yes") || way.tags.Has("tunnel") && !way.tags.Is("tunnel", "no")) continue;
                if (!TryLineStyle(way.tags, out var style)) continue;
                if (!GeoUtil.Overlaps(way.points, half + 20f)) continue;
                Ribbon(sink, way.points, style, half);
                if (style.marking) DashedLine(sink, way.points, half, style.y + 0.012f);
                count++;
            }
            return count;
        }

        static bool TryLineStyle(Dictionary<string, string> t, out LineStyle s)
        {
            s = new LineStyle { mat = WorldStyle.MatGround, color = WorldStyle.Asphalt, y = WorldStyle.YRoadMinor };
            var hw = t.Get("highway");
            if (hw != null)
            {
                var explicitWidth = GeoUtil.ParseMeters(t.Get("width"));
                switch (hw)
                {
                    case "motorway": case "trunk": case "primary": case "motorway_link": case "trunk_link": case "primary_link":
                        s.width = 10f; s.y = WorldStyle.YRoadMajor; break;
                    case "secondary": case "secondary_link": s.width = 8.5f; s.y = WorldStyle.YRoadMajor; break;
                    case "tertiary": case "tertiary_link": s.width = 7.5f; s.y = WorldStyle.YRoadMajor; break;
                    case "residential": case "unclassified": s.width = 6f; break;
                    case "living_street": case "road": s.width = 6f; break;
                    case "service": s.width = 4f; break;
                    case "pedestrian": s.width = 6f; s.color = WorldStyle.Pedestrian; s.y = WorldStyle.YRoadMinor - 0.005f; break;
                    case "track": s.width = 3f; s.color = WorldStyle.Path; s.y = WorldStyle.YPath; break;
                    case "cycleway": s.width = 2.2f; s.color = WorldStyle.Cycleway; s.y = WorldStyle.YPath; break;
                    case "footway": case "path": case "steps": case "bridleway": case "corridor":
                        s.width = 2f; s.color = t.Is("surface", "sand") || t.Is("surface", "unpaved") || t.Is("surface", "ground") ? WorldStyle.Path : WorldStyle.Paving;
                        s.y = WorldStyle.YPath; break;
                    default: return false;
                }
                if (explicitWidth.HasValue && explicitWidth.Value > 0.5f && explicitWidth.Value < 30f) s.width = explicitWidth.Value;
                s.marking = s.width >= 6f && s.color.Equals(WorldStyle.Asphalt) && hw != "living_street" && hw != "road"
                            && !t.Is("oneway", "yes") && !t.Is("lanes", "1");
                return true;
            }
            var rw = t.Get("railway");
            if (rw == "rail" || rw == "light_rail" || rw == "tram" || rw == "narrow_gauge")
            {
                s.width = rw == "narrow_gauge" ? 2f : 3f; s.color = WorldStyle.Rail; s.y = WorldStyle.YPath; return true;
            }
            switch (t.Get("waterway"))
            {
                case "river": s.width = 14f; break;
                case "canal": s.width = 10f; break;
                case "stream": s.width = 3f; break;
                case "ditch": case "drain": s.width = 1.5f; break;
                default: return false;
            }
            s.color = WorldStyle.Water; s.y = WorldStyle.YWater; s.mat = WorldStyle.MatWater;
            return true;
        }

        static void Ribbon(MeshSink sink, List<Vector2> pts, LineStyle style, float half)
        {
            float hw = style.width * 0.5f;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                if (!ClipSegment(ref a, ref b, half)) continue; // segment fully outside the area
                Vector2 dir = b - a;
                if (dir.sqrMagnitude < 1e-4f) continue;
                dir.Normalize();
                Vector2 n = new Vector2(-dir.y, dir.x) * hw; // left normal
                var batch = sink.Get(style.mat, (a + b) * 0.5f);
                // Segment quad (clockwise from above): left-a, left-b, right-b, right-a.
                MeshSink.Quad(batch, V(a + n, style.y), V(b + n, style.y), V(b - n, style.y), V(a - n, style.y), style.color);
                // Round-ish joint so consecutive segments and junctions have no gaps.
                Disc(batch, b, hw, style.y, style.color);
                if (i == 0) Disc(batch, a, hw, style.y, style.color);
            }
        }

        /// <summary>Liang–Barsky clip of segment a-b to the square [-half, half]².</summary>
        static bool ClipSegment(ref Vector2 a, ref Vector2 b, float half)
        {
            float t0 = 0f, t1 = 1f;
            Vector2 d = b - a;
            float[] p = { -d.x, d.x, -d.y, d.y };
            float[] q = { a.x + half, half - a.x, a.y + half, half - a.y };
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

        /// <summary>Dashed centre line: 3 m dash, 4 m gap, continuous across vertices.</summary>
        static void DashedLine(MeshSink sink, List<Vector2> pts, float half, float y)
        {
            const float dash = 3f, gap = 4f, hw = 0.08f;
            float phase = 0f; // distance into the current dash+gap cycle
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                float len = Vector2.Distance(a, b);
                if (len < 0.01f) continue;
                Vector2 dir = (b - a) / len, n = new Vector2(-dir.y, dir.x) * hw;
                float d = 0f;
                while (d < len)
                {
                    float inCycle = phase % (dash + gap);
                    if (inCycle < dash)
                    {
                        float segLen = Mathf.Min(dash - inCycle, len - d);
                        Vector2 p0 = a + dir * d, p1 = a + dir * (d + segLen);
                        if (Mathf.Abs(p0.x) < half && Mathf.Abs(p0.y) < half && Mathf.Abs(p1.x) < half && Mathf.Abs(p1.y) < half)
                            MeshSink.Quad(sink.Get(WorldStyle.MatGround, p0), V(p0 + n, y), V(p1 + n, y), V(p1 - n, y), V(p0 - n, y), WorldStyle.LaneMarking);
                        d += segLen; phase += segLen;
                    }
                    else
                    {
                        float skip = Mathf.Min(dash + gap - inCycle, len - d);
                        d += skip; phase += skip;
                    }
                }
            }
        }

        /// <summary>Hedges, walls and fences as thin extruded strips (with colliders via the Props bucket).</summary>
        public static int BuildBarriers(OsmData data, MeshSink sink, float half)
        {
            int count = 0;
            foreach (var w in data.ways)
            {
                float height, width;
                Color32 color;
                switch (w.tags.Get("barrier"))
                {
                    case "hedge": height = 1.4f; width = 0.9f; color = WorldStyle.Hedge; break;
                    case "wall": case "city_wall": case "retaining_wall": height = 1.6f; width = 0.35f; color = WorldStyle.StoneWall; break;
                    case "fence": case "guard_rail": case "railing": height = 1.1f; width = 0.08f; color = WorldStyle.Fence; break;
                    default: continue;
                }
                var h = GeoUtil.ParseMeters(w.tags.Get("height"));
                if (h.HasValue && h.Value > 0.3f && h.Value < 6f) height = h.Value;
                for (int i = 0; i < w.points.Count - 1; i++)
                {
                    Vector2 a = w.points[i], b = w.points[i + 1];
                    if (!ClipSegment(ref a, ref b, half)) continue;
                    Vector2 dir = b - a;
                    if (dir.sqrMagnitude < 1e-4f) continue;
                    Vector2 n = new Vector2(-dir.y, dir.x).normalized * (width * 0.5f);
                    var batch = sink.Get(WorldStyle.MatProps, (a + b) * 0.5f);
                    Vector3 al = V(a + n, 0), bl = V(b + n, 0), ar = V(a - n, 0), br = V(b - n, 0), up = Vector3.up * height;
                    // Quad(p, p+up, q+up, q) faces the right-hand side of travel p->q.
                    MeshSink.Quad(batch, ar, ar + up, br + up, br, color); // right side (faces -n)
                    MeshSink.Quad(batch, bl, bl + up, al + up, al, color); // left side (faces +n)
                    MeshSink.Quad(batch, al + up, bl + up, br + up, ar + up, WorldStyle.Tint(color, new System.Random(i), 0.05f)); // top
                    MeshSink.Quad(batch, al, al + up, ar + up, ar, color); // start cap (faces -dir)
                    MeshSink.Quad(batch, br, br + up, bl + up, bl, color); // end cap (faces +dir)
                }
                count++;
            }
            return count;
        }

        static void Disc(MeshSink.Batch batch, Vector2 c, float r, float y, Color32 color)
        {
            const int segments = 8;
            var center = V(c, y);
            for (int k = 0; k < segments; k++)
            {
                float a0 = k * Mathf.PI * 2f / segments, a1 = (k + 1) * Mathf.PI * 2f / segments;
                // Clockwise from above: decreasing angle.
                MeshSink.Tri(batch, center, V(c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, y),
                    V(c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, y), color);
            }
        }

        static Vector3 V(Vector2 p, float y) => new Vector3(p.x, y, p.y);

        /// <summary>Base ground slightly larger than the playable area, tiled with soft colour variation.</summary>
        public static void BuildBase(MeshSink sink, float half, Color32 color)
        {
            const float tile = 20f;
            float h = half + 60f;
            int n = Mathf.CeilToInt(2f * h / tile);
            var tris = new List<int> { 0, 1, 2, 0, 2, 3 };
            for (int ix = 0; ix < n; ix++)
            for (int iz = 0; iz < n; iz++)
            {
                float x0 = -h + ix * tile, z0 = -h + iz * tile;
                float noise = Mathf.PerlinNoise(x0 * 0.012f + 31.7f, z0 * 0.012f + 11.3f) - 0.5f;
                float f = 1f + noise * 0.14f;
                var c = new Color32((byte)Mathf.Clamp(color.r * f, 0, 255), (byte)Mathf.Clamp(color.g * (f + noise * 0.04f), 0, 255),
                    (byte)Mathf.Clamp(color.b * f, 0, 255), 255);
                var ring = new List<Vector2> { new Vector2(x0, z0), new Vector2(x0 + tile, z0), new Vector2(x0 + tile, z0 + tile), new Vector2(x0, z0 + tile) };
                MeshSink.FlatPolygon(sink.Get(WorldStyle.MatGround, new Vector2(x0, z0)), ring, tris, WorldStyle.YBase, c);
            }
        }
    }
}
