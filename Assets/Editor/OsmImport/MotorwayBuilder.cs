using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// What makes a motorway look like one: white edge lines (hard shoulder on the right), dashed lane lines, and guard
    /// rails on both sides of every carriageway and ramp (gaps where a ramp leaves or joins, none on bridges – the deck has
    /// parapets; drawn as W-profile beams on posts, held by an invisible closed wall). Where a carriageway leaves the map
    /// it is closed by a barrier with lamps and beacons. Ramps (motorway_link) get edge lines too. One-way carriageways
    /// run along their points; left = median.
    /// Markings on bridge decks are built at deck height (<see cref="BridgeBuilder.DeckLine"/>), all others are draped.
    /// Blue exit signs come from <see cref="TrafficBuilder"/> (motorway_junction nodes in traffic.json).
    /// No shared random numbers – object ids stay as they are.
    /// </summary>
    public static class MotorwayBuilder
    {
        public struct Stats { public int carriageways, ramps, deadEnds; public float railKm; }

        static Vector2Int Key(Vector2 p) => new Vector2Int(Mathf.RoundToInt(p.x * 10f), Mathf.RoundToInt(p.y * 10f));

        const float Line = 0.075f;          // half width of a marking line
        const float Shoulder = 2.3f;        // hard shoulder right of the solid line
        const float RailOut = 0.8f;         // guard rail outside the carriageway edge
        static readonly Color32 RailColor = WorldStyle.Hex("#A9AEB2");
        static readonly Color32 PostColor = WorldStyle.Hex("#7E8388");

        /// <summary>Where a carriageway leaves the built part of the area: <see cref="dir"/> points out of the map,
        /// <see cref="half"/> = half the closed width (rail to rail); entry = the carriageway's start (wrong-way side).</summary>
        public struct DeadEnd { public Vector2 point, dir; public float half; public bool entry; }

        /// <summary>Closures of the last <see cref="Build"/> – TrafficBuilder puts the "Ende des Kartenbereichs" signs there.</summary>
        public static readonly List<DeadEnd> DeadEnds = new List<DeadEnd>();

        public static Stats Build(OsmData data, MeshSink sink, AreaRect area, TerrainField terrain)
        {
            var st = new Stats();
            DeadEnds.Clear();
            var ways = new List<OsmWay>();
            foreach (var w in data.ways)
            {
                string hw = w.tags.Get("highway");
                if ((hw == "motorway" || hw == "motorway_link") && !w.tags.Has("tunnel") && w.points.Count >= 2) ways.Add(w);
            }
            if (ways.Count == 0) return st;

            foreach (var w in ways)
            {
                if (!GroundBuilder.Carriageway(w.tags, out float width, out float y)) continue;
                float hw = width * 0.5f;
                BridgeBuilder.TryGet(w, out var deck);
                if (w.tags.Is("highway", "motorway"))
                {
                    int lanes = int.TryParse(w.tags.Get("lanes"), out int l) ? Mathf.Clamp(l, 1, 5) : 2;
                    float left = hw - 0.35f, right = -(hw - Shoulder);
                    Mark(sink, area, w, deck, left, 0f, y);
                    Mark(sink, area, w, deck, right, 0f, y);
                    float lane = (left - right) / lanes;
                    for (int k = 1; k < lanes; k++) Mark(sink, area, w, deck, left - k * lane, 6f, y);
                    st.carriageways++;
                }
                else
                {
                    Mark(sink, area, w, deck, hw - 0.3f, 0f, y);
                    Mark(sink, area, w, deck, -(hw - 0.3f), 0f, y);
                    st.ramps++;
                }
            }

            // Guard rails along whole carriageways and ramps (consecutive ways chained, mitred offset – no wedge gaps in bends or
            // at way joints), with gaps only where another carriageway comes close: a ramp leaving or joining. The
            // opposite carriageway never opens a gap (median rail), bridges over the motorway neither.
            var roads = new List<(OsmWay way, float hw, Rect bounds)>();
            foreach (var o in data.ways)
                if (o.points.Count >= 2 && !BridgeBuilder.IsDeck(o) && GroundBuilder.Carriageway(o.tags, out float ow, out _))
                    roads.Add((o, ow * 0.5f, GeoUtil.Bounds(o.points)));
            var ends = new List<DeadEnd>();
            foreach (var chain in Chains(ways, "motorway").Concat(Chains(ways, "motorway_link")))
            {
                if (!GroundBuilder.Carriageway(chain[0].tags, out float width, out _)) continue;
                var pts = new List<Vector2>();
                foreach (var w in chain)
                    for (int i = pts.Count == 0 ? 0 : 1; i < w.points.Count; i++) pts.Add(w.points[i]);
                var own = new HashSet<OsmWay>(chain);
                var others = new PropPlacer.Clearance();
                var near = GeoUtil.Bounds(pts);
                near.xMin -= 60f; near.yMin -= 60f; near.xMax += 60f; near.yMax += 60f;
                // Only roads that actually join the chain open a gap (ramps, the road at a ramp's end, merging
                // motorways) – a parallel country road or the opposite carriageway gets a rail between.
                var ownKeys = new HashSet<Vector2Int>();
                foreach (var q in pts) ownKeys.Add(Key(q));
                var cand = roads.FindAll(r => !own.Contains(r.way) && near.Overlaps(r.bounds) && !Opposite(r.way, pts));
                var gapKeys = new HashSet<Vector2Int>(ownKeys);
                var added = new HashSet<OsmWay>();
                // A ramp of several ways: pieces joining a joining ramp count too (3 rounds).
                for (int round = 0; round < 3; round++)
                {
                    var newKeys = new List<Vector2Int>();
                    foreach (var (o, ohw, _) in cand)
                    {
                        if (added.Contains(o) || round > 0 && !o.tags.Is("highway", "motorway_link")) continue;
                        bool joins = false;
                        foreach (var q in o.points) if (gapKeys.Contains(Key(q))) { joins = true; break; }
                        if (!joins) continue;
                        added.Add(o);
                        others.AddLine(o.points, ohw + 0.4f);
                        foreach (var q in o.points) newKeys.Add(Key(q));
                    }
                    foreach (var k in newKeys) gapKeys.Add(k);
                }
                foreach (float side in new[] { 1f, -1f })
                    st.railKm += Rail(sink, area, terrain, pts, side * (width * 0.5f + RailOut), others) / 1000f;
                // Map end (the motorway leaves the built part of the area): closed off at both ends of the chain – at its
                // end for the traffic, at its start for wrong-way drivers.
                for (int e = 0; e < 2; e++)
                {
                    var tip = e == 0 ? pts[pts.Count - 1] : pts[0];
                    var outward = e == 0 ? (tip - pts[pts.Count - 2]).normalized : (pts[0] - pts[1]).normalized;
                    bool continues = false;
                    foreach (var o in data.ways) // incl. bridges: a chain also ends where the motorway goes onto a deck
                    {
                        if (own.Contains(o) || o.points.Count < 2 || o.tags.Get("highway") == null) continue;
                        foreach (var q in o.points) if ((q - tip).sqrMagnitude < 9f) { continues = true; break; }
                        if (continues) break;
                    }
                    // Also where it only continues onto a stub that leaves the built part right away (cut at the mask).
                    if ((!continues || !AreaMask.AllowsFine(tip + outward * 30f)) && area.Contains(tip, 2f))
                        ends.Add(new DeadEnd { point = tip, dir = outward, half = width * 0.5f + RailOut, entry = e == 1 });
                }
            }
            // The mask edge cuts a carriageway into a few short pieces: only the first closure from inside is built – none
            // on pieces that lie beyond another closure of the same carriageway (the opposite one is further aside).
            foreach (var d in ends)
            {
                bool shadowed = false;
                foreach (var o in ends)
                    if (o.point != d.point && (o.point - d.point).sqrMagnitude < 150f * 150f && Vector2.Dot(d.dir, o.dir) > 0f &&
                        Vector2.Dot(d.point - o.point, o.dir) > 1f && Mathf.Abs(Cross(d.point - o.point, o.dir)) < o.half)
                    { shadowed = true; break; }
                // A piece outside the built part (its "inside" is not built either) needs none.
                if (!AreaMask.AllowsFine(d.point - d.dir * 40f)) shadowed = true;
                if (shadowed) continue;
                Closure(sink, d);
                DeadEnds.Add(d);
                st.deadEnds++;
                Debug.Log($"[OSM] motorway map end at {d.point} ({(d.entry ? "start" : "end")} of the carriageway, out {d.dir}, half {d.half:0.0})");
            }
            return st;
        }


        /// <summary>A marking line at <paramref name="offset"/> (left of travel), solid (dash 0) or 6 m dash / 12 m gap.</summary>
        static void Mark(MeshSink sink, AreaRect area, OsmWay w, BridgeBuilder.Deck deck, float offset, float dash, float y)
        {
            if (deck != null) { BridgeBuilder.DeckLine(sink, deck, offset, dash > 0f ? dash : 1e6f, dash > 0f ? dash * 2f : 0f, Line); return; }
            var pts = w.points;
            float phase = 0f, h = y + 0.012f;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                float len = Vector2.Distance(a, b);
                if (len < 0.05f) continue;
                Vector2 dir = (b - a) / len, n = new Vector2(-dir.y, dir.x);
                if (dash <= 0f)
                {
                    // Solid: the segment, stretched a little into the bends so consecutive pieces overlap.
                    Vector2 p0 = a + n * offset - dir * 0.3f, p1 = b + n * offset + dir * 0.3f;
                    if (area.Contains(p0) && area.Contains(p1)) Strip(sink, p0, p1, n, h);
                    continue;
                }
                float d = 0f;
                while (d < len)
                {
                    float inCycle = phase % (dash * 3f);
                    float step = inCycle < dash ? Mathf.Min(dash - inCycle, len - d) : Mathf.Min(dash * 3f - inCycle, len - d);
                    if (inCycle < dash)
                    {
                        Vector2 p0 = a + dir * d + n * offset, p1 = a + dir * (d + step) + n * offset;
                        if (area.Contains(p0) && area.Contains(p1)) Strip(sink, p0, p1, n, h);
                    }
                    d += step; phase += step;
                }
            }
        }

        static void Strip(MeshSink sink, Vector2 p0, Vector2 p1, Vector2 n, float y) =>
            MeshSink.Quad(sink.Get(WorldStyle.MatGround, p0), V(p0 + n * Line, y), V(p1 + n * Line, y), V(p1 - n * Line, y), V(p0 - n * Line, y),
                WorldStyle.LaneMarking);

        /// <summary>Non-bridge motorway (or ramp) ways joined end to start into carriageway chains (a junction with two
        /// continuations ends the chain).</summary>
        static List<List<OsmWay>> Chains(List<OsmWay> ways, string highway)
        {
            var list = ways.FindAll(w => w.tags.Is("highway", highway) && !BridgeBuilder.IsDeck(w));
            Vector2Int K(Vector2 p) => new Vector2Int(Mathf.RoundToInt(p.x * 10f), Mathf.RoundToInt(p.y * 10f));
            var starting = new Dictionary<Vector2Int, List<OsmWay>>();
            var ending = new Dictionary<Vector2Int, List<OsmWay>>();
            foreach (var w in list)
            {
                var ks = K(w.points[0]); var ke = K(w.points[w.points.Count - 1]);
                if (!starting.TryGetValue(ks, out var a)) starting[ks] = a = new List<OsmWay>();
                if (!ending.TryGetValue(ke, out var b)) ending[ke] = b = new List<OsmWay>();
                a.Add(w); b.Add(w);
            }
            OsmWay Next(OsmWay w) => starting.TryGetValue(K(w.points[w.points.Count - 1]), out var n) && n.Count == 1 ? n[0] : null;
            OsmWay Prev(OsmWay w) => ending.TryGetValue(K(w.points[0]), out var p) && p.Count == 1 ? p[0] : null;
            var used = new HashSet<OsmWay>();
            var chains = new List<List<OsmWay>>();
            foreach (var w in list)
            {
                if (used.Contains(w)) continue;
                var first = w;
                for (var p = Prev(first); p != null && !used.Contains(p) && p != w && Next(p) == first; p = Prev(first)) first = p;
                var chain = new List<OsmWay>();
                for (var c = first; c != null && used.Add(c); c = Next(c) is OsmWay n && Prev(n) == c ? n : null) chain.Add(c);
                chains.Add(chain);
            }
            return chains;
        }

        /// <summary>The way runs against the chain where it is closest (the other carriageway of the same motorway).</summary>
        static bool Opposite(OsmWay o, List<Vector2> pts)
        {
            if (!o.tags.Is("highway", "motorway")) return false;
            float best = float.MaxValue, dot = 0f;
            var op = o.points;
            for (int i = 0; i < op.Count - 1; i++)
            {
                var m = (op[i] + op[i + 1]) * 0.5f;
                for (int k = 0; k < pts.Count - 1; k++)
                {
                    float d = ((pts[k] + pts[k + 1]) * 0.5f - m).sqrMagnitude;
                    if (d < best) { best = d; dot = Vector2.Dot((op[i + 1] - op[i]).normalized, (pts[k + 1] - pts[k]).normalized); }
                }
            }
            return dot < -0.5f;
        }

        /// <summary>
        /// Guard rail at <paramref name="offset"/> (left of travel) along a carriageway, mitred at the vertices. What you
        /// see (<see cref="WorldStyle.MatRails"/>, no collider): a galvanised W-profile beam 0.44–0.75 m on posts every
        /// 3–4 m. What holds the car (<see cref="WorldStyle.MatBlockers"/>, never drawn): a continuous, closed wall up to
        /// 1.05 m, 20 cm thick. Both are lifted onto the terrain per vertex; where the ground falls away beside the road
        /// (embankments) they keep the height of the carriageway edge. Pieces of ≤ 4 m are left out where another
        /// carriageway comes close (ramps). Returns the built length.
        /// </summary>
        static float Rail(MeshSink sink, AreaRect area, TerrainField t, List<Vector2> pts, float offset, PropPlacer.Clearance others)
        {
            // Mitred offset line + the outward direction at every vertex.
            int n = pts.Count;
            var line = new List<Vector2>(n);
            var outs = new List<Vector2>(n);
            for (int i = 0; i < n; i++)
            {
                Vector2 dIn = i > 0 ? (pts[i] - pts[i - 1]).normalized : Vector2.zero;
                Vector2 dOut = i < n - 1 ? (pts[i + 1] - pts[i]).normalized : Vector2.zero;
                Vector2 d = (dIn + dOut).sqrMagnitude > 1e-6f ? (dIn + dOut).normalized : (dOut != Vector2.zero ? dOut : dIn);
                var nrm = new Vector2(-d.y, d.x);
                float scale = 1f;
                if (dIn != Vector2.zero && dOut != Vector2.zero)
                    scale = 1f / Mathf.Max(0.5f, Vector2.Dot(nrm, new Vector2(-dOut.y, dOut.x)));
                line.Add(pts[i] + nrm * offset * scale);
                outs.Add(nrm * Mathf.Sign(offset));
            }
            float built = 0f, sincePost = 0f;
            bool open = false;
            Vector2 prevA = default, prevO = default, prevT = default;
            float prevD = 0f;
            for (int i = 0; i < n - 1; i++)
            {
                Vector2 a = line[i], b = line[i + 1];
                float len = Vector2.Distance(a, b);
                if (len < 0.05f) continue;
                int pieces = Mathf.Max(1, Mathf.CeilToInt(len / 4f));
                for (int k = 0; k < pieces; k++)
                {
                    float u0 = (float)k / pieces, u1 = (float)(k + 1) / pieces;
                    Vector2 s0 = Vector2.Lerp(a, b, u0), s1 = Vector2.Lerp(a, b, u1);
                    Vector2 o0 = Vector2.Lerp(outs[i], outs[i + 1], u0).normalized, o1 = Vector2.Lerp(outs[i], outs[i + 1], u1).normalized;
                    var mid = (s0 + s1) * 0.5f;
                    bool ok = area.Contains(mid, 2f) && AreaMask.Allows(mid) && !others.IsBlocked(mid, 0f);
                    if (!ok)
                    {
                        if (open) { Cap(sink, prevA, prevO, prevD, false); Post(sink, prevA, prevO, prevT, prevD); }
                        open = false;
                        continue;
                    }
                    float d0 = Drop(t, s0, o0, offset), d1 = Drop(t, s1, o1, offset);
                    var tan = (s1 - s0).normalized;
                    if (!open) { Cap(sink, s0, o0, d0, true); sincePost = float.MaxValue; }
                    if (sincePost >= 3f) { Post(sink, s0, o0, tan, d0); sincePost = 0f; }
                    Wall(sink, s0, s1, o0, o1, d0, d1);
                    Beam(sink, s0, s1, o0, o1, d0, d1);
                    open = true;
                    float pl = Vector2.Distance(s0, s1);
                    sincePost += pl;
                    prevA = s1; prevO = o1; prevT = tan; prevD = d1;
                    built += pl;
                }
            }
            if (open) { Cap(sink, prevA, prevO, prevD, false); Post(sink, prevA, prevO, prevT, prevD); }
            return built;
        }

        // Collision wall: well above the car capsule's lower hemisphere (radius 0.95 m) – lower walls are climbed by the
        // character controller (at 1.05 m it still happened now and then). Invisible, so its height costs nothing; the
        // visible beam is lower (real rails: top edge 0.75 m).
        const float Half = 0.1f, Top = 1.4f;
        // W-profile beam on the road side: (distance from the rail line, outwards +; height), top to bottom.
        static readonly Vector2[] Profile = { new Vector2(-0.05f, 0.75f), new Vector2(-0.14f, 0.675f), new Vector2(-0.08f, 0.595f),
                                              new Vector2(-0.14f, 0.515f), new Vector2(-0.05f, 0.44f) };
        const float BeamBack = -0.03f, PostTop = 0.72f, PostOut = 0.09f, PostHalf = 0.05f;

        /// <summary>Quad a-b-c-d turned so that its front faces <paramref name="towards"/>.</summary>
        static void Face(MeshSink.Batch batch, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 towards, Color32 col)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), towards) >= 0f) MeshSink.Quad(batch, a, b, c, d, col);
            else MeshSink.Quad(batch, d, c, b, a, col);
        }

        /// <summary>Invisible collision piece: road side, outer side, top.</summary>
        static void Wall(MeshSink sink, Vector2 s0, Vector2 s1, Vector2 o0, Vector2 o1, float d0, float d1)
        {
            var batch = sink.Get(WorldStyle.MatBlockers, (s0 + s1) * 0.5f);
            Vector2 i0 = s0 - o0 * Half, i1 = s1 - o1 * Half, e0 = s0 + o0 * Half, e1 = s1 + o1 * Half;
            var o = V((o0 + o1).normalized, 0f);
            Face(batch, V(i0, 0f), V(i0, Top + d0), V(i1, Top + d1), V(i1, 0f), -o, PostColor);
            Face(batch, V(e0, 0f), V(e0, Top + d0), V(e1, Top + d1), V(e1, 0f), o, PostColor);
            Face(batch, V(i0, Top + d0), V(e0, Top + d0), V(e1, Top + d1), V(i1, Top + d1), Vector3.up, PostColor);
        }

        /// <summary>Visible beam of one piece: the W profile facing the road and a flat back.</summary>
        static void Beam(MeshSink sink, Vector2 s0, Vector2 s1, Vector2 o0, Vector2 o1, float d0, float d1)
        {
            var batch = sink.Get(WorldStyle.MatRails, (s0 + s1) * 0.5f);
            var o = V((o0 + o1).normalized, 0f);
            Vector3 P0(Vector2 q) => V(s0 + o0 * q.x, d0 + q.y);
            Vector3 P1(Vector2 q) => V(s1 + o1 * q.x, d1 + q.y);
            for (int k = 0; k < Profile.Length - 1; k++)
            {
                Vector2 p = Profile[k], q = Profile[k + 1], e = q - p;
                var n2 = new Vector2(e.y, -e.x);
                if (n2.x > 0f) n2 = -n2; // towards the road
                Face(batch, P0(p), P1(p), P1(q), P0(q), o * n2.x + Vector3.up * n2.y, RailColor);
            }
            Vector2 top = new Vector2(BeamBack, Profile[0].y), bottom = new Vector2(BeamBack, Profile[Profile.Length - 1].y);
            Face(batch, P0(top), P1(top), P1(bottom), P0(bottom), o, PostColor);
        }

        /// <summary>Post behind the beam (road side, both flanks, back), from the ground to just under the beam top.</summary>
        static void Post(MeshSink sink, Vector2 s, Vector2 o2, Vector2 tan, float d)
        {
            var batch = sink.Get(WorldStyle.MatRails, s);
            Vector2 f0 = s + o2 * BeamBack, f1 = s + o2 * PostOut, t = tan * PostHalf;
            float h = d + PostTop;
            var o = V(o2, 0f); var tv = V(tan, 0f);
            Face(batch, V(f0 - t, 0f), V(f0 - t, h), V(f0 + t, h), V(f0 + t, 0f), -o, PostColor);
            Face(batch, V(f1 - t, 0f), V(f1 - t, h), V(f1 + t, h), V(f1 + t, 0f), o, PostColor);
            Face(batch, V(f0 - t, 0f), V(f0 - t, h), V(f1 - t, h), V(f1 - t, 0f), -tv, PostColor);
            Face(batch, V(f0 + t, 0f), V(f0 + t, h), V(f1 + t, h), V(f1 + t, 0f), tv, PostColor);
        }

        /// <summary>Invisible end face of a wall run (both orientations: a cap is tiny and the run direction is not known here).</summary>
        static void Cap(MeshSink sink, Vector2 s, Vector2 o, float d, bool start)
        {
            var batch = sink.Get(WorldStyle.MatBlockers, s);
            Vector2 i = s - o * Half, e = s + o * Half;
            Vector2 p = start ? i : e, q = start ? e : i;
            MeshSink.Quad(batch, V(p, 0f), V(p, Top + d), V(q, Top + d), V(q, 0f), PostColor);
            MeshSink.Quad(batch, V(q, 0f), V(q, Top + d), V(p, Top + d), V(p, 0f), PostColor);
        }

        static readonly Color32 BarrierRed = new Color32(196, 30, 36, 255);
        static readonly Color32 BarrierWhite = new Color32(244, 244, 240, 255);
        static readonly Color32 LampYellow = new Color32(255, 196, 40, 255);
        static readonly Color32 Housing = new Color32(38, 40, 42, 255);

        /// <summary>
        /// End of the map on a carriageway: a red-white barrier (Absperrschranke, Z. 600) across from rail to rail 3 m
        /// before the end with yellow warning lamps, and red-white beacons (Leitbaken, Z. 605) along both edges for the
        /// last 40 m. An invisible wall behind the barrier holds the car; the sign above comes from TrafficBuilder.
        /// </summary>
        static void Closure(MeshSink sink, DeadEnd d)
        {
            var nrm = new Vector2(-d.dir.y, d.dir.x);
            var c = d.point - d.dir * 3f;
            Vector2 a = c + nrm * d.half, b = c - nrm * d.half;
            var fwd = V(d.dir, 0f);
            // Closed collision wall (both faces + top), just behind the board.
            var wall = sink.Get(WorldStyle.MatBlockers, c);
            Vector2 a0 = a, b0 = b, a1 = a + d.dir * (2f * Half), b1 = b + d.dir * (2f * Half);
            Face(wall, V(a0, 0f), V(a0, Top), V(b0, Top), V(b0, 0f), -fwd, PostColor);
            Face(wall, V(a1, 0f), V(a1, Top), V(b1, Top), V(b1, 0f), fwd, PostColor);
            Face(wall, V(a0, Top), V(a1, Top), V(b1, Top), V(b0, Top), Vector3.up, PostColor);

            // Board 0.85–1.1 m in 0.5 m red/white blocks (odd count: red at both ends), a post with a lamp every ~2.5 m.
            var props = sink.Get(WorldStyle.MatProps, c);
            float width = 2f * d.half;
            int blocks = Mathf.Max(3, Mathf.RoundToInt(width / 0.5f) | 1);
            const float y0 = 0.85f, y1 = 1.1f, thick = 0.03f;
            for (int k = 0; k < blocks; k++)
            {
                Vector2 p = Vector2.Lerp(a, b, (float)k / blocks), q = Vector2.Lerp(a, b, (float)(k + 1) / blocks);
                var col = k % 2 == 0 ? BarrierRed : BarrierWhite;
                Face(props, V(p - d.dir * thick, y0), V(p - d.dir * thick, y1), V(q - d.dir * thick, y1), V(q - d.dir * thick, y0), -fwd, col);
                Face(props, V(p + d.dir * thick, y0), V(p + d.dir * thick, y1), V(q + d.dir * thick, y1), V(q + d.dir * thick, y0), fwd, col);
            }
            Face(props, V(a - d.dir * thick, y1), V(a + d.dir * thick, y1), V(b + d.dir * thick, y1), V(b - d.dir * thick, y1), Vector3.up, BarrierWhite);
            float yaw = Mathf.Atan2(d.dir.x, d.dir.y) * Mathf.Rad2Deg;
            int posts = Mathf.Max(2, Mathf.RoundToInt(width / 2.5f));
            for (int k = 0; k < posts; k++)
            {
                var p = Vector2.Lerp(a, b, (k + 0.5f) / posts) + d.dir * (thick + 0.04f);
                FurniturePlacer.Box(props, V(p, y1 * 0.5f), new Vector3(0.06f, y1, 0.06f), yaw, PostColor);
                FurniturePlacer.Box(props, V(p, 0.03f), new Vector3(0.7f, 0.06f, 0.35f), yaw + 90f, Housing); // foot
                // Warning lamp on the board: dark housing, yellow lens on both faces.
                var lamp = p - d.dir * (thick + 0.04f);
                FurniturePlacer.Box(props, V(lamp, y1 + 0.12f), new Vector3(0.2f, 0.24f, 0.08f), yaw, Housing);
                foreach (float sgn in new[] { -1f, 1f })
                {
                    var f = lamp + d.dir * (sgn * 0.045f);
                    var r = nrm * 0.08f;
                    Face(props, V(f + r, y1 + 0.05f), V(f + r, y1 + 0.21f), V(f - r, y1 + 0.21f), V(f - r, y1 + 0.05f), fwd * sgn, LampYellow);
                }
            }
            // Beacons along both edges (inside the rails), striped towards the traffic.
            foreach (float side in new[] { 1f, -1f })
                for (float back = 10f; back <= 40.1f; back += 10f)
                    Beacon(props, c - d.dir * back + nrm * side * (d.half - 0.7f), -d.dir, side);
        }

        /// <summary>Leitbake: 0.25 × 1 m board on a foot, red-white stripes at 45°, falling towards the free lane.</summary>
        static void Beacon(MeshSink.Batch b, Vector2 pos, Vector2 facing, float side)
        {
            var right = new Vector2(-facing.y, facing.x); // viewer's right when looking at the face
            var f = V(facing, 0f);
            const float w = 0.25f, yb = 0.3f, yt = 1.3f, band = 0.18f;
            float yaw = Mathf.Atan2(facing.x, facing.y) * Mathf.Rad2Deg;
            FurniturePlacer.Box(b, V(pos, 0.05f), new Vector3(0.45f, 0.1f, 0.45f), yaw, Housing);
            FurniturePlacer.Box(b, V(pos - facing * 0.03f, yb * 0.5f + 0.05f), new Vector3(0.05f, yb, 0.05f), yaw, Housing);
            // The stripes fall towards the carriageway's middle (side +1 = left edge for the traffic: falling to the right).
            float slope = -side;
            Vector3 P(Vector2 uv) => V(pos + right * uv.x + facing * 0.012f, uv.y);
            var board = new List<Vector2> { new Vector2(-w / 2, yb), new Vector2(w / 2, yb), new Vector2(w / 2, yt), new Vector2(-w / 2, yt) };
            for (int k = -2; k * band < yt - yb + w; k++)
            {
                var poly = ClipBand(board, slope, yb + k * band, yb + (k + 1) * band);
                if (poly.Count < 3) continue;
                var col = (k & 1) == 0 ? BarrierRed : BarrierWhite;
                for (int i = 1; i < poly.Count - 1; i++)
                {
                    Vector3 p0 = P(poly[0]), p1 = P(poly[i]), p2 = P(poly[i + 1]);
                    if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), f) >= 0f) MeshSink.Tri(b, p0, p1, p2, col);
                    else MeshSink.Tri(b, p0, p2, p1, col);
                }
            }
            Vector2 l = pos - right * (w / 2) - facing * 0.012f, r = pos + right * (w / 2) - facing * 0.012f;
            Face(b, V(l, yb), V(l, yt), V(r, yt), V(r, yb), -f, BarrierWhite);
        }

        /// <summary>Convex polygon (u, y) clipped to lo ≤ y − slope·u ≤ hi.</summary>
        static List<Vector2> ClipBand(List<Vector2> poly, float slope, float lo, float hi)
        {
            List<Vector2> Clip(List<Vector2> src, System.Func<Vector2, float> f) // keeps f ≥ 0 (f linear)
            {
                var dst = new List<Vector2>();
                for (int i = 0; i < src.Count; i++)
                {
                    Vector2 p = src[i], q = src[(i + 1) % src.Count];
                    float fp = f(p), fq = f(q);
                    if (fp >= 0f) dst.Add(p);
                    if (fp >= 0f != fq >= 0f) dst.Add(Vector2.Lerp(p, q, fp / (fp - fq)));
                }
                return dst;
            }
            poly = Clip(poly, v => v.y - slope * v.x - lo);
            return poly.Count < 3 ? poly : Clip(poly, v => hi - (v.y - slope * v.x));
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        /// <summary>How far the ground at the rail lies below the carriageway (the highest point between the centre line
        /// and the rail – embankments are often narrower than the road in the 5 m terrain; 0 if the rail stands higher).</summary>
        static float Drop(TerrainField t, Vector2 rail, Vector2 outwards, float offset)
        {
            float road = float.MinValue;
            for (float f = 0f; f <= 1.001f; f += 0.2f)
            {
                var q = rail - outwards * (Mathf.Abs(offset) * f);
                road = Mathf.Max(road, t.Height(q.x, q.y));
            }
            return Mathf.Clamp(road - t.Height(rail.x, rail.y), 0f, 12f); // next to bridge abutments the ground falls away steeply
        }

        static Vector3 V(Vector2 p, float y) => new Vector3(p.x, y, p.y);
    }
}
