using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Road bridges (highway=* with bridge=*) that span a real gap: the laser terrain (DGM1) has no bridges, so the ground
    /// under a motorway bridge is the road below – a draped ribbon would dive down into it. Such ways get a deck instead:
    /// straight between the heights of its two ends (they sit on the ramps), asphalt with kerbs, parapets and a thick
    /// slab, pillars where nothing drives below. Built into <see cref="WorldStyle.MatTerrain"/> (collider, not draped), so
    /// cars drive over it while the road below stays reachable. Small bridges over ditches (no gap) stay draped ribbons.
    /// Call <see cref="Prepare"/> after CoarseTerrain (final ground heights), then <see cref="RoadProfile.Apply"/> and
    /// <see cref="EaseEnds"/>, all before GroundBuilder.BuildBase/BuildLines (BuildLines skips the decks).
    /// </summary>
    public static class BridgeBuilder
    {
        public class Deck
        {
            public OsmWay way;
            public List<Vector2> pts; // built centre line: the way's points, ends extended by ext0/ext1
            public float ext0, ext1;  // m the deck runs on over the approach (a way passing close below the OSM end)
            public float[] along;   // distance from the first point per point (of pts)
            public float h0, h1;    // surface height at both ends
            public float width, y;  // carriageway width, layer offset of the road style
            public Rect bounds;     // of the centre line
            public float g0, g1;    // draped road surface at both ends (the approaches) – the deck eases onto it
            public float r0, r1;    // length of those ramps (0 = none)
            public float Length => along[along.Length - 1];

            /// <summary>Straight between the abutment heights, eased onto the approach road within r0/r1 of the ends (the
            /// faceted terrain at a bridge end often lies a little lower or higher than the abutment – no step at the joint).</summary>
            public float HeightAt(float s)
            {
                float len = Length, h = Mathf.Lerp(h0, h1, len > 0f ? s / len : 0f);
                if (s < r0) h += (g0 - h0) * (1f - Smooth(s / r0));
                if (len - s < r1) h += (g1 - h1) * (1f - Smooth((len - s) / r1));
                return h;
            }

            /// <summary>[s0, s1] reaches into a ramp – the deck is built in short pieces there.</summary>
            public bool Ramped(float s0, float s1) => s0 < r0 || Length - s1 < r1;

            static float Smooth(float x) => x * x * (3f - 2f * x);

            /// <summary>Runs the deck on by e0/e1 metres beyond the way's first/last point (straight on).</summary>
            public void Extend(float e0, float e1)
            {
                var wp = way.points;
                ext0 = e0; ext1 = e1;
                pts = new List<Vector2>(wp);
                if (e0 > 0f) pts.Insert(0, wp[0] + (wp[0] - wp[1]).normalized * e0);
                if (e1 > 0f) pts.Add(wp[wp.Count - 1] + (wp[wp.Count - 1] - wp[wp.Count - 2]).normalized * e1);
                along = new float[pts.Count];
                for (int i = 1; i < pts.Count; i++) along[i] = along[i - 1] + Vector2.Distance(pts[i - 1], pts[i]);
                bounds = GeoUtil.Bounds(pts);
            }
        }

        const float MinGap = 1.8f;      // m between a straight deck and the ground to count as a bridge
        const float MinGapOverWay = 1f; // … over a road, path or railway (the laser terrain often rounds off the underpass;
                                        // draped, the smoothed road profile would bury the way below)
        const float MinLength = 6f;
        const float Slab = 1.1f;        // deck thickness under the road surface
        const float Parapet = 0.95f;
        const float Edge = 0.7f;        // kerb/parapet strip beside the carriageway

        static readonly Dictionary<OsmWay, Deck> decks = new Dictionary<OsmWay, Deck>();
        static readonly Color32 Concrete = WorldStyle.Hex("#B4B2AC");
        static readonly Color32 ConcreteDark = WorldStyle.Hex("#8E8C86");

        public static IEnumerable<Deck> Decks => decks.Values;
        public static bool IsDeck(OsmWay w) => decks.ContainsKey(w);
        public static bool TryGet(OsmWay w, out Deck d) => decks.TryGetValue(w, out d);

        public static int Prepare(OsmData data, AreaRect area, TerrainField t)
        {
            decks.Clear();
            shared.Clear();
            below = null;
            if (t.IsFlat) return 0;
            var pits = new List<(Deck d, Vector2 u0, float s0, float a0, Vector2 u1, float s1, float a1)>();
            // Other carriageways (with their edges) are never raised by a pit fill – the road below, a road alongside.
            var roadsByEnd = new Dictionary<Vector2Int, List<OsmWay>>();
            var allRoads = new List<OsmWay>();
            foreach (var w in data.ways)
            {
                if (w.points.Count < 2 || !GroundBuilder.Carriageway(w.tags, out _, out _)) continue;
                allRoads.Add(w);
                foreach (var e in new[] { w.points[0], w.points[w.points.Count - 1] })
                {
                    var k = new Vector2Int(Mathf.RoundToInt(e.x * 10f), Mathf.RoundToInt(e.y * 10f));
                    if (!roadsByEnd.TryGetValue(k, out var l)) roadsByEnd[k] = l = new List<OsmWay>();
                    l.Add(w);
                }
            }
            foreach (var way in data.ways)
            {
                var tags = way.tags;
                if (tags.Get("highway") == null || !tags.Has("bridge") || tags.Is("bridge", "no") || tags.Is("area", "yes")) continue;
                if (!GroundBuilder.Carriageway(tags, out float width, out float y) && GroundBuilder.LineWidth(tags) <= 0f) continue;
                if (width <= 0f) { width = GroundBuilder.LineWidth(tags); y = WorldStyle.YPath; }
                var pts = way.points;
                if (pts.Count < 2) continue;
                bool inside = true;
                foreach (var p in pts) inside &= area.Contains(p, 2f);
                if (!inside) continue;
                var along = new float[pts.Count];
                for (int i = 1; i < pts.Count; i++) along[i] = along[i - 1] + Vector2.Distance(pts[i - 1], pts[i]);
                float len = along[pts.Count - 1];
                if (len < MinLength) continue;
                // The laser terrain lacks the bridge and often the last metres of the ramp before it (a pit at the
                // abutment): each end takes the highest ground just outside it.
                var u0 = (pts[0] - pts[1]).normalized;
                var u1 = (pts[pts.Count - 1] - pts[pts.Count - 2]).normalized;
                float a0 = Abutment(t, pts[0], u0, out float s0), a1 = Abutment(t, pts[pts.Count - 1], u1, out float s1);
                var d = new Deck { way = way, pts = pts, along = along, width = width, y = y, bounds = GeoUtil.Bounds(pts), h0 = a0 + y, h1 = a1 + y };
                // Is there a gap under the straight deck anywhere (away from the ends)?
                float gap = 0f;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    float seg = along[i + 1] - along[i];
                    for (float s = 0f; s < seg; s += 2f)
                    {
                        float at = along[i] + s;
                        if (at < 3f || at > len - 3f) continue;
                        var q = Vector2.Lerp(pts[i], pts[i + 1], seg > 0f ? s / seg : 0f);
                        gap = Mathf.Max(gap, d.HeightAt(at) - t.Height(q.x, q.y));
                    }
                }
                if (gap < MinGap && (gap < MinGapOverWay || !CrossesWay(data, way))) continue;
                decks[way] = d;
                if (s0 > 0f || s1 > 0f) pits.Add((d, u0, s0, a0, u1, s1, a1));
            }
            // Pits are filled once all decks are known: decks are never draped, so the ground under the neighbouring
            // bridge (the other carriageway's) may rise too – otherwise the pit stays on the median side of the lane.
            foreach (var (d, u0, s0, a0, u1, s1, a1) in pits)
            {
                var pts = d.way.points;
                // Keep-out: every carriageway except decks and the approaches joining this bridge's ends.
                var keep = new PropPlacer.Clearance();
                var near = d.bounds;
                near.xMin -= 60f; near.yMin -= 60f; near.xMax += 60f; near.yMax += 60f;
                var joined = new HashSet<OsmWay> { d.way };
                foreach (var e in new[] { pts[0], pts[pts.Count - 1] })
                    if (roadsByEnd.TryGetValue(new Vector2Int(Mathf.RoundToInt(e.x * 10f), Mathf.RoundToInt(e.y * 10f)), out var l)) joined.UnionWith(l);
                foreach (var o in allRoads)
                    if (!joined.Contains(o) && !decks.ContainsKey(o) && GeoUtil.Bounds(o.points).Overlaps(near) && GroundBuilder.Carriageway(o.tags, out float ow, out _))
                        keep.AddLine(o.points, ow * 0.5f + 2f);
                FillPit(t, pts[0], u0, s0, a0, d.width * 0.5f + 2.5f, keep);
                FillPit(t, pts[pts.Count - 1], u1, s1, a1, d.width * 0.5f + 2.5f, keep);
            }
            // Decks joined end to end (viaducts in several ways): one height at the joint – each end's own abutment search
            // looks the other way and finds different ground, a step of up to several metres.
            var joints = new Dictionary<Vector2Int, List<(Deck d, bool start)>>();
            foreach (var d in decks.Values)
                foreach (bool start in new[] { true, false })
                {
                    var e = start ? d.way.points[0] : d.way.points[d.way.points.Count - 1];
                    var k = new Vector2Int(Mathf.RoundToInt(e.x * 10f), Mathf.RoundToInt(e.y * 10f));
                    if (!joints.TryGetValue(k, out var l)) joints[k] = l = new List<(Deck, bool)>();
                    l.Add((d, start));
                }
            shared.Clear();
            foreach (var l in joints.Values)
            {
                if (l.Count < 2) continue;
                float h = float.MinValue;
                foreach (var (d, start) in l) h = Mathf.Max(h, start ? d.h0 : d.h1);
                foreach (var (d, start) in l) { if (start) d.h0 = h; else d.h1 = h; shared.Add((d, start)); }
            }
            // A road, path or railway passing right next to a free deck end (OSM bridges are often drawn short): with 5 m
            // terrain cells the approach's last cell and the way below cannot both be right, so the deck runs on over
            // the approach until the way below is clear (RoadProfile stops the approach's profile there).
            foreach (var d in decks.Values)
            {
                var wp = d.way.points;
                float e0 = shared.Contains((d, true)) ? 0f : Extension(data, t, d, wp[0], (wp[0] - wp[1]).normalized, d.h0);
                float e1 = shared.Contains((d, false)) ? 0f : Extension(data, t, d, wp[wp.Count - 1], (wp[wp.Count - 1] - wp[wp.Count - 2]).normalized, d.h1);
                if (e0 <= 0f && e1 <= 0f) continue;
                d.Extend(e0, e1);
                Debug.Log($"[OSM] bridge {d.way.id} ({d.way.tags.Get("highway")}) extended by {e0:0}/{e1:0} m: a way passes close below its end");
            }
            below = null;
            return decks.Count;
        }

        /// <summary>Deck ends joined to another deck (viaducts) – no approach road there.</summary>
        static readonly HashSet<(Deck, bool)> shared = new HashSet<(Deck, bool)>();

        /// <summary>Free deck ends (onto an approach road): the way's end point, where the built deck ends (further out
        /// when extended) and the deck surface height there – anchors for <see cref="RoadProfile"/> (the approach's
        /// ground = this minus the road's layer offset).</summary>
        public static IEnumerable<(Vector2 end, Vector2 built, float surface)> FreeEnds()
        {
            foreach (var d in decks.Values)
            {
                var wp = d.way.points;
                if (!shared.Contains((d, true))) yield return (wp[0], d.pts[0], d.h0);
                if (!shared.Contains((d, false))) yield return (wp[wp.Count - 1], d.pts[d.pts.Count - 1], d.h1);
            }
        }

        /// <summary>
        /// Eases each free deck end onto its approach road as it will be draped. Call after every change to the height
        /// field near the bridges (pit fills in <see cref="Prepare"/>, <see cref="RoadProfile"/>) and before the decks are built.
        /// </summary>
        public static void EaseEnds(TerrainField t)
        {
            // Ends onto the approach roads as they will be draped (after all pit fills): ease the deck at ~1:12.
            foreach (var d in decks.Values)
            {
                var pts = d.pts;
                d.g0 = shared.Contains((d, true)) ? d.h0 : t.Height(pts[0].x, pts[0].y) + d.y;
                d.g1 = shared.Contains((d, false)) ? d.h1 : t.Height(pts[pts.Count - 1].x, pts[pts.Count - 1].y) + d.y;
                // Bigger differences are a pit the fill could not reach (a road passing close below): diving into it
                // would be worse – and the step holds cars – so the deck keeps the abutment height there.
                if (Mathf.Abs(d.g0 - d.h0) > MaxStep) { Debug.Log($"[OSM] bridge {d.way.id}: {d.g0 - d.h0:+0.00;-0.00} m step at {pts[0]} left"); d.g0 = d.h0; }
                if (Mathf.Abs(d.g1 - d.h1) > MaxStep) { Debug.Log($"[OSM] bridge {d.way.id}: {d.g1 - d.h1:+0.00;-0.00} m step at {pts[pts.Count - 1]} left"); d.g1 = d.h1; }
                // A short deck may ease over its whole length if only one end needs it (the last span of a viaduct).
                bool both = Mathf.Abs(d.g0 - d.h0) >= MinStep && Mathf.Abs(d.g1 - d.h1) >= MinStep;
                d.r0 = RampLength(d.g0 - d.h0, d.Length * (both ? 0.5f : 1f));
                d.r1 = RampLength(d.g1 - d.h1, d.Length * (both ? 0.5f : 1f));
                if (d.r0 > 0f || d.r1 > 0f)
                    Debug.Log($"[OSM] bridge {d.way.id} ({d.way.tags.Get("highway")}) eased onto the road: {d.g0 - d.h0:+0.00;-0.00} m over {d.r0:0} m at {pts[0]}, " +
                              $"{d.g1 - d.h1:+0.00;-0.00} m over {d.r1:0} m at {pts[pts.Count - 1]}");
            }
        }

        const float MinStep = 0.03f, MaxStep = 1.2f;
        const float MaxExtension = 8f, BelowClear = 5f; // m: deck run-on at most; way below clear of the end by its half width + this

        /// <summary>How far the deck has to run on beyond <paramref name="end"/> (along <paramref name="u"/>) until every
        /// way passing below (its ground ≥ 2.5 m under the deck) is <see cref="BelowClear"/> beyond its edge; 0 = none
        /// or no help (a way running alongside).</summary>
        static float Extension(OsmData data, TerrainField t, Deck d, Vector2 end, Vector2 u, float surface)
        {
            BuildBelow(data);
            var key = Key10(end);
            float ext = 0f;
            foreach (var (o, ob) in below)
            {
                if (o == d.way || end.x < ob.xMin - 30f || end.x > ob.xMax + 30f || end.y < ob.yMin - 30f || end.y > ob.yMax + 30f) continue;
                var op = o.points;
                if (Key10(op[0]) == key || Key10(op[op.Count - 1]) == key) continue; // the approach itself
                float need = GroundBuilder.LineWidth(o.tags) * 0.5f + BelowClear;
                if (Distance(op, end, out var closest) >= need || t.Height(closest.x, closest.y) > surface - d.y - 2.5f) continue;
                float e = 1f;
                while (e <= MaxExtension && Distance(op, end + u * e, out _) < need) e += 1f;
                if (e > MaxExtension) return 0f;
                ext = Mathf.Max(ext, e);
            }
            return ext;
        }

        static Vector2Int Key10(Vector2 p) => new Vector2Int(Mathf.RoundToInt(p.x * 10f), Mathf.RoundToInt(p.y * 10f));

        static float Distance(List<Vector2> pts, Vector2 p, out Vector2 closest)
        {
            float best = float.MaxValue;
            closest = p;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i], ab = pts[i + 1] - a;
                var c = a + ab * Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                float dd = Vector2.Distance(p, c);
                if (dd < best) { best = dd; closest = c; }
            }
            return best;
        }

        static List<(OsmWay way, Rect bounds)> below; // roads/paths/railways on the ground (lazy, per Prepare)

        static void BuildBelow(OsmData data)
        {
            if (below != null) return;
            below = new List<(OsmWay, Rect)>();
            foreach (var o in data.ways)
            {
                if (o.points.Count < 2 || o.tags.Get("highway") == null && o.tags.Get("railway") == null) continue;
                if (GroundBuilder.LineWidth(o.tags) <= 0f || o.tags.Has("bridge") && !o.tags.Is("bridge", "no")) continue;
                below.Add((o, GeoUtil.Bounds(o.points)));
            }
        }

        /// <summary>The bridge crosses a road, path or railway (segments intersect, no shared node).</summary>
        static bool CrossesWay(OsmData data, OsmWay bridge)
        {
            BuildBelow(data);
            var bb = GeoUtil.Bounds(bridge.points);
            var bp = bridge.points;
            foreach (var (o, ob) in below)
            {
                if (o == bridge || !ob.Overlaps(bb)) continue;
                for (int i = 0; i < bp.Count - 1; i++)
                for (int j = 0; j < o.points.Count - 1; j++)
                    if (Intersect(bp[i], bp[i + 1], o.points[j], o.points[j + 1])) return true;
            }
            return false;
        }

        static bool Intersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            Vector2 r = b - a, s = d - c;
            float den = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(den) < 1e-6f) return false;
            float t = ((c.x - a.x) * s.y - (c.y - a.y) * s.x) / den, u = ((c.x - a.x) * r.y - (c.y - a.y) * r.x) / den;
            return t > 0.01f && t < 0.99f && u > 0.01f && u < 0.99f;
        }

        static float RampLength(float step, float max) =>
            Mathf.Abs(step) < MinStep ? 0f : Mathf.Min(Mathf.Max(Mathf.Abs(step) * 12f, 4f), max);

        /// <summary>Highest ground within 25 m outside a bridge end (along <paramref name="u"/>, pointing away from the
        /// bridge); <paramref name="at"/> = its distance.</summary>
        static float Abutment(TerrainField t, Vector2 end, Vector2 u, out float at)
        {
            float best = t.Height(end.x, end.y);
            at = 0f;
            for (float s = 2f; s <= 25f; s += 2f)
            {
                var q = end + u * s;
                float h = t.Height(q.x, q.y);
                if (h > best + 0.05f) { best = h; at = s; }
            }
            return best;
        }

        /// <summary>Raises the height samples in the road strip between a bridge end and its abutment height (only in the
        /// detailed terrain – it is built after this, the coarse one already exists).</summary>
        static void FillPit(TerrainField t, Vector2 end, Vector2 u, float until, float h, float halfWidth, PropPlacer.Clearance keep)
        {
            if (until <= 0f || t.Samples == null) return;
            var s = t.Samples;
            float step = t.Step;
            var nrm = new Vector2(-u.y, u.x);
            float reach = until + halfWidth + step;
            int i0 = Mathf.Max(0, Mathf.FloorToInt((end.x - reach - t.XMin) / step)), i1 = Mathf.Min(t.Nx - 1, Mathf.CeilToInt((end.x + reach - t.XMin) / step));
            int k0 = Mathf.Max(0, Mathf.FloorToInt((end.y - reach - t.ZMin) / step)), k1 = Mathf.Min(t.Nz - 1, Mathf.CeilToInt((end.y + reach - t.ZMin) / step));
            for (int k = k0; k <= k1; k++)
            for (int i = i0; i <= i1; i++)
            {
                var q = new Vector2(t.XMin + i * step, t.ZMin + k * step) - end;
                float along = Vector2.Dot(q, u), lat = Mathf.Abs(Vector2.Dot(q, nrm));
                if (along < -step || along > until || lat > halfWidth) continue;
                var w = end + q;
                if (!AreaMask.FineTerrain(w.x - 1f, w.y - 1f, 2f) || keep.IsBlocked(w, 0f)) continue;
                int idx = k * t.Nx + i;
                s[idx] = Mathf.Max(s[idx], h);
            }
        }

        /// <summary>Deck, kerbs, parapets, slab and pillars of every prepared bridge.</summary>
        public static int Build(OsmData data, MeshSink sink, TerrainField t)
        {
            if (decks.Count == 0) return 0;
            // Everything that drives or flows below: pillars keep off it.
            var below = new PropPlacer.Clearance();
            foreach (var w in data.ways)
            {
                if (decks.ContainsKey(w)) continue;
                float lw = GroundBuilder.LineWidth(w.tags);
                if (lw > 0f) below.AddLine(w.points, lw * 0.5f + 1.2f);
            }
            foreach (var d in decks.Values) BuildDeck(sink, t, d, below);
            return decks.Count;
        }

        static void BuildDeck(MeshSink sink, TerrainField t, Deck d, PropPlacer.Clearance below)
        {
            var pts = d.pts;
            float hw = d.width * 0.5f, ow = hw + Edge;
            var road = WorldStyle.Asphalt;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 dir = pts[i + 1] - pts[i];
                if (dir.sqrMagnitude < 1e-4f) continue;
                dir.Normalize();
                var n = new Vector2(-dir.y, dir.x); // left
                // Straight segments in one piece; where the deck eases onto the road, pieces of ≤ 2 m.
                float sa = d.along[i], sb = d.along[i + 1];
                int pieces = d.Ramped(sa, sb) ? Mathf.Max(1, Mathf.CeilToInt((sb - sa) / 2f)) : 1;
                for (int j = 0; j < pieces; j++)
                {
                    Vector2 a = Vector2.Lerp(pts[i], pts[i + 1], (float)j / pieces), b = Vector2.Lerp(pts[i], pts[i + 1], (float)(j + 1) / pieces);
                    float ya = d.HeightAt(Mathf.Lerp(sa, sb, (float)j / pieces)), yb = d.HeightAt(Mathf.Lerp(sa, sb, (float)(j + 1) / pieces));
                    bool first = i == 0 && j == 0, last = i == pts.Count - 2 && j == pieces - 1;
                    var batch = sink.Get(WorldStyle.MatTerrain, (a + b) * 0.5f);
                    // Road surface + kerb strips (slightly raised).
                    MeshSink.Quad(batch, P(a + n * hw, ya), P(b + n * hw, yb), P(b - n * hw, yb), P(a - n * hw, ya), road);
                    const float kerb = 0.15f;
                    MeshSink.Quad(batch, P(a + n * ow, ya + kerb), P(b + n * ow, yb + kerb), P(b + n * hw, yb + kerb), P(a + n * hw, ya + kerb), Concrete);
                    MeshSink.Quad(batch, P(a - n * hw, ya + kerb), P(b - n * hw, yb + kerb), P(b - n * ow, yb + kerb), P(a - n * ow, ya + kerb), Concrete);
                    // Kerb faces towards the road.
                    MeshSink.Quad(batch, P(a + n * hw, ya), P(a + n * hw, ya + kerb), P(b + n * hw, yb + kerb), P(b + n * hw, yb), ConcreteDark);
                    MeshSink.Quad(batch, P(b - n * hw, yb), P(b - n * hw, yb + kerb), P(a - n * hw, ya + kerb), P(a - n * hw, ya), ConcreteDark);
                    // Parapets (thin walls on the outer edge, both faces + top).
                    foreach (float side in new[] { 1f, -1f })
                    {
                        var o = n * side * ow;
                        var inner = n * side * (ow - 0.25f);
                        Vector3 ao = P(a + o, ya + kerb), bo = P(b + o, yb + kerb), ai = P(a + inner, ya + kerb), bi = P(b + inner, yb + kerb);
                        var up = Vector3.up * Parapet;
                        if (side > 0)
                        {
                            MeshSink.Quad(batch, bo, bo + up, ao + up, ao, Concrete);          // outside (left)
                            MeshSink.Quad(batch, ai, ai + up, bi + up, bi, Concrete);          // inside
                            MeshSink.Quad(batch, ao + up, bo + up, bi + up, ai + up, ConcreteDark);
                        }
                        else
                        {
                            MeshSink.Quad(batch, ao, ao + up, bo + up, bo, Concrete);          // outside (right)
                            MeshSink.Quad(batch, bi, bi + up, ai + up, ai, Concrete);          // inside
                            MeshSink.Quad(batch, ai + up, bi + up, bo + up, ao + up, ConcreteDark);
                        }
                        // Closed ends at both ends of the deck (a car sliding along the guard rail must not slip into the wall).
                        var low = Vector3.down * 0.4f;
                        if (first)
                        {
                            MeshSink.Quad(batch, ai + low, ai + up, ao + up, ao + low, Concrete);
                            MeshSink.Quad(batch, ao + low, ao + up, ai + up, ai + low, Concrete);
                        }
                        if (last)
                        {
                            MeshSink.Quad(batch, bi + low, bi + up, bo + up, bo + low, Concrete);
                            MeshSink.Quad(batch, bo + low, bo + up, bi + up, bi + low, Concrete);
                        }
                    }
                    // Slab: outer faces down to the underside, and the underside itself (seen from the road below).
                    var down = Vector3.down * Slab;
                    Vector3 la = P(a + n * ow, ya + kerb), lb = P(b + n * ow, yb + kerb), ra = P(a - n * ow, ya + kerb), rb = P(b - n * ow, yb + kerb);
                    MeshSink.Quad(batch, lb + down, lb, la, la + down, ConcreteDark);
                    MeshSink.Quad(batch, ra + down, ra, rb, rb + down, ConcreteDark);
                    MeshSink.Quad(batch, la + down, ra + down, rb + down, lb + down, ConcreteDark);
                    // Joint to the next segment (bends): a small fan at the vertex.
                    if (j == pieces - 1 && i + 1 < pts.Count - 1)
                    {
                        var c = pts[i + 1];
                        var next = (pts[i + 2] - c).normalized;
                        var n2 = new Vector2(-next.y, next.x);
                        float yc = d.HeightAt(d.along[i + 1]);
                        MeshSink.Tri(batch, P(c, yc), P(c + n * hw, yc), P(c + n2 * hw, yc), road);
                        MeshSink.Tri(batch, P(c, yc), P(c - n2 * hw, yc), P(c - n * hw, yc), road);
                        MeshSink.Tri(batch, P(c, yc), P(c + n2 * hw, yc), P(c + n * hw, yc), road);
                        MeshSink.Tri(batch, P(c, yc), P(c - n * hw, yc), P(c - n2 * hw, yc), road);
                    }
                }
            }
            // Markings on the deck: MarkingBuilder (same lines as on the road, at deck height).
            Pillars(sink, t, d, below, ow);
        }

        /// <summary>A marking line on the deck at <paramref name="offset"/> (left of the way's direction), dashed when gap &gt; 0.</summary>
        public static void DeckLine(MeshSink sink, Deck d, float offset, float dash, float gap, float halfWidth)
        {
            var pts = d.pts;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                float len = Vector2.Distance(a, b);
                if (len < 0.05f) continue;
                Vector2 dir = (b - a) / len, n = new Vector2(-dir.y, dir.x);
                for (float s = 0f; s < len; s += gap > 0f ? dash + gap : len)
                {
                    float e = gap > 0f ? Mathf.Min(s + dash, len) : len;
                    // Follow the eased ends in pieces of ≤ 2 m.
                    int pieces = d.Ramped(d.along[i] + s, d.along[i] + e) ? Mathf.Max(1, Mathf.CeilToInt((e - s) / 2f)) : 1;
                    for (int k = 0; k < pieces; k++)
                    {
                        float u0 = Mathf.Lerp(s, e, (float)k / pieces), u1 = Mathf.Lerp(s, e, (float)(k + 1) / pieces);
                        Vector2 p0 = a + dir * u0 + n * offset, p1 = a + dir * u1 + n * offset;
                        float y0 = d.HeightAt(d.along[i] + u0) + 0.012f, y1 = d.HeightAt(d.along[i] + u1) + 0.012f;
                        MeshSink.Quad(sink.Get(WorldStyle.MatTerrain, p0), P(p0 + n * halfWidth, y0), P(p1 + n * halfWidth, y1),
                            P(p1 - n * halfWidth, y1), P(p0 - n * halfWidth, y0), WorldStyle.LaneMarking);
                    }
                }
            }
        }

        /// <summary>Pillar rows every ~20 m where the deck is high enough and nothing runs below.</summary>
        static void Pillars(MeshSink sink, TerrainField t, Deck d, PropPlacer.Clearance below, float ow)
        {
            var pts = d.pts;
            float len = d.Length;
            int n = Mathf.FloorToInt(len / 20f);
            for (int k = 1; k <= n; k++)
            {
                float s = len * k / (n + 1);
                int i = 0;
                while (i < pts.Count - 2 && d.along[i + 1] < s) i++;
                float seg = d.along[i + 1] - d.along[i];
                var c = Vector2.Lerp(pts[i], pts[i + 1], seg > 0f ? (s - d.along[i]) / seg : 0f);
                var dir = (pts[i + 1] - pts[i]).normalized;
                var nrm = new Vector2(-dir.y, dir.x);
                float top = d.HeightAt(s) + 0.15f - Slab;
                foreach (float off in new[] { -0.6f, 0.6f })
                {
                    var p = c + nrm * off * (ow - 0.8f);
                    float ground = t.Height(p.x, p.y);
                    if (top - ground < 1.5f || below.IsBlocked(p, 0.6f)) continue;
                    float h = top - ground + 0.5f;
                    FurniturePlacer.Box(sink.Get(WorldStyle.MatTerrain, p), new Vector3(p.x, ground - 0.5f + h * 0.5f, p.y),
                        new Vector3(0.9f, h, 0.9f), Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg, Concrete);
                }
            }
        }

        /// <summary>Highest deck surface above the point (deck incl. kerbs/parapets + margin); false if there is none.</summary>
        public static bool Above(Vector2 p, float margin, out float surface)
        {
            surface = float.MinValue;
            foreach (var d in decks.Values)
            {
                var pts = d.pts;
                float r = d.width * 0.5f + Edge + margin;
                var b = d.bounds;
                if (p.x < b.xMin - r || p.x > b.xMax + r || p.y < b.yMin - r || p.y > b.yMax + r) continue;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Vector2 a = pts[i], ab = pts[i + 1] - a;
                    float tt = Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude);
                    if (tt < 0f || tt > 1f || (a + ab * tt - p).sqrMagnitude >= r * r) continue;
                    surface = Mathf.Max(surface, d.HeightAt(d.along[i] + tt * (d.along[i + 1] - d.along[i])));
                }
            }
            return surface > float.MinValue;
        }

        /// <summary>The point lies on a deck (with margin) – no LiDAR trees there (the surface model sees the bridge).</summary>
        public static bool Covers(Vector2 p, float margin)
        {
            foreach (var d in decks.Values)
            {
                var pts = d.pts;
                float r = d.width * 0.5f + Edge + margin;
                var b = d.bounds;
                if (p.x < b.xMin - r || p.x > b.xMax + r || p.y < b.yMin - r || p.y > b.yMax + r) continue;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Vector2 a = pts[i], ab = pts[i + 1] - a;
                    float tt = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                    if ((a + ab * tt - p).sqrMagnitude < r * r) return true;
                }
            }
            return false;
        }

        static Vector3 P(Vector2 p, float y) => new Vector3(p.x, y, p.y);
    }
}
