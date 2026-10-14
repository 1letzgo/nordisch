using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LeGamez.Editor.OsmImport;
using LeGamez.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LeGamez.Editor
{
    /// <summary>
    /// Finds vertical steps, drops and holes in the built driving surface. Loads the generated tiles along the chosen roads,
    /// makes colliders of what the car drives on (terrain parts incl. bridge decks, collision-only Blockers) and walks every
    /// carriageway's centre line and ±half its width every 0.5 m on the top surface (from 3 m below to 1.5 m above the
    /// last height – bridges above/below are ignored). Step = a rise of more than 4 cm over 5 cm; drop = the grade over
    /// 1 m leaves the grade over ±10 m by more than 12 % (a tilted 5 m terrain cell); hole = no surface; WALL in lane = a
    /// Blockers wall (guard rail, barrier) standing on the line.
    /// Batch: -executeMethod LeGamez.Editor.RoadStepCheck.Run -area westerstede [-classes motorway,motorway_link|all]
    /// [-min 0.05] [-out Builds/RoadSteps/&lt;id&gt;.csv] [-detail] → spots (merged within 15 m) as "[Steps]" + a -views list
    /// for AreaViewSnapshot. -at "x,z;…" prints profiles and deck ends at a spot, -ray "x,y,z,dx,dz;…" what stands there.
    /// </summary>
    public static class RoadStepCheck
    {
        const int PhysLayer = 30, VisLayer = 29;

        public static void Run()
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string k, string d) { int i = System.Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string id = Arg("-area", "westerstede");
            var classes = new HashSet<string>(Arg("-classes", "motorway,motorway_link").Split(','));
            bool all = classes.Contains("all");
            float minStep = float.Parse(Arg("-min", "0.05"), CultureInfo.InvariantCulture);
            bool visual = System.Array.IndexOf(args, "-visual") >= 0;
            string outPath = Arg("-out", Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "RoadSteps", id + ".csv")));

            // Decks/terrain as imported (for the cause guess).
            var field = OsmAreaImporter.PrepareTerrain(id);
            string dataDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Data"));
            var data = OsmData.Load(Path.Combine(dataDir, id + ".osm.json"));
            AreaMask.Load(dataDir, id)?.Filter(data);

            var ways = new List<(OsmWay w, float hw, float y)>();
            foreach (var w in data.ways)
            {
                if (w.points.Count < 2 || !GroundBuilder.Carriageway(w.tags, out float width, out float y)) continue;
                if (!all && !classes.Contains(w.tags.Get("highway"))) continue;
                ways.Add((w, width * 0.5f, y));
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var keys = new HashSet<Vector2Int>();
            foreach (var (w, hw, _) in ways)
                for (int i = 0; i < w.points.Count - 1; i++)
                {
                    Vector2 a = w.points[i], b = w.points[i + 1];
                    float len = Vector2.Distance(a, b);
                    for (float s = 0f; s <= len + 5f; s += 5f)
                    {
                        var p = Vector2.Lerp(a, b, len > 0f ? Mathf.Min(1f, s / len) : 0f);
                        for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                            keys.Add(WorldStreamer.KeyOf(p.x + dx * (hw + 3f), p.y + dz * (hw + 3f)));
                    }
                }
            int parts = 0, unreadable = 0;
            foreach (var k in keys)
            {
                var t = AssetDatabase.LoadAssetAtPath<WorldTile>(WorldStreamer.EditorTilePath(id, k));
                if (t == null) continue;
                foreach (var part in t.parts)
                {
                    bool phys = part.group == WorldStyle.MatTerrain || part.group == WorldStyle.MatBlockers; // what the car drives on
                    bool vis = visual && part.group == WorldStyle.MatGround;
                    if (!phys && !vis || part.mesh == null) continue;
                    if (!part.mesh.isReadable) { unreadable++; continue; }
                    var go = new GameObject(part.group + " " + k) { layer = phys ? PhysLayer : VisLayer };
                    go.AddComponent<MeshCollider>().sharedMesh = part.mesh;
                    parts++;
                }
            }
            Physics.SyncTransforms();
            Debug.Log($"[Steps] {ways.Count} ways, {keys.Count} tiles, {parts} collider meshes ({unreadable} unreadable skipped)");

            string ray = Arg("-ray", null); // x,y,z,dx,dz;… horizontal rays: what stands there
            if (ray != null)
            {
                foreach (var spec in ray.Split(';'))
                {
                    var v = System.Array.ConvertAll(spec.Split(','), x => float.Parse(x, CultureInfo.InvariantCulture));
                    var o = new Vector3(v[0], v[1], v[2]);
                    var dir = new Vector3(v[3], 0f, v[4]).normalized;
                    foreach (float up in new[] { 0.3f, 0.8f, 1.5f })
                    {
                        var rayHits = Physics.RaycastAll(o + Vector3.up * up, dir, 12f, (1 << PhysLayer));
                        System.Array.Sort(rayHits, (a, b) => a.distance.CompareTo(b.distance));
                        var sb = new StringBuilder();
                        foreach (var hh in rayHits) sb.Append($"{hh.collider.name}@{hh.distance:F2} n({hh.normal.x:F2},{hh.normal.y:F2},{hh.normal.z:F2}) ");
                        Debug.Log($"[Ray] {o} +{up} dir {dir}: {sb}");
                    }
                    var down = Physics.RaycastAll(o + Vector3.up * 3f, Vector3.down, 10f, 1 << PhysLayer);
                    var sd = new StringBuilder();
                    foreach (var hh in down) sd.Append($"{hh.collider.name}@y{hh.point.y:F2} ");
                    Debug.Log($"[Ray] {o} down: {sd}");
                }
                return;
            }
            string probeAt = Arg("-at", null);
            if (probeAt != null)
            {
                foreach (var spec in probeAt.Split(';'))
                {
                    var xz = System.Array.ConvertAll(spec.Split(','), v => float.Parse(v, CultureInfo.InvariantCulture));
                    Probe(new Vector2(xz[0], xz[1]), ways, field);
                }
                return;
            }
            var found = new List<Hit>();
            int lines = 0; double metres = 0;
            foreach (var (w, hw, y) in ways)
            {
                var offs = new List<float> { 0f };
                if (hw > 2.6f) { offs.Add(hw * 0.5f); offs.Add(-hw * 0.5f); } // about the outer lanes' centres
                foreach (float off in offs)
                {
                    foreach (int layer in visual ? new[] { PhysLayer, VisLayer } : new[] { PhysLayer })
                    {
                        lines++;
                        metres += Walk(w, off, field, y, 1 << layer, layer == VisLayer, minStep, found);
                    }
                }
            }

            // Merge into spots (15 m).
            found.Sort((a, b) => b.jump.CompareTo(a.jump));
            var spots = new List<(Hit top, int n, HashSet<long> ways, bool phys, bool vis)>();
            foreach (var h in found)
            {
                int at = -1;
                for (int i = 0; i < spots.Count; i++)
                    if ((spots[i].top.p - h.p).sqrMagnitude < 15f * 15f && spots[i].top.wall == h.wall) { at = i; break; }
                if (at < 0) spots.Add((h, 1, new HashSet<long> { h.way }, !h.visual, h.visual));
                else { var s = spots[at]; s.n++; s.ways.Add(h.way); s.phys |= !h.visual; s.vis |= h.visual; spots[at] = s; }
            }
            var csv = new StringBuilder("x,z,jump,kind,surface,way,highway,offset,cause,count\n");
            int holes = 0, physSpots = 0, walls = 0;
            foreach (var s in spots)
            {
                var h = s.top;
                string cause = Cause(h.p, field);
                if (h.hole) holes++;
                if (h.wall) walls++;
                if (s.phys) physSpots++;
                csv.Append(F(h.p.x)).Append(',').Append(F(h.p.y)).Append(',').Append(F(h.jump)).Append(',').Append(h.wall ? "wall" : h.hole ? "hole" : h.drop ? "drop" : "step").Append(',')
                   .Append(s.phys && s.vis ? "both" : s.phys ? "collider" : "ribbon").Append(',').Append(h.way).Append(',').Append(h.highway).Append(',')
                   .Append(F(h.offset)).Append(',').Append(cause).Append(',').Append(s.n).Append('\n');
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, csv.ToString());
            Debug.Log($"[Steps] {lines} lines, {metres / 1000.0:F1} km walked: {found.Count} steps > {minStep * 100f:0} cm, {spots.Count - walls} surface spots ({physSpots - walls} in the collider surface, {holes} holes), {walls} collision walls standing in a lane → {outPath}");
            int shown = 0;
            var views = new StringBuilder();
            for (int i = 0; i < Mathf.Min(spots.Count, 24); i++)
            {
                var h = spots[i].top;
                var c = h.p - h.dir * 16f;
                float yaw = Mathf.Atan2(h.dir.x, h.dir.y) * Mathf.Rad2Deg;
                views.Append($"s{i:00}:{F(c.x)},{F(c.y)},{F(yaw)},9,28;");
            }
            Debug.Log($"[Steps] views {views}");
            foreach (var s in spots)
            {
                if (shown++ >= 40) break;
                var h = s.top;
                Debug.Log($"[Steps] ({h.p.x:F1}, {h.p.y:F1}) {(h.wall ? "WALL in lane" : h.hole ? "HOLE" : h.drop ? "drop" : "step")} {h.jump:F2} m {(s.phys ? "collider" : "ribbon")} way {h.way} {h.highway} off {h.offset:F1} ×{s.n} – {Cause(h.p, field)}");
                if (shown <= 12 && System.Array.IndexOf(args, "-detail") >= 0) Detail(h.p, field);
            }
        }

        class Hit { public Vector2 p; public float jump; public bool hole, visual; public long way; public string highway; public float offset; public Vector2 dir; public bool drop, wall; }

        static float Walk(OsmWay w, float off, TerrainField field, float y, int mask, bool visual, float minStep, List<Hit> found)
        {
            var pts = w.points;
            // Offset polyline (simple: per segment normal, joints averaged).
            var line = new List<Vector2>();
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 dIn = i > 0 ? (pts[i] - pts[i - 1]).normalized : Vector2.zero, dOut = i < pts.Count - 1 ? (pts[i + 1] - pts[i]).normalized : Vector2.zero;
                var dir = (dIn + dOut).normalized;
                if (dir.sqrMagnitude < 0.5f) dir = dOut.sqrMagnitude > 0f ? dOut : dIn;
                line.Add(pts[i] + new Vector2(-dir.y, dir.x) * off);
            }
            float total = 0f;
            float? prev = null;
            Vector2 prevP = Vector2.zero;
            bool inStep = false, inWall = false;
            var samples = new List<(Vector2 p, float h, Vector2 dir)>();
            for (int i = 0; i < line.Count - 1; i++)
            {
                Vector2 a = line[i], b = line[i + 1];
                float len = Vector2.Distance(a, b);
                if (len < 0.01f) continue;
                total += len;
                for (float s = 0f; s < len; s += 0.5f)
                {
                    var p = Vector2.Lerp(a, b, s / len);
                    float? h = Surface(p, prev ?? (BridgeBuilder.Above(p, 0f, out float deck) ? deck : field.Height(p.x, p.y) + y), mask);
                    if (wallHit && !inWall) found.Add(new Hit { p = p, jump = 0f, wall = true, visual = visual, way = w.id, highway = w.tags.Get("highway"), offset = off, dir = (b - a) / len });
                    inWall = wallHit;
                    if (!h.HasValue)
                    {
                        if (prev.HasValue) found.Add(new Hit { p = p, jump = 9f, hole = true, visual = visual, way = w.id, highway = w.tags.Get("highway"), offset = off, dir = (b - a) / len });
                        prev = null; inStep = false;
                        continue;
                    }
                    if (prev.HasValue && Mathf.Abs(h.Value - prev.Value) > minStep * 0.5f)
                    {
                        // Refine: largest rise over 5 cm between the two samples.
                        float best = 0f; Vector2 at = p;
                        float? last = prev;
                        for (int k = 1; k <= 10; k++)
                        {
                            var q = Vector2.Lerp(prevP, p, k / 10f);
                            var hq = Surface(q, last.Value, mask);
                            if (!hq.HasValue) continue;
                            float j = Mathf.Abs(hq.Value - last.Value);
                            if (j > best) { best = j; at = q; }
                            last = hq;
                        }
                        if (best > minStep * 0.8f)
                        {
                            if (!inStep) found.Add(new Hit { p = at, jump = best, visual = visual, way = w.id, highway = w.tags.Get("highway"), offset = off, dir = (b - a) / len });
                            else if (found[found.Count - 1].jump < best) found[found.Count - 1].jump = best;
                            inStep = true;
                        }
                        else inStep = false;
                    }
                    else inStep = false;
                    prev = h; prevP = p;
                    samples.Add((p, h.Value, (b - a) / len));
                }
            }
            // Drops: the surface leaves its grade over a short stretch (a steep facet instead of a vertical face) – the
            // grade over 1 m differs from the grade over ±10 m by more than 12 %; reported as the height it gains or
            // loses against that grade within the stretch.
            int n = samples.Count;
            for (int i = 1; i < n - 1; i++)
            {
                int i0 = Mathf.Max(0, i - 20), i1 = Mathf.Min(n - 1, i + 20);
                if (i1 - i0 < 10 || (samples[i + 1].p - samples[i - 1].p).sqrMagnitude > 1.2f) continue;
                float g = samples[i + 1].h - samples[i - 1].h;
                float gm = (samples[i1].h - samples[i0].h) / ((i1 - i0) * 0.5f);
                if (Mathf.Abs(g - gm) < 0.12f) continue;
                // Extent of the anomaly (at most 6 m): height gained or lost against the grade over it.
                int j = i;
                while (j < n - 1 && j - i < 12 && Mathf.Abs(samples[j + 1].h - samples[j - 1].h - gm) >= 0.06f) j++;
                float excess = samples[j].h - samples[i - 1].h - gm * 0.5f * (j - i + 1);
                if (Mathf.Abs(excess) >= minStep * 2f)
                    found.Add(new Hit { p = samples[i].p, jump = Mathf.Abs(excess), visual = visual, way = w.id, highway = w.tags.Get("highway"), offset = off, dir = samples[i].dir, drop = true });
                i = j;
            }
            return total;
        }

        static readonly RaycastHit[] hits = new RaycastHit[16];
        static bool wallHit;

        /// <summary>Top surface at p from 3 m below to 1.5 m above <paramref name="near"/>.</summary>
        static float? Surface(Vector2 p, float near, int mask)
        {
            int n = Physics.RaycastNonAlloc(new Vector3(p.x, near + 3f, p.y), Vector3.down, hits, 6f, mask);
            // The top surface (what the car rides on), unless it is far above (a wall, the deck seen from below).
            float? best = null;
            wallHit = false;
            for (int i = 0; i < n; i++)
            {
                float y = hits[i].point.y;
                if (y > near + 1.5f) continue;
                // Collision-only walls (guard rail walls, barriers) are no surface – but one standing in a lane is reported.
                if (y > near + 0.3f && hits[i].collider.name.StartsWith(WorldStyle.MatBlockers)) { wallHit = true; continue; }
                if (!best.HasValue || y > best.Value) best = y;
            }
            return best;
        }

        static string Cause(Vector2 p, TerrainField field)
        {
            var sb = new List<string>();
            foreach (var d in BridgeBuilder.Decks)
            {
                var pts = d.pts;
                if (Vector2.Distance(p, pts[0]) < 15f || Vector2.Distance(p, pts[pts.Count - 1]) < 15f) { sb.Add($"deck-end {d.way.id}"); break; }
            }
            if (sb.Count == 0 && BridgeBuilder.Above(p, 0f, out _)) sb.Add("on-deck");
            float tx = Mathf.Repeat(p.x, WorldStreamer.TileSize), tz = Mathf.Repeat(p.y, WorldStreamer.TileSize);
            if (Mathf.Min(tx, WorldStreamer.TileSize - tx) < 1f || Mathf.Min(tz, WorldStreamer.TileSize - tz) < 1f) sb.Add("tile-edge");
            float cx = Mathf.Repeat(p.x, 40f), cz = Mathf.Repeat(p.y, 40f);
            if (Mathf.Min(cx, 40f - cx) < 0.6f || Mathf.Min(cz, 40f - cz) < 0.6f) sb.Add("block-edge");
            if (CoarseTerrain.Owns(p.x, p.y)) sb.Add("coarse");
            return sb.Count == 0 ? "?" : string.Join(" ", sb);
        }

        static void Probe(Vector2 p, List<(OsmWay w, float hw, float y)> ways, TerrainField field)
        {
            Debug.Log($"[Probe] ===== ({p.x:F1}, {p.y:F1})");
            Detail(p, field);
            foreach (var (w, hw, y) in ways)
            {
                var pts = w.points;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Vector2 a = pts[i], ab = pts[i + 1] - a;
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                    if (Vector2.Distance(a + ab * t, p) > hw + 4f) continue;
                    var dir = ab.normalized; var n = new Vector2(-dir.y, dir.x);
                    var c = a + ab * t;
                    Debug.Log($"[Probe] way {w.id} {w.tags.Get("highway")} lanes {w.tags.Get("lanes")} hw {hw:F1} seg {i}/{pts.Count - 1} dir ({dir.x:F2},{dir.y:F2}) deck {BridgeBuilder.IsDeck(w)} first {pts[0]} last {pts[pts.Count - 1]}");
                    foreach (float off in new[] { -hw + 1f, -hw * 0.5f, 0f, hw * 0.5f, hw - 1f })
                    {
                        var sb = new StringBuilder();
                        float? last = null;
                        for (float s = -16f; s <= 16.01f; s += 2f)
                        {
                            var q = c + dir * s + n * off;
                            var h = Surface(q, last ?? field.Height(q.x, q.y) + y, 1 << PhysLayer);
                            last = h ?? last;
                            sb.Append(h.HasValue ? h.Value.ToString("F2", CultureInfo.InvariantCulture) : "-").Append('/').Append((field.Height(q.x, q.y) + y).ToString("F2", CultureInfo.InvariantCulture)).Append(' ');
                        }
                        Debug.Log($"[Probe]   off {off:+0.0;-0.0}: {sb}");
                    }
                    break;
                }
            }
        }

        static void Detail(Vector2 p, TerrainField field)
        {
            foreach (var d in BridgeBuilder.Decks)
            {
                var pts = d.pts;
                if (Vector2.Distance(p, pts[0]) > 30f && Vector2.Distance(p, pts[pts.Count - 1]) > 30f) continue;
                var wp = d.way.points;
                Debug.Log($"[Probe]   deck {d.way.id} {d.way.tags.Get("highway")} w {d.width:F1} y {d.y:F2} len {d.Length:F0} ends {pts[0]}→{pts[pts.Count - 1]} (osm {wp[0]}→{wp[wp.Count - 1]}) " +
                          $"h {d.h0:F2}/{d.h1:F2} g {d.g0:F2}/{d.g1:F2} r {d.r0:F1}/{d.r1:F1} ext {d.ext0:F0}/{d.ext1:F0}");
                foreach (bool start in new[] { true, false })
                {
                    var e = start ? pts[0] : pts[pts.Count - 1];
                    var u = start ? (pts[0] - pts[1]).normalized : (pts[pts.Count - 1] - pts[pts.Count - 2]).normalized;
                    var n = new Vector2(-u.y, u.x);
                    var sb = new StringBuilder();
                    foreach (float along in new[] { -2f, 0.3f, 2f, 5f })
                    {
                        sb.Append($" a{along:0.#}:");
                        for (float o = -8f; o <= 8.01f; o += 2f)
                        {
                            var q = e + u * along + n * o;
                            float? hh = Surface(q, d.HeightAt(start ? 0f : d.Length), 1 << PhysLayer);
                            sb.Append(hh.HasValue ? (hh.Value - (start ? d.h0 : d.h1)).ToString("+0.00;-0.00", CultureInfo.InvariantCulture) : "  -  ").Append(' ');
                        }
                        sb.Append(" field:");
                        for (float o = -8f; o <= 8.01f; o += 4f) { var q = e + u * along + n * o; sb.Append((field.Height(q.x, q.y) + d.y - (start ? d.h0 : d.h1)).ToString("+0.00;-0.00", CultureInfo.InvariantCulture)).Append(' '); }
                    }
                    Debug.Log($"[Probe]     {(start ? "start" : "end")}{sb}");
                }
            }
        }

        static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
