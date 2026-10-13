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

        public static void BuildAreas(OsmData data, MeshSink sink, AreaRect area)
        {
            foreach (var poly in data.polygons)
            {
                if (poly.tags.Has("building") || poly.tags.Has("building:part")) continue;
                if (!TryAreaStyle(poly.tags, out var style)) continue;
                if (!area.Overlaps(poly.outer)) continue;

                var outer = area.Clip(PolygonTriangulator.Clean(poly.outer));
                if (outer.Count < 3) continue;
                var holes = new List<List<Vector2>>();
                foreach (var h in poly.holes)
                {
                    var ch = area.Clip(PolygonTriangulator.Clean(h));
                    if (ch.Count >= 3) holes.Add(ch);
                }
                if (!PolygonTriangulator.Triangulate(outer, holes, out var verts, out var tris)) continue;
                MeshSink.FlatPolygon(sink.Get(style.mat, GeoUtil.Centroid(outer)), verts, tris, style.y, style.color);
            }
        }

        static bool TryAreaStyle(Dictionary<string, string> t, out AreaStyle s)
        {
            s = new AreaStyle { mat = WorldStyle.MatGround, y = WorldStyle.YLanduse };
            if (CoastBuilder.SkipArea(t)) { s = default; return false; } // islands: aerial cover + sea instead
            if (t.Is("natural", "water") || t.Has("water") || t.Is("waterway", "riverbank") || t.Is("landuse", "basin") || t.Is("leisure", "swimming_pool"))
            {
                s.color = CoastBuilder.Active ? CoastBuilder.SeaColor : WorldStyle.Water; s.y = WorldStyle.YWater; s.mat = WorldStyle.MatWater; return true;
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
                // Generic built-up landuse would hide the measured ground cover of gardens and yards.
                case "residential" when AerialImage.Current != null: s = default; return false;
                case "commercial" when AerialImage.Current != null: case "retail" when AerialImage.Current != null:
                case "industrial" when AerialImage.Current != null: s = default; return false;
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

        public static int BuildLines(OsmData data, MeshSink sink, AreaRect area)
        {
            int count = 0;
            foreach (var way in data.ways)
            {
                if (way.tags.Is("area", "yes") || way.tags.Is("tunnel", "yes") || way.tags.Has("tunnel") && !way.tags.Is("tunnel", "no")) continue;
                if (!TryLineStyle(way.tags, out var style)) continue;
                if (!area.Overlaps(way.points, 20f)) continue;
                Ribbon(sink, way.points, style, area);
                if (style.marking) DashedLine(sink, way.points, area, style.y + 0.012f);
                count++;
            }
            Kerbs(data, sink, area);
            return count;
        }

        static readonly Color32 Kerbstone = WorldStyle.Hex("#B8B4AA");

        /// <summary>
        /// Light kerbstone band along both edges of every carriageway. Pieces that fall onto another carriageway
        /// (junctions, crossings, joining service roads) are left out, so the band never runs across a road.
        /// </summary>
        static void Kerbs(OsmData data, MeshSink sink, AreaRect area)
        {
            var roads = new List<(OsmWay way, LineStyle style)>();
            var carriageways = new PropPlacer.Clearance();
            foreach (var way in data.ways)
            {
                if (way.tags.Is("area", "yes") || way.tags.Has("tunnel") && !way.tags.Is("tunnel", "no")) continue;
                if (!TryLineStyle(way.tags, out var style) || !style.color.Equals(WorldStyle.Asphalt)) continue;
                if (!area.Overlaps(way.points, 20f)) continue;
                roads.Add((way, style));
                carriageways.AddLine(way.points, style.width * 0.5f);
            }
            const float piece = 1.2f, band = 0.2f;
            foreach (var (way, style) in roads)
            {
                float hw = style.width * 0.5f, y = style.y + 0.006f;
                var pts = way.points;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Vector2 a = pts[i], b = pts[i + 1];
                    if (!area.ClipSegment(ref a, ref b)) continue;
                    float len = Vector2.Distance(a, b);
                    if (len < 0.3f) continue;
                    Vector2 dir = (b - a) / len, nrm = new Vector2(-dir.y, dir.x);
                    int pieces = Mathf.Max(1, Mathf.RoundToInt(len / piece));
                    float step = len / pieces;
                    var batch = sink.Get(WorldStyle.MatGround, (a + b) * 0.5f);
                    foreach (float side in new[] { 1f, -1f })
                    {
                        var n = nrm * side;
                        for (int k = 0; k < pieces; k++)
                        {
                            var s0 = a + dir * (k * step);
                            var s1 = a + dir * ((k + 1) * step);
                            // Just outside this road's edge: inside another carriageway -> junction, no kerb.
                            if (carriageways.IsBlocked((s0 + s1) * 0.5f + n * (hw + 0.15f), 0f)) continue;
                            Vector2 o0 = s0 + n * hw, o1 = s1 + n * hw, i0 = s0 + n * (hw - band), i1 = s1 + n * (hw - band);
                            // Clockwise from above for the left side; mirror for the right.
                            if (side > 0) MeshSink.Quad(batch, V(o0, y), V(o1, y), V(i1, y), V(i0, y), Kerbstone);
                            else MeshSink.Quad(batch, V(i0, y), V(i1, y), V(o1, y), V(o0, y), Kerbstone);
                        }
                    }
                }
            }
        }

        /// <summary>Drawn width of a road/path/rail/waterway ribbon (0 = not drawn), for keeping props off it.</summary>
        public static float LineWidth(Dictionary<string, string> t)
        {
            if (t.Is("area", "yes") || t.Has("tunnel") && !t.Is("tunnel", "no")) return 0f;
            return TryLineStyle(t, out var s) ? s.width : 0f;
        }

        /// <summary>Asphalt carriageway (car road): drawn width and surface height.</summary>
        public static bool Carriageway(Dictionary<string, string> t, out float width, out float y)
        {
            width = 0f; y = 0f;
            if (t.Is("area", "yes") || t.Has("tunnel") && !t.Is("tunnel", "no")) return false;
            if (!TryLineStyle(t, out var s) || !s.color.Equals(WorldStyle.Asphalt) || t.Get("highway") == null) return false;
            width = s.width; y = s.y;
            return true;
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
            s.color = CoastBuilder.Active ? CoastBuilder.SeaColor : WorldStyle.Water; s.y = WorldStyle.YWater; s.mat = WorldStyle.MatWater;
            return true;
        }

        static void Ribbon(MeshSink sink, List<Vector2> pts, LineStyle style, AreaRect area)
        {
            float hw = style.width * 0.5f;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                if (!area.ClipSegment(ref a, ref b)) continue; // segment fully outside the area
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

        /// <summary>Dashed centre line: 3 m dash, 4 m gap, continuous across vertices.</summary>
        static void DashedLine(MeshSink sink, List<Vector2> pts, AreaRect area, float y)
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
                        if (area.Contains(p0) && area.Contains(p1))
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
        public static int BuildBarriers(OsmData data, MeshSink sink, AreaRect area)
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
                    if (!area.ClipSegment(ref a, ref b)) continue;
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

        /// <summary>
        /// Faceted ground height field slightly larger than the playable area (TerrainField.CellSize cells,
        /// split along the x0z0–x1z1 diagonal – the same split TerrainField.Height uses), with soft colour
        /// variation and a little extra shading on slopes.
        /// </summary>
        static Color32 Shade(Color32 color, float f, float noise) =>
            new Color32((byte)Mathf.Clamp(color.r * f, 0, 255), (byte)Mathf.Clamp(color.g * (f + noise * 0.04f), 0, 255),
                (byte)Mathf.Clamp(color.b * f, 0, 255), 255);

        /// <summary>Largest merged terrain block in cells (8 × 5 m = 40 m), see <see cref="BuildBase"/>.</summary>
        const int MaxBlock = 8;
        /// <summary>Max. deviation (m) of any height sample from the block's plane for the block to be merged.</summary>
        const float PlanarTolerance = 0.02f;

        /// <summary>
        /// Terrain cells are merged quadtree-wise (40 → 20 → 10 m) where the ground is planar and has one ground cover:
        /// large flat areas (Watt, beach, fields, sea floor) cost two triangles per 40 m instead of 128. Heights of merged
        /// blocks still match <see cref="TerrainField.Height"/> within <see cref="PlanarTolerance"/>.
        /// </summary>
        public static void BuildBase(MeshSink sink, AreaRect area, Color32 color, TerrainField t)
        {
            float cs = TerrainField.CellSize;
            var g = area.Expand(60f);
            float startX = Mathf.Floor(g.xMin / cs) * cs, startZ = Mathf.Floor(g.zMin / cs) * cs;
            int nx = Mathf.CeilToInt((g.xMax - startX) / cs), nz = Mathf.CeilToInt((g.zMax - startZ) / cs);
            var aerial = AerialImage.Current;
            for (int bx = 0; bx < nx; bx += MaxBlock)
            for (int bz = 0; bz < nz; bz += MaxBlock)
            {
                if (!AreaMask.FineTerrain(startX + bx * cs, startZ + bz * cs, MaxBlock * cs)) continue; // CoarseTerrain
                Block(sink, t, aerial, color, startX, startZ, bx, bz, MaxBlock, nx, nz);
            }
        }

        static void Block(MeshSink sink, TerrainField t, AerialImage aerial, Color32 color, float startX, float startZ,
            int ix, int iz, int n, int nx, int nz)
        {
            float cs = TerrainField.CellSize;
            if (ix >= nx || iz >= nz) return;
            if (n > 1 && (ix + n > nx || iz + n > nz || !Mergeable(t, aerial, startX, startZ, ix, iz, n)))
            {
                int h = n / 2;
                Block(sink, t, aerial, color, startX, startZ, ix, iz, h, nx, nz);
                Block(sink, t, aerial, color, startX, startZ, ix + h, iz, h, nx, nz);
                Block(sink, t, aerial, color, startX, startZ, ix, iz + h, h, nx, nz);
                Block(sink, t, aerial, color, startX, startZ, ix + h, iz + h, h, nx, nz);
                return;
            }
            float x0 = startX + ix * cs, z0 = startZ + iz * cs, x1 = x0 + n * cs, z1 = z0 + n * cs;
            Vector3 p00 = new Vector3(x0, t.Raw(x0, z0), z0), p10 = new Vector3(x1, t.Raw(x1, z0), z0);
            Vector3 p01 = new Vector3(x0, t.Raw(x0, z1), z1), p11 = new Vector3(x1, t.Raw(x1, z1), z1);
            var batch = sink.Get(WorldStyle.MatTerrain, new Vector2(x0, z0));
            if (n == 1)
            {
                float noise = Mathf.PerlinNoise(x0 * 0.012f + 31.7f, z0 * 0.012f + 11.3f) - 0.5f;
                float f = 1f + noise * 0.14f;
                var c = Shade(color, f, noise);
                // With an aerial image each triangle takes the measured ground cover (garden, yard, sand, field).
                Color32 cUpper = c, cLower = c;
                if (aerial != null)
                {
                    cUpper = Shade(aerial.ColorAt(aerial.CoverIn(x0, z0, cs, true), x0 + cs * 0.33f, z0 + cs * 0.67f, color), f, noise);
                    cLower = Shade(aerial.ColorAt(aerial.CoverIn(x0, z0, cs, false), x0 + cs * 0.67f, z0 + cs * 0.33f, color), f, noise);
                }
                // Clockwise from above → facing up.
                MeshSink.Tri(batch, p00, p01, p11, cUpper);
                MeshSink.Tri(batch, p00, p11, p10, cLower);
                return;
            }
            // Merged block: one cover, colour variation per corner (blends smoothly across the block).
            var kind = aerial != null ? aerial.CoverIn(x0, z0, cs, true) : AerialImage.Cover.Unknown;
            Color32 C(Vector3 p)
            {
                float noise = Mathf.PerlinNoise(p.x * 0.012f + 31.7f, p.z * 0.012f + 11.3f) - 0.5f;
                return Shade(aerial != null ? aerial.ColorAt(kind, p.x, p.z, color) : color, 1f + noise * 0.14f, noise);
            }
            Color32 c00 = C(p00), c10 = C(p10), c01 = C(p01), c11 = C(p11);
            Tri3(batch, p00, p01, p11, c00, c01, c11);
            Tri3(batch, p00, p11, p10, c00, c11, c10);
        }

        /// <summary>Block of n×n cells is planar (all samples within tolerance) and of a single ground cover.</summary>
        static bool Mergeable(TerrainField t, AerialImage aerial, float startX, float startZ, int ix, int iz, int n)
        {
            float cs = TerrainField.CellSize;
            float x0 = startX + ix * cs, z0 = startZ + iz * cs, size = n * cs;
            float h00 = t.Raw(x0, z0), h10 = t.Raw(x0 + size, z0), h01 = t.Raw(x0, z0 + size), h11 = t.Raw(x0 + size, z0 + size);
            // The block is two triangles split x0z0–x1z1 (like every cell), so each half only has to be planar.
            for (int i = 0; i <= n; i++)
            for (int k = 0; k <= n; k++)
            {
                float u = (float)i / n, v = (float)k / n;
                float plane = u >= v ? h00 + (h10 - h00) * u + (h11 - h10) * v : h00 + (h11 - h01) * u + (h01 - h00) * v;
                if (Mathf.Abs(t.Raw(x0 + i * cs, z0 + k * cs) - plane) > PlanarTolerance) return false;
            }
            if (aerial == null) return true;
            var first = aerial.CoverIn(x0, z0, cs, true);
            for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                float cx = x0 + i * cs, cz = z0 + k * cs;
                if (aerial.CoverIn(cx, cz, cs, true) != first || aerial.CoverIn(cx, cz, cs, false) != first) return false;
            }
            return true;
        }

        /// <summary>Flat-normal triangle with its own colour per corner.</summary>
        static void Tri3(MeshSink.Batch b, Vector3 a, Vector3 c1, Vector3 c2, Color32 ca, Color32 cb, Color32 cc)
        {
            var nrm = Vector3.Cross(c1 - a, c2 - a).normalized;
            int i = b.vertices.Count;
            b.vertices.Add(a); b.vertices.Add(c1); b.vertices.Add(c2);
            b.normals.Add(nrm); b.normals.Add(nrm); b.normals.Add(nrm);
            b.colors.Add(ca); b.colors.Add(cb); b.colors.Add(cc);
            b.uvs.Add(MeshSink.NoFacade); b.uvs.Add(MeshSink.NoFacade); b.uvs.Add(MeshSink.NoFacade);
            b.triangles.Add(i); b.triangles.Add(i + 1); b.triangles.Add(i + 2);
        }
    }
}
