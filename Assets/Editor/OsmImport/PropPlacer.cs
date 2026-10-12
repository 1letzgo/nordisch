using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Vegetation and street furniture: OSM trees, scattered groves in parks/woods (kept off
    /// paths, plazas and pitches), bushes on dunes/heath, street lamps along roads.
    /// </summary>
    public static class PropPlacer
    {
        /// <summary>Spatial lookup of everything vegetation must keep clear of.</summary>
        class Clearance
        {
            const float Cell = 20f;
            readonly Dictionary<Vector2Int, List<(Vector2 a, Vector2 b, float r)>> segments = new Dictionary<Vector2Int, List<(Vector2, Vector2, float)>>();
            readonly List<List<Vector2>> polygons = new List<List<Vector2>>();
            readonly List<Rect> polygonBounds = new List<Rect>();

            public void AddLine(List<Vector2> pts, float radius)
            {
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Vector2 a = pts[i], b = pts[i + 1];
                    var min = Vector2.Min(a, b) - Vector2.one * radius;
                    var max = Vector2.Max(a, b) + Vector2.one * radius;
                    for (int x = Mathf.FloorToInt(min.x / Cell); x <= Mathf.FloorToInt(max.x / Cell); x++)
                    for (int y = Mathf.FloorToInt(min.y / Cell); y <= Mathf.FloorToInt(max.y / Cell); y++)
                    {
                        var key = new Vector2Int(x, y);
                        if (!segments.TryGetValue(key, out var list)) segments[key] = list = new List<(Vector2, Vector2, float)>();
                        list.Add((a, b, radius));
                    }
                }
            }

            public void AddPolygon(List<Vector2> ring)
            {
                if (ring.Count < 3) return;
                polygons.Add(ring);
                polygonBounds.Add(GeoUtil.Bounds(ring));
            }

            public bool IsBlocked(Vector2 p, float extra)
            {
                var key = new Vector2Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell));
                if (segments.TryGetValue(key, out var list))
                {
                    foreach (var (a, b, r) in list)
                    {
                        Vector2 ab = b - a;
                        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                        float lim = r + extra;
                        if ((p - (a + ab * t)).sqrMagnitude < lim * lim) return true;
                    }
                }
                for (int i = 0; i < polygons.Count; i++)
                    if (polygonBounds[i].Contains(p) && OsmData.PointInRing(p, polygons[i])) return true;
                return false;
            }

            /// <summary>Moves <paramref name="p"/> sideways out of every line band (+extra). False if it can't get free.</summary>
            public bool PushOut(ref Vector2 p, float extra)
            {
                for (int iter = 0; iter < 4; iter++)
                {
                    var key = new Vector2Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell));
                    if (!segments.TryGetValue(key, out var list)) return true;
                    float worst = 0f;
                    Vector2 target = p;
                    foreach (var (a, b, r) in list)
                    {
                        Vector2 ab = b - a;
                        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                        Vector2 c = a + ab * t, off = p - c;
                        float depth = r + extra - off.magnitude;
                        if (depth <= worst) continue;
                        worst = depth;
                        Vector2 n = off.sqrMagnitude > 1e-6f ? off.normalized : new Vector2(-ab.y, ab.x).normalized;
                        target = c + n * (r + extra + 0.05f);
                    }
                    if (worst <= 0f) return true;
                    p = target;
                }
                return !IsBlocked(p, extra - 0.01f);
            }
        }

        /// <summary>
        /// Trees detected from LGLN laser data (Tools/dgm/fetch_trees.py): x, z, height, crown radius.
        /// When present they replace OSM single trees, tree rows and the scattered park/forest trees.
        /// </summary>
        public static List<Vector4> LoadLidarTrees(string path)
        {
            if (!System.IO.File.Exists(path)) return null;
            var root = (Dictionary<string, object>)MiniJson.Parse(System.IO.File.ReadAllText(path));
            var list = new List<Vector4>();
            foreach (List<object> t in (List<object>)root["trees"])
                list.Add(new Vector4((float)(double)t[0], (float)(double)t[1], (float)(double)t[2], (float)(double)t[3]));
            return list;
        }

        public static int PlaceTrees(OsmData data, MeshSink sink, AreaRect area, System.Random rng, List<Vector4> lidar = null)
        {
            var clear = BuildClearance(data);
            int count = 0;
            bool useLidar = lidar != null && lidar.Count > 0;

            if (useLidar)
            {
                foreach (var t in lidar)
                {
                    var p = new Vector2(t.x, t.y);
                    if (!area.Contains(p)) continue;
                    if (t.z > 32f) continue; // church spires, masts, cranes – not trees
                    // Crowns often hang over a street – move the trunk to the kerb instead of the carriageway.
                    if (clear.IsBlocked(p, 0f) && !clear.PushOut(ref p, 0.2f)) continue;
                    float h = Mathf.Clamp(t.z, 3f, 32f), r = Mathf.Clamp(t.w, 1f, 9f);
                    float yaw = (float)rng.NextDouble() * 360f;
                    EditsPlacer.Emit(sink, "tree", p, yaw, h / 8f, (s, q, y) => TreeSized(s, q, h, r, rng));
                    count++;
                }
            }

            // 1. Mapped single trees and tree rows: real positions, but never on a carriageway.
            foreach (var n in data.nodes)
            {
                if (useLidar) break;
                if (!n.tags.Is("natural", "tree")) continue;
                var p = data.projection.ToLocal(n.lat, n.lon);
                if (!area.Contains(p) || clear.IsBlocked(p, 0f)) continue;
                EditsPlacer.Emit(sink, "tree", p, 0f, 0.75f, (s, q, y) => Tree(s, q, rng, 0.75f));
                count++;
            }
            foreach (var w in data.ways)
            {
                if (useLidar) break;
                if (!w.tags.Is("natural", "tree_row")) continue;
                for (int i = 0; i < w.points.Count - 1; i++)
                {
                    Vector2 a = w.points[i], b = w.points[i + 1];
                    float len = Vector2.Distance(a, b);
                    for (float d = 0; d < len; d += 9f)
                    {
                        var p = Vector2.Lerp(a, b, d / len);
                        if (!area.Contains(p) || clear.IsBlocked(p, 0f)) continue;
                        EditsPlacer.Emit(sink, "tree", p, 0f, 0.8f, (s, q, y) => Tree(s, q, rng, 0.8f));
                        count++;
                    }
                }
            }

            // 2. Scattered vegetation inside area polygons. Noise gives groves and clearings instead of a grid.
            float noiseSeed = (float)rng.NextDouble() * 1000f;
            foreach (var poly in data.polygons)
            {
                VegetationKind kind;
                float spacing, coverage;
                if (poly.tags.Is("landuse", "forest") || poly.tags.Is("natural", "wood")) { if (useLidar) continue; kind = VegetationKind.Tree; spacing = 8f; coverage = 0.85f; }
                else if (poly.tags.Is("leisure", "park") || poly.tags.Is("landuse", "cemetery")) { if (useLidar) continue; kind = VegetationKind.Tree; spacing = 11f; coverage = 0.38f; }
                else if (poly.tags.Is("natural", "scrub")) { kind = VegetationKind.Bush; spacing = 7f; coverage = 0.55f; }
                else if (poly.tags.Is("natural", "heath") || poly.tags.Is("natural", "dune")) { kind = VegetationKind.Bush; spacing = 9f; coverage = 0.3f; }
                else continue;

                var ring = area.Clip(PolygonTriangulator.Clean(poly.outer));
                if (ring.Count < 3) continue;
                var bounds = GeoUtil.Bounds(ring);
                for (float x = bounds.xMin; x < bounds.xMax; x += spacing)
                for (float y = bounds.yMin; y < bounds.yMax; y += spacing)
                {
                    var p = new Vector2(x + (float)(rng.NextDouble() - 0.5) * spacing, y + (float)(rng.NextDouble() - 0.5) * spacing);
                    float noise = Mathf.PerlinNoise(p.x * 0.025f + noiseSeed, p.y * 0.025f + noiseSeed);
                    if (noise > coverage + 0.15f || noise < 0.5f - coverage) continue; // clearings
                    if (!OsmData.PointInRing(p, ring)) continue;
                    bool inHole = false;
                    foreach (var h in poly.holes) if (OsmData.PointInRing(p, h)) { inHole = true; break; }
                    if (inHole || clear.IsBlocked(p, kind == VegetationKind.Tree ? 2.2f : 0.8f)) continue;
                    if (kind == VegetationKind.Tree) EditsPlacer.Emit(sink, "tree", p, 0f, 1f, (s, q, y) => Tree(s, q, rng, 1f));
                    else EditsPlacer.Emit(sink, "bush", p, 0f, 1f, (s, q, y) => Bush(s, q, rng));
                    count++;
                }
            }
            return count;
        }

        enum VegetationKind { Tree, Bush }


        static Clearance BuildClearance(OsmData data)
        {
            var clear = new Clearance();
            foreach (var w in data.ways)
            {
                var hw = w.tags.Get("highway");
                if (hw != null)
                {
                    float r;
                    switch (hw)
                    {
                        case "footway": case "path": case "cycleway": case "steps": case "bridleway": case "track": r = 1.8f; break;
                        case "service": r = 2.5f; break;
                        case "pedestrian": r = 3.5f; break;
                        default: r = 4.5f; break;
                    }
                    clear.AddLine(w.points, r);
                }
                else if (w.tags.Has("railway") || w.tags.Has("waterway")) clear.AddLine(w.points, 3f);
            }
            foreach (var p in data.polygons)
            {
                var t = p.tags;
                if (t.Has("building") || t.Is("natural", "water") || t.Has("water") || t.Is("amenity", "parking") ||
                    t.Is("highway", "pedestrian") || t.Is("place", "square") || t.Is("amenity", "marketplace") ||
                    t.Is("leisure", "pitch") || t.Is("leisure", "playground") || t.Is("leisure", "swimming_pool") ||
                    t.Is("natural", "beach") || t.Is("natural", "sand"))
                    clear.AddPolygon(PolygonTriangulator.Clean(p.outer));
            }
            return clear;
        }

        static void Tree(MeshSink sink, Vector2 p, System.Random rng, float sizeFactor)
        {
            var crowns = sink.Get(WorldStyle.MatTrees, p);
            var trunks = sink.Get(WorldStyle.MatTreeTrunks, p);
            float scale = (0.75f + (float)rng.NextDouble() * 0.5f) * sizeFactor;
            float trunkH = 2.4f * scale, trunkR = 0.2f * scale;
            float crownR = 1.9f * scale, crownH = 4.0f * scale;
            float rot = (float)rng.NextDouble() * Mathf.PI * 2f;
            var leaves = WorldStyle.Tint(WorldStyle.TreeLeaves, rng, 0.12f);
            var basePos = new Vector3(p.x, 0f, p.y);

            Prism(trunks, basePos, trunkR, 0f, trunkH + 0.4f, 5, rot, WorldStyle.TreeTrunk);
            // Crown: low-poly double cone, conifers taller and narrower.
            bool conifer = rng.NextDouble() < 0.25;
            if (conifer) crownR *= 0.8f;
            int sides = 7;
            float midY = trunkH + crownH * (conifer ? 0.15f : 0.4f);
            float topY = trunkH + crownH * (conifer ? 1.3f : 1f);
            var top = basePos + Vector3.up * topY;
            var bottom = basePos + Vector3.up * trunkH;
            for (int i = 0; i < sides; i++)
            {
                float a0 = rot + i * Mathf.PI * 2f / sides, a1 = rot + (i + 1) * Mathf.PI * 2f / sides;
                var r0 = basePos + new Vector3(Mathf.Cos(a0) * crownR, midY, Mathf.Sin(a0) * crownR);
                var r1 = basePos + new Vector3(Mathf.Cos(a1) * crownR, midY, Mathf.Sin(a1) * crownR);
                MeshSink.Tri(crowns, r0, top, r1, leaves);
                MeshSink.Tri(crowns, r1, bottom, r0, WorldStyle.Tint(leaves, rng, 0.05f));
            }
        }

        /// <summary>Lean low-poly tree with measured height and crown radius (laser trees can be many).</summary>
        static void TreeSized(MeshSink sink, Vector2 p, float height, float crownR, System.Random rng)
        {
            var crowns = sink.Get(WorldStyle.MatTrees, p);
            var trunks = sink.Get(WorldStyle.MatTreeTrunks, p);
            bool conifer = rng.NextDouble() < 0.2;
            float trunkTop = height * (conifer ? 0.18f : 0.32f);
            float trunkR = Mathf.Clamp(height * 0.022f, 0.12f, 0.45f);
            float rot = (float)rng.NextDouble() * Mathf.PI * 2f;
            var basePos = new Vector3(p.x, 0f, p.y);
            Prism(trunks, basePos, trunkR, 0f, trunkTop + 0.3f, 4, rot, WorldStyle.TreeTrunk);

            var leaves = WorldStyle.Tint(WorldStyle.TreeLeaves, rng, 0.14f);
            float r = conifer ? crownR * 0.7f : crownR;
            float midY = conifer ? trunkTop + (height - trunkTop) * 0.2f : trunkTop + (height - trunkTop) * 0.45f;
            var top = basePos + Vector3.up * height;
            var bottom = basePos + Vector3.up * trunkTop;
            const int sides = 5;
            for (int i = 0; i < sides; i++)
            {
                float a0 = rot + i * Mathf.PI * 2f / sides, a1 = rot + (i + 1) * Mathf.PI * 2f / sides;
                var r0 = basePos + new Vector3(Mathf.Cos(a0) * r, midY, Mathf.Sin(a0) * r);
                var r1 = basePos + new Vector3(Mathf.Cos(a1) * r, midY, Mathf.Sin(a1) * r);
                MeshSink.Tri(crowns, r0, top, r1, leaves);
                MeshSink.Tri(crowns, r1, bottom, r0, WorldStyle.Tint(leaves, rng, 0.05f));
            }
        }

        static void Bush(MeshSink sink, Vector2 p, System.Random rng)
        {
            var b = sink.Get(WorldStyle.MatTrees, p);
            float r = 0.6f + (float)rng.NextDouble() * 0.7f;
            float h = r * (0.9f + (float)rng.NextDouble() * 0.4f);
            float rot = (float)rng.NextDouble() * Mathf.PI * 2f;
            var color = WorldStyle.Tint(WorldStyle.Bush, rng, 0.12f);
            var basePos = new Vector3(p.x, 0f, p.y);
            var top = basePos + Vector3.up * h;
            int sides = 6;
            for (int i = 0; i < sides; i++)
            {
                float a0 = rot + i * Mathf.PI * 2f / sides, a1 = rot + (i + 1) * Mathf.PI * 2f / sides;
                var r0 = basePos + new Vector3(Mathf.Cos(a0) * r, h * 0.35f, Mathf.Sin(a0) * r);
                var r1 = basePos + new Vector3(Mathf.Cos(a1) * r, h * 0.35f, Mathf.Sin(a1) * r);
                var g0 = basePos + new Vector3(Mathf.Cos(a0) * r * 0.7f, 0f, Mathf.Sin(a0) * r * 0.7f);
                var g1 = basePos + new Vector3(Mathf.Cos(a1) * r * 0.7f, 0f, Mathf.Sin(a1) * r * 0.7f);
                MeshSink.Tri(b, r0, top, r1, color);
                MeshSink.Quad(b, g0, r0, r1, g1, WorldStyle.Tint(color, rng, 0.06f));
            }
        }

        static void Prism(MeshSink.Batch b, Vector3 basePos, float r, float y0, float y1, int sides, float rot, Color32 c)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = rot + i * Mathf.PI * 2f / sides, a1 = rot + (i + 1) * Mathf.PI * 2f / sides;
                var p0 = basePos + new Vector3(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r);
                var p1 = basePos + new Vector3(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r);
                // Increasing angle = CCW from above, so p0->p1 matches building outer-ring wall winding.
                MeshSink.Quad(b, p0 + Vector3.up * y0, p0 + Vector3.up * y1, p1 + Vector3.up * y1, p1 + Vector3.up * y0, c);
            }
        }

        /// <summary>Street lamps every ~30 m along urban roads, alternating sides, kept off buildings.</summary>
        /// <summary>One plant or lamp of <paramref name="type"/> at the origin (map editor objects).</summary>
        public static bool BuildSingle(string type, MeshSink sink, System.Random rng)
        {
            switch (type)
            {
                case "tree": Tree(sink, Vector2.zero, rng, 1f); return true;
                case "bush": Bush(sink, Vector2.zero, rng); return true;
                case "lamp": Lamp(sink.Get(WorldStyle.MatProps, Vector2.zero), Vector2.zero, Vector2.right); return true;
                default: return false;
            }
        }

        public static int PlaceStreetLamps(OsmData data, MeshSink sink, AreaRect area)
        {
            var buildings = new Clearance();
            foreach (var p in data.polygons)
                if (p.tags.Has("building")) buildings.AddPolygon(PolygonTriangulator.Clean(p.outer));

            // Every drawn road, path, rail and waterway at its real ribbon width: poles stay off all of them
            // (crossing streets at junctions, separately mapped footways, explicit width=* tags).
            const float Kerb = 0.6f;
            var roads = new Clearance();
            foreach (var w in data.ways)
            {
                float lw = GroundBuilder.LineWidth(w.tags);
                if (lw > 0f) roads.AddLine(w.points, lw * 0.5f);
            }

            int count = 0;

            // Mapped lamps (highway=street_lamp) at their real positions – nudged to the kerb if the node sits
            // on a carriageway; the 30 m pattern fills the gaps.
            var mapped = new Clearance();
            foreach (var n in data.nodes)
            {
                if (!n.tags.Is("highway", "street_lamp")) continue;
                var p = data.projection.ToLocal(n.lat, n.lon);
                if (!area.Contains(p, 1f)) continue;
                if (roads.IsBlocked(p, Kerb) && (!roads.PushOut(ref p, Kerb) || buildings.IsBlocked(p, 0.3f))) continue;
                EditLamp(sink, p, Vector2.right);
                mapped.AddLine(new List<Vector2> { p, p + Vector2.one * 0.01f }, 15f);
                count++;
            }

            foreach (var w in data.ways)
            {
                switch (w.tags.Get("highway"))
                {
                    case "primary": case "secondary": case "tertiary":
                    case "residential": case "living_street": case "unclassified": break;
                    default: continue;
                }
                float halfWidth = GroundBuilder.LineWidth(w.tags) * 0.5f;
                if (halfWidth <= 0f) continue;
                float carry = 12f;
                int side = (int)(w.id & 1) * 2 - 1;
                for (int i = 0; i < w.points.Count - 1; i++)
                {
                    Vector2 a = w.points[i], b = w.points[i + 1];
                    float len = Vector2.Distance(a, b);
                    if (len < 0.01f) continue;
                    Vector2 dir = (b - a) / len, normal = new Vector2(-dir.y, dir.x);
                    float d = carry;
                    for (; d < len; d += 30f)
                    {
                        // Kerbside first; further out if a footway/cycleway runs along. Nothing fits (junction) -> skip.
                        Vector2? spot = null;
                        foreach (float gap in LampGaps)
                        {
                            var q = a + dir * d + normal * side * (halfWidth + gap);
                            if (!roads.IsBlocked(q, Kerb)) { spot = q; break; }
                        }
                        side = -side;
                        if (spot == null) continue;
                        var p = spot.Value;
                        if (!area.Contains(p, 2f) || buildings.IsBlocked(p, 1.2f) || mapped.IsBlocked(p, 0f)) continue;
                        EditLamp(sink, p, dir);
                        count++;
                    }
                    carry = d - len;
                }
            }
            return count;
        }

        static readonly float[] LampGaps = { 0.9f, 2.1f, 3.4f };

        /// <summary>Lamp via <see cref="EditsPlacer"/>; yaw turns the head's +X towards <paramref name="roadDir"/>.</summary>
        static void EditLamp(MeshSink sink, Vector2 p, Vector2 roadDir)
        {
            float yaw = Mathf.Atan2(-roadDir.y, roadDir.x) * Mathf.Rad2Deg;
            EditsPlacer.Emit(sink, "lamp", p, yaw, 1f, (s, q, y) =>
                Lamp(s.Get(WorldStyle.MatProps, q), q, new Vector2(Mathf.Cos(y * Mathf.Deg2Rad), -Mathf.Sin(y * Mathf.Deg2Rad))));
        }

        static void Lamp(MeshSink.Batch b, Vector2 p, Vector2 roadDir)
        {
            var basePos = new Vector3(p.x, 0, p.y);
            Prism(b, basePos, 0.09f, 0f, 4.6f, 6, 0f, WorldStyle.LampPole);
            // Head: small box with a lighter underside.
            var c = basePos + Vector3.up * 4.6f;
            float s = 0.28f;
            Vector3 x = new Vector3(roadDir.x, 0, roadDir.y) * s, z = new Vector3(-roadDir.y, 0, roadDir.x) * s;
            Vector3 lo = Vector3.up * -0.05f, hi = Vector3.up * 0.25f;
            Vector3 p00 = c - x - z, p10 = c + x - z, p11 = c + x + z, p01 = c - x + z;
            MeshSink.Quad(b, p00 + hi, p01 + hi, p11 + hi, p10 + hi, WorldStyle.LampPole);   // top
            MeshSink.Quad(b, p00 + lo, p10 + lo, p11 + lo, p01 + lo, WorldStyle.LampGlow);   // bottom
            Box4(b, p00, p10, p11, p01, lo, hi, WorldStyle.LampPole);
        }

        static void Box4(MeshSink.Batch b, Vector3 p00, Vector3 p10, Vector3 p11, Vector3 p01, Vector3 lo, Vector3 hi, Color32 c)
        {
            var ring = new[] { p00, p10, p11, p01 };
            for (int i = 0; i < 4; i++)
            {
                Vector3 a = ring[i], d = ring[(i + 1) % 4];
                MeshSink.Quad(b, a + lo, a + hi, d + hi, d + lo, c);
                MeshSink.Quad(b, d + lo, d + hi, a + hi, a + lo, c); // double-sided: ring winding not guaranteed
            }
        }
    }
}
