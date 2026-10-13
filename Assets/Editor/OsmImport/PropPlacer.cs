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
        internal class Clearance
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

        const float MinTrunkToHouse = 1.5f;

        /// <summary>Building footprints with a distance query (0 inside), bucketed in a coarse grid.</summary>
        class Footprints
        {
            const float Cell = 25f;
            readonly Dictionary<Vector2Int, List<(List<Vector2> ring, bool hole)>> grid = new Dictionary<Vector2Int, List<(List<Vector2>, bool)>>();

            public static Footprints Build(OsmData data, bool withLod2)
            {
                var f = new Footprints();
                foreach (var poly in data.polygons)
                    if (poly.tags.Has("building") || poly.tags.Has("building:part"))
                    {
                        f.Add(PolygonTriangulator.Clean(poly.outer), false);
                        foreach (var hole in poly.holes) f.Add(PolygonTriangulator.Clean(hole), true); // courtyards
                    }
                if (withLod2) foreach (var outline in Lod2Builder.Outlines) f.Add(outline, false);
                return f;
            }

            void Add(List<Vector2> ring, bool hole)
            {
                if (ring.Count < 3) return;
                var b = GeoUtil.Bounds(ring);
                for (int x = Mathf.FloorToInt(b.xMin / Cell); x <= Mathf.FloorToInt(b.xMax / Cell); x++)
                for (int y = Mathf.FloorToInt(b.yMin / Cell); y <= Mathf.FloorToInt(b.yMax / Cell); y++)
                {
                    var key = new Vector2Int(x, y);
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<(List<Vector2>, bool)>();
                    list.Add((ring, hole));
                }
            }

            /// <summary>Distance from p to the nearest footprint (0 if inside); at most <paramref name="max"/>.</summary>
            public float Distance(Vector2 p, float max)
            {
                float best = max;
                bool inside = false, inHole = false;
                var seen = new HashSet<List<Vector2>>();
                for (int x = Mathf.FloorToInt((p.x - max) / Cell); x <= Mathf.FloorToInt((p.x + max) / Cell); x++)
                for (int y = Mathf.FloorToInt((p.y - max) / Cell); y <= Mathf.FloorToInt((p.y + max) / Cell); y++)
                {
                    if (!grid.TryGetValue(new Vector2Int(x, y), out var list)) continue;
                    foreach (var (ring, hole) in list)
                    {
                        if (!seen.Add(ring)) continue;
                        if (OsmData.PointInRing(p, ring)) { if (hole) inHole = true; else inside = true; }
                        for (int i = 0; i < ring.Count; i++)
                        {
                            Vector2 a = ring[i], ab = ring[(i + 1) % ring.Count] - a;
                            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                            best = Mathf.Min(best, (p - (a + ab * t)).magnitude);
                        }
                    }
                }
                return inside && !inHole ? 0f : best;
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
            var houses = useLidar ? Footprints.Build(data, Lod2Builder.Outlines.Count > 0) : null;

            if (useLidar)
            {
                int dropped = 0, shrunk = 0;
                foreach (var t in lidar)
                {
                    var p = new Vector2(t.x, t.y);
                    if (!area.Contains(p)) continue;
                    if (t.z > 32f) continue; // church spires, masts, cranes – not trees
                    // Crowns often hang over a street – move the trunk to the kerb instead of the carriageway.
                    if (clear.IsBlocked(p, 0f) && !clear.PushOut(ref p, 0.2f)) continue;
                    float h = Mathf.Clamp(t.z, 3f, 32f), r = Mathf.Clamp(t.w, 1f, 9f);
                    // House beats tree (OSM and LoD2 footprints): the laser scan can predate new-build estates, so no
                    // trunk near a footprint and no crown reaching into a wall – the crown shrinks, the tree with it.
                    float wall = houses.Distance(p, r);
                    if (wall < MinTrunkToHouse) { dropped++; continue; }
                    if (wall < r)
                    {
                        shrunk++;
                        r = Mathf.Max(1f, wall);
                        h = Mathf.Min(h, 3f + r * 3f);
                    }
                    float yaw = (float)rng.NextDouble() * 360f;
                    EditsPlacer.Emit(sink, "tree", p, yaw, h / 8f, (s, q, y) => TreeSized(s, q, h, r, rng));
                    count++;
                }
                Debug.Log($"[Trees] LiDAR: {count} placed, {dropped} dropped at houses, {shrunk} crowns trimmed");
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

        // Every plant builder draws exactly as many numbers from the shared generator as the original simple
        // version did (object ids and all later positions depend on that sequence); detail comes from Fork().

        static void Tree(MeshSink sink, Vector2 p, System.Random rng, float sizeFactor)
        {
            float scale = (0.75f + (float)rng.NextDouble() * 0.5f) * sizeFactor;
            float rot = (float)rng.NextDouble() * Mathf.PI * 2f;
            var leaves = WorldStyle.Tint(WorldStyle.TreeLeaves, rng, 0.12f);
            bool conifer = rng.NextDouble() < 0.25;
            var local = Fork(rng, 7);
            float height = 6.4f * scale, crownR = 1.9f * scale * (conifer ? 0.8f : 1f);
            TreeMesh(sink, p, height, crownR, conifer ? 0.18f : 0.38f, conifer, rot, leaves, local);
        }

        /// <summary>Low-poly tree with measured height and crown radius (laser trees).</summary>
        static void TreeSized(MeshSink sink, Vector2 p, float height, float crownR, System.Random rng)
        {
            bool conifer = rng.NextDouble() < 0.2;
            float rot = (float)rng.NextDouble() * Mathf.PI * 2f;
            var leaves = WorldStyle.Tint(WorldStyle.TreeLeaves, rng, 0.14f);
            var local = Fork(rng, 5);
            TreeMesh(sink, p, height, conifer ? crownR * 0.7f : crownR, conifer ? 0.18f : 0.32f, conifer, rot, leaves, local);
        }

        /// <summary>Local generator seeded from <paramref name="draws"/> numbers of the shared one.</summary>
        internal static System.Random Fork(System.Random rng, int draws)
        {
            int seed = 17;
            for (int i = 0; i < draws; i++) seed = seed * 31 + (int)(rng.NextDouble() * 1e6);
            return new System.Random(seed);
        }

        internal static readonly Color32 LeavesLight = WorldStyle.Hex("#7DB04E");
        static readonly Color32 ConiferLeaves = WorldStyle.Hex("#3F6E3C");

        static void TreeMesh(MeshSink sink, Vector2 p, float height, float crownR, float trunkFrac, bool conifer, float rot,
            Color32 leaves, System.Random rng)
        {
            var crowns = sink.Get(WorldStyle.MatTrees, p);
            var trunks = sink.Get(WorldStyle.MatTreeTrunks, p);
            var basePos = new Vector3(p.x, 0f, p.y);
            float trunkTop = height * trunkFrac;
            float trunkR = Mathf.Clamp(height * 0.022f, 0.12f, 0.45f);
            var bark = WorldStyle.Tint(WorldStyle.TreeTrunk, rng, 0.1f);
            // Slightly tapered trunk, a touch of lean.
            var lean = new Vector3((float)rng.NextDouble() - 0.5f, 0f, (float)rng.NextDouble() - 0.5f) * (height * 0.03f);

            if (conifer)
            {
                Frustum(trunks, basePos, basePos + lean + Vector3.up * (trunkTop + 0.4f), trunkR, trunkR * 0.7f, 4, rot, bark);
                // Spruce/pine: three stacked cones, each narrower, darker blue-green.
                var needles = Color32.Lerp(leaves, ConiferLeaves, 0.6f);
                float crownH = height - trunkTop;
                float y = trunkTop;
                for (int tier = 0; tier < 3; tier++)
                {
                    float r = crownR * (1f - tier * 0.27f);
                    float h = crownH * (tier == 2 ? 0.5f : 0.42f);
                    float y0 = y, y1 = Mathf.Min(height, y + h);
                    Cone(crowns, basePos + lean + Vector3.up * y0, r, y1 - y0, 6, rot + tier * 0.4f,
                        WorldStyle.Tint(needles, rng, 0.06f), WorldStyle.Tint(needles, rng, 0.04f));
                    y += crownH * 0.29f;
                }
                return;
            }

            // Broadleaf: a faceted main lobe plus a ring of smaller lobes out to the measured crown radius – a clumpy
            // low-poly crown at ~40–120 triangles (Westerstede alone has 13 000 laser trees). Lobes are never much
            // taller than wide: broad island trees become a wide cluster, tall narrow street trees get a second
            // lobe on top instead of a spindle. The crown hangs from the measured top; the trunk reaches into it.
            float crownH2 = Mathf.Max(height - trunkTop, crownR * 0.6f);
            float mainR = crownR * 0.75f;
            float halfH = Mathf.Min(crownH2 * 0.5f, mainR * 1.2f);
            bool stacked = crownH2 > halfH * 2.6f;
            float topR = mainR * 0.72f, topHalf = halfH * 0.72f;
            float topOffset = stacked ? halfH * 0.85f + topHalf : halfH;
            float centreY = Mathf.Max(height - topOffset, 1.8f + halfH * 0.75f); // keep head room under the crown
            var centre = basePos + lean + Vector3.up * centreY;
            Frustum(trunks, basePos, centre, trunkR, trunkR * 0.7f, 4, rot, bark);
            Blob(crowns, centre, mainR, halfH, rot, leaves, rng);
            if (stacked)
                Blob(crowns, centre + new Vector3(((float)rng.NextDouble() - 0.5f) * mainR * 0.3f, halfH * 0.85f, ((float)rng.NextDouble() - 0.5f) * mainR * 0.3f),
                    topR, topHalf, rot + 0.7f, Color32.Lerp(leaves, LeavesLight, 0.25f), rng);
            float maxR = halfH * 1.35f;
            float subR = Mathf.Min(crownR * 0.5f, maxR * 0.8f);
            float d = Mathf.Max(crownR - subR, crownR * 0.35f);
            int extra = Mathf.Clamp(Mathf.RoundToInt(2f * Mathf.PI * d / (subR * 2.2f)), crownR < 3f ? 1 : 2, 4);
            for (int i = 0; i < extra; i++)
            {
                float a = rot + (i + 0.3f + (float)rng.NextDouble() * 0.4f) * Mathf.PI * 2f / extra;
                float dd = d * (0.85f + (float)rng.NextDouble() * 0.2f);
                float dy = halfH * (-0.25f + (float)rng.NextDouble() * 0.45f);
                var c = centre + new Vector3(Mathf.Cos(a) * dd, dy, Mathf.Sin(a) * dd);
                float r = subR * (0.85f + (float)rng.NextDouble() * 0.25f);
                var tint = Color32.Lerp(leaves, LeavesLight, 0.1f + (float)rng.NextDouble() * 0.3f);
                Lobe(crowns, c, r, Mathf.Min(r * 0.85f, halfH * 0.8f), a, WorldStyle.Tint(tint, rng, 0.05f));
            }
        }

        /// <summary>Simple lobe: pentagonal ring with a low cap and a flat-ish bottom, apex nudged off-centre – 20 triangles.</summary>
        static void Lobe(MeshSink.Batch b, Vector3 c, float r, float halfH, float rot, Color32 color)
        {
            const int n = 5;
            var top = c + new Vector3(Mathf.Cos(rot) * r * 0.12f, halfH, Mathf.Sin(rot) * r * 0.12f);
            var bottom = c + Vector3.down * halfH * 0.55f;
            var light = Color32.Lerp(color, LeavesLight, 0.15f);
            var dark = Shade(color, 0.8f);
            var cap = new Vector3[n];
            var ring = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = rot + i * Mathf.PI * 2f / n;
                cap[i] = c + new Vector3(Mathf.Cos(a + 0.6f) * r * 0.6f, halfH * 0.6f, Mathf.Sin(a + 0.6f) * r * 0.6f);
                ring[i] = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                MeshSink.Tri(b, cap[i], top, cap[j], light);
                MeshSink.Tri(b, ring[j], cap[i], cap[j], color);   // band, two triangles per side
                MeshSink.Tri(b, ring[j], bottom, ring[i], dark);
                MeshSink.Tri(b, ring[i], cap[i], ring[j], color);
            }
        }

        /// <summary>
        /// Faceted crown lobe: pentagonal top cap, a twisted lower ring and a flat-ish bottom – 20 triangles.
        /// <paramref name="halfH"/> is the half height.
        /// </summary>
        internal static void Blob(MeshSink.Batch b, Vector3 c, float r, float halfH, float rot, Color32 color, System.Random rng)
        {
            const int n = 5;
            var top = c + Vector3.up * halfH;
            var bottom = c + Vector3.down * halfH * 0.75f;
            var up = new Vector3[n];
            var lo = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = rot + i * Mathf.PI * 2f / n;
                float ra = r * (0.72f + (float)rng.NextDouble() * 0.1f);
                float rb = r * (0.92f + (float)rng.NextDouble() * 0.12f);
                up[i] = c + new Vector3(Mathf.Cos(a) * ra, halfH * 0.5f, Mathf.Sin(a) * ra);
                float b2 = a + Mathf.PI / n;
                lo[i] = c + new Vector3(Mathf.Cos(b2) * rb, -halfH * 0.2f, Mathf.Sin(b2) * rb);
            }
            var light = Color32.Lerp(color, LeavesLight, 0.18f);
            var dark = Shade(color, 0.82f);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                MeshSink.Tri(b, up[i], top, up[j], light);
                MeshSink.Tri(b, up[i], up[j], lo[i], color);
                MeshSink.Tri(b, lo[i], up[j], lo[j], WorldStyle.Tint(color, rng, 0.04f));
                MeshSink.Tri(b, lo[j], bottom, lo[i], dark);
            }
        }

        /// <summary>Cone with a slightly hollow underside (conifer tier).</summary>
        static void Cone(MeshSink.Batch b, Vector3 baseC, float r, float h, int sides, float rot, Color32 side, Color32 under)
        {
            var tip = baseC + Vector3.up * h;
            var hollow = baseC + Vector3.up * h * 0.18f;
            for (int i = 0; i < sides; i++)
            {
                float a0 = rot + i * Mathf.PI * 2f / sides, a1 = rot + (i + 1) * Mathf.PI * 2f / sides;
                var r0 = baseC + new Vector3(Mathf.Cos(a0) * r, 0f, Mathf.Sin(a0) * r);
                var r1 = baseC + new Vector3(Mathf.Cos(a1) * r, 0f, Mathf.Sin(a1) * r);
                MeshSink.Tri(b, r0, tip, r1, side);
                MeshSink.Tri(b, r1, hollow, r0, Shade(under, 0.75f));
            }
        }

        static void Frustum(MeshSink.Batch b, Vector3 bottom, Vector3 top, float r0, float r1, int sides, float rot, Color32 c)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = rot + i * Mathf.PI * 2f / sides, a1 = rot + (i + 1) * Mathf.PI * 2f / sides;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                MeshSink.Quad(b, bottom + d0 * r0, top + d0 * r1, top + d1 * r1, bottom + d1 * r0, c);
            }
        }

        static Color32 Shade(Color32 c, float f) =>
            new Color32((byte)(c.r * f), (byte)(c.g * f), (byte)(c.b * f), c.a);

        static void Bush(MeshSink sink, Vector2 p, System.Random rng)
        {
            var b = sink.Get(WorldStyle.MatTrees, p);
            float r = 0.6f + (float)rng.NextDouble() * 0.7f;
            float h = r * (0.9f + (float)rng.NextDouble() * 0.4f);
            float rot = (float)rng.NextDouble() * Mathf.PI * 2f;
            var color = WorldStyle.Tint(WorldStyle.Bush, rng, 0.12f);
            var local = Fork(rng, 6);
            var basePos = new Vector3(p.x, 0f, p.y);
            // Two or three overlapping lobes sitting on the ground.
            Blob(b, basePos + Vector3.up * h * 0.45f, r, h * 0.55f, rot, color, local);
            int extra = 1 + local.Next(2);
            for (int i = 0; i < extra; i++)
            {
                float a = rot + 1.3f + i * 2.4f;
                float rr = r * (0.55f + (float)local.NextDouble() * 0.2f);
                var c = basePos + new Vector3(Mathf.Cos(a) * r * 0.6f, rr * 0.5f, Mathf.Sin(a) * r * 0.6f);
                Lobe(b, c, rr, rr * 0.6f, a, WorldStyle.Tint(Color32.Lerp(color, LeavesLight, 0.2f), local, 0.05f));
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
