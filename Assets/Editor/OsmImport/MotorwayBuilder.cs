using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// What makes a motorway look like one: white edge lines (hard shoulder on the right), dashed lane lines, and guard
    /// rails on both sides of every carriageway (gaps where a ramp leaves or joins, none on bridges – the deck has
    /// parapets). Ramps (motorway_link) get edge lines. One-way carriageways run along their points; left = median.
    /// Markings on bridge decks are built at deck height (<see cref="BridgeBuilder.DeckLine"/>), all others are draped.
    /// Blue exit signs come from <see cref="TrafficBuilder"/> (motorway_junction nodes in traffic.json).
    /// No shared random numbers – object ids stay as they are.
    /// </summary>
    public static class MotorwayBuilder
    {
        public struct Stats { public int carriageways, ramps; public float railKm; }

        const float Line = 0.075f;          // half width of a marking line
        const float Shoulder = 2.3f;        // hard shoulder right of the solid line
        const float RailOut = 0.8f;         // guard rail outside the carriageway edge
        const float Piece = 8f;             // rail beam piece; posts every half piece
        static readonly Color32 RailColor = WorldStyle.Hex("#A9AEB2");
        static readonly Color32 PostColor = WorldStyle.Hex("#7E8388");

        public static Stats Build(OsmData data, MeshSink sink, AreaRect area)
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

            // Guard rails: off every other carriageway (ramps leaving / joining, the opposite carriageway).
            var roads = new List<(OsmWay way, float hw, Rect bounds)>();
            foreach (var o in data.ways)
                if (o.points.Count >= 2 && GroundBuilder.Carriageway(o.tags, out float ow, out _))
                    roads.Add((o, ow * 0.5f, GeoUtil.Bounds(o.points)));
            foreach (var w in ways)
            {
                if (!w.tags.Is("highway", "motorway") || BridgeBuilder.IsDeck(w)) continue;
                if (!GroundBuilder.Carriageway(w.tags, out float width, out _)) continue;
                var others = new PropPlacer.Clearance();
                var near = GeoUtil.Bounds(w.points);
                near.xMin -= 60f; near.yMin -= 60f; near.xMax += 60f; near.yMax += 60f;
                foreach (var (o, ohw, ob) in roads)
                    if (o != w && near.Overlaps(ob)) others.AddLine(o.points, ohw + 0.4f);
                foreach (float side in new[] { 1f, -1f })
                    st.railKm += Rail(sink, area, w.points, side * (width * 0.5f + RailOut), others) / 1000f;
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

        /// <summary>Guard rail at <paramref name="offset"/> beside a carriageway: beam on posts (Props: collider, lifted
        /// onto the terrain per vertex). Returns the built length.</summary>
        static float Rail(MeshSink sink, AreaRect area, List<Vector2> pts, float offset, PropPlacer.Clearance others)
        {
            float built = 0f;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                float len = Vector2.Distance(a, b);
                if (len < 0.5f) continue;
                Vector2 dir = (b - a) / len, n = new Vector2(-dir.y, dir.x);
                int pieces = Mathf.Max(1, Mathf.RoundToInt(len / Piece));
                float step = len / pieces;
                for (int k = 0; k < pieces; k++)
                {
                    Vector2 s0 = a + dir * (k * step) + n * offset, s1 = a + dir * ((k + 1) * step) + n * offset;
                    var mid = (s0 + s1) * 0.5f;
                    if (!area.Contains(mid, 2f) || others.IsBlocked(mid, 0f) || BridgeBuilder.Covers(mid, 0.5f)) continue;
                    if (!AreaMask.Allows(mid)) continue;
                    // Beam ends a bit beyond the piece, so bends close up.
                    s0 -= dir * 0.15f; s1 += dir * 0.15f;
                    var batch = sink.Get(WorldStyle.MatProps, mid);
                    var nn = n * Mathf.Sign(offset); // outwards
                    Vector2 i0 = s0 - nn * 0.04f, i1 = s1 - nn * 0.04f, o0 = s0 + nn * 0.04f, o1 = s1 + nn * 0.04f;
                    const float lo = 0.45f, hi = 0.78f;
                    // Faces: towards the road, away from it, top (orientation by the side's normal).
                    if (offset > 0)
                    {
                        MeshSink.Quad(batch, V(i0, lo), V(i0, hi), V(i1, hi), V(i1, lo), RailColor);
                        MeshSink.Quad(batch, V(o1, lo), V(o1, hi), V(o0, hi), V(o0, lo), RailColor);
                        MeshSink.Quad(batch, V(i0, hi), V(o0, hi), V(o1, hi), V(i1, hi), PostColor);
                    }
                    else
                    {
                        MeshSink.Quad(batch, V(i1, lo), V(i1, hi), V(i0, hi), V(i0, lo), RailColor);
                        MeshSink.Quad(batch, V(o0, lo), V(o0, hi), V(o1, hi), V(o1, lo), RailColor);
                        MeshSink.Quad(batch, V(i1, hi), V(o1, hi), V(o0, hi), V(i0, hi), PostColor);
                    }
                    // Two posts per piece (behind the beam), front and back face only.
                    for (int p = 0; p < 2; p++)
                    {
                        var c = Vector2.Lerp(s0, s1, 0.25f + 0.5f * p) + nn * 0.1f;
                        Vector2 l = c - dir * 0.06f, r = c + dir * 0.06f;
                        MeshSink.Quad(batch, V(l, 0f), V(l, hi), V(r, hi), V(r, 0f), PostColor);
                        MeshSink.Quad(batch, V(r, 0f), V(r, hi), V(l, hi), V(l, 0f), PostColor);
                    }
                    built += step;
                }
            }
            return built;
        }

        static Vector3 V(Vector2 p, float y) => new Vector3(p.x, y, p.y);
    }
}
