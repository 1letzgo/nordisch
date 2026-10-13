using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Garden borders from ALKIS parcels (Data/&lt;area&gt;.parcels.json, see Tools/alkis/fetch_parcels.py):
    /// every residential plot gets a front border along the street (low hedge, Jägerzaun, low Klinker wall or
    /// open lawn) with gaps for the path and the driveway, and its borders to the neighbours / the back
    /// (tall hedge, wooden privacy fence or green mesh fence). Houses and garages built on the boundary interrupt
    /// it. Style is random per plot but stable between imports (seeded by the parcel id).
    /// Real hedges measured in the laser data (Data/&lt;area&gt;.hedges.json, Tools/dgm/fetch_hedges.py) are built first
    /// at their measured height/width; parcel borders skip stretches where such a hedge already runs.
    /// </summary>
    public static class GardenBuilder
    {
        public struct Stats { public int plots, hedges, fences, walls, measured, drawn; }

        enum Kind { None, HedgeLow, HedgeTall, Picket, WallLow, Privacy, Mesh }

        class Parcel
        {
            public string id;
            public List<Vector2> ring;
            public Rect bounds;
            public bool residential;
            public Vector2 house;      // centroid of its main (addressed) house
            public bool newBuild;      // main house only in OSM = newer than the laser scan
            public int seed;
        }

        const float Cell = 40f;
        static readonly Color32 HedgeGreen = new Color32(74, 112, 58, 255);
        static readonly Color32 HedgeDark = new Color32(58, 92, 50, 255);
        static readonly Color32 Wood = new Color32(150, 112, 74, 255);
        static readonly Color32 WoodDark = new Color32(112, 82, 56, 255);
        static readonly Color32 PicketWhite = new Color32(232, 228, 218, 255);
        static readonly Color32 MeshGreen = new Color32(52, 78, 58, 255);
        static readonly Color32 Klinker = new Color32(150, 70, 52, 255);
        static readonly Color32 Coping = new Color32(178, 172, 162, 255);

        public static Stats Build(string path, OsmData data, MeshSink sink, AreaRect area)
        {
            var st = new Stats();
            LoadEdits(path.Replace(".parcels.json", ".garden.json"));

            // ---- roads: drawn width, for classifying street-side edges (and keeping measured hedges off them)
            var roads = new List<(Vector2 a, Vector2 b, float half)>();
            foreach (var w in data.ways)
            {
                if (w.tags.Get("highway") == null) continue;
                float lw = GroundBuilder.LineWidth(w.tags);
                if (lw <= 0f) continue;
                for (int i = 0; i < w.points.Count - 1; i++) roads.Add((w.points[i], w.points[i + 1], lw * 0.5f));
            }
            var roadGrid = Index(roads.Count, i => GrowRect(roads[i].a, roads[i].b, roads[i].half + 12f));

            var items = new List<object>();
            if (File.Exists(path) && MiniJson.Parse(File.ReadAllText(path)) is Dictionary<string, object> root &&
                root.TryGetValue("parcels", out var list) && list is List<object> l0) items = l0;

            // ---- parcels
            var parcels = new List<Parcel>();
            foreach (Dictionary<string, object> o in items)
            {
                var ring = new List<Vector2>();
                foreach (List<object> p in (List<object>)o["ring"]) ring.Add(new Vector2((float)(double)p[0], (float)(double)p[1]));
                ring = PolygonTriangulator.Clean(ring);
                if (ring.Count < 3) continue;
                PolygonTriangulator.EnsureWinding(ring, true);
                string id = (string)o["id"];
                parcels.Add(new Parcel { id = id, ring = ring, bounds = GeoUtil.Bounds(ring), seed = StableHash(id) });
            }
            var parcelGrid = Index(parcels.Count, i => parcels[i].bounds);

            // ---- houses (everything the builders made; addressed ones are main houses)
            var houses = new List<(List<Vector2> ring, Rect bounds, bool main)>();
            var osmHouse = new List<bool>();
            foreach (var (ring, addressed, osm) in BuildingOverrides.Footprints())
            {
                houses.Add((ring, GeoUtil.Bounds(ring), addressed));
                osmHouse.Add(osm);
            }
            var houseGrid = Index(houses.Count, i => houses[i].bounds);

            // ---- areas that are not private gardens
            var excluded = new List<List<Vector2>>();
            foreach (var poly in data.polygons)
            {
                var t = poly.tags;
                if (t.Is("landuse", "retail") || t.Is("landuse", "commercial") || t.Is("landuse", "industrial") ||
                    t.Is("amenity", "school") || t.Is("amenity", "kindergarten") || t.Is("amenity", "parking") ||
                    t.Is("amenity", "place_of_worship") || t.Is("amenity", "hospital") || t.Is("landuse", "cemetery") ||
                    t.Is("leisure", "park") || t.Is("leisure", "playground") || t.Is("leisure", "pitch"))
                    excluded.Add(PolygonTriangulator.Clean(poly.outer));
            }


            // ---- which plots are private gardens
            foreach (var p in parcels)
            {
                float a = Mathf.Abs(PolygonTriangulator.SignedArea(p.ring));
                if (a < 120f || a > 2500f || !area.Contains(p.bounds.center, 5f)) continue;
                float best = 0f;
                foreach (int hi in Query(houseGrid, p.bounds))
                {
                    var h = houses[hi];
                    if (!h.main) continue;
                    var c = GeoUtil.Centroid(h.ring);
                    if (!OsmData.PointInRing(c, p.ring)) continue;
                    float ha = h.bounds.width * h.bounds.height;
                    if (ha > best) { best = ha; p.house = c; p.newBuild = osmHouse[hi]; }
                }
                if (best <= 0f) continue;
                var pc = GeoUtil.Centroid(p.ring);
                bool skip = false;
                foreach (var ex in excluded) if (ex.Count > 2 && OsmData.PointInRing(pc, ex)) { skip = true; break; }
                if (skip) continue;
                p.residential = true;
                st.plots++;
            }

            // ---- measured hedges (bDOM20 surface model + aerial, same flight – see Tools/dgm/fetch_hedges.py)
            measured.Clear();
            string hedgePath = path.Replace(".parcels.json", ".hedges.json");
            if (File.Exists(hedgePath) && MiniJson.Parse(File.ReadAllText(hedgePath)) is Dictionary<string, object> hr &&
                hr.TryGetValue("hedges", out var hl) && hl is List<object> hitems)
            {
                foreach (List<object> h in hitems)
                {
                    var a = new Vector2((float)(double)h[0], (float)(double)h[1]);
                    var b = new Vector2((float)(double)h[2], (float)(double)h[3]);
                    // kind 1 = detected on a plot border (checked green in the current aerial), 0 = free-standing.
                    bool onBorder = h.Count > 6 && (double)h[6] > 0.5;
                    if (!area.Contains((a + b) * 0.5f, 1f) || OnRoad((a + b) * 0.5f, roads, roadGrid)) continue;
                    if (!onBorder && st.plots > 0 && !NearGarden((a + b) * 0.5f, parcels, parcelGrid, 4f)) continue; // dune scrub, field shrubs
                    float height = Mathf.Clamp((float)(double)h[4], 0.8f, 3.5f), width = Mathf.Clamp((float)(double)h[5], 0.6f, 2f);
                    measured.Add((a, b)); // also when removed in the editor: no generated fence takes its place
                    if (Record("hedge", a, b, height, width)) continue;
                    var col = WorldStyle.Tint((Mathf.RoundToInt(a.x + a.y) & 1) == 0 ? HedgeGreen : HedgeDark, new System.Random(Mathf.RoundToInt(a.x * 7 + a.y * 13)), 0.08f);
                    // Ends reaching into a street or footway (plot corners sit right at the kerb) are cut off.
                    foreach (var (ca, cb) in ClipOffRoads(a, b, roads, roadGrid, 0.5f + width * 0.5f))
                    {
                        float len = Vector2.Distance(ca, cb);
                        int pieces = Mathf.Max(1, Mathf.CeilToInt(len / 4f));
                        for (int q = 0; q < pieces; q++)
                        {
                            Vector2 p0 = Vector2.Lerp(ca, cb, (float)q / pieces), p1 = Vector2.Lerp(ca, cb, (float)(q + 1) / pieces);
                            var pm = (p0 + p1) * 0.5f;
                            if (!onBorder && AerialImage.Current != null && AerialImage.Current.IsPaved(pm, 0.6f)) continue; // paved over
                            if (NearBuilding(pm, houses, houseGrid, 0.4f)) continue; // along a wall: the house, not a hedge
                            Hedge(sink.Get(WorldStyle.MatProps, pm), p0, p1, height * Jitter(p0), width, col);
                        }
                    }
                    st.measured++;
                }
            }
            measuredGrid = Index(measured.Count, i => GrowRect(measured[i].a, measured[i].b, 2f));

            // ---- borders. With measured hedges, generated borders are only fences, walls or nothing.
            bool haveMeasured = measured.Count > 0;
            var done = new HashSet<long>();
            foreach (var p in parcels)
            {
                if (!p.residential) continue;
                var rng = new System.Random(p.seed);
                bool hedgesOk = !haveMeasured; // measured data is current: no invented hedges next to it
                var front = hedgesOk ? Pick(rng, (Kind.HedgeLow, 40), (Kind.Picket, 18), (Kind.WallLow, 14), (Kind.None, 28))
                                     : Pick(rng, (Kind.Picket, 30), (Kind.WallLow, 22), (Kind.None, 48));
                var side = hedgesOk ? Pick(rng, (Kind.HedgeTall, 40), (Kind.Privacy, 22), (Kind.Mesh, 28), (Kind.None, 10))
                                    : Pick(rng, (Kind.Privacy, 30), (Kind.Mesh, 50), (Kind.None, 20));
                var hedgeTint = WorldStyle.Tint(rng.NextDouble() < 0.5 ? HedgeGreen : HedgeDark, rng, 0.08f);

                for (int i = 0; i < p.ring.Count; i++)
                {
                    Vector2 a = p.ring[i], b = p.ring[(i + 1) % p.ring.Count];
                    float len = Vector2.Distance(a, b);
                    if (len < 0.8f) continue;
                    long key = EdgeKey(a, b);
                    if (!done.Add(key)) continue; // shared border already built from the neighbour
                    Vector2 dir = (b - a) / len, inward = new Vector2(-dir.y, dir.x); // CCW ring: inside on the left
                    Vector2 mid = (a + b) * 0.5f;

                    if (IsStreetSide(mid, dir, roads, roadGrid))
                    {
                        if (front == Kind.None) continue;
                        // Just inside the plot; gaps for the garden path (towards the house) and the driveway.
                        Vector2 a2 = a + inward * 0.35f, b2 = b + inward * 0.35f;
                        float th = Mathf.Clamp(Vector2.Dot(p.house - a, dir), 0f, len);
                        var gaps = new List<(float from, float to)>();
                        if (AerialImage.Current == null) gaps.Add((th - 0.8f, th + 0.8f));
                        if (len > 9f && AerialImage.Current == null)
                        {
                            float dw = th > len * 0.5f ? Mathf.Max(1.8f, th - 5f) : Mathf.Min(len - 1.8f, th + 5f);
                            gaps.Add((dw - 1.6f, dw + 1.6f));
                        }
                        Emit(sink, a2, b2, gaps, front, hedgeTint, houses, houseGrid, roads, roadGrid, ref st, rng);
                    }
                    else
                    {
                        // Neighbour or back border; in the front garden (first 4 m from the street) it takes the low
                        // front style so it still meets the street-side border.
                        var outside = mid - inward * 0.8f;
                        var gaps = new List<(float from, float to)>();
                        float frontA = NearRoad(a, roads, roadGrid, 9f) ? 4f : 0f, frontB = NearRoad(b, roads, roadGrid, 9f) ? 4f : 0f;
                        frontA = Mathf.Min(frontA, len * 0.4f); frontB = Mathf.Min(frontB, len * 0.4f);
                        bool neighbour = false, theirs = false;
                        foreach (int qi in Query(parcelGrid, new Rect(outside, Vector2.zero)))
                        {
                            var q = parcels[qi];
                            if (q == p || !q.residential || !OsmData.PointInRing(outside, q.ring)) continue;
                            neighbour = true;
                            theirs = string.CompareOrdinal(q.id, p.id) < 0; // one owner per shared border
                            break;
                        }
                        if (theirs) continue;
                        var kind = side;
                        if (!neighbour && kind == Kind.Privacy) kind = hedgesOk ? Kind.HedgeTall : Kind.Mesh; // fields/parks behind
                        Vector2 fa = a + dir * frontA, fb = b - dir * frontB;
                        if (kind != Kind.None && Vector2.Distance(fa, fb) > 1f)
                            Emit(sink, fa, fb, gaps, kind, hedgeTint, houses, houseGrid, roads, roadGrid, ref st, rng);
                        var low = front == Kind.HedgeLow || front == Kind.Picket || front == Kind.WallLow ? front : Kind.None;
                        if (low != Kind.None && frontA > 0.5f) Emit(sink, a, fa, gaps, low, hedgeTint, houses, houseGrid, roads, roadGrid, ref st, rng);
                        if (low != Kind.None && frontB > 0.5f) Emit(sink, fb, b, gaps, low, hedgeTint, houses, houseGrid, roads, roadGrid, ref st, rng);
                    }
                }
            }
            st.drawn = BuildDrawn(sink);
            return st;
        }

        // ------------------------------------------------------------------ map editor (Data/<area>.garden.json)
        //   { "removed": ["hedge@-15843,-5091", "picket@…"], "lines": [ { "kind": "hedgeTall", "points": [[x,z],…], "height": 2.0 } ] }
        // Every built line (measured hedge or generated run) gets an id "<kind>@<x·10>,<z·10>" (its midpoint) and is
        // listed in Assets/Generated/<area>/<area>_gardens.json for the editor.

        static readonly HashSet<string> removedIds = new HashSet<string>();
        static readonly List<(string kind, Vector2 p)> removedPos = new List<(string, Vector2)>();
        static readonly List<(string kind, List<Vector2> pts, float height)> drawn = new List<(string, List<Vector2>, float)>();
        static readonly List<(string id, string kind, Vector2 a, Vector2 b, float h, float w)> catalog = new List<(string, string, Vector2, Vector2, float, float)>();

        static void LoadEdits(string path)
        {
            removedIds.Clear(); removedPos.Clear(); drawn.Clear(); catalog.Clear();
            if (!File.Exists(path) || !(MiniJson.Parse(File.ReadAllText(path)) is Dictionary<string, object> root)) return;
            if (root.TryGetValue("removed", out var rm) && rm is List<object> rl)
                foreach (var o in rl)
                {
                    if (!(o is string id)) continue;
                    removedIds.Add(id);
                    var at = id.Split('@');
                    var xz = at.Length == 2 ? at[1].Split('#')[0].Split(',') : new string[0];
                    if (xz.Length == 2 && int.TryParse(xz[0], out int x) && int.TryParse(xz[1], out int z))
                        removedPos.Add((at[0], new Vector2(x / 10f, z / 10f)));
                }
            if (root.TryGetValue("lines", out var ln) && ln is List<object> ll)
                foreach (Dictionary<string, object> o in ll)
                {
                    var pts = new List<Vector2>();
                    if (o.TryGetValue("points", out var po) && po is List<object> pl)
                        foreach (List<object> q in pl) pts.Add(new Vector2((float)(double)q[0], (float)(double)q[1]));
                    float h = o.TryGetValue("height", out var hv) && hv is double hd ? (float)hd : 0f;
                    if (pts.Count >= 2) drawn.Add((o.TryGetValue("kind", out var k) ? (string)k : "hedgeTall", pts, h));
                }
        }

        /// <summary>Lists the line for the editor; true if it was removed there (then it isn't built).</summary>
        static bool Record(string kind, Vector2 a, Vector2 b, float h, float w)
        {
            var mid = (a + b) * 0.5f;
            string id = $"{kind}@{Mathf.RoundToInt(mid.x * 10f)},{Mathf.RoundToInt(mid.y * 10f)}";
            catalog.Add((id, kind, a, b, h, w));
            if (removedIds.Contains(id)) return true;
            // Data refreshed and the line moved a little: same kind within 1.5 m still counts.
            for (int i = 0; i < removedPos.Count; i++)
                if (removedPos[i].kind == kind && (removedPos[i].p - mid).sqrMagnitude < 1.5f * 1.5f) { removedPos.RemoveAt(i); return true; }
            return false;
        }

        static int BuildDrawn(MeshSink sink)
        {
            foreach (var (kindName, pts, height) in drawn)
            {
                var kind = KindOf(kindName);
                var col = WorldStyle.Tint(HedgeGreen, new System.Random(Mathf.RoundToInt(pts[0].x * 7 + pts[0].y * 13)), 0.06f);
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Vector2 a = pts[i], b = pts[i + 1];
                    int pieces = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 4f));
                    for (int q = 0; q < pieces; q++)
                    {
                        Vector2 p0 = Vector2.Lerp(a, b, (float)q / pieces), p1 = Vector2.Lerp(a, b, (float)(q + 1) / pieces);
                        var batch = sink.Get(WorldStyle.MatProps, (p0 + p1) * 0.5f);
                        if ((kind == Kind.HedgeLow || kind == Kind.HedgeTall) && height > 0.3f)
                            Hedge(batch, p0, p1, height, kind == Kind.HedgeLow ? 0.6f : 0.8f, col);
                        else
                            Piece(batch, p0, p1, kind, col, q == 0 && i == 0, q == pieces - 1);
                    }
                }
            }
            return drawn.Count;
        }

        static string KindName(Kind k) => k switch
        {
            Kind.HedgeLow => "hedgeLow", Kind.HedgeTall => "hedgeTall", Kind.Picket => "picket",
            Kind.WallLow => "wall", Kind.Privacy => "privacy", Kind.Mesh => "mesh", _ => "none",
        };

        static Kind KindOf(string s) => s switch
        {
            "hedgeLow" => Kind.HedgeLow, "hedge" => Kind.HedgeTall, "hedgeTall" => Kind.HedgeTall, "picket" => Kind.Picket,
            "wall" => Kind.WallLow, "privacy" => Kind.Privacy, "mesh" => Kind.Mesh, _ => Kind.HedgeTall,
        };

        static float KindHeight(Kind k) => k switch
        {
            Kind.HedgeLow => 1.0f, Kind.HedgeTall => 1.8f, Kind.Picket => 0.95f, Kind.WallLow => 0.68f,
            Kind.Privacy => 1.85f, Kind.Mesh => 1.25f, _ => 1f,
        };

        /// <summary>Every garden line of the last Build (for the map editor).</summary>
        public static void SaveCatalog(string path)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("{\"lines\":[\n");
            for (int i = 0; i < catalog.Count; i++)
            {
                var c = catalog[i];
                sb.Append(string.Format(ci, "{{\"id\":\"{0}\",\"kind\":\"{1}\",\"a\":[{2:0.##},{3:0.##}],\"b\":[{4:0.##},{5:0.##}],\"h\":{6:0.##}}}",
                    c.id, c.kind, c.a.x, c.a.y, c.b.x, c.b.y, c.h));
                sb.Append(i < catalog.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("]}\n");
            File.WriteAllText(path, sb.ToString());
        }

        static readonly List<(Vector2 a, Vector2 b)> measured = new List<(Vector2 a, Vector2 b)>();
        static Dictionary<Vector2Int, List<int>> measuredGrid = new Dictionary<Vector2Int, List<int>>();

        static bool NearGarden(Vector2 p, List<Parcel> parcels, Dictionary<Vector2Int, List<int>> grid, float dist)
        {
            foreach (int i in Query(grid, new Rect(p - Vector2.one * dist, Vector2.one * 2f * dist)))
            {
                var q = parcels[i];
                if (!q.residential) continue;
                if (OsmData.PointInRing(p, q.ring)) return true;
                for (int k = 0, j = q.ring.Count - 1; k < q.ring.Count; j = k++)
                {
                    Vector2 a = q.ring[j], ab = q.ring[k] - a;
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                    if ((p - (a + ab * t)).sqrMagnitude < dist * dist) return true;
                }
            }
            return false;
        }

        static bool InNewBuild(Vector2 p, List<Parcel> parcels, Dictionary<Vector2Int, List<int>> grid)
        {
            foreach (int i in Query(grid, new Rect(p, Vector2.zero)))
                if (parcels[i].residential && parcels[i].newBuild && OsmData.PointInRing(p, parcels[i].ring)) return true;
            return false;
        }

        static bool NearMeasuredHedge(Vector2 p, Vector2 dir)
        {
            foreach (int i in Query(measuredGrid, new Rect(p, Vector2.zero)))
            {
                var (a, b) = measured[i];
                Vector2 ab = b - a;
                if (ab.sqrMagnitude < 0.01f || Mathf.Abs(Vector2.Dot(ab.normalized, dir)) < 0.85f) continue;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                if ((p - (a + ab * t)).sqrMagnitude < 1.6f * 1.6f) return true;
            }
            return false;
        }

        /// <summary>Parts of a→b at least <paramref name="clearance"/> away from every drawn road/path (pieces ≥ 1 m).</summary>
        static List<(Vector2 a, Vector2 b)> ClipOffRoads(Vector2 a, Vector2 b, List<(Vector2 a, Vector2 b, float half)> roads,
            Dictionary<Vector2Int, List<int>> grid, float clearance)
        {
            var result = new List<(Vector2, Vector2)>();
            float len = Vector2.Distance(a, b);
            int n = Mathf.Max(1, Mathf.CeilToInt(len / 0.25f));
            int start = -1;
            for (int k = 0; k <= n; k++)
            {
                bool free = k < n && !NearRoad(Vector2.Lerp(a, b, (k + 0.5f) / n), roads, grid, clearance);
                if (free && start < 0) start = k;
                if (!free && start >= 0)
                {
                    Vector2 p0 = Vector2.Lerp(a, b, (float)start / n), p1 = Vector2.Lerp(a, b, (float)k / n);
                    if (Vector2.Distance(p0, p1) >= 1f) result.Add((p0, p1));
                    start = -1;
                }
            }
            return result;
        }

        static bool OnRoad(Vector2 p, List<(Vector2 a, Vector2 b, float half)> roads, Dictionary<Vector2Int, List<int>> grid)
        {
            foreach (int i in Query(grid, new Rect(p, Vector2.zero)))
            {
                var (a, b, half) = roads[i];
                Vector2 ab = b - a;
                float t = ab.sqrMagnitude < 0.01f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                if (Vector2.Distance(p, a + ab * t) < half + 0.6f) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ classification

        static bool IsStreetSide(Vector2 mid, Vector2 dir, List<(Vector2 a, Vector2 b, float half)> roads, Dictionary<Vector2Int, List<int>> grid)
        {
            foreach (int i in Query(grid, new Rect(mid, Vector2.zero)))
            {
                var (a, b, half) = roads[i];
                Vector2 ab = b - a;
                if (ab.sqrMagnitude < 0.01f) continue;
                float t = Mathf.Clamp01(Vector2.Dot(mid - a, ab) / ab.sqrMagnitude);
                float d = Vector2.Distance(mid, a + ab * t);
                if (d < half + 7f && Mathf.Abs(Vector2.Dot(dir, ab.normalized)) > 0.8f) return true;
            }
            return false;
        }

        static bool NearRoad(Vector2 p, List<(Vector2 a, Vector2 b, float half)> roads, Dictionary<Vector2Int, List<int>> grid, float extra)
        {
            foreach (int i in Query(grid, new Rect(p, Vector2.zero)))
            {
                var (a, b, half) = roads[i];
                Vector2 ab = b - a;
                float t = ab.sqrMagnitude < 0.01f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                if (Vector2.Distance(p, a + ab * t) < half + extra) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ geometry

        /// <summary>Builds <paramref name="kind"/> along a→b except in the gaps and wherever a building stands.</summary>
        static void Emit(MeshSink sink, Vector2 a, Vector2 b, List<(float from, float to)> gaps, Kind kind, Color32 hedge,
            List<(List<Vector2> ring, Rect bounds, bool main)> houses, Dictionary<Vector2Int, List<int>> houseGrid,
            List<(Vector2 a, Vector2 b, float half)> roads, Dictionary<Vector2Int, List<int>> roadGrid, ref Stats st, System.Random rng)
        {
            float len = Vector2.Distance(a, b);
            if (len < 0.5f) return;
            Vector2 dir = (b - a) / len;
            const float step = 0.5f;
            int n = Mathf.Max(1, Mathf.CeilToInt(len / step));
            // Keep-mask along the edge. Hard stops: gaps, buildings, streets, real hedges. Soft stop: paved ground
            // in the aerial image (driveways, paths) – single stray samples there are noise and get closed again.
            var keep = new bool[n];
            var soft = new bool[n];
            var aerial = AerialImage.Current;
            for (int k = 0; k < n; k++)
            {
                float t = (k + 0.5f) * len / n;
                var p = a + dir * t;
                bool ok = true;
                foreach (var g in gaps) if (t > g.from && t < g.to) { ok = false; break; }
                if (ok && NearBuilding(p, houses, houseGrid, 0.9f)) ok = false;
                if (ok && NearRoad(p, roads, roadGrid, 1.0f)) ok = false;           // never on or at a street
                if (ok && NearMeasuredHedge(p, dir)) ok = false;                    // a real hedge already runs here
                if (ok && aerial != null && aerial.IsPaved(p)) { ok = false; soft[k] = true; }
                keep[k] = ok;
            }
            // Paved runs shorter than 1 m are noise (a stepping stone, a shadow): close them.
            for (int k = 0; k < n; )
            {
                if (!soft[k]) { k++; continue; }
                int e = k;
                while (e + 1 < n && soft[e + 1]) e++;
                bool bounded = k > 0 && e < n - 1 && keep[k - 1] && keep[e + 1];
                if (bounded && (e - k + 1) * len / n < 1.0f) for (int q = k; q <= e; q++) keep[q] = true;
                k = e + 1;
            }
            // Runs of kept samples -> pieces (≤ 4 m so they follow the terrain).
            int s = 0;
            while (s < n)
            {
                if (!keep[s]) { s++; continue; }
                int e = s;
                while (e + 1 < n && keep[e + 1]) e++;
                float t0 = s * len / n, t1 = (e + 1) * len / n;
                if (t1 - t0 >= 1.2f && !Record(KindName(kind), a + dir * t0, a + dir * t1, KindHeight(kind), 0f))
                {
                    int pieces = Mathf.CeilToInt((t1 - t0) / 4f);
                    for (int q = 0; q < pieces; q++)
                    {
                        Vector2 p0 = a + dir * Mathf.Lerp(t0, t1, (float)q / pieces), p1 = a + dir * Mathf.Lerp(t0, t1, (float)(q + 1) / pieces);
                        Piece(sink.Get(WorldStyle.MatProps, (p0 + p1) * 0.5f), p0, p1, kind, hedge, q == 0, q == pieces - 1);
                    }
                    if (kind == Kind.HedgeLow || kind == Kind.HedgeTall) st.hedges++;
                    else if (kind == Kind.WallLow) st.walls++;
                    else st.fences++;
                }
                s = e + 1;
            }
        }

        static void Piece(MeshSink.Batch b, Vector2 p0, Vector2 p1, Kind kind, Color32 hedge, bool first, bool last)
        {
            switch (kind)
            {
                case Kind.HedgeLow:
                    Hedge(b, p0, p1, 1.0f * Jitter(p0), 0.6f, hedge);
                    break;
                case Kind.HedgeTall:
                    Hedge(b, p0, p1, 1.8f * Jitter(p0), 0.8f, hedge);
                    break;
                case Kind.WallLow:
                    Box(b, p0, p1, 0f, 0.6f, 0.3f, Klinker, Klinker);
                    Box(b, p0, p1, 0.6f, 0.68f, 0.36f, Coping, Coping);
                    break;
                case Kind.Picket:   // Jägerzaun: posts + two rails, white or wood
                {
                    var c = (Mathf.Abs(p0.x * 7f + p0.y * 3f) % 2f) < 1f ? PicketWhite : Wood;
                    Posts(b, p0, p1, 2f, 0.95f, 0.07f, c, first, last);
                    Box(b, p0, p1, 0.72f, 0.78f, 0.04f, c, c);
                    // Crossed diagonal laths between the posts.
                    float len = Vector2.Distance(p0, p1);
                    int bays = Mathf.Max(1, Mathf.RoundToInt(len / 2f));
                    for (int i = 0; i < bays; i++)
                    {
                        Vector2 q0 = Vector2.Lerp(p0, p1, (float)i / bays), q1 = Vector2.Lerp(p0, p1, (float)(i + 1) / bays);
                        Beam(b, V(q0, 0.18f), V(q1, 0.82f), 0.05f, 0.025f, c);
                        Beam(b, V(q0, 0.82f), V(q1, 0.18f), 0.05f, 0.025f, c);
                    }
                    break;
                }
                case Kind.Privacy:  // wooden screen panels between posts
                    Box(b, p0, p1, 0.05f, 1.8f, 0.05f, Wood, Wood);
                    Posts(b, p0, p1, 1.8f, 1.85f, 0.1f, WoodDark, first, last);
                    break;
                case Kind.Mesh:     // green mesh fence: posts, top and bottom wire
                    Posts(b, p0, p1, 2.5f, 1.25f, 0.06f, MeshGreen, first, last);
                    Box(b, p0, p1, 1.15f, 1.2f, 0.03f, MeshGreen, MeshGreen);
                    Box(b, p0, p1, 0.08f, 0.12f, 0.03f, MeshGreen, MeshGreen);
                    Box(b, p0, p1, 0.6f, 0.63f, 0.02f, MeshGreen, MeshGreen);
                    break;
            }
        }

        static void Posts(MeshSink.Batch b, Vector2 p0, Vector2 p1, float spacing, float h, float w, Color32 c, bool first, bool last)
        {
            float len = Vector2.Distance(p0, p1);
            Vector2 dir = (p1 - p0) / Mathf.Max(len, 1e-4f);
            int n = Mathf.Max(1, Mathf.RoundToInt(len / spacing));
            for (int i = first ? 0 : 1; i <= n; i++)
            {
                if (i == n && !last) continue;
                var c0 = p0 + dir * (len * i / n);
                Box(b, c0 - dir * w * 0.5f, c0 + dir * w * 0.5f, 0f, h, w, c, c);
            }
        }

        /// <summary>Box from a to b (centre line), width w, heights y0..y1 – four sides, top and both ends.</summary>
        static void Box(MeshSink.Batch b, Vector2 a, Vector2 c, float y0, float y1, float w, Color32 side, Color32 top)
        {
            Vector2 d = c - a;
            if (d.sqrMagnitude < 1e-6f) return;
            Vector2 n = new Vector2(-d.y, d.x).normalized * (w * 0.5f);
            Vector3 al = V(a + n, y0), bl = V(c + n, y0), ar = V(a - n, y0), br = V(c - n, y0), up = Vector3.up * (y1 - y0);
            MeshSink.Quad(b, ar, ar + up, br + up, br, side);
            MeshSink.Quad(b, bl, bl + up, al + up, al, side);
            MeshSink.Quad(b, al + up, bl + up, br + up, ar + up, top);
            MeshSink.Quad(b, al, al + up, ar + up, ar, side);
            MeshSink.Quad(b, br, br + up, bl + up, bl, side);
        }

        /// <summary>Hedge: straight sides up to a shoulder, then bevelled to a narrower, lighter top.</summary>
        static void Hedge(MeshSink.Batch b, Vector2 a, Vector2 c, float h, float w, Color32 col)
        {
            Vector2 d = c - a;
            if (d.sqrMagnitude < 1e-6f) return;
            Vector2 n = new Vector2(-d.y, d.x).normalized * (w * 0.5f);
            float sh = h - Mathf.Min(0.25f, h * 0.2f);
            var top = Lighter(col);
            Vector3 al = V(a + n, 0f), bl = V(c + n, 0f), ar = V(a - n, 0f), br = V(c - n, 0f);
            Vector3 als = V(a + n, sh), bls = V(c + n, sh), ars = V(a - n, sh), brs = V(c - n, sh);
            Vector3 alt = V(a + n * 0.6f, h), blt = V(c + n * 0.6f, h), art = V(a - n * 0.6f, h), brt = V(c - n * 0.6f, h);
            MeshSink.Quad(b, ar, ars, brs, br, col);
            MeshSink.Quad(b, bl, bls, als, al, col);
            MeshSink.Quad(b, ars, art, brt, brs, top);
            MeshSink.Quad(b, bls, blt, alt, als, top);
            MeshSink.Quad(b, alt, blt, brt, art, top);
            MeshSink.Quad(b, al, als, ars, ar, col);
            MeshSink.Quad(b, als, alt, art, ars, top);
            MeshSink.Quad(b, br, brs, bls, bl, col);
            MeshSink.Quad(b, brs, brt, blt, bls, top);
        }

        /// <summary>Square lath from p to q (any direction), cross-section w × t.</summary>
        static void Beam(MeshSink.Batch b, Vector3 p, Vector3 q, float w, float t, Color32 col)
        {
            Vector3 d = q - p;
            Vector3 side = Vector3.Cross(Vector3.up, d).normalized * (t * 0.5f);
            Vector3 up = Vector3.Cross(d, side).normalized * (w * 0.5f);
            void Face(Vector3 o, Vector3 e, Vector3 facing)
            {
                Vector3 p0 = p + o - e, p1 = p + o + e, p2 = q + o + e, p3 = q + o - e;
                if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), facing) < 0f) { var x = p1; p1 = p3; p3 = x; }
                MeshSink.Quad(b, p0, p1, p2, p3, col);
            }
            Face(side, up, side);
            Face(-side, up, -side);
            Face(up, side, up);
            Face(-up, side, -up);
        }

        static float Jitter(Vector2 p) => 0.94f + 0.12f * Mathf.Abs(Mathf.Sin(p.x * 12.9898f + p.y * 78.233f));

        static Color32 Lighter(Color32 c) => new Color32((byte)Mathf.Min(255, c.r + 14), (byte)Mathf.Min(255, c.g + 20), (byte)Mathf.Min(255, c.b + 10), c.a);
        static Vector3 V(Vector2 p, float y) => new Vector3(p.x, y, p.y);

        // ------------------------------------------------------------------ helpers

        static bool NearBuilding(Vector2 p, List<(List<Vector2> ring, Rect bounds, bool main)> houses, Dictionary<Vector2Int, List<int>> grid, float clearance)
        {
            foreach (int i in Query(grid, new Rect(p, Vector2.zero)))
            {
                var (ring, bounds, _) = houses[i];
                var grown = new Rect(bounds.xMin - clearance, bounds.yMin - clearance, bounds.width + 2 * clearance, bounds.height + 2 * clearance);
                if (!grown.Contains(p)) continue;
                if (OsmData.PointInRing(p, ring)) return true;
                for (int k = 0, j = ring.Count - 1; k < ring.Count; j = k++)
                {
                    Vector2 a = ring[j], ab = ring[k] - a;
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                    if ((p - (a + ab * t)).sqrMagnitude < clearance * clearance) return true;
                }
            }
            return false;
        }

        static Rect GrowRect(Vector2 a, Vector2 b, float r) =>
            Rect.MinMaxRect(Mathf.Min(a.x, b.x) - r, Mathf.Min(a.y, b.y) - r, Mathf.Max(a.x, b.x) + r, Mathf.Max(a.y, b.y) + r);

        static Dictionary<Vector2Int, List<int>> Index(int count, System.Func<int, Rect> bounds)
        {
            var grid = new Dictionary<Vector2Int, List<int>>();
            for (int i = 0; i < count; i++)
            {
                var r = bounds(i);
                for (int x = Mathf.FloorToInt(r.xMin / Cell); x <= Mathf.FloorToInt(r.xMax / Cell); x++)
                for (int z = Mathf.FloorToInt(r.yMin / Cell); z <= Mathf.FloorToInt(r.yMax / Cell); z++)
                {
                    var k = new Vector2Int(x, z);
                    if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                    l.Add(i);
                }
            }
            return grid;
        }

        static IEnumerable<int> Query(Dictionary<Vector2Int, List<int>> grid, Rect r)
        {
            var seen = new HashSet<int>();
            for (int x = Mathf.FloorToInt(r.xMin / Cell); x <= Mathf.FloorToInt(r.xMax / Cell); x++)
            for (int z = Mathf.FloorToInt(r.yMin / Cell); z <= Mathf.FloorToInt(r.yMax / Cell); z++)
                if (grid.TryGetValue(new Vector2Int(x, z), out var l))
                    foreach (int i in l) if (seen.Add(i)) yield return i;
        }

        static long EdgeKey(Vector2 a, Vector2 b)
        {
            long A = ((long)Mathf.RoundToInt(a.x * 5f) << 32) ^ (uint)Mathf.RoundToInt(a.y * 5f);
            long B = ((long)Mathf.RoundToInt(b.x * 5f) << 32) ^ (uint)Mathf.RoundToInt(b.y * 5f);
            return A < B ? A * 1000003L ^ B : B * 1000003L ^ A;
        }

        static int StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                foreach (char ch in s) h = h * 31 + ch;
                return h;
            }
        }

        static Kind Pick(System.Random rng, params (Kind k, int w)[] options)
        {
            int total = 0;
            foreach (var o in options) total += o.w;
            int r = rng.Next(total);
            foreach (var o in options) { if (r < o.w) return o.k; r -= o.w; }
            return options[0].k;
        }
    }
}
