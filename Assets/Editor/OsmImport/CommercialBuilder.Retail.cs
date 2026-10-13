using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Supermarkets (shop=supermarket / building=supermarket): the building stays the LoD2/OSM one; on the wall facing
    /// its car park (else the street) it gets a glass shop front with entrance and canopy plus a brand sign; parking
    /// stall lines are painted along the aisles of the car parks next to it.
    /// </summary>
    public static partial class CommercialBuilder
    {
        class Market
        {
            public long id;
            public string name;
            public Brand brand;
            public Vector2 p;
            public List<Vector2> ring;      // OSM outline (building or shop area), null for a lone node
            public readonly List<(List<Vector2> ring, float eave, float height)> parts = new List<(List<Vector2>, float, float)>();
        }

        /// <summary>Built supermarkets (snapshots): centre = middle of the shop front, street = a point 25 m in front of it.</summary>
        public static readonly List<Site> LastMarkets = new List<Site>();

        static List<Market> markets = new List<Market>();

        static readonly Color32 Glass = new Color32(50, 68, 82, 255);
        static readonly Color32 DoorGlass = new Color32(32, 44, 54, 255);
        static readonly Color32 Aluminium = new Color32(188, 192, 196, 255);

        static void FindMarkets(OsmData data)
        {
            markets = new List<Market>();
            foreach (var poly in data.polygons)
            {
                if (!(poly.tags.Is("shop", "supermarket") || poly.tags.Is("building", "supermarket"))) continue;
                var ring = PolygonTriangulator.Clean(poly.outer);
                if (ring.Count < 3) continue;
                markets.Add(new Market { id = poly.id, name = poly.tags.Get("name"), brand = PickMarketBrand(poly.tags), p = GeoUtil.Centroid(ring), ring = ring });
            }
            foreach (var n in data.nodes)
            {
                if (!n.tags.Is("shop", "supermarket")) continue;
                var p = data.projection.ToLocal(n.lat, n.lon);
                if (markets.Exists(m => m.ring != null && OsmData.PointInRing(p, m.ring))) continue;
                // The building it is in (if mapped).
                List<Vector2> ring = null;
                foreach (var poly in data.polygons)
                    if (poly.tags.Has("building") && OsmData.PointInRing(p, poly.outer)) { ring = PolygonTriangulator.Clean(poly.outer); break; }
                markets.Add(new Market { id = n.id, name = n.tags.Get("name"), brand = PickMarketBrand(n.tags), p = p, ring = ring });
            }
        }

        /// <summary>LoD2 building under a supermarket: remembered as one of its parts.</summary>
        static void NoteMarketPart(List<Vector2> outline, float eave, float height)
        {
            if (markets.Count == 0 || outline == null || outline.Count < 3) return;
            var c = GeoUtil.Centroid(outline);
            foreach (var m in markets)
            {
                if ((m.p - c).sqrMagnitude > 120f * 120f) continue;
                bool hit = m.ring != null ? OsmData.PointInRing(c, m.ring) || OsmData.PointInRing(GeoUtil.Centroid(m.ring), outline)
                                          : OsmData.PointInRing(m.p, outline);
                if (hit) m.parts.Add((PolygonTriangulator.Clean(outline), eave, height));
            }
        }

        static void BuildMarkets(OsmData data, MeshSink sink, AreaRect area, List<OsmWay> roads, PropPlacer.Clearance clear, ref Stats st)
        {
            LastMarkets.Clear();
            if (markets.Count == 0) return;
            var terrain = TerrainField.Current;
            var parkings = new List<(long id, List<Vector2> ring)>();
            foreach (var poly in data.polygons)
            {
                if (!poly.tags.Is("amenity", "parking")) continue;
                string kind = poly.tags.Get("parking");
                if (kind != null && kind != "surface") continue;
                var r = PolygonTriangulator.Clean(poly.outer);
                if (r.Count >= 3) parkings.Add((poly.id, r));
            }
            var aisles = new List<OsmWay>();
            foreach (var w in data.ways) if (w.tags.Is("highway", "service") && !w.tags.Is("area", "yes")) aisles.Add(w);
            var painted = new HashSet<long>();

            foreach (var m in markets)
            {
                if (!area.Contains(m.p, -5f)) continue;
                // Main part: the largest LoD2 building, else the OSM outline (extruded by BuildingBuilder).
                List<Vector2> ring = null;
                float eave = 0f, best = 0f;
                foreach (var (r, e, h) in m.parts)
                {
                    float a = Mathf.Abs(PolygonTriangulator.SignedArea(r));
                    if (a > best && e > 2.4f) { best = a; ring = r; eave = e; }
                }
                if (ring == null && m.ring != null && m.parts.Count == 0)
                {
                    ring = m.ring;
                    eave = 4.5f;
                }
                if (ring == null || Mathf.Abs(PolygonTriangulator.SignedArea(ring)) < 150f) continue;
                ring = Simplify(ring);
                PolygonTriangulator.EnsureWinding(ring, true);
                var centre = GeoUtil.Centroid(ring);

                // Car parks next to it; the shop front faces the nearest one (else the street).
                // Big and close car parks weigh most.
                var near = new List<(long id, List<Vector2> ring)>();
                Vector2? target = null;
                Vector2 sum = Vector2.zero;
                float weight = 0f;
                foreach (var pk in parkings)
                {
                    float d = RingGap(ring, pk.ring);
                    if (d > 40f) continue;
                    near.Add(pk);
                    float w = Mathf.Abs(PolygonTriangulator.SignedArea(pk.ring)) / (5f + d);
                    sum += GeoUtil.Centroid(pk.ring) * w;
                    weight += w;
                }
                if (weight > 0f) target = sum / weight;
                else if (NearestRoad(roads, centre, 80f, out var q, out _, out _)) target = q;
                if (target == null) continue;
                var want = (target.Value - centre).normalized;

                int edge = -1;
                float bestScore = 0f, run0 = 0f, run1 = 0f;
                for (int i = 0; i < ring.Count; i++)
                {
                    Vector2 a = ring[i], d = ring[(i + 1) % ring.Count];
                    float len = (d - a).magnitude;
                    if (len < 8f) continue;
                    var outN = new Vector2(d.y - a.y, a.x - d.x) / len;
                    float dot = Vector2.Dot(outN, want);
                    if (dot < 0.3f) continue;
                    // Longest stretch that is open in front (not a party wall to the next building).
                    float r0 = 0f, r1 = 0f, cur = -1f;
                    for (float s = 0.5f; s <= len; s += 1f)
                    {
                        bool open = s < len - 0.4f && !InAnyOutline(a + (d - a) * (s / len) + outN * 2f);
                        if (open && cur < 0f) cur = s - 0.5f;
                        if (!open && cur >= 0f) { if (s - 0.5f - cur > r1 - r0) { r0 = cur; r1 = s - 0.5f; } cur = -1f; }
                    }
                    if (cur >= 0f && len - cur > r1 - r0) { r0 = cur; r1 = len; }
                    if (r1 - r0 < 8f) continue;
                    float score = (r1 - r0) * dot * dot * dot * dot; // facing matters more than length
                    if (score > bestScore) { bestScore = score; edge = i; run0 = r0; run1 = r1; }
                }
                if (edge < 0) continue;
                st.markets++;
                Vector2 e0 = ring[edge], e1 = ring[(edge + 1) % ring.Count];
                var et = (e1 - e0).normalized;
                (e0, e1) = (e0 + et * run0, e0 + et * run1);
                float gy = terrain.MinUnder(ring);
                var front = ShopFront(sink, e0, e1, eave, gy, target.Value, m.brand, terrain);
                st.shopFronts++;
                var n2 = new Vector2(e1.y - e0.y, e0.x - e1.x).normalized;
                Debug.Log($"[OSM] supermarket {m.brand.text} ({m.id}): {m.parts.Count} LoD2 parts, main {Mathf.Abs(PolygonTriangulator.SignedArea(ring)):0} m² eave {eave:0.0} m, front at {front}, {near.Count} car parks");
                LastMarkets.Add(new Site { name = m.name ?? "market", centre = front, street = front + n2 * 25f, size = (e1 - e0).magnitude });

                foreach (var pk in near)
                    if (painted.Add(pk.id)) st.stalls += Stalls(sink, pk.ring, aisles, clear);
            }
        }

        /// <summary>
        /// Glass front with mullions, sliding door and entrance canopy, brand sign above; on wall e0→e1 of a CCW ring
        /// (outside = right of the edge). The entrance sits towards the car park. Returns the entrance position.
        /// </summary>
        static Vector2 ShopFront(MeshSink sink, Vector2 e0, Vector2 e1, float eave, float gy, Vector2 parking, Brand brand, TerrainField terrain)
        {
            float len = (e1 - e0).magnitude;
            var t = (e1 - e0) / len;
            var outN = new Vector2(t.y, -t.x);
            var b = sink.Get(WorldStyle.MatBuildings, (e0 + e1) * 0.5f);

            // Entrance a third along the wall, from the end nearer the car park.
            float sp = Mathf.Clamp(Vector2.Dot(parking - e0, t), 0f, len);
            float se = sp < len * 0.5f ? len * 0.3f : len * 0.7f;
            float g = Mathf.Min(len * 0.45f, 14f);
            float s0 = Mathf.Max(0.6f, se - g * 0.5f), s1 = Mathf.Min(len - 0.6f, se + g * 0.5f);
            se = (s0 + s1) * 0.5f;
            Vector2 At(float s, float off) => e0 + t * s + outN * off;

            float glassTop = Mathf.Clamp(eave - 0.9f, 2.4f, 3.0f);
            float y0 = gy + 0.12f, y1 = gy + glassTop;
            // Glass band a little in front of the wall (clears the runtime window frames), aluminium frame and mullions.
            const float off = 0.14f;
            Panel(b, At((s0 + s1) * 0.5f, off), t, outN, (s1 - s0) * 0.5f, y0, y1, Glass);
            Box(b, At((s0 + s1) * 0.5f, off * 0.5f), t, (s1 - s0) * 0.5f + 0.08f, off * 0.5f + 0.04f, y1, y1 + 0.16f, Aluminium, Aluminium);
            Box(b, At((s0 + s1) * 0.5f, off * 0.5f), t, (s1 - s0) * 0.5f + 0.08f, off * 0.5f + 0.04f, gy - 0.2f, y0, Aluminium, Aluminium);
            const float doorHalf = 1.3f;
            int bays = Mathf.Max(1, Mathf.RoundToInt((s1 - s0) / 1.6f));
            for (int i = 0; i <= bays; i++)
            {
                float s = s0 + (s1 - s0) * i / bays;
                if (Mathf.Abs(s - se) < doorHalf + 0.3f) continue;
                Box(b, At(s, off * 0.5f), t, 0.05f, off * 0.5f + 0.03f, y0, y1, Aluminium, Aluminium);
            }
            // Sliding door: darker glass, frame, and a flat entrance canopy above it.
            Panel(b, At(se, off + 0.02f), t, outN, doorHalf, y0, gy + 2.35f, DoorGlass);
            foreach (float side in new[] { -1f, 1f })
                Box(b, At(se + side * (doorHalf + 0.05f), off * 0.5f), t, 0.07f, off * 0.5f + 0.05f, y0, y1, Aluminium, Aluminium);
            Box(b, At(se, off + 1.0f), t, doorHalf + 1.2f, 1.0f, y1 + 0.2f, y1 + 0.45f, White, White);
            Box(b, At(se, off + 2.0f), t, doorHalf + 1.25f, 0.05f, y1 + 0.18f, y1 + 0.47f, brand.Accent, brand.Accent);

            // Sign: on the wall above the glass, or standing over the roof edge on low buildings.
            float signLen = Mathf.Clamp(g * 0.8f, 6f, 12f);
            float sb, stp;
            if (eave >= glassTop + 1.7f) { stp = eave - 0.15f; sb = Mathf.Max(glassTop + 0.6f, stp - 1.3f); }
            else { sb = eave - 0.35f; stp = eave + 1.0f; }
            Box(b, At(se, 0.13f), t, signLen * 0.5f, 0.13f, gy + sb, gy + stp, brand.band, brand.band);
            Box(b, At(se, 0.14f), t, signLen * 0.5f + 0.01f, 0.14f, gy + sb - 0.01f, gy + sb + 0.14f, brand.stripe, brand.stripe);
            float th = Mathf.Min((stp - sb) * 0.62f, signLen * 0.85f / Mathf.Max(1, brand.text.Length) / 0.62f);
            var tp = At(se, 0.29f);
            float ty = gy + (sb + 0.14f + stp) * 0.5f;
            TrafficBuilder.AddText(new Vector3(tp.x, ty - terrain.Height(tp.x, tp.y), tp.y), outN, brand.text, brand.textColor, th);
            return At(se, 0f);
        }

        /// <summary>White stall lines (5 m, every 2.5 m) on both sides of the service ways crossing a car park.</summary>
        static int Stalls(MeshSink sink, List<Vector2> park, List<OsmWay> aisles, PropPlacer.Clearance clear)
        {
            var bb = GeoUtil.Bounds(park);
            bb.xMin -= 3f; bb.yMin -= 3f; bb.xMax += 3f; bb.yMax += 3f;
            var b = sink.Get(WorldStyle.MatGround, GeoUtil.Centroid(park));
            int count = 0;
            const float pitch = 2.5f, depth = 5f, y = WorldStyle.YLeisure + 0.03f;
            var done = new List<Vector2>();
            foreach (var w in aisles)
            {
                GroundBuilder.Carriageway(w.tags, out float width, out _);
                float hw = Mathf.Max(2f, width * 0.5f);
                for (int i = 0; i < w.points.Count - 1; i++)
                {
                    Vector2 a = w.points[i], c = w.points[i + 1];
                    if (!bb.Contains(a) && !bb.Contains(c)) continue;
                    float len = (c - a).magnitude;
                    if (len < pitch) continue;
                    var t = (c - a) / len;
                    var n = new Vector2(-t.y, t.x);
                    for (float s = pitch * 0.5f; s <= len - pitch * 0.5f + 1e-3f; s += pitch)
                    {
                        var q = a + t * s;
                        if (!OsmData.PointInRing(q, park)) continue;
                        foreach (float side in new[] { -1f, 1f })
                        {
                            var p0 = q + n * (side * (hw + 0.1f));
                            var p1 = q + n * (side * (hw + depth));
                            var pm = (p0 + p1) * 0.5f;
                            if (!OsmData.PointInRing(p0, park) || !OsmData.PointInRing(p1, park)) continue;
                            // Not into buildings / other carriageways (this aisle ends where the stall starts).
                            if (clear.IsBlocked(pm, 0.3f) || clear.IsBlocked(p1, 0.2f)) continue;
                            if (done.Exists(o => (o - pm).sqrMagnitude < 1.2f * 1.2f)) continue; // aisles meeting at a corner
                            done.Add(pm);
                            GroundLine(b, p0, p1, 0.12f, y, WorldStyle.LaneMarking);
                            count++;
                        }
                    }
                }
            }
            if (count > 0) return count;

            // No aisle mapped through it: one row of stalls along each long side (two if it is wide enough).
            var ring = Simplify(park);
            OrientedRect(ring, out var c0, out var u, out float len2, out float wid);
            if (wid < 4.6f || len2 < 5f) return 0;
            var v = new Vector2(-u.y, u.x);
            float rowDepth = Mathf.Min(depth, wid - 0.3f);
            var rows = wid >= 2f * depth + 3f ? new[] { -1f, 1f } : new[] { -1f };
            foreach (float side in rows)
            {
                var edge = c0 + v * (side * wid * 0.5f);
                for (float s = -len2 * 0.5f + 0.4f; s <= len2 * 0.5f - 0.4f + 1e-3f; s += pitch)
                {
                    var p0 = edge + u * s - v * (side * 0.3f);
                    var p1 = edge + u * s - v * (side * rowDepth);
                    if (!OsmData.PointInRing(p0, park) || !OsmData.PointInRing(p1, park)) continue;
                    if (clear.IsBlocked((p0 + p1) * 0.5f, 0.3f)) continue;
                    GroundLine(b, p0, p1, 0.12f, y, WorldStyle.LaneMarking);
                    count++;
                }
            }
            return count;
        }

        static void GroundLine(MeshSink.Batch b, Vector2 a, Vector2 c, float width, float y, Color32 col)
        {
            var t = (c - a).normalized;
            var n = new Vector2(-t.y, t.x) * (width * 0.5f);
            Vector3 p0 = V(a - n, y), p1 = V(c - n, y), p2 = V(c + n, y), p3 = V(a + n, y);
            if (Vector3.Cross(p1 - p0, p2 - p0).y < 0f) MeshSink.Quad(b, p0, p1, p2, p3, col);
            else MeshSink.Quad(b, p0, p3, p2, p1, col);
        }

        static bool InAnyOutline(Vector2 p)
        {
            foreach (var o in Lod2Builder.Outlines)
                if (o.Count > 2 && Mathf.Abs(o[0].x - p.x) < 150f && Mathf.Abs(o[0].y - p.y) < 150f && OsmData.PointInRing(p, o)) return true;
            return false;
        }

        /// <summary>Smallest distance between two rings (0 if they overlap).</summary>
        static float RingGap(List<Vector2> a, List<Vector2> b)
        {
            float min = float.MaxValue;
            foreach (var p in a) min = Mathf.Min(min, DistToRing(p, b));
            foreach (var p in b) min = Mathf.Min(min, DistToRing(p, a));
            return min;
        }

        static Brand PickMarketBrand(Dictionary<string, string> tags)
        {
            string raw = tags.Get("brand") ?? tags.Get("name") ?? "";
            string all = (raw + " " + (tags.Get("name") ?? "")).ToLowerInvariant();
            bool W(string w) => Regex.IsMatch(all, $@"\b{w}\b");
            Brand B(string text, Color32 band, Color32 stripe, Color32 textColor) =>
                new Brand { text = text, band = band, stripe = stripe, textColor = textColor };
            if (W("lidl")) return B("Lidl", new Color32(0, 80, 170, 255), Red, Yellow);
            if (W("aldi")) return B("ALDI", new Color32(0, 42, 110, 255), new Color32(0, 160, 225, 255), White);
            if (W("netto")) return B("Netto", Yellow, Red, Red);
            if (W("penny")) return B("PENNY", Red, Yellow, White);
            if (W("rewe")) return B("REWE", Red, White, White);
            if (W("edeka")) return B("EDEKA", new Color32(26, 66, 138, 255), Yellow, Yellow);
            if (W("kaufland")) return B("Kaufland", Red, White, White);
            if (W("famila")) return B("famila", new Color32(0, 90, 160, 255), Red, White);
            if (W("combi")) return B("combi", Red, Yellow, White);
            if (W("markant")) return B("Markant", Red, White, White);
            string text = raw.Length > 0 && raw.Length <= 14 ? raw : "Markt";
            return B(text, White, new Color32(30, 62, 112, 255), new Color32(30, 62, 112, 255));
        }
    }
}
