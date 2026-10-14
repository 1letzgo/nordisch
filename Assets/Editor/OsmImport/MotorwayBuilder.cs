using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// What makes a motorway look like one: white edge lines (hard shoulder on the right), dashed lane lines, and guard
    /// rails on both sides of every carriageway and ramp (gaps where a ramp leaves or joins, none on bridges – the deck has
    /// parapets). Ramps (motorway_link) get edge lines too. One-way carriageways run along their points; left = median.
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

        public static Stats Build(OsmData data, MeshSink sink, AreaRect area, TerrainField terrain)
        {
            var st = new Stats();
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
                // Dead end (the motorway leaves the built part of the area): a barrier across the carriageway.
                var end = pts[pts.Count - 1];
                bool continues = false;
                foreach (var o in data.ways) // incl. bridges: a chain also ends where the motorway goes onto a deck
                {
                    if (own.Contains(o) || o.points.Count < 2 || o.tags.Get("highway") == null) continue;
                    foreach (var q in o.points) if ((q - end).sqrMagnitude < 9f) { continues = true; break; }
                    if (continues) break;
                }
                var dir = (end - pts[pts.Count - 2]).normalized;
                // Also where it only continues onto a stub that leaves the built part right away (cut at the mask).
                if ((!continues || !AreaMask.AllowsFine(end + dir * 30f)) && area.Contains(end, 2f))
                {
                    var nrm = new Vector2(-dir.y, dir.x);
                    float half = width * 0.5f + RailOut;
                    var a = end - dir * 3f + nrm * half;
                    var b = end - dir * 3f - nrm * half;
                    Segment(sink, a, b, -dir, -dir, 0f, 0f);
                    st.deadEnds++;
                    Debug.Log($"[OSM] motorway dead end at {end}");
                }
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
        /// Guard rail at <paramref name="offset"/> (left of travel) along a carriageway: a continuous, closed strip
        /// (beam 0.5–0.85 m over a darker underride plate down to the ground, 20 cm thick; Props = collider, lifted onto
        /// the terrain per vertex), mitred at the vertices. Where the ground falls away beside the road (embankments)
        /// it keeps the height of the carriageway edge. Pieces of ≤ 4 m are left out where another carriageway comes
        /// close (ramps). Returns the built length.
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
            float built = 0f;
            bool open = false;
            Vector2 prevA = default, prevO = default;
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
                        if (open) Cap(sink, prevA, prevO, prevD, false);
                        open = false;
                        continue;
                    }
                    float d0 = Drop(t, s0, o0, offset), d1 = Drop(t, s1, o1, offset);
                    if (!open) Cap(sink, s0, o0, d0, true);
                    Segment(sink, s0, s1, o0, o1, d0, d1);
                    open = true;
                    prevA = s1; prevO = o1; prevD = d1;
                    built += Vector2.Distance(s0, s1);
                }
            }
            if (open) Cap(sink, prevA, prevO, prevD, false);
            return built;
        }

        // Top above the car capsule's lower hemisphere (radius 0.95 m): lower walls are climbed by the character controller.
        const float Half = 0.1f, Plate = 0.6f, Top = 1.05f;

        /// <summary>One piece: road-side and outer faces (plate + beam) and the top.</summary>
        static void Segment(MeshSink sink, Vector2 s0, Vector2 s1, Vector2 o0, Vector2 o1, float d0, float d1)
        {
            var batch = sink.Get(WorldStyle.MatProps, (s0 + s1) * 0.5f);
            Vector2 i0 = s0 - o0 * Half, i1 = s1 - o1 * Half, e0 = s0 + o0 * Half, e1 = s1 + o1 * Half;
            // Orientation: faces must look away from the rail's centre line, whichever way the piece runs.
            bool flip = Cross(s1 - s0, o0) < 0f; // outwards on the right
            void Face(Vector2 p, Vector2 q, float y0p, float y0q, float y1p, float y1q, Color32 c)
            {
                if (flip) MeshSink.Quad(batch, V(q, y0q), V(q, y1q), V(p, y1p), V(p, y0p), c);
                else MeshSink.Quad(batch, V(p, y0p), V(p, y1p), V(q, y1q), V(q, y0q), c);
            }
            // Road side (faces -outwards) and outer side (faces +outwards): swap p/q for the outer one.
            Face(i0, i1, 0f, 0f, Plate + d0, Plate + d1, PostColor);
            Face(i0, i1, Plate + d0, Plate + d1, Top + d0, Top + d1, RailColor);
            Face(e1, e0, 0f, 0f, Plate + d1, Plate + d0, PostColor);
            Face(e1, e0, Plate + d1, Plate + d0, Top + d1, Top + d0, RailColor);
            if (flip) MeshSink.Quad(batch, V(i0, Top + d0), V(i1, Top + d1), V(e1, Top + d1), V(e0, Top + d0), RailColor);
            else MeshSink.Quad(batch, V(i1, Top + d1), V(i0, Top + d0), V(e0, Top + d0), V(e1, Top + d1), RailColor);
        }

        /// <summary>End face of a rail run (start: facing back along the run).</summary>
        static void Cap(MeshSink sink, Vector2 s, Vector2 o, float d, bool start)
        {
            var batch = sink.Get(WorldStyle.MatProps, s);
            Vector2 i = s - o * Half, e = s + o * Half;
            Vector2 p = start ? i : e, q = start ? e : i;
            // Both orientations: a cap is tiny and the run direction is not known here.
            MeshSink.Quad(batch, V(p, 0f), V(p, Top + d), V(q, Top + d), V(q, 0f), PostColor);
            MeshSink.Quad(batch, V(q, 0f), V(q, Top + d), V(p, Top + d), V(p, 0f), PostColor);
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
