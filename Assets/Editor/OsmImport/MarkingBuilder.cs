using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Road markings by rule (after the German RMS), generated from chained centre lines – not per OSM way:
    /// <list type="bullet">
    /// <item>Ways of the same class are chained through nodes where exactly two of their ends meet; dash phases run along
    /// the chain (no restart at way splits, tiles or bridges). Each way contributes its own cross section (lanes, aux lane,
    /// built-up or not); lines at the same offset continue seamlessly across way joints (mitred offset curve).</item>
    /// <item>Motorway (and one-way trunk): edge lines 0.30 m (left; right between the lanes and the hard shoulder),
    /// Leitlinien 6 m / 12 m, 0.15 m, from OSM <c>lanes</c>. Ein-/Ausfädelstreifen (lanes = n+1 with the last lane
    /// slight_right / merge_to_left, OSM line right_of:1): Breitstrich 6 m / 6 m, 0.30 m, between the main lanes and the
    /// aux lane, solid edge line outside it. Where a ramp leaves/joins, the right edge line turns into the Breitstrich as
    /// long as it lies in the ramp; the ramp's lines start where they leave the main lanes (gore tip), the gore between
    /// them is hatched (Sperrfläche) until the carriageways separate. Ramps: 0.30 m edge lines both sides.</item>
    /// <item>Two-way roads: centre Leitlinie on primary…tertiary (unclassified only with lanes ≥ 2): rural 4 m / 8 m,
    /// built-up 3 m / 6 m; solid with overtaking=no or ≥ 4 lanes. Approaching a road of higher rank: Warnlinie
    /// (rural 6/3, built-up 3/1.5) then a solid Fahrstreifenbegrenzung (rural 30 m, built-up 15 m). Edge lines (0.15 m)
    /// outside built-up areas (maxspeed &gt; 50, else aerial "open country"). Residential/service/living streets, tracks,
    /// paths: no markings.</item>
    /// <item>Junctions: lines stop inside another carriageway's marked area. Rank decides: the minor road's lines end at
    /// the major road's edge; the major road's centre/lane lines run through, its edge line is interrupted only on the side
    /// the minor road leaves; equal ranks interrupt each other. Roads on different levels (bridges, layers) without a
    /// shared node don't interact.</item>
    /// <item>Bridges: the same lines on the deck surface (<see cref="BridgeBuilder.Deck.HeightAt"/>, also over the deck's
    /// extension onto the approach); everything else is draped like the road ribbons.</item>
    /// </list>
    /// No shared random numbers – object ids stay as they are.
    /// </summary>
    public static class MarkingBuilder
    {
        public struct Stats { public int chains, gores, stripes; public float km; }

        enum Kind : byte { Edge, Centre, Lane, Aux }
        enum State : byte { Hidden, Normal, Solid, Warn, Block }

        struct LineDef
        {
            public float offset, half;  // left of travel (chain direction); half width
            public Kind kind;
            public float dash, gap;     // gap 0 = solid
            public float warnDash, warnGap, approach, warnLength;
        }

        class WayMarks
        {
            public OsmWay way;
            public float hw, y, bandL, bandR; // band: marked area (left/right offsets) – other roads' lines stop in it
            public int rank, level;
            public bool oneway, rural, blockable, link;
            public readonly List<LineDef> lines = new List<LineDef>();
        }

        class Chain
        {
            public int id, rank;
            public bool oneway, blockable, link;
            public readonly List<WayMarks> ways = new List<WayMarks>();
            public readonly List<bool> reversed = new List<bool>();
            public readonly List<float> wayS0 = new List<float>(), wayS1 = new List<float>();
            public readonly List<Vector2> pts = new List<Vector2>();
            public readonly List<float> s = new List<float>();
            public readonly List<int> segWay = new List<int>();  // way index per segment
            public readonly HashSet<Vector2Int> keys = new HashSet<Vector2Int>();
            public readonly List<float> junctions = new List<float>(); // arc positions where a road of higher rank joins
            public readonly List<(float s0, float s1)> deckRanges = new List<(float, float)>(); // arcs on/near bridge decks
            public float Length => s[s.Count - 1];
        }

        const float Step = 0.5f;           // sampling of the rules along a line
        const float Shoulder = 2.3f;       // motorway hard shoulder right of the edge line
        const float MotorwayEdgeIn = 0.4f; // motorway edge line centre inside the asphalt edge
        const float RoadEdgeIn = 0.35f;
        static readonly Color32 Paint = WorldStyle.LaneMarking;

        static Vector2Int Key(Vector2 p) => new Vector2Int(Mathf.RoundToInt(p.x * 10f), Mathf.RoundToInt(p.y * 10f));
        static Vector3 V(Vector2 p, float y) => new Vector3(p.x, y, p.y);
        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static List<Chain> chains;
        static Dictionary<Vector2Int, List<(int c, int j)>> grid; // chain segments per cell
        const float Cell = 20f;
        static readonly Dictionary<long, bool> shared = new Dictionary<long, bool>();
        static readonly Dictionary<(int, int, Vector2Int), int> sides = new Dictionary<(int, int, Vector2Int), int>();
        static readonly Dictionary<(int, int), List<(Vector2 p, Vector2Int k)>> nodes = new Dictionary<(int, int), List<(Vector2, Vector2Int)>>();
        static readonly HashSet<(int c, int r, int side)> gorePairs = new HashSet<(int, int, int)>();
        static List<(Vector2 a, Vector2 b, float hw, BridgeBuilder.Deck deck, bool start)> deckExt;

        public static Stats Build(OsmData data, MeshSink sink, AreaRect area)
        {
            var st = new Stats();
            shared.Clear(); sides.Clear(); nodes.Clear(); gorePairs.Clear();
            var marks = new List<WayMarks>();
            foreach (var w in data.ways)
            {
                if (w.points.Count < 2 || !GroundBuilder.Carriageway(w.tags, out float width, out float y)) continue;
                int rank = Rank(w.tags.Get("highway"));
                if (rank < 1) continue; // service roads: no markings, they don't interrupt others
                marks.Add(Profile(w, width, y, rank));
            }
            BuildChains(marks);
            BuildGrid();
            FindJunctions();
            PrepareDecks();

            foreach (var c in chains)
            {
                bool any = false;
                for (int wi = 0; wi < c.ways.Count; wi++)
                    foreach (var def in c.ways[wi].lines)
                    {
                        var d = c.reversed[wi] ? Flip(def) : def;
                        st.km += Line(sink, area, c, wi, d) / 1000f;
                        any = true;
                    }
                if (any) st.chains++;
            }
            foreach (var (ci, ri, side) in gorePairs)
            {
                int n = Gore(sink, area, chains[ci], chains[ri], side);
                if (n > 0) { st.gores++; st.stripes += n; }
            }
            chains = null; grid = null; deckExt = null;
            return st;
        }

        // ---------------------------------------------------------------- cross sections

        static int Rank(string hw)
        {
            switch (hw)
            {
                case "motorway": return 8;
                case "trunk": return 7;
                case "primary": return 6;
                case "secondary": return 5;
                case "tertiary": return 4;
                case "motorway_link": case "trunk_link": case "primary_link": case "secondary_link": case "tertiary_link": return 3;
                case "unclassified": case "residential": return 2;
                case "living_street": case "road": return 1;
                default: return 0;
            }
        }

        static int Lanes(Dictionary<string, string> t, string key, int fallback)
        {
            var v = t.Get(key);
            return v != null && int.TryParse(v.Split(';')[0].Trim(), out int n) && n > 0 ? n : fallback;
        }

        static bool IsOneway(Dictionary<string, string> t)
        {
            string hw = t.Get("highway"), ow = t.Get("oneway");
            if (ow == "no") return false;
            return ow == "yes" || ow == "1" || ow == "true" || ow == "-1" || hw == "motorway" || t.Is("junction", "roundabout") || t.Is("junction", "circular");
        }

        /// <summary>Outside built-up areas: maxspeed above 50 (or a rural zone), else the aerial image's open country.</summary>
        static bool IsRural(OsmWay w)
        {
            var t = w.tags;
            string hw = t.Get("highway");
            if (hw == "motorway" || hw == "motorway_link") return true;
            string ms = t.Get("maxspeed");
            if (ms != null)
            {
                if (ms == "none" || ms.EndsWith(":rural") || ms.EndsWith(":motorway") || ms.EndsWith(":trunk")) return true;
                if (ms.EndsWith(":urban") || ms.Contains("zone") || ms.EndsWith(":living_street")) return false;
                if (int.TryParse(ms.Split(' ')[0], out int v)) return v > 50;
            }
            var img = AerialImage.Current;
            if (img == null) return false;
            int open = 0, all = 0;
            var pts = w.points;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                float len = Vector2.Distance(pts[i], pts[i + 1]);
                int n = Mathf.Max(1, Mathf.CeilToInt(len / 20f));
                for (int k = 0; k < n; k++)
                {
                    var p = Vector2.Lerp(pts[i], pts[i + 1], (k + 0.5f) / n);
                    all++; if (img.Open(p.x, p.y)) open++;
                }
            }
            return all > 0 && open >= 0.6f * all;
        }

        static int Level(Dictionary<string, string> t)
        {
            var l = t.Get("layer");
            if (l != null && int.TryParse(l, out int v)) return v;
            return t.Has("bridge") && !t.Is("bridge", "no") ? 1 : 0;
        }

        static LineDef Solid(float offset, float half, Kind kind) => new LineDef { offset = offset, half = half, kind = kind };
        static LineDef Dashed(float offset, float half, Kind kind, float dash, float gap) =>
            new LineDef { offset = offset, half = half, kind = kind, dash = dash, gap = gap };

        static LineDef Flip(LineDef d) { d.offset = -d.offset; return d; }

        static WayMarks Profile(OsmWay w, float width, float y, int rank)
        {
            var t = w.tags;
            string hw = t.Get("highway");
            var m = new WayMarks { way = w, hw = width * 0.5f, y = y + 0.012f, rank = rank, level = Level(t), oneway = IsOneway(t) };
            m.rural = IsRural(w);
            m.link = hw.EndsWith("_link");
            m.bandL = m.hw - 0.2f; m.bandR = -(m.hw - 0.2f);
            if (t.Is("lane_markings", "no") || t.Is("area", "yes")) return m;
            bool roundabout = t.Is("junction", "roundabout") || t.Is("junction", "circular");
            float h = m.hw;

            if ((hw == "motorway" || hw == "trunk") && m.oneway)
            {
                // Motorway carriageway: edge lines, lanes; an Ein-/Ausfädelstreifen as the last lane on the right.
                m.blockable = true;
                int lanes = Mathf.Clamp(Lanes(t, "lanes", 2), 1, 6);
                string turn = t.Get("turn:lanes") ?? "", place = t.Get("placement") ?? "";
                string last = turn.Length > 0 ? turn.Substring(turn.LastIndexOf('|') + 1) : "";
                bool aux = lanes >= 3 && (last.Contains("slight_right") || last.Contains("merge_to_left") ||
                                          lanes == 3 && (place == "right_of:1" || place == "left_of:2"));
                int reg = lanes - (aux ? 1 : 0);
                float left = h - MotorwayEdgeIn;
                float right = reg <= 2 || aux ? -(h - Shoulder) : -(h - MotorwayEdgeIn);
                m.lines.Add(Solid(left, 0.15f, Kind.Edge));
                if (aux)
                {
                    m.lines.Add(Dashed(right, 0.15f, Kind.Aux, 6f, 6f));      // Breitstrich to the aux lane
                    m.lines.Add(Solid(-(h - MotorwayEdgeIn), 0.15f, Kind.Edge));
                    m.bandR = -(h - MotorwayEdgeIn) - 0.15f;
                }
                else
                {
                    m.lines.Add(Solid(right, 0.15f, Kind.Edge));
                    m.bandR = right - 0.15f;
                }
                float lane = (left - right) / reg;
                for (int k = 1; k < reg; k++) m.lines.Add(Dashed(left - k * lane, 0.075f, Kind.Lane, 6f, 12f));
                m.bandL = left + 0.15f;
                return m;
            }
            if (m.link && m.oneway)
            {
                int lanes = Mathf.Clamp(Lanes(t, "lanes", 1), 1, 4);
                float e = h - MotorwayEdgeIn;
                m.lines.Add(Solid(e, 0.15f, Kind.Edge));
                m.lines.Add(Solid(-e, 0.15f, Kind.Edge));
                for (int k = 1; k < lanes; k++) m.lines.Add(Dashed(e - k * 2f * e / lanes, 0.075f, Kind.Lane, 6f, 12f));
                m.bandL = e + 0.15f; m.bandR = -e - 0.15f;
                return m;
            }

            bool major = rank >= 4 || m.link;
            bool marked = major || hw == "unclassified" && Lanes(t, "lanes", 1) >= 2;
            if (!marked) return m;
            float dash = m.rural ? 4f : 3f, gap = m.rural ? 8f : 6f;
            float warnDash = m.rural ? 6f : 3f, warnGap = m.rural ? 3f : 1.5f;
            float approach = m.rural ? 30f : 15f, warnLen = m.rural ? 50f : 20f;
            float edge = h - RoadEdgeIn;
            if (m.rural)
            {
                m.lines.Add(Solid(edge, 0.075f, Kind.Edge));
                m.lines.Add(Solid(-edge, 0.075f, Kind.Edge));
                m.bandL = edge + 0.1f; m.bandR = -edge - 0.1f;
            }
            if (roundabout) return m;
            if (m.oneway)
            {
                int lanes = Mathf.Clamp(Lanes(t, "lanes", 1), 1, 4);
                float span = m.rural ? edge : h - 0.2f;
                for (int k = 1; k < lanes; k++) m.lines.Add(Dashed(span - k * 2f * span / lanes, 0.075f, Kind.Lane, dash, gap));
                return m;
            }
            int all = Lanes(t, "lanes", 2);
            if (all < 2) return m; // single-lane road: no centre line
            bool noOvertaking = t.Is("overtaking", "no") || t.Is("overtaking:forward", "no") || t.Is("overtaking:backward", "no");
            var c = Dashed(0f, 0.075f, Kind.Centre, dash, gap);
            c.warnDash = warnDash; c.warnGap = warnGap; c.approach = approach; c.warnLength = warnLen;
            if (noOvertaking || all >= 4) c.gap = 0f;
            m.lines.Add(c);
            if (all >= 4)
            {
                float span = m.rural ? edge : h - 0.2f;
                m.lines.Add(Dashed(span * 0.5f, 0.075f, Kind.Lane, dash, gap));
                m.lines.Add(Dashed(-span * 0.5f, 0.075f, Kind.Lane, dash, gap));
            }
            return m;
        }

        // ---------------------------------------------------------------- chains

        static string ClassKey(WayMarks m) => m.way.tags.Get("highway") + (m.oneway ? "|1" : "|0");

        static void BuildChains(List<WayMarks> marks)
        {
            chains = new List<Chain>();
            // Way ends per node and class; oneway=-1 ways run against their points.
            var ends = new Dictionary<(Vector2Int, string), List<(int way, bool start)>>();
            var rev0 = new bool[marks.Count];
            for (int i = 0; i < marks.Count; i++)
            {
                var m = marks[i];
                rev0[i] = m.way.tags.Is("oneway", "-1");
                if (m.way.IsClosed) continue;
                var p = m.way.points;
                string ck = ClassKey(m);
                foreach (bool start in new[] { true, false })
                {
                    var k = (Key(start ? p[0] : p[p.Count - 1]), ck);
                    if (!ends.TryGetValue(k, out var l)) ends[k] = l = new List<(int, bool)>();
                    l.Add((i, start));
                }
            }
            // Partner of a way end: the only other way end of the same class at that node (one-way: an end meets a start).
            (int way, bool start) Partner(int i, bool start)
            {
                var m = marks[i];
                var p = m.way.points;
                if (!ends.TryGetValue((Key(start ? p[0] : p[p.Count - 1]), ClassKey(m)), out var l) || l.Count != 2) return (-1, false);
                var o = l[0].way == i && l[0].start == start ? l[1] : l[0];
                if (o.way == i) return (-1, false);
                if (m.oneway)
                {
                    // Travel start/end (oneway=-1 flips): an incoming end must meet an outgoing start.
                    bool myIn = start ^ rev0[i], otherIn = o.start ^ rev0[o.way];
                    if (myIn == otherIn) return (-1, false);
                }
                return o;
            }
            var used = new bool[marks.Count];
            for (int i = 0; i < marks.Count; i++)
            {
                if (used[i]) continue;
                // Walk back to the first way of the chain.
                int first = i; bool firstRev = rev0[i];
                var seen = new HashSet<int> { i };
                while (true)
                {
                    // The end at which travel along the chain enters the current first way.
                    bool entryIsStart = !firstRev;
                    var (pw, ps) = Partner(first, entryIsStart);
                    if (pw < 0 || used[pw] || !seen.Add(pw)) break;
                    first = pw;
                    firstRev = ps; // we arrive at the partner's end 'ps': if that is its start, we walk it backwards
                }
                var c = new Chain { id = chains.Count, rank = marks[first].rank, oneway = marks[first].oneway, blockable = marks[first].blockable, link = marks[first].link };
                int cur = first; bool rev = firstRev;
                while (cur >= 0 && !used[cur])
                {
                    used[cur] = true;
                    var m = marks[cur];
                    c.ways.Add(m); c.reversed.Add(rev);
                    var p = m.way.points;
                    int wi = c.ways.Count - 1;
                    c.wayS0.Add(c.s.Count > 0 ? c.s[c.s.Count - 1] : 0f);
                    for (int k = 0; k < p.Count; k++)
                    {
                        var q = p[rev ? p.Count - 1 - k : k];
                        if (c.pts.Count > 0 && k == 0) continue; // shared joint
                        if (c.pts.Count > 0)
                        {
                            float len = Vector2.Distance(c.pts[c.pts.Count - 1], q);
                            if (len < 0.01f) continue;
                            c.s.Add(c.s[c.s.Count - 1] + len);
                            c.segWay.Add(wi);
                        }
                        else c.s.Add(0f);
                        c.pts.Add(q);
                        c.keys.Add(Key(q));
                    }
                    c.wayS1.Add(c.s[c.s.Count - 1]);
                    // Leave through the end opposite to where we entered.
                    bool exitIsStart = rev;
                    var (nw, ns) = Partner(cur, exitIsStart);
                    if (nw < 0 || used[nw]) break;
                    cur = nw; rev = !ns; // entering at its start → forward
                }
                if (c.pts.Count >= 2) chains.Add(c);
            }
        }

        static void BuildGrid()
        {
            grid = new Dictionary<Vector2Int, List<(int, int)>>();
            foreach (var c in chains)
            {
                for (int j = 0; j < c.pts.Count - 1; j++)
                {
                    var m = c.ways[c.segWay[j]];
                    float r = Mathf.Max(m.bandL, -m.bandR) + 0.5f;
                    Vector2 a = c.pts[j], b = c.pts[j + 1];
                    var min = Vector2.Min(a, b) - Vector2.one * r;
                    var max = Vector2.Max(a, b) + Vector2.one * r;
                    for (int x = Mathf.FloorToInt(min.x / Cell); x <= Mathf.FloorToInt(max.x / Cell); x++)
                    for (int z = Mathf.FloorToInt(min.y / Cell); z <= Mathf.FloorToInt(max.y / Cell); z++)
                    {
                        var k = new Vector2Int(x, z);
                        if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<(int, int)>();
                        l.Add((c.id, j));
                    }
                }
            }
        }

        /// <summary>Arc positions along each chain where a road of higher rank joins (shared node).</summary>
        static void FindJunctions()
        {
            var byKey = new Dictionary<Vector2Int, int>(); // highest rank at the node
            foreach (var c in chains)
                foreach (var k in c.keys)
                    byKey[k] = byKey.TryGetValue(k, out int r) ? Mathf.Max(r, c.rank) : c.rank;
            foreach (var c in chains)
                for (int i = 0; i < c.pts.Count; i++)
                    if (byKey.TryGetValue(Key(c.pts[i]), out int r) && r > c.rank) c.junctions.Add(c.s[i]);
        }

        /// <summary>Per chain the arc ranges that run over a deck – its own deck ways and other decks' extensions onto
        /// the approach (those pieces are cut to 1 m and put on the deck surface).</summary>
        static void PrepareDecks()
        {
            deckExt = new List<(Vector2, Vector2, float, BridgeBuilder.Deck, bool)>();
            foreach (var d in BridgeBuilder.Decks)
            {
                var p = d.pts;
                if (d.ext0 > 0.01f) deckExt.Add((p[0], p[1], d.width * 0.5f, d, true));
                if (d.ext1 > 0.01f) deckExt.Add((p[p.Count - 1], p[p.Count - 2], d.width * 0.5f, d, false));
            }
            foreach (var c in chains)
                for (int wi = 0; wi < c.ways.Count; wi++)
                    if (BridgeBuilder.IsDeck(c.ways[wi].way)) c.deckRanges.Add((c.wayS0[wi] - 1f, c.wayS1[wi] + 1f));
            foreach (var (a, b, hw, _, _) in deckExt)
            {
                var mid = (a + b) * 0.5f;
                if (!grid.TryGetValue(new Vector2Int(Mathf.FloorToInt(mid.x / Cell), Mathf.FloorToInt(mid.y / Cell)), out var list)) continue;
                var done = new HashSet<int>();
                foreach (var id in list)
                {
                    var c = chains[id.c];
                    if (!done.Add(c.id)) continue;
                    if (!Project(c, a, out float sa, out float la) || !Project(c, b, out float sb, out float lb)) continue;
                    if (Mathf.Abs(la) > hw + 2f && Mathf.Abs(lb) > hw + 2f) continue;
                    c.deckRanges.Add((Mathf.Min(sa, sb) - 2f, Mathf.Max(sa, sb) + 2f));
                }
            }
        }

        static bool InDeckRange(Chain c, float a, float b)
        {
            foreach (var (s0, s1) in c.deckRanges) if (b > s0 && a < s1) return true;
            return false;
        }

        // ---------------------------------------------------------------- geometry helpers

        static int SegmentAt(Chain c, float s)
        {
            int lo = 0, hi = c.s.Count - 2;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (c.s[mid] <= s) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        static Vector2 SegNormal(Chain c, int j)
        {
            var d = (c.pts[j + 1] - c.pts[j]).normalized;
            return new Vector2(-d.y, d.x);
        }

        /// <summary>Point at arc s, lateral offset o (segment normal; mitred at vertices when s hits one).</summary>
        static Vector2 At(Chain c, float s, float o)
        {
            s = Mathf.Clamp(s, 0f, c.Length);
            int j = SegmentAt(c, s);
            float len = c.s[j + 1] - c.s[j];
            float t = len > 0f ? (s - c.s[j]) / len : 0f;
            var p = Vector2.Lerp(c.pts[j], c.pts[j + 1], t);
            if (t < 1e-4f && j > 0) return p + Mitre(c, j) * o;
            if (t > 1f - 1e-4f && j + 1 < c.pts.Count - 1) return p + Mitre(c, j + 1) * o;
            return p + SegNormal(c, j) * o;
        }

        static Vector2 Mitre(Chain c, int i)
        {
            var n0 = SegNormal(c, i - 1); var n1 = SegNormal(c, i);
            var n = n0 + n1;
            if (n.sqrMagnitude < 1e-6f) return n1;
            n.Normalize();
            float cos = Vector2.Dot(n, n1);
            return n / Mathf.Max(0.35f, cos);
        }

        static Vector2 Dir(Chain c, float s)
        {
            int j = SegmentAt(c, Mathf.Clamp(s, 0f, c.Length));
            return (c.pts[j + 1] - c.pts[j]).normalized;
        }

        /// <summary>Nearest point of a chain: arc position and signed lateral offset (left +).</summary>
        static bool Project(Chain c, Vector2 p, out float s, out float lat)
        {
            s = 0f; lat = 0f; float best = float.MaxValue;
            for (int j = 0; j < c.pts.Count - 1; j++)
            {
                Vector2 a = c.pts[j], ab = c.pts[j + 1] - a;
                float l2 = ab.sqrMagnitude;
                if (l2 < 1e-6f) continue;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2);
                var q = a + ab * t;
                float d = (p - q).sqrMagnitude;
                if (d < best)
                {
                    best = d; s = Mathf.Lerp(c.s[j], c.s[j + 1], t);
                    lat = Cross(ab / Mathf.Sqrt(l2), p - a);
                }
            }
            return best < float.MaxValue;
        }

        // ---------------------------------------------------------------- rules

        static bool Shared(Chain a, Chain b)
        {
            long k = a.id < b.id ? (long)a.id << 32 | (uint)b.id : (long)b.id << 32 | (uint)a.id;
            if (shared.TryGetValue(k, out bool v)) return v;
            var (small, big) = a.keys.Count < b.keys.Count ? (a, b) : (b, a);
            v = false;
            foreach (var x in small.keys) if (big.keys.Contains(x)) { v = true; break; }
            shared[k] = v;
            return v;
        }

        /// <summary>On which side of C the road R leaves at their shared node nearest to p: bit 1 = left, bit 2 = right
        /// (3 = both, also when they share no node).</summary>
        static int Sides(Chain c, Chain r, Vector2 p)
        {
            if (!nodes.TryGetValue((c.id, r.id), out var common))
            {
                common = new List<(Vector2, Vector2Int)>();
                for (int i = 0; i < r.pts.Count; i++)
                {
                    var k = Key(r.pts[i]);
                    if (c.keys.Contains(k)) common.Add((r.pts[i], k));
                }
                nodes[(c.id, r.id)] = common;
            }
            if (common.Count == 0) return 3;
            Vector2Int node = default; float best = float.MaxValue;
            foreach (var (q, k) in common)
            {
                float d = (q - p).sqrMagnitude;
                if (d < best) { best = d; node = k; }
            }
            if (sides.TryGetValue((c.id, r.id, node), out int v)) return v;
            int ci = -1;
            for (int i = 0; i < c.pts.Count; i++) if (Key(c.pts[i]) == node) { ci = i; break; }
            var n = c.pts[ci];
            var dirC = (c.pts[Mathf.Min(ci + 1, c.pts.Count - 1)] - c.pts[Mathf.Max(ci - 1, 0)]).normalized;
            v = 0;
            for (int i = 0; i < r.pts.Count; i++)
            {
                if (Key(r.pts[i]) != node) continue;
                foreach (int dir in new[] { 1, -1 })
                {
                    if (dir > 0 && i == r.pts.Count - 1 || dir < 0 && i == 0) continue;
                    float lat = 0f;
                    foreach (float reach in new[] { 30f, 60f })
                    {
                        float target = r.s[i] + dir * reach;
                        var q = At(r, target, 0f);
                        lat = Cross(dirC, q - n);
                        if (Mathf.Abs(lat) > 1.5f) break;
                    }
                    v |= lat >= 0f ? 1 : 2;
                }
            }
            if (v == 0) v = 3;
            sides[(c.id, r.id, node)] = v;
            return v;
        }

        static readonly List<int> hitChains = new List<int>();

        /// <summary>The chains (other than c) whose marked band contains p, on the same level or joined to c.</summary>
        static void Hits(Chain c, int level, Vector2 p)
        {
            hitChains.Clear();
            if (!grid.TryGetValue(new Vector2Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell)), out var list)) return;
            foreach (var id in list)
            {
                int ri = id.c, j = id.j;
                if (ri == c.id || hitChains.Contains(ri)) continue;
                var r = chains[ri];
                var m = r.ways[r.segWay[j]];
                Vector2 a = r.pts[j], ab = r.pts[j + 1] - a;
                float l2 = ab.sqrMagnitude;
                if (l2 < 1e-6f) continue;
                float t = Vector2.Dot(p - a, ab) / l2;
                bool inside = false;
                if (t >= 0f && t <= 1f)
                {
                    float lat = Cross(ab / Mathf.Sqrt(l2), p - a);
                    inside = lat <= m.bandL && lat >= m.bandR;
                }
                // Bends: the wedge at an interior vertex.
                if (!inside && j > 0 && (p - a).sqrMagnitude < Mathf.Min(m.bandL, -m.bandR) * Mathf.Min(m.bandL, -m.bandR)) inside = true;
                if (!inside) continue;
                if (m.level != level && !Shared(c, r)) continue; // over/under each other
                hitChains.Add(ri);
            }
        }

        static State Evaluate(Chain c, WayMarks m, LineDef d, float s, Vector2 p, out int blockBy)
        {
            blockBy = -1;
            Hits(c, m.level, p);
            var state = State.Normal;
            foreach (int ri in hitChains)
            {
                var r = chains[ri];
                if (r.rank > c.rank) { hider = ri; return State.Hidden; }
                bool edge = d.kind == Kind.Edge;
                if (r.rank == c.rank)
                {
                    if (!edge) { hider = ri; return State.Hidden; }
                    if ((Sides(c, r, p) & (d.offset >= 0f ? 1 : 2)) != 0) { hider = ri; return State.Hidden; }
                    continue;
                }
                // A minor road: the major road's centre/lane lines run through, its edge line opens on that side.
                if (!edge) continue;
                if ((Sides(c, r, p) & (d.offset >= 0f ? 1 : 2)) == 0) continue;
                if (c.blockable && r.link) { state = State.Block; blockBy = ri; }
                else { hider = ri; return State.Hidden; }
            }
            if (state == State.Normal && d.kind == Kind.Centre && d.approach > 0f && c.junctions.Count > 0)
            {
                float near = float.MaxValue;
                foreach (float js in c.junctions) near = Mathf.Min(near, Mathf.Abs(js - s));
                if (near < d.approach) return State.Solid;
                if (near < d.approach + d.warnLength) return State.Warn;
            }
            return state;
        }

        // ---------------------------------------------------------------- drawing

        /// <summary>One line of one way of the chain: rules sampled every <see cref="Step"/>, then drawn. Returns metres.</summary>
        static float Line(MeshSink sink, AreaRect area, Chain c, int wi, LineDef d)
        {
            var m = c.ways[wi];
            float s0 = c.wayS0[wi], s1 = c.wayS1[wi];
            if (s1 - s0 < 0.05f) return 0f;
            int n = Mathf.Max(1, Mathf.CeilToInt((s1 - s0) / Step));
            float h = (s1 - s0) / n;
            float drawn = 0f;
            State run = State.Hidden; float runStart = s0;
            bool dbg = Debug_(m.way);
            var log = dbg ? new System.Text.StringBuilder($"[Mark] way {m.way.id} {m.way.tags.Get("highway")} chain {c.id} rank {c.rank} rev {c.reversed[wi]} {d.kind} off {d.offset:0.00} s {s0:0}-{s1:0}:") : null;
            for (int k = 0; k <= n; k++)
            {
                State st = State.Hidden;
                hider = -1;
                if (k < n)
                {
                    float s = s0 + (k + 0.5f) * h;
                    var p = At(c, s, d.offset);
                    if (area.Contains(p))
                    {
                        st = Evaluate(c, m, d, s, p, out int by);
                        if (st == State.Block && by >= 0) gorePairs.Add((c.id, by, d.offset >= 0f ? 1 : -1));
                    }
                }
                if (st == run) continue;
                float sNow = s0 + k * h;
                if (dbg) log.Append($" {st}@{sNow:0.0}" + (hider >= 0 ? $"(by chain {hider} {chains[hider].ways[0].way.id} r{chains[hider].rank})" : ""));
                if (run != State.Hidden) drawn += Pattern(sink, c, m, d, run, runStart, sNow);
                run = st; runStart = sNow;
            }
            if (dbg) Debug.Log(log.ToString());
            return drawn;
        }

        static int hider;
        static Vector2? debugAt;

        /// <summary>
        /// Check without importing (~1 min): -executeMethod LeGamez.Editor.OsmImport.MarkingBuilder.DebugBatch -area &lt;id&gt;
        /// [-at x,z] – builds the markings on the prepared terrain and logs the stats; with -at the rule states of every
        /// line of the ways within 40 m ("[Mark] way … Normal@s Hidden@s(by chain …)").
        /// </summary>
        public static void DebugBatch()
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string k) { int i = System.Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string id = Arg("-area") ?? "westerstede", at = Arg("-at");
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "Data"));
            var data = OsmData.Load(System.IO.Path.Combine(dir, id + ".osm.json"));
            var area = data.meta.bounds;
            AreaMask.Load(dir, id)?.Filter(data);
            var terrain = TerrainField.Load(System.IO.Path.Combine(dir, id + ".dgm.bin"));
            var aerial = AerialImage.Load(dir, id);
            CoastBuilder.Prepare(data, area, terrain, aerial);
            aerial?.SetBuiltUp(data);
            CoarseTerrain.Build(new MeshSink(), area, WorldStyle.GroundColor(data.meta.ground), terrain);
            BridgeBuilder.Prepare(data, area, terrain);
            RoadProfile.Apply(data, area, terrain, id);
            BridgeBuilder.EaseEnds(terrain);
            if (at != null)
            {
                var xz = at.Split(',');
                debugAt = new Vector2(float.Parse(xz[0], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(xz[1], System.Globalization.CultureInfo.InvariantCulture));
            }
            var sink = new MeshSink();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var st = Build(data, sink, area);
            debugAt = null;
            int tris = 0;
            foreach (var kv in sink.Batches) tris += kv.Value.triangles.Count / 3;
            Debug.Log($"[Mark] {id}: {st.chains} chains, {st.km:0.0} km paint, {st.gores} gores ({st.stripes} stripes), {tris} triangles before draping, {sw.ElapsedMilliseconds} ms");
        }

        /// <summary>The way has a point within 40 m of the debug point.</summary>
        static bool Debug_(OsmWay w)
        {
            if (debugAt == null) return false;
            foreach (var p in w.points) if ((p - debugAt.Value).sqrMagnitude < 1600f) return true;
            return false;
        }

        static float Pattern(MeshSink sink, Chain c, WayMarks m, LineDef d, State st, float a, float b)
        {
            float dash = d.dash, gap = d.gap, half = d.half;
            switch (st)
            {
                case State.Solid: gap = 0f; break;
                case State.Warn: dash = d.warnDash; gap = d.warnGap; break;
                case State.Block: dash = 6f; gap = 6f; half = 0.15f; break;
            }
            if (gap <= 0f) { Strip(sink, c, m, d.offset, half, a, b); return b - a; }
            float period = dash + gap, total = 0f;
            // Phase along the whole chain: dashes don't restart at way joints.
            for (float k = Mathf.Floor(a / period) * period; k < b; k += period)
            {
                float u0 = Mathf.Max(a, k), u1 = Mathf.Min(b, k + dash);
                if (u1 - u0 < 0.3f) continue;
                Strip(sink, c, m, d.offset, half, u0, u1);
                total += u1 - u0;
            }
            return total;
        }

        /// <summary>Paint strip between arcs a..b at offset o: split at the chain's vertices (mitred), on the deck surface
        /// where the chain runs over a bridge deck (or its extension, in 1 m pieces), else draped later with the ground.</summary>
        static void Strip(MeshSink sink, Chain c, WayMarks m, float o, float half, float a, float b)
        {
            var cuts = new List<float> { a };
            int j0 = SegmentAt(c, a), j1 = SegmentAt(c, b);
            for (int i = j0 + 1; i <= j1; i++) if (c.s[i] > a + 1e-3f && c.s[i] < b - 1e-3f) cuts.Add(c.s[i]);
            cuts.Add(b);
            for (int i = 0; i < cuts.Count - 1; i++)
            {
                float u0 = cuts[i], u1 = cuts[i + 1];
                bool deckish = InDeckRange(c, u0, u1);
                int pieces = deckish ? Mathf.Max(1, Mathf.CeilToInt((u1 - u0) / 1f)) : 1;
                for (int k = 0; k < pieces; k++)
                {
                    float v0 = Mathf.Lerp(u0, u1, (float)k / pieces), v1 = Mathf.Lerp(u0, u1, (float)(k + 1) / pieces);
                    Vector2 p0 = At(c, v0, o), p1 = At(c, v1, o);
                    // Paint width across the line: mitred at vertices, so consecutive pieces join without gaps.
                    Vector2 n0 = Across(c, v0, half), n1 = Across(c, v1, half);
                    if (deckish && OnDeck(c, v0, v1, out var deck, out float sd0, out float sd1))
                    {
                        float y0 = deck.HeightAt(sd0) + 0.012f, y1 = deck.HeightAt(sd1) + 0.012f;
                        MeshSink.Quad(sink.Get(WorldStyle.MatTerrain, p0), V(p0 + n0, y0), V(p1 + n1, y1), V(p1 - n1, y1), V(p0 - n0, y0), Paint);
                    }
                    else
                        MeshSink.Quad(sink.Get(WorldStyle.MatGround, p0), V(p0 + n0, m.y), V(p1 + n1, m.y), V(p1 - n1, m.y), V(p0 - n0, m.y), Paint);
                }
            }
        }

        /// <summary>Half-width vector across the line at arc s (mitred at vertices, so joints have no gaps).</summary>
        static Vector2 Across(Chain c, float s, float half)
        {
            int j = SegmentAt(c, s);
            float len = c.s[j + 1] - c.s[j];
            float t = len > 0f ? (s - c.s[j]) / len : 0f;
            if (t < 1e-4f && j > 0) return Mitre(c, j) * half;
            if (t > 1f - 1e-4f && j + 1 < c.pts.Count - 1) return Mitre(c, j + 1) * half;
            return SegNormal(c, j) * half;
        }

        /// <summary>The deck under the piece v0..v1 of the chain (its own deck way, or another deck's extension over this
        /// approach road) and the deck arcs of both ends.</summary>
        static bool OnDeck(Chain c, float v0, float v1, out BridgeBuilder.Deck deck, out float sd0, out float sd1)
        {
            sd0 = sd1 = 0f;
            float s = (v0 + v1) * 0.5f;
            int wi = c.segWay[SegmentAt(c, s)];
            if (BridgeBuilder.TryGet(c.ways[wi].way, out deck))
            {
                // The deck's points start ext0 before the way's first point (which lies at wayS1 when walked backwards).
                if (!c.reversed[wi]) { sd0 = deck.ext0 + (v0 - c.wayS0[wi]); sd1 = deck.ext0 + (v1 - c.wayS0[wi]); }
                else { sd0 = deck.ext0 + (c.wayS1[wi] - v0); sd1 = deck.ext0 + (c.wayS1[wi] - v1); }
                sd0 = Mathf.Clamp(sd0, 0f, deck.Length); sd1 = Mathf.Clamp(sd1, 0f, deck.Length);
                return true;
            }
            // Another deck's extension over this (approach) road.
            var p = At(c, s, 0f);
            foreach (var (a, b, hw, d, start) in deckExt)
            {
                Vector2 ab = b - a;
                float l2 = ab.sqrMagnitude;
                if (l2 < 1e-6f) continue;
                float t = Vector2.Dot(p - a, ab) / l2;
                if (t < 0f || t > 1f || Mathf.Abs(Cross(ab / Mathf.Sqrt(l2), p - a)) > hw) continue;
                deck = d;
                var p0 = At(c, v0, 0f); var p1 = At(c, v1, 0f);
                float ext = Mathf.Sqrt(l2);
                float t0 = Mathf.Clamp01(Vector2.Dot(p0 - a, ab) / l2), t1 = Mathf.Clamp01(Vector2.Dot(p1 - a, ab) / l2);
                if (start) { sd0 = t0 * ext; sd1 = t1 * ext; }
                else { sd0 = d.Length - t0 * ext; sd1 = d.Length - t1 * ext; }
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- gores

        /// <summary>
        /// Sperrfläche between a motorway carriageway and a ramp leaving/joining it: from the gore tip (where the ramp's
        /// line leaves the main lanes) diagonal 0.30 m stripes every 5 m until the carriageways separate. Returns stripes.
        /// </summary>
        static int Gore(MeshSink sink, AreaRect area, Chain c, Chain r, int side)
        {
            // The ramp end at the main carriageway.
            int ri = -1;
            if (c.keys.Contains(Key(r.pts[0]))) ri = 0;
            else if (c.keys.Contains(Key(r.pts[r.pts.Count - 1]))) ri = r.pts.Count - 1;
            if (ri < 0) return 0;
            int dir = ri == 0 ? 1 : -1;
            float sStart = r.s[ri];
            var node = r.pts[ri];
            if (!Project(c, node, out float sNodeC, out _)) return 0;
            // The ramp's line facing the main carriageway.
            float probe = Mathf.Clamp(sStart + dir * 40f, 0f, r.Length);
            var rp = At(r, probe, 0f);
            if (!Project(c, rp, out float sc, out _)) return 0;
            var cp = At(c, sc, 0f);
            int facing = Cross(Dir(r, probe), cp - rp) >= 0f ? 1 : -1; // side of r (chain frame) towards c
            float rEdge = float.NaN, cEdge;
            int rwi = r.segWay[SegmentAt(r, probe)];
            var rm = r.ways[rwi];
            foreach (var d0 in rm.lines)
            {
                var d = r.reversed[rwi] ? Flip(d0) : d0;
                if (d.kind == Kind.Edge && (d.offset >= 0f ? 1 : -1) == facing) rEdge = d.offset;
            }
            if (float.IsNaN(rEdge)) return 0;
            float hwR = rm.hw;

            int stripes = 0;
            float tip = float.NaN, lastStripe = float.NegativeInfinity;
            for (float u = 0f; u < 250f; u += Step)
            {
                float s = sStart + dir * u;
                if (s < 0f || s > r.Length) break;
                var pc = At(r, s, 0f);
                if (!Project(c, pc, out float scc, out float latCentre)) break;
                var cm = c.ways[c.segWay[SegmentAt(c, scc)]];
                cEdge = float.NaN;
                foreach (var d in cm.lines)
                {
                    var dd = c.reversed[c.segWay[SegmentAt(c, scc)]] ? Flip(d) : d;
                    if (dd.kind == Kind.Edge && (dd.offset >= 0f ? 1 : -1) == side) cEdge = dd.offset;
                }
                if (float.IsNaN(cEdge)) break;
                var pl = At(r, s, rEdge);
                Project(c, pl, out float sl, out float latLine);
                // Beyond the main edge line (on the ramp's side)?
                bool beyond = side > 0 ? latLine > cEdge + 0.2f : latLine < cEdge - 0.2f;
                if (float.IsNaN(tip)) { if (beyond) tip = u; continue; }
                // Separated: the ramp's asphalt no longer touches the main carriageway.
                if (Mathf.Abs(latCentre) > hwR + cm.hw + 0.5f) break;
                if (u - lastStripe < 5f || u - tip < 2f) continue;
                float gapW = Mathf.Abs(latLine - cEdge);
                if (gapW < 0.8f) continue;
                lastStripe = u;
                // Diagonal: from the ramp's line back towards the tip on the main edge line.
                float back = sl < sNodeC ? 1f : -1f; // along c towards the node
                float sq = sl + back * gapW; // 45°
                if (back > 0f ? sq > sNodeC : sq < sNodeC) sq = sNodeC;
                var q = At(c, sq, cEdge);
                var pIn = pl + (q - pl).normalized * 0.15f;
                var qIn = q - (q - pl).normalized * 0.15f;
                if (!area.Contains(pIn) || !area.Contains(qIn)) continue;
                if (InDeckRange(r, s - 3f, s + 3f) || InDeckRange(c, sl - 3f, sl + 3f)) continue;
                var along = (qIn - pIn).normalized;
                var n = new Vector2(-along.y, along.x) * 0.15f;
                float y = Mathf.Max(rm.y, cm.y);
                MeshSink.Quad(sink.Get(WorldStyle.MatGround, pIn), V(pIn + n, y), V(qIn + n, y), V(qIn - n, y), V(pIn - n, y), Paint);
                stripes++;
            }
            return stripes;
        }
    }
}
