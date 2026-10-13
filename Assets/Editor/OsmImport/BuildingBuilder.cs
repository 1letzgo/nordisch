using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>Extrudes OSM building footprints into low-poly walls + roofs.</summary>
    public static class BuildingBuilder
    {
        public const float LevelHeight = 3f;
        const float MaxPitchedArea = 260f;

        /// <param name="skip">Optional filter, e.g. <see cref="Lod2Builder.Covers"/> to only add buildings LoD2 lacks.</param>
        public static int Build(OsmData data, MeshSink sink, AreaRect area, System.Random rng, System.Func<List<Vector2>, bool> skip = null)
        {
            // Outlines that contain building:part pieces are drawn via their parts instead.
            var partCentroids = new List<Vector2>();
            foreach (var poly in data.polygons)
                if (poly.tags.Has("building:part") && !poly.tags.Is("building:part", "no"))
                    partCentroids.Add(GeoUtil.Centroid(poly.outer));

            osm = data;
            int count = 0;
            foreach (var poly in data.polygons)
            {
                bool isPart = poly.tags.Has("building:part") && !poly.tags.Is("building:part", "no");
                bool isBuilding = poly.tags.Has("building") && !poly.tags.Is("building", "no");
                if (!isPart && !isBuilding) continue;
                if (poly.tags.Is("location", "underground") || poly.tags.Is("building", "roof") && !isPart) continue;
                if (!area.Overlaps(poly.outer)) continue;
                if (skip != null && skip(poly.outer)) continue;

                if (!isPart && partCentroids.Count > 0)
                {
                    var outerClean = PolygonTriangulator.Clean(poly.outer);
                    bool hasParts = false;
                    foreach (var c in partCentroids)
                        if (OsmData.PointInRing(c, outerClean)) { hasParts = true; break; }
                    if (hasParts) continue;
                }

                if (BuildOne(poly, sink, rng)) count++;
            }
            return count;
        }

        static OsmData osm; // streets for the door side

        static bool BuildOne(OsmPolygon poly, MeshSink sink, System.Random rng)
        {
            var outer = PolygonTriangulator.Clean(poly.outer);
            if (outer.Count < 3) return false;
            PolygonTriangulator.EnsureWinding(outer, true);
            float area = PolygonTriangulator.SignedArea(outer);
            if (area < 4f) return false;

            var tags = poly.tags;
            float minHeight = GeoUtil.ParseMeters(tags.Get("min_height")) ??
                              (GeoUtil.ParseMeters(tags.Get("building:min_level")) ?? 0f) * LevelHeight;
            float height = EstimateHeight(tags, area, rng);
            if (height <= minHeight + 0.5f) height = minHeight + 3f;

            // Hand tweaks (Data/<area>.buildings.json); random picks below still happen so the sequence stays the same.
            string houseId = "osm:" + poly.id;
            var addresses = BuildingOverrides.OsmAddresses(tags);
            var custom = BuildingOverrides.Find(houseId, addresses);

            string roofShape = custom?.roofShape ?? tags.Get("roof:shape");
            float roofHeight = GeoUtil.ParseMeters(tags.Get("roof:height")) ?? 0f;
            bool wantsPitched = roofShape == "gabled" || roofShape == "hipped" || roofShape == "pyramidal" ||
                                (roofShape == null && IsHouseLike(tags) && area < MaxPitchedArea && !tags.Has("building:part"));
            bool pitched = wantsPitched && poly.holes.Count == 0 && roofShape != "flat";
            if (pitched && roofHeight <= 0f) roofHeight = Mathf.Clamp(Mathf.Sqrt(area) * 0.35f, 2f, 5f);
            if (!pitched) roofHeight = 0f;
            float wallTop = Mathf.Max(minHeight + 2f, height - roofHeight);
            if (custom != null && custom.floors > 0) wallTop = minHeight + custom.floors * LevelHeight + 0.3f; // + plinth
            else if (minHeight < 0.5f && wallTop > 2.5f)
            {
                // Estimated walls snap to whole storeys (3 m + 0.3 m plinth): an upper floor either gets its full
                // window row (shader needs lintel + 0.6 m under the eaves) or doesn't exist – no half storeys.
                int storeys = Mathf.Max(1, Mathf.RoundToInt((wallTop - 0.3f) / LevelHeight));
                wallTop = storeys * LevelHeight + 0.3f;
            }

            var wallColor = WallStyleColor(tags, rng);
            var roofColor = WorldStyle.RoofColor(tags, rng, pitched);
            // Measured colour from the aerial image unless OSM states one (roof:colour).
            if (AerialImage.Current != null && !tags.Has("roof:colour") &&
                AerialImage.Current.RoofColor(new List<List<Vector2>> { outer }, !pitched) is Color32 measured)
                roofColor = new Color32(measured.r, measured.g, measured.b, roofColor.a);
            if (custom?.wall != null)
            {
                var w = custom.wall.Value;
                wallColor = new Color32(w.r, w.g, w.b, custom.brick ? Lod2Builder.CodeBrick : (byte)255);
            }
            if (custom?.roof != null) roofColor = custom.roof.Value;
            BuildingOverrides.Register(houseId, "osm", addresses, outer, Mathf.Max(1, Mathf.RoundToInt((wallTop - minHeight) / LevelHeight)));
            if (custom != null && custom.hidden) return false; // removed in the map editor
            var batch = sink.Get(WorldStyle.MatBuildings, GeoUtil.Centroid(outer));
            int firstVertex = batch.vertices.Count;
            bool windowed = HasWindows(tags, wallTop - minHeight);
            bool baseWindowed = windowed;
            if (custom?.windows != null) windowed = custom.windows.Value;

            Walls(batch, outer, minHeight, wallTop, wallColor, windowed);
            string doorSide = custom?.door;
            bool wantDoor = doorSide != "none" && (baseWindowed || doorSide != null);
            if (wantDoor && minHeight < 0.5f && wallTop >= 2.8f)
                FrontDoor(batch, outer, wallTop, doorSide == "street" ? null : doorSide, custom?.doorColor);
            var holes = new List<List<Vector2>>();
            foreach (var h in poly.holes)
            {
                var ch = PolygonTriangulator.Clean(h);
                if (ch.Count < 3) continue;
                PolygonTriangulator.EnsureWinding(ch, false);
                Walls(batch, ch, minHeight, wallTop, wallColor, windowed);
                holes.Add(ch);
            }

            List<Vector2> roofRect = null;
            if (pitched && roofShape != "pyramidal")
            {
                if (outer.Count == 4) roofRect = outer;
                else
                {
                    // Near-rectangular footprints (bay windows, small notches) get a ridge roof over
                    // their minimum-area bounding rectangle – it reads as a slight eave overhang.
                    var rect = MinAreaRect(outer, out float rectArea);
                    if (rect != null && area / rectArea > 0.82f) roofRect = rect;
                }
            }

            if (roofRect != null)
            {
                GabledRoof(batch, roofRect, wallTop, roofHeight, roofColor, wallColor, roofShape == "hipped");
                if (custom?.chimney == true) RidgeChimney(batch, roofRect, wallTop + roofHeight, poly.id);
                if (custom != null && custom.gableWindows > 0)
                    GableWindows(batch, roofRect, minHeight, wallTop, roofShape == "hipped" ? 0f : roofHeight, custom.gableWindows, custom.gableWindowFloor);
            }
            else if (pitched && GeoUtil.IsConvex(outer))
                PyramidRoof(batch, outer, wallTop, roofHeight, roofColor);
            else
                FlatRoof(batch, outer, holes, wallTop, roofColor);

            // Underside for floating parts (e.g. overhangs) so they are not see-through from below.
            if (minHeight > 0.5f && PolygonTriangulator.Triangulate(outer, holes, out var bv, out var bt))
            {
                var flipped = new List<int>(bt.Count);
                for (int t = 0; t < bt.Count; t += 3) { flipped.Add(bt[t]); flipped.Add(bt[t + 2]); flipped.Add(bt[t + 1]); }
                MeshSink.FlatPolygon(batch, bv, flipped, minHeight, wallColor);
            }
            TerrainDrape.Offset(batch, firstVertex, TerrainField.Current.MinUnder(outer));
            return true;
        }

        static Color32 WallStyleColor(Dictionary<string, string> tags, System.Random rng) => WorldStyle.WallColor(tags, rng);

        static bool IsHouseLike(Dictionary<string, string> tags)
        {
            switch (tags.Get("building"))
            {
                case "house": case "detached": case "semidetached_house": case "terrace": case "residential":
                case "bungalow": case "farm": case "farm_auxiliary": case "barn": case "cabin": case "yes":
                    return true;
                default:
                    return false;
            }
        }

        static float EstimateHeight(Dictionary<string, string> tags, float area, System.Random rng)
        {
            var h = GeoUtil.ParseMeters(tags.Get("height")) ?? GeoUtil.ParseMeters(tags.Get("building:height"));
            if (h.HasValue && h.Value > 1f) return h.Value;

            var levels = GeoUtil.ParseMeters(tags.Get("building:levels"));
            var roofLevels = GeoUtil.ParseMeters(tags.Get("roof:levels")) ?? 0f;
            if (levels.HasValue && levels.Value > 0f) return (levels.Value + roofLevels * 0.6f) * LevelHeight + 1f;

            switch (tags.Get("building"))
            {
                case "garage": case "garages": case "shed": case "carport": case "hut": case "kiosk": return 3f;
                case "church": case "cathedral": return 16f;
                case "chapel": return 9f;
                case "commercial": case "retail": case "office": case "public": case "civic":
                case "apartments": case "hotel": case "school": case "university":
                    return 10f + (float)rng.NextDouble() * 4f;
            }
            if (area < 40f) return 3f;
            return 6.5f + (float)rng.NextDouble() * 2.5f;
        }

        const float WindowSpacing = 2.6f;

        static bool HasWindows(Dictionary<string, string> tags, float wallHeight)
        {
            if (wallHeight < 2.6f) return false;
            switch (tags.Get("building"))
            {
                case "garage": case "garages": case "shed": case "carport": case "hut": case "roof":
                case "barn": case "farm_auxiliary": case "warehouse": case "industrial": case "silo": case "storage_tank":
                    return false;
            }
            return true;
        }

        /// <summary>Door on the street-facing segment, over one of its window bays (same layout as Walls).</summary>
        static void FrontDoor(MeshSink.Batch b, List<Vector2> ring, float wallTop, string side, Color32? colour)
        {
            var walls = new List<Doors.Wall>();
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 p = ring[i], q = ring[(i + 1) % ring.Count];
                float len = Vector2.Distance(p, q);
                int windows = Mathf.FloorToInt((len - 0.6f) / WindowSpacing);
                if (windows <= 0) continue;
                var dir = (q - p) / len;
                walls.Add(new Doors.Wall
                {
                    start = p, dir = dir, normal = new Vector2(dir.y, -dir.x), // CCW ring: outside is on the right
                    bayOffset = (len - windows * WindowSpacing) * 0.5f, bays = windows, heightScale = 1f, top = wallTop,
                });
            }
            if (Doors.Pick(osm, walls, out var front, out float bay, side)) Doors.Emit(b, front, bay, colour);
        }

        static void Walls(MeshSink.Batch b, List<Vector2> ring, float y0, float y1, Color32 color, bool windowed)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 p = ring[i], q = ring[(i + 1) % ring.Count];
                // Facade data: windows are centred per wall segment so none straddle a corner.
                Vector4 uvP = MeshSink.NoFacade, uvQ = MeshSink.NoFacade;
                float len = Vector2.Distance(p, q);
                int windows = windowed ? Mathf.FloorToInt((len - 0.6f) / WindowSpacing) : 0;
                if (windows > 0)
                {
                    float span = windows * WindowSpacing;
                    float offset = (len - span) * 0.5f;
                    uvP = new Vector4(-offset, 0f, y1, span);
                    uvQ = new Vector4(len - offset, 0f, y1, span);
                }
                Vector4 Bottom(Vector4 uv) => uv.y < 0 ? uv : new Vector4(uv.x, y0, uv.z, uv.w);
                Vector4 Top(Vector4 uv) => uv.y < 0 ? uv : new Vector4(uv.x, y1, uv.z, uv.w);

                // Ring CCW (outer) or CW (hole): wall faces the right-hand side of travel = outside.
                MeshSink.Quad(b,
                    new Vector3(p.x, y0, p.y), new Vector3(p.x, y1, p.y),
                    new Vector3(q.x, y1, q.y), new Vector3(q.x, y0, q.y), color,
                    Bottom(uvP), Top(uvP), Top(uvQ), Bottom(uvQ));
            }
        }

        /// <summary>Minimum-area bounding rectangle (rotating edges). Returns CCW corners.</summary>
        static List<Vector2> MinAreaRect(List<Vector2> ring, out float bestArea)
        {
            bestArea = float.MaxValue;
            List<Vector2> best = null;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 e = ring[(i + 1) % ring.Count] - ring[i];
                if (e.sqrMagnitude < 0.25f) continue;
                Vector2 u = e.normalized, v = new Vector2(-u.y, u.x);
                float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                foreach (var p in ring)
                {
                    float a = Vector2.Dot(p, u), c = Vector2.Dot(p, v);
                    minU = Mathf.Min(minU, a); maxU = Mathf.Max(maxU, a);
                    minV = Mathf.Min(minV, c); maxV = Mathf.Max(maxV, c);
                }
                float area = (maxU - minU) * (maxV - minV);
                if (area >= bestArea) continue;
                bestArea = area;
                best = new List<Vector2>
                {
                    u * minU + v * minV, u * maxU + v * minV, u * maxU + v * maxV, u * minU + v * maxV,
                };
            }
            return best;
        }

        static void FlatRoof(MeshSink.Batch b, List<Vector2> outer, List<List<Vector2>> holes, float y, Color32 color)
        {
            if (PolygonTriangulator.Triangulate(outer, holes, out var verts, out var tris))
                MeshSink.FlatPolygon(b, verts, tris, y, color);
        }

        static void PyramidRoof(MeshSink.Batch b, List<Vector2> outer, float y, float h, Color32 color)
        {
            var c = GeoUtil.Centroid(outer);
            var apex = new Vector3(c.x, y + h, c.y);
            for (int i = 0; i < outer.Count; i++)
            {
                Vector2 p = outer[i], q = outer[(i + 1) % outer.Count];
                MeshSink.Tri(b, new Vector3(p.x, y, p.y), apex, new Vector3(q.x, y, q.y), color);
            }
        }

        /// <summary>Chimney a third along the ridge (map editor option for OSM houses).</summary>
        static void RidgeChimney(MeshSink.Batch b, List<Vector2> r, float ridgeY, long id)
        {
            if ((r[2] - r[1]).sqrMagnitude > (r[1] - r[0]).sqrMagnitude) r = new List<Vector2> { r[1], r[2], r[3], r[0] };
            Vector2 m12 = (r[1] + r[2]) * 0.5f, m30 = (r[3] + r[0]) * 0.5f;
            Lod2Builder.Chimney(b, V(Vector2.Lerp(m30, m12, 0.32f), ridgeY), new System.Random((int)id));
        }

        /// <summary>Gabled (or hipped) roof over a 4-corner footprint, ridge along the longer axis.</summary>
        static void GabledRoof(MeshSink.Batch b, List<Vector2> r, float y, float h, Color32 roof, Color32 wall, bool hipped)
        {
            // Rotate so that edge r0->r1 is a long side.
            float e01 = (r[1] - r[0]).sqrMagnitude, e12 = (r[2] - r[1]).sqrMagnitude;
            if (e12 > e01) r = new List<Vector2> { r[1], r[2], r[3], r[0] };

            // Long sides: r0->r1 and r2->r3. Short sides (gables): r1->r2 and r3->r0.
            Vector2 m12 = (r[1] + r[2]) * 0.5f, m30 = (r[3] + r[0]) * 0.5f;
            if (hipped)
            {
                Vector2 dir = (m12 - m30);
                float inset = Mathf.Min(dir.magnitude * 0.25f, (r[2] - r[1]).magnitude * 0.5f);
                dir.Normalize();
                m12 -= dir * inset;
                m30 += dir * inset;
            }
            Vector3 A = V(r[0], y), B = V(r[1], y), C = V(r[2], y), D = V(r[3], y);
            Vector3 R1 = V(m12, y + h), R0 = V(m30, y + h);

            // Slopes (clockwise seen from outside/above).
            MeshSink.Quad(b, A, R0, R1, B, roof);
            MeshSink.Quad(b, C, R1, R0, D, roof);
            // Gable ends: triangles in wall colour, hipped ends in roof colour.
            MeshSink.Tri(b, B, R1, C, hipped ? roof : wall);
            MeshSink.Tri(b, D, R0, A, hipped ? roof : wall);
        }

        static readonly Color32 WindowFrame = new Color32(238, 234, 226, 255);
        static readonly Color32 WindowGlass = new Color32(68, 82, 94, 255);
        public const float WinWidth = 1.0f, WinHeight = 1.3f, WinSill = 0.9f, WinGap = 0.8f;
        /// <summary>Minimum clearance between a window and the roof (eaves or gable slope).</summary>
        public const float RoofGap = 0.6f;

        /// <summary>
        /// <paramref name="count"/> windows centred side by side on both short (gable) walls of a ridge-roofed
        /// rectangle, on floor <paramref name="floor"/>. Kept inside the gable triangle if they reach above the eaves.
        /// </summary>
        static void GableWindows(MeshSink.Batch b, List<Vector2> r, float y0, float wallTop, float roofHeight, int count, int floor)
        {
            if ((r[2] - r[1]).sqrMagnitude > (r[1] - r[0]).sqrMagnitude) r = new List<Vector2> { r[1], r[2], r[3], r[0] };
            var centre = (r[0] + r[1] + r[2] + r[3]) * 0.25f;
            float sill = y0 + floor * LevelHeight + WinSill;
            // Never above what the wall + gable can hold; without a gable (hipped) stay clear of the eaves.
            float maxTop = roofHeight > 0f ? wallTop + roofHeight * 0.55f : wallTop - RoofGap;
            if (sill + WinHeight > maxTop) sill = Mathf.Max(y0 + 0.5f, maxTop - WinHeight);
            foreach (var (p, q) in new[] { (r[1], r[2]), (r[3], r[0]) })
            {
                Vector2 along = (q - p).normalized, mid = (p + q) * 0.5f;
                Vector2 n2 = new Vector2(-along.y, along.x);
                if (Vector2.Dot(n2, mid - centre) < 0f) n2 = -n2;
                float halfLen = Vector2.Distance(p, q) * 0.5f;
                Vector3 n = new Vector3(n2.x, 0f, n2.y), u = new Vector3(along.x, 0f, along.y);
                // Reaching up into the gable: keep RoofGap to the roof slopes (checked a bit above the lintel).
                bool Fits(float offset, float sl)
                {
                    float top = sl + WinHeight;
                    if (top <= wallTop - RoofGap) return true;
                    if (roofHeight <= 0f) return false;
                    float avail = halfLen * (1f - (top + 0.35f - wallTop) / roofHeight) - RoofGap;
                    return Mathf.Abs(offset) + WinWidth * 0.5f <= avail;
                }
                float Offset(int i) => (i - (count - 1) * 0.5f) * (WinWidth + WinGap);
                // Too close to the slopes: the whole row moves down (up to 0.6 m, not below its storey).
                float rowSill = sill;
                for (float drop = 0f; drop <= 0.6f + 1e-3f; drop += 0.1f)
                {
                    rowSill = Mathf.Max(y0 + floor * LevelHeight + 0.3f, sill - drop);
                    bool all = true;
                    for (int i = 0; i < count && all; i++) all = Fits(Offset(i), rowSill);
                    if (all) break;
                }
                for (int i = 0; i < count; i++)
                    if (Fits(Offset(i), rowSill))
                        Window(b, V(mid, 0f) + u * Offset(i) + n * 0.03f, u, n, rowSill);
            }
        }

        /// <summary>Frame, glass and a mullion cross, slightly proud of the wall (normal <paramref name="n"/>).</summary>
        public static void Window(MeshSink.Batch b, Vector3 c, Vector3 u, Vector3 n, float sill)
        {
            void Rect(float x0, float x1, float yA, float yB, float out_, Color32 col)
            {
                Vector3 o = c + n * out_;
                Vector3 p0 = o + u * x0 + Vector3.up * yA, p1 = o + u * x0 + Vector3.up * yB;
                Vector3 p2 = o + u * x1 + Vector3.up * yB, p3 = o + u * x1 + Vector3.up * yA;
                if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), n) < 0f) { var t = p0; p0 = p3; p3 = t; t = p1; p1 = p2; p2 = t; }
                MeshSink.Quad(b, p0, p1, p2, p3, col);
            }
            float h = WinWidth * 0.5f, f = 0.08f, m = 0.035f;
            Rect(-h, h, sill, sill + WinHeight, 0f, WindowFrame);
            Rect(-h + f, h - f, sill + f, sill + WinHeight - f, 0.01f, WindowGlass);
            Rect(-m, m, sill + f, sill + WinHeight - f, 0.02f, WindowFrame);                        // vertical bar
            float mid = sill + WinHeight * 0.62f;
            Rect(-h + f, h - f, mid - m, mid + m, 0.02f, WindowFrame);                               // transom
        }

        static Vector3 V(Vector2 p, float y) => new Vector3(p.x, y, p.y);
    }
}
