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
    /// Call <see cref="Prepare"/> after CoarseTerrain (final ground heights) and before GroundBuilder.BuildLines, which
    /// skips the decks.
    /// </summary>
    public static class BridgeBuilder
    {
        public class Deck
        {
            public OsmWay way;
            public float[] along;   // distance from the first point per point
            public float h0, h1;    // surface height at both ends
            public float width, y;  // carriageway width, layer offset of the road style
            public Rect bounds;     // of the centre line
            public float Length => along[along.Length - 1];
            public float HeightAt(float s) => Mathf.Lerp(h0, h1, Length > 0f ? s / Length : 0f);
        }

        const float MinGap = 1.8f;      // m between a straight deck and the ground to count as a bridge
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
            if (t.IsFlat) return 0;
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
                var d = new Deck { way = way, along = along, width = width, y = y, bounds = GeoUtil.Bounds(pts),
                                   h0 = t.Height(pts[0].x, pts[0].y) + y, h1 = t.Height(pts[pts.Count - 1].x, pts[pts.Count - 1].y) + y };
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
                if (gap >= MinGap) decks[way] = d;
            }
            return decks.Count;
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
            var pts = d.way.points;
            float hw = d.width * 0.5f, ow = hw + Edge;
            var road = WorldStyle.Asphalt;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                Vector2 dir = b - a;
                if (dir.sqrMagnitude < 1e-4f) continue;
                dir.Normalize();
                var n = new Vector2(-dir.y, dir.x); // left
                float ya = d.HeightAt(d.along[i]), yb = d.HeightAt(d.along[i + 1]);
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
                }
                // Slab: outer faces down to the underside, and the underside itself (seen from the road below).
                var down = Vector3.down * Slab;
                Vector3 la = P(a + n * ow, ya + kerb), lb = P(b + n * ow, yb + kerb), ra = P(a - n * ow, ya + kerb), rb = P(b - n * ow, yb + kerb);
                MeshSink.Quad(batch, lb + down, lb, la, la + down, ConcreteDark);
                MeshSink.Quad(batch, ra + down, ra, rb, rb + down, ConcreteDark);
                MeshSink.Quad(batch, la + down, ra + down, rb + down, lb + down, ConcreteDark);
                // Joint to the next segment (bends): a small fan at the vertex.
                if (i + 1 < pts.Count - 1)
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
            if (d.width >= 5f) CentreLine(sink, d);
            Pillars(sink, t, d, below, ow);
        }

        /// <summary>Dashed centre line of two-way roads (one-way carriageways get their markings from MotorwayBuilder).</summary>
        static void CentreLine(MeshSink sink, Deck d)
        {
            var tags = d.way.tags;
            string hw = tags.Get("highway");
            if (tags.Is("oneway", "yes") || hw == "motorway" || hw == "motorway_link" || tags.Is("lanes", "1")) return;
            DeckLine(sink, d, 0f, 3f, 4f, 0.08f);
        }

        /// <summary>A marking line on the deck at <paramref name="offset"/> (left of the way's direction), dashed when gap &gt; 0.</summary>
        public static void DeckLine(MeshSink sink, Deck d, float offset, float dash, float gap, float halfWidth)
        {
            var pts = d.way.points;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                float len = Vector2.Distance(a, b);
                if (len < 0.05f) continue;
                Vector2 dir = (b - a) / len, n = new Vector2(-dir.y, dir.x);
                for (float s = 0f; s < len; s += gap > 0f ? dash + gap : len)
                {
                    float e = gap > 0f ? Mathf.Min(s + dash, len) : len;
                    Vector2 p0 = a + dir * s + n * offset, p1 = a + dir * e + n * offset;
                    float y0 = d.HeightAt(d.along[i] + s) + 0.012f, y1 = d.HeightAt(d.along[i] + e) + 0.012f;
                    MeshSink.Quad(sink.Get(WorldStyle.MatTerrain, p0), P(p0 + n * halfWidth, y0), P(p1 + n * halfWidth, y1),
                        P(p1 - n * halfWidth, y1), P(p0 - n * halfWidth, y0), WorldStyle.LaneMarking);
                }
            }
        }

        /// <summary>Pillar rows every ~20 m where the deck is high enough and nothing runs below.</summary>
        static void Pillars(MeshSink sink, TerrainField t, Deck d, PropPlacer.Clearance below, float ow)
        {
            var pts = d.way.points;
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

        /// <summary>The point lies on a deck (with margin) – no LiDAR trees there (the surface model sees the bridge).</summary>
        public static bool Covers(Vector2 p, float margin)
        {
            foreach (var d in decks.Values)
            {
                var pts = d.way.points;
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
