using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Recognisable commercial properties. Petrol stations (OSM amenity=fuel, node or area):
    /// <list type="bullet">
    /// <item>canopy (Tankdach) on slim pillars – from the LoD2 "building" ALKIS 2130 Tankstelle / 1610 Überdachung next
    ///   to the station (claimed in <see cref="ClaimLod2"/>, so Lod2Builder builds no box), else an OSM building=roof,
    ///   else a generated one on the paved forecourt; fascia band in brand colours with the brand name</item>
    /// <item>pump islands with dispensers, bollards and lights under the canopy</item>
    /// <item>price pylon (Preismast) at the street edge, clear of carriageways, driveways and buildings</item>
    /// </list>
    /// The shop stays the LoD2/OSM building. Supermarkets: see CommercialBuilder.Retail.cs.
    /// Nothing draws from the shared random sequence (own generator per OSM id),
    /// so generated object ids elsewhere stay stable. Texts go through <see cref="TrafficBuilder.AddText"/>.
    /// </summary>
    public static partial class CommercialBuilder
    {
        public struct Stats { public int fuel, canopiesLod2, canopiesOsm, canopiesGenerated, islands, pylons, markets, shopFronts, stalls; }

        /// <summary>One built station (for snapshots/debugging): canopy centre, pylon and the street it faces.</summary>
        public struct Site { public string name; public Vector2 centre, pylon, street; public float size; }

        public static readonly List<Site> LastSites = new List<Site>();

        class FuelSite
        {
            public long id;
            public Vector2 p;
            public List<Vector2> area;      // mapped as area (else null)
            public Brand brand;
            public string name;
            public readonly List<Canopy> canopies = new List<Canopy>();
        }

        class Canopy { public List<Vector2> ring; public float height; }

        class Brand
        {
            public string text;
            public Color32 band, stripe, textColor;
            public Color32? topStripe;
            /// <summary>Colour for small parts (pump heads, pylon head) – the band unless it is white.</summary>
            public Color32 Accent => band.r > 225 && band.g > 225 && band.b > 225 ? stripe : band;
            /// <summary>Text on an <see cref="Accent"/> background.</summary>
            public Color32 AccentText => Accent.Equals(band) ? textColor : White;
        }

        static OsmData siteData;
        static List<FuelSite> sites = new List<FuelSite>();
        static List<(List<Vector2> ring, Rect bb, bool roof)> osmBuildings = new List<(List<Vector2>, Rect, bool)>();

        // Palette.
        static readonly Color32 White = new Color32(244, 244, 241, 255);
        static readonly Color32 Concrete = new Color32(176, 174, 168, 255);
        static readonly Color32 ConcreteLight = new Color32(198, 196, 190, 255);
        static readonly Color32 Underside = new Color32(222, 223, 225, 255);
        static readonly Color32 LampPanel = new Color32(255, 253, 236, 255);
        static readonly Color32 PumpBody = new Color32(232, 233, 230, 255);
        static readonly Color32 Display = new Color32(34, 38, 44, 255);
        static readonly Color32 Rubber = new Color32(30, 30, 32, 255);
        static readonly Color32 Warning = new Color32(240, 186, 0, 255);
        static readonly Color32 PylonGrey = new Color32(205, 207, 209, 255);
        static readonly Color32 PriceDigits = new Color32(255, 214, 60, 255);
        static readonly Color32 Red = new Color32(214, 16, 32, 255);
        static readonly Color32 Blue = new Color32(14, 64, 150, 255);
        static readonly Color32 Yellow = new Color32(255, 206, 0, 255);

        const byte CodePlain = 255;
        const float SiteRadius = 45f;

        // ================================================================ detection

        static void EnsureSites(OsmData data)
        {
            if (siteData == data) return;
            siteData = data;
            canopiesFromLod2 = 0;
            sites = new List<FuelSite>();
            osmBuildings = new List<(List<Vector2>, Rect, bool)>();
            FindMarkets(data);
            foreach (var n in data.nodes)
                if (n.tags.Is("amenity", "fuel"))
                    sites.Add(NewSite(n.id, data.projection.ToLocal(n.lat, n.lon), null, n.tags));
            foreach (var poly in data.polygons)
                if (poly.tags.Is("amenity", "fuel") && !poly.tags.Has("building"))
                {
                    var ring = PolygonTriangulator.Clean(poly.outer);
                    if (ring.Count < 3) continue;
                    var c = GeoUtil.Centroid(ring);
                    // A node inside the area is the same station: give it the area.
                    var same = sites.Find(s => s.area == null && OsmData.PointInRing(s.p, ring));
                    if (same != null) { same.area = ring; continue; }
                    sites.Add(NewSite(poly.id, c, ring, poly.tags));
                }
            if (sites.Count == 0) return;
            foreach (var poly in data.polygons)
            {
                if (!poly.tags.Has("building")) continue;
                var ring = PolygonTriangulator.Clean(poly.outer);
                if (ring.Count < 3) continue;
                var c = GeoUtil.Centroid(ring);
                if (NearestSite(c, SiteRadius + 20f) == null) continue;
                osmBuildings.Add((ring, GeoUtil.Bounds(ring), poly.tags.Is("building", "roof") || poly.tags.Is("building", "carport")));
            }
        }

        static FuelSite NewSite(long id, Vector2 p, List<Vector2> area, Dictionary<string, string> tags)
        {
            string name = tags.Get("brand") ?? tags.Get("name") ?? tags.Get("operator");
            return new FuelSite { id = id, p = p, area = area, name = name, brand = PickBrand(tags) };
        }

        static FuelSite NearestSite(Vector2 p, float radius)
        {
            FuelSite best = null;
            float bestD = radius;
            foreach (var s in sites)
            {
                float d = s.area != null && OsmData.PointInRing(p, s.area) ? 0f : Vector2.Distance(p, s.p);
                if (d < bestD) { bestD = d; best = s; }
            }
            return best;
        }

        /// <summary>
        /// Called by Lod2Builder for every LoD2 building (heights relative to its ground). True if it is a petrol station
        /// canopy (ALKIS 2130 Tankstelle or 1610 Überdachung, flat, low, next to an amenity=fuel) – Lod2Builder then
        /// builds nothing and the canopy is built here. The shop often carries the same function code: an OSM building
        /// other than building=roof over it, or an address (only the shop has one), keeps it a normal building.
        /// Buildings under a supermarket are remembered for its sign and shop front.
        /// </summary>
        public static bool ClaimLod2(OsmData data, string function, List<string> addresses, List<Vector2> outline, float eave, float height, bool flatRoof)
        {
            if (data == null) return false;
            EnsureSites(data);
            NoteMarketPart(outline, eave, height);
            if (string.IsNullOrEmpty(function) || !flatRoof || height < 2.8f || height > 9f) return false;
            if (!(function == "31001_2130" || function == "51009_1610")) return false;
            if (sites.Count == 0) return false;
            var ring = PolygonTriangulator.Clean(outline);
            if (ring.Count < 3) return false;
            float area = Mathf.Abs(PolygonTriangulator.SignedArea(ring));
            if (area < 25f || area > 1500f) return false;
            var c = GeoUtil.Centroid(ring);
            var site = NearestSite(c, SiteRadius);
            if (site == null) return false;
            switch (OsmBuildingAt(c))
            {
                case false: return false;                       // mapped as a normal building: the shop
                case null when addresses.Count > 0: return false; // unmapped but addressed: the shop
            }
            site.canopies.Add(new Canopy { ring = ring, height = height });
            canopiesFromLod2++;
            return true;
        }

        static int canopiesFromLod2;

        /// <summary>true = an OSM building=roof covers the point, false = another OSM building, null = none.</summary>
        static bool? OsmBuildingAt(Vector2 p)
        {
            bool? result = null;
            foreach (var (ring, bb, roof) in osmBuildings)
            {
                if (!bb.Contains(p) || !OsmData.PointInRing(p, ring)) continue;
                if (roof) return true;
                result = false;
            }
            return result;
        }

        // ================================================================ build

        public static Stats Build(OsmData data, MeshSink sink, AreaRect area)
        {
            EnsureSites(data);
            var st = new Stats { canopiesLod2 = canopiesFromLod2 };
            LastSites.Clear();
            var terrain = TerrainField.Current;

            // Obstacles for pylons and generated canopies: carriageways (incl. driveways), footways, buildings.
            var roads = new List<OsmWay>();
            var clear = new PropPlacer.Clearance();
            foreach (var w in data.ways)
            {
                if (!w.tags.Has("highway") || !area.Overlaps(w.points, 60f)) continue;
                if (GroundBuilder.Carriageway(w.tags, out float width, out _))
                {
                    clear.AddLine(w.points, width * 0.5f);
                    string hw = w.tags.Get("highway");
                    if (hw != "service" && hw != "track") roads.Add(w);
                }
                else
                {
                    float lw = GroundBuilder.LineWidth(w.tags);
                    if (lw > 0f) clear.AddLine(w.points, lw * 0.5f);
                }
            }
            foreach (var o in Lod2Builder.Outlines) clear.AddPolygon(o);
            foreach (var (ring, _, _) in osmBuildings) clear.AddPolygon(ring);

            foreach (var site in sites)
            {
                if (!area.Contains(site.p, -5f)) continue;
                st.fuel++;
                var rng = new System.Random((int)(site.id ^ (site.id >> 32)));
                if (site.canopies.Count == 0)
                {
                    // OSM building=roof at the station (only where LoD2 has no building of its own).
                    foreach (var (ring, _, roof) in osmBuildings)
                    {
                        if (!roof) continue;
                        var c = GeoUtil.Centroid(ring);
                        float a = Mathf.Abs(PolygonTriangulator.SignedArea(ring));
                        if (a < 25f || a > 1500f || NearestSite(c, SiteRadius) != site || Lod2Builder.Covers(ring)) continue;
                        site.canopies.Add(new Canopy { ring = ring, height = 4.6f });
                        st.canopiesOsm++;
                    }
                }
                if (site.canopies.Count == 0 && GenerateCanopy(site, roads, clear) is Canopy gen)
                {
                    site.canopies.Add(gen);
                    clear.AddPolygon(gen.ring);
                    st.canopiesGenerated++;
                }
                Vector2 centre = site.p;
                float size = 10f;
                int islands = 0;
                foreach (var cp in site.canopies)
                {
                    islands += BuildCanopy(sink, cp, site.brand, terrain);
                    centre = GeoUtil.Centroid(cp.ring);
                    size = Mathf.Max(size, Mathf.Sqrt(Mathf.Abs(PolygonTriangulator.SignedArea(cp.ring))));
                }
                Vector2 pylon = Vector2.zero, street = Vector2.zero;
                if (PlacePylon(site, centre, roads, clear, out pylon, out var tangent, out street))
                {
                    BuildPylon(sink, pylon, tangent, site.brand, rng);
                    st.pylons++;
                }
                st.islands += islands;
                Debug.Log($"[OSM] fuel {site.brand.text} ({site.id}): {site.canopies.Count} canopies, {islands} islands, pylon {(st.pylons > 0 && pylon != Vector2.zero ? pylon.ToString() : "-")}");
                LastSites.Add(new Site { name = site.name ?? "fuel", centre = centre, pylon = pylon, street = street, size = size });
            }
            BuildMarkets(data, sink, area, roads, clear, ref st);
            Debug.Log($"[OSM] commercial: {st.fuel} petrol stations, canopies {st.canopiesLod2} LoD2/{st.canopiesOsm} OSM/{st.canopiesGenerated} generated, {st.islands} pump islands, {st.pylons} price pylons; " +
                      $"{st.markets} supermarkets ({st.shopFronts} shop fronts), {st.stalls} parking stall lines");
            return st;
        }

        // ================================================================ canopy

        /// <summary>Canopy roof with fascia, pillars, pump islands, dispensers. Returns the number of islands.</summary>
        static int BuildCanopy(MeshSink sink, Canopy cp, Brand brand, TerrainField terrain)
        {
            var ring = Simplify(cp.ring);
            PolygonTriangulator.EnsureWinding(ring, true);
            var centre = GeoUtil.Centroid(ring);
            float gy = terrain.MinUnder(ring);
            float top = gy + cp.height;
            float fascia = cp.height >= 4f ? 1.0f : 0.8f;
            float bottom = top - fascia;
            var b = sink.Get(WorldStyle.MatBuildings, centre);

            // Deck: roof on top (aerial colour of flat roofs if known), light underside.
            if (PolygonTriangulator.Triangulate(ring, null, out var verts, out var tris))
            {
                Color32 roof = new Color32(168, 170, 172, 255);
                if (AerialImage.Current != null && AerialImage.Current.RoofColor(new List<List<Vector2>> { ring }, true) is Color32 rc) roof = rc;
                MeshSink.FlatPolygon(b, verts, tris, top, WithCode(roof, Lod2Builder.CodeRoofFlat));
                FlatDown(b, verts, tris, bottom, Underside);
            }
            // Fascia: brand band with a stripe along the bottom (and optionally the top).
            float stripe = 0.17f;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 a2 = ring[i], d2 = ring[(i + 1) % ring.Count];
                Vector3 a = V(a2, 0f), d = V(d2, 0f);
                Band(b, a, d, bottom, bottom + stripe, brand.stripe);
                if (brand.topStripe is Color32 ts)
                {
                    Band(b, a, d, bottom + stripe, top - 0.12f, brand.band);
                    Band(b, a, d, top - 0.12f, top, ts);
                }
                else Band(b, a, d, bottom + stripe, top, brand.band);

                // Brand name on every long side, centred in the band.
                float len = (d2 - a2).magnitude;
                float th = fascia * 0.62f;
                float textW = brand.text.Length * th * 0.62f;
                if (len < textW + 1.5f) continue;
                var outN = new Vector2(d2.y - a2.y, a2.x - d2.x) / len;
                var mid = (a2 + d2) * 0.5f + outN * 0.03f;
                float y = (bottom + stripe + top) * 0.5f;
                TrafficBuilder.AddText(new Vector3(mid.x, y - terrain.Height(mid.x, mid.y), mid.y), outN, brand.text, brand.textColor, th);
            }

            // Pump islands along the long axis, in rows across the short one; one pillar in the middle of each island.
            OrientedRect(ring, out var c, out var u, out float len2, out float wid);
            var v = new Vector2(-u.y, u.x);
            int rows = wid >= 9f ? Mathf.Clamp(Mathf.RoundToInt(wid / 6.5f), 2, 4) : 1;
            int segs = Mathf.Clamp(Mathf.FloorToInt(len2 / 7.5f), 1, 4);
            var props = sink.Get(WorldStyle.MatProps, centre);
            int islands = 0;
            for (int r = 0; r < rows; r++)
            for (int s = 0; s < segs; s++)
            {
                float ov = rows == 1 ? 0f : -wid * 0.5f + wid * (r + 0.5f) / rows;
                float ou = -len2 * 0.5f + len2 * (s + 0.5f) / segs;
                var ic = c + u * ou + v * ov;
                float half = Mathf.Min(2.8f, len2 / segs * 0.5f - 1.2f);
                if (half < 1.6f || !OsmData.PointInRing(ic + u * half, ring) || !OsmData.PointInRing(ic - u * half, ring)) continue;
                Island(props, ic, u, half, brand);
                // Pillar (absolute heights: the deck is not lifted per vertex).
                Box(b, ic, u, 0.19f, 0.19f, gy - 0.3f, bottom, White, White);
                Box(b, ic, u, 0.21f, 0.21f, gy - 0.3f, gy + 0.18f + 0.9f, brand.Accent, brand.Accent);
                // Light panels either side of the island.
                foreach (float side in new[] { -1f, 1f })
                {
                    var lp = ic + v * (side * 2.2f);
                    if (OsmData.PointInRing(lp, ring)) DownQuad(b, lp, u, 1.4f, 0.35f, bottom - 0.02f, LampPanel);
                }
                islands++;
            }
            return islands;
        }

        /// <summary>Concrete kerb island with two dispensers and yellow bollards at the ends (Props: lifted onto the terrain).</summary>
        static void Island(MeshSink.Batch p, Vector2 c, Vector2 u, float half, Brand brand)
        {
            var v = new Vector2(-u.y, u.x);
            Box(p, c, u, half, 0.6f, -0.1f, 0.18f, Concrete, ConcreteLight);
            foreach (float end in new[] { -1f, 1f })
                Box(p, c + u * (end * (half - 0.2f)), u, 0.08f, 0.08f, 0.18f, 1.05f, Warning, Rubber);
            foreach (float side in new[] { -1f, 1f })
            {
                var pc = c + u * (side * Mathf.Min(1.45f, half - 0.75f));
                // Dispenser: body, brand-coloured head, displays and holsters on both faces.
                Box(p, pc, u, 0.42f, 0.27f, 0.18f, 1.72f, PumpBody, PumpBody);
                Box(p, pc, u, 0.44f, 0.29f, 1.72f, 2.02f, brand.Accent, brand.Accent);
                Box(p, pc, u, 0.46f, 0.31f, 0.18f, 0.32f, Rubber, Rubber);
                foreach (float face in new[] { -1f, 1f })
                {
                    var f = pc + v * (face * 0.275f);
                    Panel(p, f, u, v * face, 0.26f, 1.28f, 1.58f, Display);
                    Panel(p, f + u * 0.1f, u, v * face, 0.1f, 1.05f, 1.15f, PriceDigitsDim);
                    // Nozzles in holsters left and right of the display.
                    foreach (float s in new[] { -0.3f, 0.3f })
                        Box(p, f + u * s + v * (face * 0.05f), u, 0.05f, 0.05f, 0.8f, 1.2f, Rubber, Rubber);
                }
            }
        }

        static readonly Color32 PriceDigitsDim = new Color32(120, 200, 120, 255);

        // ================================================================ generated canopy

        /// <summary>Station without a mapped canopy: a 14 × 9 m one on the paved forecourt next to the nearest street.</summary>
        static Canopy GenerateCanopy(FuelSite site, List<OsmWay> roads, PropPlacer.Clearance clear)
        {
            if (!NearestRoad(roads, site.p, 60f, out var q, out var t, out _)) return null;
            var n = site.p - q;
            if (n.sqrMagnitude < 0.01f) return null;
            n.Normalize();
            const float hl = 7f, hw = 4.5f;
            for (float back = 0f; back <= 12f; back += 1.5f)
            for (int k = 0; k < 9; k++)
            {
                float along = (k % 2 == 0 ? 1 : -1) * (k + 1) / 2 * 2f;
                var c = site.p + n * back + t * along;
                var ring = new List<Vector2> { c - t * hl - n * hw, c + t * hl - n * hw, c + t * hl + n * hw, c - t * hl + n * hw };
                bool ok = !clear.IsBlocked(c, 0.5f);
                foreach (var corner in ring) ok &= !clear.IsBlocked(corner, 0.5f);
                int paved = 0;
                if (AerialImage.Current != null)
                {
                    foreach (var corner in ring) if (AerialImage.Current.IsPaved(Vector2.Lerp(c, corner, 0.8f), 1.5f)) paved++;
                    ok &= paved >= 3;
                }
                if (ok) return new Canopy { ring = ring, height = 4.6f };
            }
            return null;
        }

        // ================================================================ price pylon

        static bool NearestRoad(List<OsmWay> roads, Vector2 p, float max, out Vector2 q, out Vector2 tangent, out float halfWidth)
        {
            q = p; tangent = Vector2.right; halfWidth = 3f;
            float best = max;
            bool found = false;
            foreach (var w in roads)
            {
                GroundBuilder.Carriageway(w.tags, out float width, out _);
                for (int i = 0; i < w.points.Count - 1; i++)
                {
                    Vector2 a = w.points[i], ab = w.points[i + 1] - a;
                    if (ab.sqrMagnitude < 1e-4f) continue;
                    float tt = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                    var c = a + ab * tt;
                    float d = Vector2.Distance(p, c) - width * 0.5f;
                    if (d < best) { best = d; q = c; tangent = ab.normalized; halfWidth = width * 0.5f; found = true; }
                }
            }
            return found;
        }

        /// <summary>
        /// Spot for the price pylon: beside the street the station faces, on the station's side, 2–5 m outside the
        /// carriageway, clear of driveways, footways, buildings and the canopy; as close to the canopy as possible.
        /// </summary>
        static bool PlacePylon(FuelSite site, Vector2 centre, List<OsmWay> roads, PropPlacer.Clearance clear,
            out Vector2 pos, out Vector2 tangent, out Vector2 street)
        {
            pos = default; street = default;
            if (!NearestRoad(roads, centre, 70f, out var q, out tangent, out float hw)) return false;
            street = q;
            var side = centre - q;
            var n = new Vector2(-tangent.y, tangent.x);
            if (Vector2.Dot(side, n) < 0f) n = -n;
            float bestScore = float.MaxValue;
            bool found = false;
            for (float s = -24f; s <= 24f; s += 1f)
            for (float d = hw + 2.2f; d <= hw + 5.6f; d += 0.8f)
            {
                var p = q + tangent * s + n * d;
                if (clear.IsBlocked(p, 1.1f) || NearRing(p, site.canopies, 3.2f) || NearOutline(p, 1.6f)) continue;
                // Must still be at the station: near the canopy / inside the mapped area.
                if (site.area != null ? !OsmData.PointInRing(p, site.area) && Vector2.Distance(p, centre) > 30f : Vector2.Distance(p, centre) > 34f) continue;
                // Close to the canopy's street front, but a few metres beside it (keeps the view on the canopy free).
                float gap = float.MaxValue;
                foreach (var cp in site.canopies) gap = Mathf.Min(gap, DistToRing(p, cp.ring));
                float score = Mathf.Abs(s) * 0.4f + (d - hw) * 0.8f + Mathf.Max(0f, 6.5f - gap) * 1.5f;
                if (AerialImage.Current != null && !AerialImage.Current.IsPaved(p, 1.2f)) score += 2.5f;
                if (score < bestScore) { bestScore = score; pos = p; found = true; }
            }
            return found;
        }

        static bool NearRing(Vector2 p, List<Canopy> canopies, float dist)
        {
            foreach (var cp in canopies) if (DistToRing(p, cp.ring) < dist) return true;
            return false;
        }

        static bool NearOutline(Vector2 p, float dist)
        {
            foreach (var o in Lod2Builder.Outlines)
                if (o.Count > 2 && Mathf.Abs(o[0].x - p.x) < 80f && Mathf.Abs(o[0].y - p.y) < 80f && DistToRing(p, o) < dist) return true;
            foreach (var (ring, _, _) in osmBuildings) if (DistToRing(p, ring) < dist) return true;
            return false;
        }

        static float DistToRing(Vector2 p, List<Vector2> ring)
        {
            if (OsmData.PointInRing(p, ring)) return 0f;
            float min = float.MaxValue;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                Vector2 a = ring[j], ab = ring[i] - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                min = Mathf.Min(min, Vector2.Distance(p, a + ab * t));
            }
            return min;
        }

        /// <summary>Preismast: slab on a plinth, brand head with the name, three price rows; readable from both directions.</summary>
        static void BuildPylon(MeshSink sink, Vector2 p, Vector2 tangent, Brand brand, System.Random rng)
        {
            var b = sink.Get(WorldStyle.MatProps, p);
            // Slab faces along the street (both faces readable for the traffic); its width runs across the street.
            var u = new Vector2(-tangent.y, tangent.x); // width axis
            const float hw = 0.85f, hd = 0.2f, height = 6.2f, head = 1.5f;
            Box(b, p, u, hw + 0.15f, hd + 0.15f, -0.2f, 0.35f, Concrete, ConcreteLight);
            Box(b, p, u, hw, hd, 0.35f, height - head, PylonGrey, PylonGrey);
            Box(b, p, u, hw + 0.02f, hd + 0.02f, height - head, height, brand.Accent, brand.Accent);
            if (brand.Accent.Equals(brand.band) && !brand.stripe.Equals(brand.band))
                Box(b, p, u, hw + 0.03f, hd + 0.03f, height - head, height - head + 0.14f, brand.stripe, brand.stripe);

            float headText = Mathf.Min(0.75f, 1.35f / Mathf.Max(1, brand.text.Length) / 0.6f);
            string[] labels = { "Super E10", "Super", "Diesel" };
            // Prices from the station's own generator: E10 1,6x–1,8x, Super +6 ct, Diesel −8…−14 ct.
            int e10 = 165 + rng.Next(0, 18);
            int[] cents = { e10, e10 + 6, e10 - 8 - rng.Next(0, 7) };
            foreach (float face in new[] { -1f, 1f })
            {
                var f = tangent * face;
                var c = p + f * (hd + 0.035f);
                // Props are lifted per vertex, texts by the terrain under them: both heights are local.
                TrafficBuilder.AddText(new Vector3(c.x, height - head * 0.5f, c.y), f, brand.text, brand.AccentText, headText);
                for (int i = 0; i < 3; i++)
                {
                    float y = height - head - 0.55f - i * 0.75f;
                    var pc = p + f * (hd + 0.01f);
                    Panel(b, pc, u * face, f, hw - 0.08f, y - 0.3f, y + 0.3f, Display);
                    var lp = c - u * face * 0.46f;
                    TrafficBuilder.AddText(new Vector3(lp.x, y, lp.y), f, labels[i], White, 0.15f);
                    var vp = c + u * face * 0.34f;
                    TrafficBuilder.AddText(new Vector3(vp.x, y, vp.y), f, $"{cents[i] / 100},{cents[i] % 100:00}9", PriceDigits, 0.32f);
                }
            }
        }

        // ================================================================ brands

        static Brand PickBrand(Dictionary<string, string> tags)
        {
            string raw = tags.Get("brand") ?? tags.Get("name") ?? tags.Get("operator") ?? "";
            string k = raw.ToLowerInvariant();
            string all = (k + " " + (tags.Get("name") ?? "") + " " + (tags.Get("operator") ?? "")).ToLowerInvariant();
            bool W(string w) => Regex.IsMatch(all, $@"\b{w}\b");
            Brand B(string text, Color32 band, Color32 stripe, Color32 textColor, Color32? top = null) =>
                new Brand { text = text, band = band, stripe = stripe, textColor = textColor, topStripe = top };
            if (W("esso")) return B("Esso", White, Red, Red, Blue);
            if (W("shell")) return B("Shell", Yellow, Red, Red);
            if (W("aral")) return B("ARAL", new Color32(0, 92, 170, 255), White, White);
            if (W("jet")) return B("JET", Yellow, Blue, Blue);
            if (W("star") || W("orlen")) return B(k.Contains("orlen") ? "ORLEN" : "star", Red, Yellow, White);
            if (W("q1")) return B("Q1", new Color32(0, 70, 150, 255), Yellow, Yellow);
            if (W("score")) return B("SCORE", White, Red, Red, Red);
            if (W("total")) return B("TotalEnergies", White, Red, Red, Blue);
            if (W("avia")) return B("AVIA", White, Red, Red);
            if (W("westfalen")) return B("Westfalen", White, Blue, Blue, Red);
            if (W("raiffeisen")) return B("Raiffeisen", new Color32(0, 122, 62, 255), Yellow, White);
            if (W("hem")) return B("HEM", Red, Yellow, White);
            if (W("eni") || W("agip")) return B("eni", Yellow, Rubber, Rubber);
            if (W("bft")) return B("bft", White, Blue, Blue);
            if (W("hoyer")) return B("HOYER", White, Red, Blue);
            if (W("team")) return B("team", White, Blue, Blue);
            string text = raw.Length > 0 && raw.Length <= 12 ? raw : "Tankstelle";
            return B(text, White, new Color32(30, 62, 112, 255), new Color32(30, 62, 112, 255));
        }

        // ================================================================ geometry helpers

        static Vector3 V(Vector2 p, float y) => new Vector3(p.x, y, p.y);
        static Color32 WithCode(Color32 c, byte code) => new Color32(c.r, c.g, c.b, code);

        /// <summary>Removes nearly collinear vertices (LoD2 outlines often split straight edges).</summary>
        static List<Vector2> Simplify(List<Vector2> ring)
        {
            var r = PolygonTriangulator.Clean(ring);
            bool changed = true;
            while (changed && r.Count > 3)
            {
                changed = false;
                for (int i = 0; i < r.Count && r.Count > 3; i++)
                {
                    Vector2 a = r[(i + r.Count - 1) % r.Count], p = r[i], c = r[(i + 1) % r.Count];
                    var ac = c - a;
                    float len = ac.magnitude;
                    if (len < 1e-3f) { r.RemoveAt(i); changed = true; break; }
                    float dist = Mathf.Abs(ac.x * (p.y - a.y) - ac.y * (p.x - a.x)) / len;
                    if (dist < 0.15f || (p - a).magnitude < 0.3f) { r.RemoveAt(i); changed = true; break; }
                }
            }
            return r;
        }

        /// <summary>Minimum-area bounding rectangle (edge directions of the ring): centre, long axis u, length ≥ width.</summary>
        static void OrientedRect(List<Vector2> ring, out Vector2 centre, out Vector2 u, out float length, out float width)
        {
            centre = GeoUtil.Centroid(ring); u = Vector2.right; length = width = 0f;
            float bestArea = float.MaxValue;
            for (int i = 0; i < ring.Count; i++)
            {
                var e = ring[(i + 1) % ring.Count] - ring[i];
                if (e.sqrMagnitude < 0.25f) continue;
                e.Normalize();
                var n = new Vector2(-e.y, e.x);
                float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
                foreach (var p in ring)
                {
                    float a = Vector2.Dot(p, e), bb = Vector2.Dot(p, n);
                    minA = Mathf.Min(minA, a); maxA = Mathf.Max(maxA, a);
                    minB = Mathf.Min(minB, bb); maxB = Mathf.Max(maxB, bb);
                }
                float area = (maxA - minA) * (maxB - minB);
                if (area >= bestArea) continue;
                bestArea = area;
                centre = e * ((minA + maxA) * 0.5f) + n * ((minB + maxB) * 0.5f);
                if (maxA - minA >= maxB - minB) { u = e; length = maxA - minA; width = maxB - minB; }
                else { u = n; length = maxB - minB; width = maxA - minA; }
            }
        }

        /// <summary>Box around centre <paramref name="c"/> with axes u (half <paramref name="hu"/>) and its left normal (half <paramref name="hv"/>).</summary>
        static void Box(MeshSink.Batch b, Vector2 c, Vector2 u, float hu, float hv, float y0, float y1, Color32 side, Color32 top)
        {
            var v = new Vector2(-u.y, u.x);
            var q = new[] { c - u * hu - v * hv, c + u * hu - v * hv, c + u * hu + v * hv, c - u * hu + v * hv }; // CCW from above
            var up = Vector3.up * (y1 - y0);
            for (int i = 0; i < 4; i++)
            {
                Vector3 a = V(q[i], y0), d = V(q[(i + 1) % 4], y0);
                MeshSink.Quad(b, a, a + up, d + up, d, side);
            }
            MeshSink.Quad(b, V(q[0], y1), V(q[3], y1), V(q[2], y1), V(q[1], y1), top);
        }

        /// <summary>Vertical band between heights y0..y1 along edge a→d of a CCW ring (faces outwards).</summary>
        static void Band(MeshSink.Batch b, Vector3 a, Vector3 d, float y0, float y1, Color32 c)
        {
            Vector3 a0 = new Vector3(a.x, y0, a.z), d0 = new Vector3(d.x, y0, d.z);
            var up = Vector3.up * (y1 - y0);
            MeshSink.Quad(b, a0, a0 + up, d0 + up, d0, c);
        }

        /// <summary>Flat panel on a vertical face: centre c on the face, along = width axis, normal = facing.</summary>
        static void Panel(MeshSink.Batch b, Vector2 c, Vector2 along, Vector2 normal, float half, float y0, float y1, Color32 col)
        {
            var o = c + normal * 0.012f;
            // Order so that the quad faces along normal: (left-bottom, left-top, right-top, right-bottom) seen from the front.
            var right = new Vector2(normal.y, -normal.x); // viewer's right when looking against normal
            if (Vector2.Dot(right, along) < 0f) along = -along;
            Vector3 l0 = V(o - along * half, y0), r0 = V(o + along * half, y0);
            var up = Vector3.up * (y1 - y0);
            MeshSink.Quad(b, r0, r0 + up, l0 + up, l0, col);
        }

        /// <summary>Downward-facing rectangle (lamp panel under the canopy).</summary>
        static void DownQuad(MeshSink.Batch b, Vector2 c, Vector2 u, float hu, float hv, float y, Color32 col)
        {
            var v = new Vector2(-u.y, u.x);
            var q = new[] { c - u * hu - v * hv, c + u * hu - v * hv, c + u * hu + v * hv, c - u * hu + v * hv };
            MeshSink.Quad(b, V(q[0], y), V(q[1], y), V(q[2], y), V(q[3], y), col);
        }

        /// <summary>Horizontal polygon facing down (CCW triangles from the triangulator kept as they are).</summary>
        static void FlatDown(MeshSink.Batch b, List<Vector2> verts, List<int> ccwTris, float y, Color32 color)
        {
            int baseIndex = b.vertices.Count;
            foreach (var p in verts)
            {
                b.vertices.Add(new Vector3(p.x, y, p.y));
                b.normals.Add(Vector3.down);
                b.colors.Add(color);
                b.uvs.Add(MeshSink.NoFacade);
            }
            foreach (var t in ccwTris) b.triangles.Add(baseIndex + t);
        }
    }
}
