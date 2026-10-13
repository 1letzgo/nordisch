using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LeGamez.World;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Traffic control from OSM (Data/&lt;id&gt;.traffic.json, see Tools/osm/fetch_traffic.py) plus signs derived
    /// from the road network:
    /// <list type="bullet">
    /// <item>traffic lights at mapped junctions / pedestrian crossings – a head per incoming arm on the right kerb,
    ///   stop line and pedestrian lanes; lamps animated at runtime by <see cref="TrafficLights"/></item>
    /// <item>zebra crossings with "Fußgängerüberweg" signs, dashed pedestrian lanes</item>
    /// <item>stop / give-way from OSM, and give-way + priority road where a minor road meets a bigger one</item>
    /// <item>speed limit / Tempo-30 zone signs where maxspeed changes, one-way and no-entry signs</item>
    /// <item>street name signs at junctions of named streets, mapped signs (city limits, footpaths, …)</item>
    /// </list>
    /// Poles and plates go into the Props bucket (lifted onto the terrain), markings into Ground (draped).
    /// Text (numbers, names) becomes TextMesh objects in <see cref="Spawn"/>.
    /// </summary>
    public static class TrafficBuilder
    {
        public struct Stats { public int junctionSignals, crossingSignals, heads, zebras, lanes, stops, giveWays, priority, speed, oneway, names, mapped; }

        class Road
        {
            public OsmWay way;
            public int rank;
            public float width, y;
            public int oneway;          // +1 along the points, -1 against, 0 both
            public string name;
            public int speed;           // km/h, 0 = unknown
            public bool zone, implicitSpeed, roundabout, link;
            public float Hw => width * 0.5f;
        }

        class Arm
        {
            public Road road;
            public int vi, dirSign;     // vertex index of the junction, direction along the points
            public Vector2 dir;         // away from the junction
            public bool incoming;       // traffic may drive towards the junction on this arm
            public string control;      // "stop" / "give_way"
        }

        class Junction
        {
            public Vector2 p;
            public readonly List<Arm> arms = new List<Arm>();
            public float clear;         // distance from the centre to the edge of the crossing roads
            public bool signal;
            public int MaxRank()
            {
                int m = -1;
                foreach (var a in arms) m = Mathf.Max(m, a.road.rank);
                return m;
            }
        }

        struct TextItem { public Vector3 pos; public Vector2 facing; public string text; public Color32 color; public float height; }

        static readonly List<TextItem> texts = new List<TextItem>();
        static readonly List<TrafficLights.Head> heads = new List<TrafficLights.Head>();
        static readonly List<TrafficLights.Signal> signals = new List<TrafficLights.Signal>();
        static readonly List<Vector2> poles = new List<Vector2>();
        static PropPlacer.Clearance carriageways;
        static MeshSink sink;
        static AreaRect bounds;
        static Stats st;

        // Palette (RAL-ish sign colours).
        static readonly Color32 Pole = new Color32(150, 154, 158, 255);
        static readonly Color32 Housing = new Color32(38, 40, 42, 255);
        static readonly Color32 Back = new Color32(128, 132, 136, 255);
        static readonly Color32 White = new Color32(244, 244, 240, 255);
        static readonly Color32 Red = new Color32(196, 30, 36, 255);
        static readonly Color32 Blue = new Color32(18, 78, 160, 255);
        static readonly Color32 Yellow = new Color32(246, 190, 0, 255);
        static readonly Color32 Black = new Color32(24, 24, 24, 255);
        static readonly Color32 Marking = WorldStyle.LaneMarking;

        const float SignHeight = 2.2f;   // plate centre above ground
        const float KerbGap = 0.55f;     // pole distance outside the carriageway edge

        public static Stats Build(string path, OsmData data, MeshSink meshSink, AreaRect area)
        {
            texts.Clear(); heads.Clear(); signals.Clear(); poles.Clear();
            st = new Stats();
            sink = meshSink;
            bounds = area;
            var nodes = Load(path, data);
            if (nodes == null) return st;

            // ---- road network
            var roads = new List<Road>();
            carriageways = new PropPlacer.Clearance();
            foreach (var w in data.ways)
            {
                if (!GroundBuilder.Carriageway(w.tags, out float width, out float y)) continue;
                int rank = RankOf(w.tags.Get("highway"));
                if (rank < 0 || !area.Overlaps(w.points, 50f)) continue;
                var r = new Road { way = w, rank = rank, width = width, y = y, name = w.tags.Get("name") };
                r.roundabout = w.tags.Is("junction", "roundabout") || w.tags.Is("junction", "circular");
                r.link = w.tags.Get("highway").EndsWith("_link");
                string ow = w.tags.Get("oneway");
                r.oneway = ow == "yes" || ow == "1" || ow == "true" || r.roundabout && ow != "no" ? 1 : ow == "-1" ? -1 : 0;
                ParseSpeed(w.tags, r);
                roads.Add(r);
                carriageways.AddLine(w.points, r.Hw);
            }

            var byVertex = new Dictionary<Vector2Int, List<(Road road, int vi)>>();
            foreach (var r in roads)
                for (int i = 0; i < r.way.points.Count; i++)
                {
                    var k = Key(r.way.points[i]);
                    if (!byVertex.TryGetValue(k, out var l)) byVertex[k] = l = new List<(Road, int)>();
                    l.Add((r, i));
                }

            // Every shared vertex with its arms; junctions are the ones with 3+ arms.
            var vertexArms = new Dictionary<Vector2Int, Junction>();
            var junctions = new List<Junction>();
            foreach (var kv in byVertex)
            {
                if (kv.Value.Count < 2) continue;
                var j = new Junction { p = kv.Value[0].road.way.points[kv.Value[0].vi] };
                foreach (var (road, vi) in kv.Value)
                {
                    if (vi > 0) j.arms.Add(MakeArm(road, vi, -1));
                    if (vi < road.way.points.Count - 1) j.arms.Add(MakeArm(road, vi, +1));
                }
                float maxHw = 0f;
                foreach (var a in j.arms) maxHw = Mathf.Max(maxHw, a.road.Hw);
                j.clear = maxHw + 1.2f;
                vertexArms[kv.Key] = j;
                if (j.arms.Count >= 3 && area.Contains(j.p, -2f)) junctions.Add(j);
            }

            // ---- traffic lights
            var signalNodes = new List<Vector2>();
            var signalCrossings = new List<Vector2>();
            foreach (var (p, t) in nodes)
            {
                if (t.Is("highway", "traffic_signals")) signalNodes.Add(p);
                else if (t.Is("crossing", "traffic_signals")) signalCrossings.Add(p);
            }
            var midblock = new List<Vector2>();
            foreach (var p in signalNodes)
            {
                var j = NearestJunction(junctions, p, 28f, 1);
                if (j != null) j.signal = true;
                else midblock.Add(p);
            }
            foreach (var p in signalCrossings)
            {
                var j = NearestJunction(junctions, p, 28f, 1);
                if (j == null || !j.signal) midblock.Add(p);
            }
            foreach (var j in junctions)
                if (j.signal) JunctionSignal(j);
            foreach (var c in Cluster(midblock, 22f))
            {
                if (NearestJunction(junctions, c, 22f, 1) is Junction jj && jj.signal) continue;
                if (NearestRoad(roads, c, 10f, 1, out var road, out var q, out var tan)) MidblockSignal(road, q, tan);
            }

            // ---- crossings without lights
            var crossings = new List<(Vector2 p, Dictionary<string, string> t)>();
            foreach (var (p, t) in nodes)
            {
                if (!(t.Is("highway", "crossing") || t.Has("crossing")) || t.Is("crossing", "traffic_signals")) continue;
                if (NearestJunction(junctions, p, 30f, 1) is Junction sj && sj.signal && (p - sj.p).magnitude < sj.clear + 6f) continue;
                bool dup = false;
                foreach (var c in crossings) if ((c.p - p).sqrMagnitude < 16f) { dup = true; break; }
                if (!dup) crossings.Add((p, t));
            }
            foreach (var (p, t) in crossings)
            {
                string kind = t.Get("crossing"), marks = t.Get("crossing:markings"), cref = t.Get("crossing_ref");
                bool zebra = kind == "zebra" || cref == "zebra" || marks == "zebra" || kind == "marked" && marks == null
                             || (kind == "uncontrolled" || kind == "marked") && marks == "yes";
                bool dashes = !zebra && marks != null && marks.Contains("dashes");
                if (!zebra && !dashes) continue;
                if (!NearestRoad(roads, p, 3f, 0, out var road, out var q, out var tan) || !area.Contains(q, 2f)) continue;
                if (zebra) Zebra(road, q, tan);
                else { PedestrianLanes(road, q, tan, 1.2f); st.lanes++; }
            }

            // ---- stop / give way from OSM
            foreach (var (p, t) in nodes)
            {
                string hw = t.Get("highway");
                if (hw != "stop" && hw != "give_way") continue;
                if (vertexArms.TryGetValue(Key(p), out var at) && at.arms.Count >= 3)
                {
                    int max = at.MaxRank();
                    foreach (var a in at.arms) if (a.road.rank < max && a.road.rank >= 1) a.control = hw;
                    continue;
                }
                Arm best = null;
                float bestD = float.MaxValue;
                foreach (var j in junctions)
                {
                    if (j.signal || (j.p - p).sqrMagnitude > 45f * 45f) continue;
                    foreach (var a in j.arms)
                    {
                        if (Vector2.Dot(a.dir, p - j.p) <= 0f) continue;
                        float d = DistanceAlong(a, p, 45f);
                        if (d < a.road.Hw + 3f && d < bestD) { bestD = d; best = a; }
                    }
                }
                if (best != null) best.control = hw;
            }

            // ---- derived priority: minor roads give way to the main road
            foreach (var j in junctions)
            {
                if (j.signal) continue;
                int max = j.MaxRank();
                bool anyControl = false;
                foreach (var a in j.arms) anyControl |= a.control != null;
                if (max >= 3)
                    foreach (var a in j.arms)
                        if (a.control == null && a.road.rank >= 1 && a.road.rank < max) { a.control = "give_way"; anyControl = true; }
                if (!anyControl) continue;
                foreach (var a in j.arms)
                {
                    if (!a.incoming || a.road.rank < 1) continue;
                    if (a.control != null) ControlSign(j, a);
                    else if (a.road.rank == max && !a.road.roundabout) { PlaceSign(j, a, j.clear + 2.5f, s => Priority(s)); st.priority++; }
                }
            }

            // ---- speed limits & zones
            var signed = new HashSet<(Road, int, int)>();
            foreach (var kv in vertexArms)
            {
                var v = kv.Value;
                foreach (var b in v.arms)
                {
                    var br = b.road;
                    if (br.rank < 1 || br.speed == 0 || br.implicitSpeed && !br.zone || !CanEnter(b)) continue;
                    // Where does the traffic come from: the straight continuation, else the biggest other road.
                    Arm from = null;
                    float bestDot = -0.7f;
                    foreach (var a in v.arms)
                        if (a != b && Vector2.Dot(a.dir, b.dir) < bestDot) { bestDot = Vector2.Dot(a.dir, b.dir); from = a; }
                    if (from == null)
                        foreach (var a in v.arms)
                            if (a != b && a.road != br && (from == null || a.road.rank > from.road.rank)) from = a;
                    if (from == null || from.road == br || from.road.speed == 0) continue;
                    var fr = from.road;
                    bool zoneStart = br.zone && !fr.zone;
                    // Turning into a bigger road needs no sign; zone exits are on the back of the zone sign.
                    bool limit = !br.zone && br.speed != fr.speed && br.rank <= fr.rank && !fr.zone;
                    if (!zoneStart && !limit) continue;
                    if (!signed.Add((br, b.vi, b.dirSign))) continue;
                    float along = v.arms.Count >= 3 ? v.clear + 4f : 6f;
                    int speed = br.speed;
                    if (zoneStart) PlaceEntering(b, along, s => Zone(s, speed, false), s => Zone(s, speed, true));
                    else PlaceEntering(b, along, s => Speed(s, speed), null);
                    st.speed++;
                }
            }

            // ---- one-way streets
            foreach (var j in junctions)
                foreach (var a in j.arms)
                {
                    var r = a.road;
                    // Main roads that are one-way are dual carriageways – no signs at every junction there.
                    if (r.oneway == 0 || r.roundabout || r.link || r.rank < 1 || r.rank >= 4) continue;
                    if (a.incoming)
                    {
                        // The one-way street continues straight on: nobody turns in against it here.
                        bool continues = false;
                        foreach (var o in j.arms)
                            if (o != a && o.road.oneway != 0 && CanEnter(o) && Vector2.Dot(o.dir, a.dir) < -0.7f) continues = true;
                        if (continues) continue;
                        // Exit of the one-way street: no entry, both sides.
                        float d = j.clear + 1.5f;
                        if (!Along(a, d, out var q, out var tan)) continue;
                        var h = tan;                     // wrong-way driver enters along the arm
                        PlacePole(q + Right(h) * (r.Hw + KerbGap), -h, s => NoEntry(s));
                        PlacePole(q - Right(h) * (r.Hw + KerbGap), -h, s => NoEntry(s));
                        st.oneway++;
                    }
                    else
                    {
                        // Entry: arrow sign parallel to the street, readable from the crossing road.
                        if (!Along(a, j.clear + 1.5f, out var q, out var tan)) continue;
                        var pos = q + Right(tan) * (r.Hw + KerbGap);
                        var n = -Right(tan);
                        PlacePole(pos, n, s => OneWay(s, tan), s => OneWay(s, tan));
                        st.oneway++;
                    }
                }

            // ---- street name signs
            foreach (var j in junctions)
            {
                Arm a = null, b = null;
                foreach (var arm in j.arms)
                    if (!string.IsNullOrEmpty(arm.road.name) && arm.road.rank >= 1 && (a == null || arm.road.rank > a.road.rank)) a = arm;
                if (a == null) continue;
                float bestCross = 0.35f;
                foreach (var arm in j.arms)
                {
                    if (string.IsNullOrEmpty(arm.road.name) || arm.road.name == a.road.name || arm.road.rank < 1) continue;
                    float c = Mathf.Abs(Cross(a.dir, arm.dir));
                    if (c > bestCross) { bestCross = c; b = arm; }
                }
                if (b == null) continue;
                var pos = j.p + a.dir * (b.road.Hw + 1.4f) + b.dir * (a.road.Hw + 1.4f);
                if (!area.Contains(pos, 1f)) continue;
                if (carriageways.IsBlocked(pos, 0.3f) && !carriageways.PushOut(ref pos, 0.4f)) continue;
                NameSign(pos, a, b);
                st.names++;
            }

            // ---- mapped signs (nodes with traffic_sign=*)
            foreach (var (p, t) in nodes)
            {
                string sign = t.Get("traffic_sign");
                if (sign == null || t.Is("highway", "stop") || t.Is("highway", "give_way") || !area.Contains(p, 2f)) continue;
                if (MappedSign(roads, p, t, sign, data.meta.name)) st.mapped++;
            }

            return st;
        }

        // ================================================================ data

        static List<(Vector2 p, Dictionary<string, string> t)> Load(string path, OsmData data)
        {
            if (!File.Exists(path) || !(MiniJson.Parse(File.ReadAllText(path)) is Dictionary<string, object> root) ||
                !(root.TryGetValue("nodes", out var n) && n is List<object> list)) return null;
            var result = new List<(Vector2, Dictionary<string, string>)>();
            foreach (var o in list)
            {
                if (!(o is Dictionary<string, object> d)) continue;
                var tags = new Dictionary<string, string>();
                if (d.TryGetValue("tags", out var to) && to is Dictionary<string, object> td)
                    foreach (var kv in td) tags[kv.Key] = kv.Value?.ToString();
                double lat = System.Convert.ToDouble(d["lat"], CultureInfo.InvariantCulture);
                double lon = System.Convert.ToDouble(d["lon"], CultureInfo.InvariantCulture);
                var local = data.projection.ToLocal(lat, lon);
                if (AreaMask.Allows(local)) result.Add((local, tags)); // large areas: only the built-up part
            }
            return result;
        }

        static int RankOf(string hw)
        {
            switch (hw)
            {
                case "motorway": case "trunk": case "primary": case "motorway_link": case "trunk_link": case "primary_link": return 5;
                case "secondary": case "secondary_link": return 4;
                case "tertiary": case "tertiary_link": return 3;
                case "unclassified": case "residential": return 2;
                case "living_street": return 1;
                case "service": return 0;
                default: return -1;
            }
        }

        static void ParseSpeed(Dictionary<string, string> t, Road r)
        {
            string ms = t.Get("maxspeed") ?? "";
            string type = (t.Get("maxspeed:type") ?? "") + ";" + (t.Get("zone:maxspeed") ?? "") + ";" + (t.Get("source:maxspeed") ?? "") + ";" + ms;
            type = type.Replace("ː", ":");
            if (type.Contains("zone30") || type.Contains("zone:30")) { r.zone = true; r.speed = 30; return; }
            if (int.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 10 && v <= 130) r.speed = v;
            else if (ms.Contains("urban")) r.speed = 50;
            if (type.Contains("urban") || type.Contains("rural")) r.implicitSpeed = true;
        }

        static Vector2Int Key(Vector2 p) => new Vector2Int(Mathf.RoundToInt(p.x * 20f), Mathf.RoundToInt(p.y * 20f));
        static Vector2 Right(Vector2 h) => new Vector2(h.y, -h.x);
        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        static Vector3 V(Vector2 p, float y = 0f) => new Vector3(p.x, y, p.y);

        static Arm MakeArm(Road road, int vi, int dirSign)
        {
            var a = new Arm { road = road, vi = vi, dirSign = dirSign };
            a.dir = Along(a, 6f, out var q, out _) ? (q - road.way.points[vi]).normalized : Vector2.zero;
            if (a.dir.sqrMagnitude < 0.5f)
            {
                var n = road.way.points[vi + dirSign] - road.way.points[vi];
                a.dir = n.sqrMagnitude > 1e-6f ? n.normalized : Vector2.right;
            }
            // Traffic towards the junction comes along the arm against dirSign.
            a.incoming = road.oneway == 0 || road.oneway == -dirSign;
            return a;
        }

        /// <summary>Traffic may drive from the junction into the arm.</summary>
        static bool CanEnter(Arm a) => a.road.oneway == 0 || a.road.oneway == a.dirSign;

        /// <summary>Point <paramref name="dist"/> metres from the arm's junction along the road, with the local direction.</summary>
        static bool Along(Arm a, float dist, out Vector2 p, out Vector2 dir)
        {
            var pts = a.road.way.points;
            int i = a.vi;
            p = pts[i];
            dir = a.dir;
            float left = dist;
            while (true)
            {
                int k = i + a.dirSign;
                if (k < 0 || k >= pts.Count) return false;
                var seg = pts[k] - pts[i];
                float len = seg.magnitude;
                if (len > 1e-4f)
                {
                    dir = seg / len;
                    if (left <= len) { p = pts[i] + dir * left; return true; }
                    left -= len;
                }
                i = k;
                p = pts[i];
            }
        }

        static float DistanceAlong(Arm a, Vector2 p, float maxDist)
        {
            float best = float.MaxValue;
            for (float d = 0f; d <= maxDist; d += 1.5f)
            {
                if (!Along(a, d, out var q, out _)) break;
                best = Mathf.Min(best, (q - p).magnitude);
            }
            return best;
        }

        static Junction NearestJunction(List<Junction> js, Vector2 p, float maxDist, int minRank)
        {
            Junction best = null;
            float bd = maxDist * maxDist;
            foreach (var j in js)
            {
                float d = (j.p - p).sqrMagnitude;
                if (d < bd && j.MaxRank() >= minRank) { bd = d; best = j; }
            }
            return best;
        }

        static bool NearestRoad(List<Road> roads, Vector2 p, float extra, int minRank, out Road road, out Vector2 q, out Vector2 tan)
        {
            road = null; q = p; tan = Vector2.right;
            float best = float.MaxValue;
            foreach (var r in roads)
            {
                if (r.rank < minRank) continue;
                var pts = r.way.points;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Vector2 a = pts[i], ab = pts[i + 1] - a;
                    float l2 = ab.sqrMagnitude;
                    if (l2 < 1e-6f) continue;
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2);
                    var c = a + ab * t;
                    float d = (c - p).magnitude;
                    if (d > r.Hw + extra || d >= best) continue;
                    best = d; road = r; q = c; tan = ab / Mathf.Sqrt(l2);
                }
            }
            return road != null;
        }

        static List<Vector2> Cluster(List<Vector2> pts, float radius)
        {
            var groups = new List<(Vector2 sum, int n)>();
            foreach (var p in pts)
            {
                int hit = -1;
                for (int i = 0; i < groups.Count; i++)
                    if ((groups[i].sum / groups[i].n - p).sqrMagnitude < radius * radius) { hit = i; break; }
                if (hit < 0) groups.Add((p, 1));
                else groups[hit] = (groups[hit].sum + p, groups[hit].n + 1);
            }
            var result = new List<Vector2>();
            foreach (var g in groups) result.Add(g.sum / g.n);
            return result;
        }

        // ================================================================ traffic lights

        static void JunctionSignal(Junction j)
        {
            Arm main = null;
            foreach (var a in j.arms)
                if (a.road.rank >= 1 && (main == null || a.road.rank > main.road.rank)) main = a;
            if (main == null) return;
            int index = signals.Count;
            signals.Add(new TrafficLights.Signal { offset = Hash(j.p) * 40f, pedestrian = false });
            int count = 0;
            foreach (var a in j.arms)
            {
                if (a.road.rank < 1) continue;
                float d = j.clear + 0.6f;
                // Pedestrian lanes over every arm, stop line + light for the incoming lanes.
                if (Along(a, d, out var q0, out var t0)) PedestrianLanes(a.road, q0 + t0 * 1.1f, t0, 1.1f);
                if (!a.incoming || !Along(a, d + 3.2f, out var q, out var tan)) continue;
                var h = -tan;
                StopLine(a.road, q, h, false);
                int group = Mathf.Abs(Vector2.Dot(a.dir, main.dir)) > 0.7f ? 0 : 1;
                SignalPole(q + Right(h) * (a.road.Hw + KerbGap), tan, index, group);
                count++;
            }
            if (count > 0) st.junctionSignals++;
        }

        static void MidblockSignal(Road road, Vector2 q, Vector2 tan)
        {
            if (!bounds.Contains(q, 2f)) return;
            int index = signals.Count;
            signals.Add(new TrafficLights.Signal { offset = Hash(q) * 40f, pedestrian = true });
            PedestrianLanes(road, q, tan, 1.6f);
            foreach (var h in new[] { tan, -tan })
            {
                if (road.oneway != 0 && Vector2.Dot(h, tan) * road.oneway < 0f) continue;
                var stop = q - h * 3.4f;
                StopLine(road, stop, h, false);
                SignalPole(stop + Right(h) * (road.Hw + KerbGap), -h, index, 0);
            }
            st.crossingSignals++;
        }

        /// <param name="facing">Direction the lamps look (towards the approaching driver).</param>
        static void SignalPole(Vector2 pos, Vector2 facing, int signal, int group)
        {
            if (carriageways.IsBlocked(pos, 0.2f)) carriageways.PushOut(ref pos, 0.3f);
            poles.Add(pos);
            var b = sink.Get(WorldStyle.MatProps, pos);
            FurniturePlacer.Cylinder(b, V(pos), 0.07f, 3.25f, 8, Pole, Pole);
            var f = V(facing).normalized;
            float yaw = Mathf.Atan2(facing.x, facing.y) * Mathf.Rad2Deg;
            var centre = V(pos, 2.75f) + f * 0.16f;
            FurniturePlacer.Box(b, centre, new Vector3(0.34f, 0.98f, 0.24f), yaw, Housing);
            // Contrast board behind the housing (white rim) and a sun visor above each lamp.
            FurniturePlacer.Box(b, centre - f * 0.13f, new Vector3(0.56f, 1.16f, 0.02f), yaw, White);
            FurniturePlacer.Box(b, centre - f * 0.12f, new Vector3(0.48f, 1.08f, 0.02f), yaw, Housing);
            var rot = Quaternion.Euler(0f, yaw, 0f);
            for (int k = -1; k <= 1; k++)
                FurniturePlacer.Box(b, centre + rot * new Vector3(0f, k * 0.29f + 0.13f, 0.18f), new Vector3(0.24f, 0.02f, 0.12f), yaw, Housing);
            heads.Add(new TrafficLights.Head { position = centre + f * 0.125f, yaw = yaw, signal = signal, group = group });
            st.heads++;
        }

        static float Hash(Vector2 p)
        {
            uint h = (uint)(Mathf.RoundToInt(p.x * 10f) * 73856093) ^ (uint)(Mathf.RoundToInt(p.y * 10f) * 19349663);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xFFFF) / 65535f;
        }

        // ================================================================ markings

        static void Bar(Road road, Vector2 a, Vector2 b, float halfWidth, float lift = 0.016f)
        {
            if (!bounds.Contains(a) || !bounds.Contains(b)) return;
            var d = b - a;
            if (d.sqrMagnitude < 1e-4f) return;
            var n = new Vector2(-d.y, d.x).normalized * halfWidth;
            float y = road.y + lift;
            MeshSink.Quad(sink.Get(WorldStyle.MatGround, (a + b) * 0.5f), V(a + n, y), V(b + n, y), V(b - n, y), V(a - n, y), Marking);
        }

        static void Zebra(Road road, Vector2 q, Vector2 tan)
        {
            var n = new Vector2(-tan.y, tan.x);
            float hw = road.Hw - 0.25f;
            for (float s = -hw + 0.25f; s <= hw - 0.2f; s += 1.0f)
                Bar(road, q + n * s - tan * 2f, q + n * s + tan * 2f, 0.25f);
            foreach (var h in new[] { tan, -tan })
            {
                if (road.oneway != 0 && Vector2.Dot(h, tan) * road.oneway < 0f) continue;
                PlacePole(q - h * 2.6f + Right(h) * (road.Hw + KerbGap), -h, s => Crosswalk(s));
            }
            st.zebras++;
        }

        /// <summary>Two dashed lines across the road (Fußgängerfurt), <paramref name="half"/> metres either side of q.</summary>
        static void PedestrianLanes(Road road, Vector2 q, Vector2 tan, float half)
        {
            var n = new Vector2(-tan.y, tan.x);
            foreach (float off in new[] { -half, half })
            {
                var c = q + tan * off;
                for (float s = -road.Hw + 0.2f; s < road.Hw - 0.3f; s += 1.0f)
                    Bar(road, c + n * s, c + n * (s + 0.5f), 0.12f);
            }
        }

        /// <summary>Line across the right half of the road for traffic heading <paramref name="h"/>.</summary>
        static void StopLine(Road road, Vector2 q, Vector2 h, bool dashed)
        {
            var r = Right(h);
            float from = road.oneway != 0 ? -road.Hw + 0.2f : 0.1f, to = road.Hw - 0.2f;
            if (!dashed) { Bar(road, q + r * from, q + r * to, 0.25f); return; }
            for (float s = from; s < to - 0.2f; s += 0.75f)
                Bar(road, q + r * s, q + r * Mathf.Min(s + 0.5f, to), 0.2f);
        }

        // ================================================================ sign placement

        /// <summary>Sign on the right kerb of traffic approaching the junction on arm a, at distance d from the centre.</summary>
        static void PlaceSign(Junction j, Arm a, float d, System.Action<Plate> front)
        {
            if (!Along(a, d, out var q, out var tan)) return;
            var h = -tan;
            PlacePole(q + Right(h) * (a.road.Hw + KerbGap), tan, front);
        }

        /// <summary>Sign for traffic leaving the vertex into arm a (right kerb, facing back to the vertex).</summary>
        static void PlaceEntering(Arm a, float d, System.Action<Plate> front, System.Action<Plate> back)
        {
            if (!Along(a, d, out var q, out var tan)) return;
            PlacePole(q + Right(tan) * (a.road.Hw + KerbGap), -tan, front, back);
        }

        static void ControlSign(Junction j, Arm a)
        {
            bool stop = a.control == "stop";
            if (Along(a, j.clear + 0.4f, out var q, out var tan)) StopLine(a.road, q, -tan, !stop);
            PlaceSign(j, a, j.clear + 1.6f, stop ? (System.Action<Plate>)(s => Stop(s)) : s => GiveWay(s));
            if (stop) st.stops++; else st.giveWays++;
        }

        /// <summary>A sign pole at pos; the front plate faces <paramref name="facing"/>, optional second plate on the back.</summary>
        static void PlacePole(Vector2 pos, Vector2 facing, System.Action<Plate> front, System.Action<Plate> back = null)
        {
            if (!bounds.Contains(pos, 1f)) return;
            if (carriageways.IsBlocked(pos, 0.15f) && !carriageways.PushOut(ref pos, 0.25f)) return;
            // Don't stack poles: an existing one closer than 0.9 m takes the plate as an extra sign below.
            float plateY = SignHeight;
            foreach (var o in poles)
                if ((o - pos).sqrMagnitude < 0.8f) { pos = o; plateY = SignHeight - 0.75f; break; }
            if (plateY == SignHeight) poles.Add(pos);
            var b = sink.Get(WorldStyle.MatProps, pos);
            if (plateY == SignHeight) FurniturePlacer.Cylinder(b, V(pos), 0.035f, SignHeight + 0.45f, 6, Pole, Pole);
            facing = facing.normalized;
            front(new Plate(b, V(pos, plateY), facing, 0.05f));
            back?.Invoke(new Plate(b, V(pos, plateY), -facing, 0.05f));
        }

        // ================================================================ sign faces

        /// <summary>Drawing surface of one sign face: local x right, y up, front towards <see cref="facing"/>.</summary>
        class Plate
        {
            readonly MeshSink.Batch b;
            readonly Vector3 origin, f, right;
            public readonly Vector2 facing;
            float z;

            public Plate(MeshSink.Batch batch, Vector3 centre, Vector2 facing, float offset)
            {
                b = batch;
                this.facing = facing;
                f = V(facing);
                right = Vector3.Cross(Vector3.up, f);      // viewer's right when looking at the face
                right = -right;
                origin = centre + f * offset;
                z = 0f;
            }

            Vector3 P(Vector2 l, float dz) => origin + right * l.x + Vector3.up * l.y + f * dz;

            /// <summary>Convex polygon (counter-clockwise as seen by the viewer), each call a layer in front of the last.</summary>
            public void Poly(Color32 c, params Vector2[] pts)
            {
                z += 0.004f;
                for (int i = 1; i < pts.Length - 1; i++) Tri(pts[0], pts[i], pts[i + 1], c, z);
                if (z <= 0.0041f) Backing(pts);
            }

            void Tri(Vector2 a, Vector2 c1, Vector2 c2, Color32 c, float dz)
            {
                // CCW for the viewer == clockwise in Unity's front-face sense once mirrored by 'right'.
                var pa = P(a, dz); var p1 = P(c1, dz); var p2 = P(c2, dz);
                if (Vector3.Dot(Vector3.Cross(p1 - pa, p2 - pa), f) < 0f) MeshSink.Tri(b, pa, p2, p1, c);
                else MeshSink.Tri(b, pa, p1, p2, c);
            }

            /// <summary>Grey back of the first (outline) layer.</summary>
            void Backing(Vector2[] pts)
            {
                for (int i = 1; i < pts.Length - 1; i++)
                {
                    var pa = P(pts[0], 0f); var p1 = P(pts[i], 0f); var p2 = P(pts[i + 1], 0f);
                    if (Vector3.Dot(Vector3.Cross(p1 - pa, p2 - pa), -f) < 0f) MeshSink.Tri(b, pa, p2, p1, Back);
                    else MeshSink.Tri(b, pa, p1, p2, Back);
                }
            }

            public void Disc(float r, Color32 c, int sides = 18, Vector2 o = default) => Poly(c, Ngon(r, sides, 0f, o));
            public void Ngon(float r, int sides, float rotDeg, Color32 c, Vector2 o = default) => Poly(c, Ngon(r, sides, rotDeg, o));
            public void Rect(float cx, float cy, float w, float h, Color32 c) =>
                Poly(c, new Vector2(cx - w / 2, cy - h / 2), new Vector2(cx + w / 2, cy - h / 2), new Vector2(cx + w / 2, cy + h / 2), new Vector2(cx - w / 2, cy + h / 2));

            /// <summary>Line segment of given thickness on the face (rotated rectangle).</summary>
            public void Line(Vector2 a, Vector2 c, float thick, Color32 col)
            {
                var d = (c - a).normalized;
                var n = new Vector2(-d.y, d.x) * thick * 0.5f;
                Poly(col, a - n, c - n, c + n, a + n);
            }

            static Vector2[] Ngon(float r, int sides, float rotDeg, Vector2 o)
            {
                var v = new Vector2[sides];
                for (int i = 0; i < sides; i++)
                {
                    float a = (rotDeg + i * 360f / sides) * Mathf.Deg2Rad;
                    v[i] = o + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                }
                return v;
            }

            public void Text(string s, float y, float height, Color32 c, float x = 0f)
            {
                texts.Add(new TextItem { pos = P(new Vector2(x, y), z + 0.006f), facing = facing, text = s, color = c, height = height });
            }
        }

        static void Speed(Plate s, int kmh)
        {
            s.Disc(0.32f, Red);
            s.Disc(0.255f, White);
            s.Text(kmh.ToString(CultureInfo.InvariantCulture), 0f, 0.26f, Black);
        }

        static void Zone(Plate s, int kmh, bool end)
        {
            s.Rect(0f, 0f, 0.62f, 0.8f, end ? Back : Black);
            s.Rect(0f, 0f, 0.58f, 0.76f, White);
            s.Disc(0.22f, end ? Back : Red, 18, new Vector2(0f, 0.1f));
            s.Disc(0.17f, White, 18, new Vector2(0f, 0.1f));
            s.Text(kmh.ToString(CultureInfo.InvariantCulture), 0.1f, 0.17f, end ? Back : Black);
            s.Text("ZONE", -0.25f, 0.11f, end ? Back : Black);
            if (end) s.Line(new Vector2(-0.26f, -0.34f), new Vector2(0.26f, 0.34f), 0.04f, Black);
        }

        static void Stop(Plate s)
        {
            s.Ngon(0.33f, 8, 22.5f, White);
            s.Ngon(0.3f, 8, 22.5f, Red);
            s.Text("STOP", 0f, 0.14f, White);
        }

        static void GiveWay(Plate s)
        {
            // Triangle pointing down.
            s.Poly(Red, new Vector2(0f, -0.38f), new Vector2(0.4f, 0.31f), new Vector2(-0.4f, 0.31f));
            s.Poly(White, new Vector2(0f, -0.23f), new Vector2(0.26f, 0.22f), new Vector2(-0.26f, 0.22f));
        }

        static void Danger(Plate s, string glyph)
        {
            s.Poly(Red, new Vector2(-0.4f, -0.3f), new Vector2(0.4f, -0.3f), new Vector2(0f, 0.39f));
            s.Poly(White, new Vector2(-0.26f, -0.22f), new Vector2(0.26f, -0.22f), new Vector2(0f, 0.24f));
            s.Text(glyph, -0.04f, 0.2f, Black);
        }

        static void Priority(Plate s)
        {
            s.Ngon(0.34f, 4, 0f, White);
            s.Ngon(0.3f, 4, 0f, Black);
            s.Ngon(0.27f, 4, 0f, White);
            s.Ngon(0.2f, 4, 0f, Yellow);
        }

        static void Crosswalk(Plate s)
        {
            s.Rect(0f, 0f, 0.6f, 0.6f, Blue);
            s.Poly(White, new Vector2(-0.26f, -0.21f), new Vector2(0.26f, -0.21f), new Vector2(0f, 0.25f));
            // Walking figure and zebra bars.
            s.Rect(0f, -0.16f, 0.36f, 0.025f, Black);
            s.Line(new Vector2(-0.02f, -0.02f), new Vector2(0.02f, 0.1f), 0.05f, Black);
            s.Line(new Vector2(-0.02f, -0.02f), new Vector2(-0.07f, -0.13f), 0.035f, Black);
            s.Line(new Vector2(-0.02f, -0.02f), new Vector2(0.05f, -0.13f), 0.035f, Black);
            s.Disc(0.032f, Black, 8, new Vector2(0.03f, 0.15f));
        }

        static void NoEntry(Plate s)
        {
            s.Disc(0.3f, White);
            s.Disc(0.285f, Red);
            s.Rect(0f, 0f, 0.4f, 0.09f, White);
        }

        /// <summary>Arrow sign; the arrow points along world direction <paramref name="dir"/> as seen on this face.</summary>
        static void OneWay(Plate s, Vector2 dir)
        {
            // Viewer's right on this face: facing rotated -90° (see Plate).
            var viewRight = new Vector2(-s.facing.y, s.facing.x);
            float sx = Vector2.Dot(viewRight, dir) >= 0f ? 1f : -1f;
            s.Rect(0f, 0f, 0.92f, 0.3f, White);
            s.Rect(0f, 0f, 0.88f, 0.26f, Blue);
            s.Rect(-0.08f * sx, 0f, 0.5f, 0.07f, White);
            s.Poly(White, sx > 0 ? new[] { new Vector2(0.17f, -0.1f), new Vector2(0.36f, 0f), new Vector2(0.17f, 0.1f) }
                                 : new[] { new Vector2(-0.17f, 0.1f), new Vector2(-0.36f, 0f), new Vector2(-0.17f, -0.1f) });
        }

        static void BlueDisc(Plate s, string glyph)
        {
            s.Disc(0.3f, White);
            s.Disc(0.285f, Blue);
            s.Text(glyph, 0f, 0.2f, White);
        }

        static void InfoRect(Plate s, string top, string bottom, Color32 bg, Color32 fg)
        {
            s.Rect(0f, 0f, 0.62f, 0.8f, White);
            s.Rect(0f, 0f, 0.56f, 0.74f, bg);
            s.Text(top, 0.12f, 0.13f, fg);
            if (bottom != null) s.Text(bottom, -0.14f, 0.1f, fg);
        }

        static void CityLimit(Plate s, string name)
        {
            float w = Mathf.Clamp(0.12f * name.Length + 0.4f, 1.0f, 1.8f);
            s.Rect(0f, 0f, w, 0.8f, Black);
            s.Rect(0f, 0f, w - 0.06f, 0.74f, Yellow);
            s.Text(name, 0.05f, 0.22f, Black);
            s.Text("Landkreis Ammerland", -0.22f, 0.07f, Black);
        }

        static void DeadEnd(Plate s)
        {
            s.Rect(0f, 0f, 0.6f, 0.6f, White);
            s.Rect(0f, 0f, 0.56f, 0.56f, Blue);
            s.Rect(0f, -0.06f, 0.1f, 0.34f, White);
            s.Rect(0f, 0.14f, 0.3f, 0.09f, Red);
        }

        static void NameSign(Vector2 pos, Arm a, Arm b)
        {
            poles.Add(pos);
            var batch = sink.Get(WorldStyle.MatProps, pos);
            FurniturePlacer.Cylinder(batch, V(pos), 0.04f, 2.95f, 6, Pole, Pole);
            float y = 2.75f;
            foreach (var arm in new[] { a, b })
            {
                string name = arm.road.name;
                float w = Mathf.Clamp(0.085f * name.Length + 0.2f, 0.6f, 2.2f);
                // Blade parallel to its street: faces point across the street.
                var n = new Vector2(-arm.dir.y, arm.dir.x);
                var centre = V(pos, y) + V(arm.dir) * (w * 0.5f - 0.1f);
                foreach (var face in new[] { n, -n })
                {
                    var s = new Plate(batch, centre, face, 0.012f);
                    s.Rect(0f, 0f, w, 0.24f, Black);
                    s.Rect(0f, 0f, w - 0.03f, 0.21f, White);
                    s.Text(name, 0f, 0.13f, Black);
                }
                y -= 0.3f;
            }
        }

        static bool MappedSign(List<Road> roads, Vector2 p, Dictionary<string, string> t, string sign, string town)
        {
            string code = sign.Split(',', ';')[0].Trim();
            System.Action<Plate> draw = null, back = null;
            string bracket = null;
            int bi = code.IndexOf('[');
            if (bi >= 0) { bracket = code.Substring(bi + 1).TrimEnd(']'); code = code.Substring(0, bi); }
            int speed = 0;
            if (code == "maxspeed" || code == "DE:274") int.TryParse(bracket ?? t.Get("maxspeed") ?? "", out speed);
            switch (code)
            {
                case "city_limit": string name = t.Get("name") ?? town; draw = s => CityLimit(s, name); break;
                case "DE:274.1": draw = s => Zone(s, 30, false); back = s => Zone(s, 30, true); break;
                case "DE:274.2": draw = s => Zone(s, 30, true); break;
                case "DE:267": draw = s => NoEntry(s); break;
                case "DE:101": draw = s => Danger(s, "!"); break;
                case "DE:205": draw = s => GiveWay(s); break;
                case "DE:206": draw = s => Stop(s); break;
                case "DE:306": draw = s => Priority(s); break;
                case "DE:350": draw = s => Crosswalk(s); break;
                case "DE:357": draw = s => DeadEnd(s); break;
                case "DE:237": draw = s => BlueDisc(s, "Rad"); break;
                case "DE:239": draw = s => BlueDisc(s, "Fuß"); break;
                case "DE:240": case "DE:241": draw = s => BlueDisc(s, "Fuß/Rad"); break;
                case "DE:242.1": draw = s => InfoRect(s, "Fußgänger", "zone", Blue, White); break;
                case "DE:244.1": draw = s => InfoRect(s, "Fahrrad", "straße", Blue, White); break;
                case "DE:325.1": draw = s => InfoRect(s, "Verkehrs-", "beruhigt", Blue, White); break;
                case "maxspeed": case "DE:274": if (speed >= 5 && speed <= 130) draw = s => Speed(s, speed); break;
            }
            if (draw == null) return false;

            // Direction of the traffic the sign is meant for.
            NearestRoad(roads, p, 15f, 0, out var road, out var q, out var tan);
            var off = p - q;
            Vector2 h;
            string dir = t.Get("traffic_sign:direction") ?? t.Get("direction");
            if (road != null && (dir == "forward" || dir == "backward"))
            {
                // Forward = along the way the node belongs to; we only know the nearest road – good enough.
                h = dir == "forward" ? tan : -tan;
            }
            else if (TryCompass(dir, out float deg)) h = new Vector2(Mathf.Sin(deg * Mathf.Deg2Rad), Mathf.Cos(deg * Mathf.Deg2Rad));
            else if (road != null && off.magnitude > 0.5f) h = Vector2.Dot(Right(tan), off) >= 0f ? tan : -tan;
            else h = road != null ? tan : Vector2.up;
            var pos = p;
            if (road != null)
            {
                // Keep it on the right kerb of that traffic.
                float side = Vector2.Dot(off, Right(h));
                if (side < road.Hw + 0.3f) pos = q + Right(h) * (road.Hw + KerbGap);
            }
            PlacePole(pos, -h, draw, back);
            return true;
        }

        static bool TryCompass(string dir, out float deg)
        {
            deg = 0f;
            if (string.IsNullOrEmpty(dir)) return false;
            if (float.TryParse(dir, NumberStyles.Float, CultureInfo.InvariantCulture, out deg)) return true;
            string[] names = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
            int i = System.Array.IndexOf(names, dir.ToUpperInvariant());
            if (i < 0) return false;
            deg = i * 22.5f;
            return true;
        }

        // ================================================================ scene objects

        /// <summary>
        /// Text from other builders (after <see cref="Build"/>, which clears the list), spawned with the sign texts.
        /// <paramref name="pos"/>.y is relative to the terrain below; the text faces <paramref name="facing"/>.
        /// </summary>
        public static void AddText(Vector3 pos, Vector2 facing, string text, Color32 color, float height) =>
            texts.Add(new TextItem { pos = pos, facing = facing, text = text, color = color, height = height });

        const string TextMaterialPath = "Assets/Generated/Materials/SignText.mat";

        internal static Material TextMaterial(Font font)
        {
            var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(TextMaterialPath);
            var shader = Shader.Find("LeGamez/SignText");
            if (mat == null)
            {
                mat = new Material(shader) { name = "SignText" };
                OsmAreaImporter.EnsureFolder("Assets/Generated/Materials");
                UnityEditor.AssetDatabase.CreateAsset(mat, TextMaterialPath);
            }
            mat.shader = shader;
            mat.mainTexture = font.material.mainTexture;
            UnityEditor.EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>Lamp positions/phases and sign texts for AreaData (terrain heights applied); built at runtime
        /// by TrafficLights and SignTexts.</summary>
        public static void Export(TerrainField terrain, List<TrafficLights.Head> outHeads, List<TrafficLights.Signal> outSignals,
            List<SignTexts.Item> outTexts)
        {
            outHeads.Clear(); outSignals.Clear(); outTexts.Clear();
            outSignals.AddRange(signals);
            foreach (var h in heads)
            {
                var c = h;
                c.position.y += terrain.Height(c.position.x, c.position.z);
                outHeads.Add(c);
            }
            foreach (var t in texts)
            {
                var pos = t.pos;
                pos.y += terrain.Height(pos.x, pos.z);
                outTexts.Add(new SignTexts.Item { text = t.text, position = pos, facing = t.facing, height = t.height, color = t.color });
            }
        }
    }
}
