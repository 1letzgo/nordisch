using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Street furniture: mapped OSM items (benches, fountains, bike stands, bins, artworks, flagpoles,
    /// bollards, picnic tables) plus sensible dressing for squares and pedestrian streets, and
    /// outdoor seating in front of cafés/restaurants that face a plaza. Everything is low-poly,
    /// vertex-coloured, collidable (Props bucket) – fountain water goes to the Water bucket.
    /// </summary>
    public static class FurniturePlacer
    {
        static readonly Color32 Wood = new Color32(150, 104, 66, 255);
        static readonly Color32 Iron = new Color32(52, 58, 64, 255);
        static readonly Color32 Stone = new Color32(184, 178, 166, 255);
        static readonly Color32 StoneDark = new Color32(142, 136, 126, 255);
        static readonly Color32 Steel = new Color32(150, 156, 160, 255);
        static readonly Color32 PlanterGreen = new Color32(88, 130, 70, 255);
        static readonly Color32 Mustard = new Color32(227, 168, 43, 255);
        static readonly Color32 Navy = new Color32(26, 58, 78, 255);
        static readonly Color32 Cream = new Color32(240, 232, 214, 255);
        static readonly Color32 Brick = new Color32(160, 74, 54, 255);

        public struct Stats { public int mapped, benches, planters, seating, fountains; }

        /// <summary>Everything placed by the last <see cref="Place"/> call (used to keep bikes clear).</summary>
        public static readonly List<Vector2> LastOccupied = new List<Vector2>();

        /// <summary>One item of <paramref name="type"/> at the origin, facing north (map editor objects).</summary>
        public static bool BuildSingle(string type, MeshSink sink, System.Random rng)
        {
            var p = Vector2.zero;
            var b = sink.Get(WorldStyle.MatProps, p);
            switch (type)
            {
                case "bench": Bench(b, p, 0f); return true;
                case "picnic_table": PicnicTable(b, p, 0f); return true;
                case "bike_stands": BikeStands(b, p, 0f); return true;
                case "bin": Bin(b, p); return true;
                case "bollard": Bollard(b, p, 0.8f, Iron); return true;
                case "flagpole": Flagpole(b, p); return true;
                case "statue": Statue(b, p, 0f, rng); return true;
                case "planter": Planter(sink, p, rng); return true;
                case "cafe_table": CafeSet(b, p, 0f, rng.NextDouble() < 0.5 ? Mustard : Navy); return true;
                case "fountain": Fountain(sink, p, 2.2f); return true;
                case "drinking_water": Bollard(b, p, 0.9f, Steel); return true;
                default: return false;
            }
        }

        const string Props = WorldStyle.MatProps;

        public static Stats Place(OsmData data, MeshSink sink, AreaRect area, System.Random rng)
        {
            var st = new Stats();
            var occupied = LastOccupied;
            occupied.Clear();
            bool Free(Vector2 p, float r)
            {
                foreach (var o in occupied) if ((o - p).sqrMagnitude < r * r) return false;
                return true;
            }
            void Take(Vector2 p) => occupied.Add(p);

            var buildings = new List<List<Vector2>>();
            foreach (var poly in data.polygons)
                if (poly.tags.Has("building")) buildings.Add(PolygonTriangulator.Clean(poly.outer));
            bool InBuilding(Vector2 p)
            {
                foreach (var b in buildings) if (b.Count > 2 && OsmData.PointInRing(p, b)) return true;
                return false;
            }

            // Orientation helper: face away from the nearest building wall / towards open space.
            var squares = new List<List<Vector2>>();
            foreach (var poly in data.polygons)
            {
                var t = poly.tags;
                if (t.Is("place", "square") || t.Is("amenity", "marketplace") || (t.Is("highway", "pedestrian") && (t.Is("area", "yes") || poly.outer.Count > 3)))
                    squares.Add(PolygonTriangulator.Clean(poly.outer));
            }
            var pedestrianLines = new List<List<Vector2>>();
            foreach (var w in data.ways)
                if (w.tags.Is("highway", "pedestrian") && !w.tags.Is("area", "yes")) pedestrianLines.Add(w.points);

            // ---- 1. Mapped items
            foreach (var n in data.nodes)
            {
                var p = data.projection.ToLocal(n.lat, n.lon);
                if (!area.Contains(p, 1f)) continue;
                float yaw = (float)(rng.NextDouble() * 360.0);
                var dir = n.tags.Get("direction");
                float d = 0f;
                bool hasDirection = dir != null && float.TryParse(dir, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d);
                if (hasDirection) yaw = d;
                else if (n.tags.Is("amenity", "bench") && OrientToSurroundings(p, buildings, data, out float oriented)) yaw = oriented;
                switch (n.tags.Get("amenity"))
                {
                    case "bench": EditsPlacer.Emit(sink, "bench", p, yaw, 1f, (s, q, y) => Bench(s.Get(Props, q), q, y)); Take(p); st.mapped++; continue;
                    case "fountain":
                        float fr = rng.NextDouble() < 0.5 ? 2.2f : 1.6f;
                        EditsPlacer.Emit(sink, "fountain", p, 0f, fr / 2.2f, (s, q, y) => Fountain(s, q, fr)); Take(p); st.fountains++; continue;
                    case "drinking_water": EditsPlacer.Emit(sink, "drinking_water", p, 0f, 1f, (s, q, y) => Bollard(s.Get(Props, q), q, 0.9f, Steel)); Take(p); st.mapped++; continue;
                    case "bicycle_parking": EditsPlacer.Emit(sink, "bike_stands", p, yaw, 1f, (s, q, y) => BikeStands(s.Get(Props, q), q, y)); Take(p); st.mapped++; continue;
                    case "waste_basket": EditsPlacer.Emit(sink, "bin", p, 0f, 1f, (s, q, y) => Bin(s.Get(Props, q), q)); Take(p); st.mapped++; continue;
                }
                if (n.tags.Is("leisure", "picnic_table")) { EditsPlacer.Emit(sink, "picnic_table", p, yaw, 1f, (s, q, y) => PicnicTable(s.Get(Props, q), q, y)); Take(p); st.mapped++; continue; }
                if (n.tags.Is("tourism", "artwork") || n.tags.Is("historic", "memorial") || n.tags.Is("historic", "monument"))
                {
                    EditsPlacer.Emit(sink, "statue", p, yaw, 1f, (s, q, y) => Statue(s.Get(Props, q), q, y, rng)); Take(p); st.mapped++; continue;
                }
                if (n.tags.Is("man_made", "flagpole")) { EditsPlacer.Emit(sink, "flagpole", p, 0f, 1f, (s, q, y) => Flagpole(s.Get(Props, q), q)); Take(p); st.mapped++; continue; }
                if (n.tags.Is("barrier", "bollard")) { EditsPlacer.Emit(sink, "bollard", p, 0f, 1f, (s, q, y) => Bollard(s.Get(Props, q), q, 0.8f, Iron)); Take(p); st.mapped++; continue; }
            }
            // Fountain areas (way polygons).
            foreach (var poly in data.polygons)
            {
                if (!poly.tags.Is("amenity", "fountain")) continue;
                var c = GeoUtil.Centroid(poly.outer);
                var r = GeoUtil.Bounds(poly.outer);
                float radius = Mathf.Clamp(Mathf.Min(r.width, r.height) * 0.5f, 1.4f, 6f);
                EditsPlacer.Emit(sink, "fountain", c, 0f, radius / 2.2f, (s, q, y) => Fountain(s, q, radius));
                Take(c);
                st.fountains++;
            }

            // ---- 2. Squares: benches around the edge facing the middle, planters with small trees
            foreach (var ring in squares)
            {
                if (ring.Count < 3) continue;
                float sqArea = Mathf.Abs(PolygonTriangulator.SignedArea(ring));
                if (sqArea < 300f) continue;
                var centre = GeoUtil.Centroid(ring);
                float perimeterStep = sqArea > 3000f ? 16f : 12f;
                float carry = perimeterStep * 0.5f;
                int k = 0;
                for (int i = 0; i < ring.Count; i++)
                {
                    Vector2 a = ring[i], c = ring[(i + 1) % ring.Count];
                    float len = Vector2.Distance(a, c);
                    for (float s = carry; s < len; s += perimeterStep)
                    {
                        var edge = Vector2.Lerp(a, c, s / len);
                        var inward = (centre - edge).normalized;
                        var p = edge + inward * 2.4f;
                        if (!OsmData.PointInRing(p, ring) || InBuilding(p) || !Free(p, 3f)) continue;
                        float yaw = Mathf.Atan2(inward.x, inward.y) * Mathf.Rad2Deg;
                        if (k++ % 3 == 2) { EditsPlacer.Emit(sink, "planter", p, 0f, 1f, (s, q, y) => Planter(s, q, rng)); st.planters++; }
                        else { EditsPlacer.Emit(sink, "bench", p, yaw, 1f, (s, q, y) => Bench(s.Get(Props, q), q, y)); st.benches++; }
                        Take(p);
                    }
                    carry = (carry + len) % perimeterStep;
                }
            }

            // ---- 3. Pedestrian streets: benches and planters along the edges, alternating sides
            foreach (var line in pedestrianLines)
            {
                float carry = 10f;
                int side = 1, k = 0;
                for (int i = 0; i < line.Count - 1; i++)
                {
                    Vector2 a = line[i], c = line[i + 1];
                    float len = Vector2.Distance(a, c);
                    if (len < 0.1f) continue;
                    Vector2 dir = (c - a) / len, nrm = new Vector2(-dir.y, dir.x);
                    float s = carry;
                    for (; s < len; s += 22f)
                    {
                        var p = a + dir * s + nrm * side * 2.6f;
                        side = -side;
                        if (!area.Contains(p, 2f) || InBuilding(p) || !Free(p, 4f)) continue;
                        var face = -nrm * -side; // fallback: face the street centre
                        float yaw = Mathf.Atan2(face.x, face.y) * Mathf.Rad2Deg;
                        if (OrientToSurroundings(p, buildings, data, out float alongFacade)) yaw = alongFacade;
                        if (k++ % 2 == 0) { EditsPlacer.Emit(sink, "bench", p, yaw, 1f, (s, q, y) => Bench(s.Get(Props, q), q, y)); st.benches++; }
                        else { EditsPlacer.Emit(sink, "planter", p, 0f, 1f, (s, q, y) => Planter(s, q, rng)); st.planters++; }
                        Take(p);
                    }
                    carry = s - len;
                }
            }

            // ---- 4. Outdoor seating for cafés/restaurants next to a plaza or pedestrian street
            foreach (var n in data.nodes)
            {
                var am = n.tags.Get("amenity");
                if (am != "cafe" && am != "restaurant" && am != "ice_cream" && am != "pub" && am != "bar") continue;
                if (n.tags.Is("outdoor_seating", "no")) continue;
                var p = data.projection.ToLocal(n.lat, n.lon);
                if (!area.Contains(p, 5f)) continue;
                // Nearest open pedestrian spot within 18 m.
                Vector2? spot = NearestPedestrian(p, squares, pedestrianLines, 18f);
                if (spot == null) continue;
                var toOpen = (spot.Value - p);
                if (toOpen.sqrMagnitude < 0.01f) toOpen = Vector2.up;
                var basePos = spot.Value;
                var right = new Vector2(toOpen.normalized.y, -toOpen.normalized.x);
                Color32 umbrella = am == "ice_cream" ? Cream : (rng.NextDouble() < 0.5 ? Mustard : Navy);
                for (int t = -1; t <= 1; t++)
                {
                    var tp = basePos + right * (t * 2.8f);
                    if (InBuilding(tp) || !Free(tp, 2.2f)) continue;
                    float cafeYaw = (float)(rng.NextDouble() * 90.0);
                    EditsPlacer.Emit(sink, "cafe_table", tp, cafeYaw, 1f, (s, q, y) => CafeSet(s.Get(Props, q), q, y, umbrella));
                    Take(tp);
                    st.seating++;
                }
            }
            return st;
        }

        /// <summary>
        /// Bench heading: parallel to the nearest façade within 12 m, back to the wall, facing the street;
        /// otherwise parallel to the nearest way within 10 m, facing it. Yaw is clockwise from north (+z).
        /// </summary>
        static bool OrientToSurroundings(Vector2 p, List<List<Vector2>> buildings, OsmData data, out float yaw)
        {
            yaw = 0f;
            float best = 12f;
            Vector2 bestA = default, bestB = default;
            bool found = false;
            foreach (var ring in buildings)
            {
                if (ring.Count < 3) continue;
                for (int i = 0; i < ring.Count; i++)
                {
                    Vector2 a = ring[i], b = ring[(i + 1) % ring.Count];
                    float d = Vector2.Distance(p, ClosestOnSegment(p, a, b));
                    if (d < best) { best = d; bestA = a; bestB = b; found = true; }
                }
            }
            if (found)
            {
                // Face away from the wall: towards p from the closest wall point.
                var q = ClosestOnSegment(p, bestA, bestB);
                var edge = (bestB - bestA).normalized;
                var away = p - q;
                var normal = new Vector2(-edge.y, edge.x);
                if (Vector2.Dot(normal, away) < 0) normal = -normal;
                yaw = Mathf.Atan2(normal.x, normal.y) * Mathf.Rad2Deg;
                return true;
            }
            // No façade nearby: line up with the nearest path/street and look at it.
            best = 10f;
            foreach (var w in data.ways)
            {
                if (!w.tags.Has("highway")) continue;
                for (int i = 0; i < w.points.Count - 1; i++)
                {
                    var q = ClosestOnSegment(p, w.points[i], w.points[i + 1]);
                    float d = Vector2.Distance(p, q);
                    if (d < best && d > 0.05f)
                    {
                        best = d;
                        var towards = (q - p).normalized;
                        yaw = Mathf.Atan2(towards.x, towards.y) * Mathf.Rad2Deg;
                        found = true;
                    }
                }
            }
            return found;
        }

        static Vector2? NearestPedestrian(Vector2 p, List<List<Vector2>> squares, List<List<Vector2>> lines, float maxDist)
        {
            Vector2? best = null;
            float bestD = maxDist;
            foreach (var ring in squares)
            {
                if (OsmData.PointInRing(p, ring)) return p + (GeoUtil.Centroid(ring) - p).normalized * 3f;
                for (int i = 0; i < ring.Count; i++)
                {
                    var q = ClosestOnSegment(p, ring[i], ring[(i + 1) % ring.Count]);
                    float d = Vector2.Distance(p, q);
                    if (d < bestD) { bestD = d; best = q + (GeoUtil.Centroid(ring) - q).normalized * 2.5f; }
                }
            }
            foreach (var line in lines)
            {
                for (int i = 0; i < line.Count - 1; i++)
                {
                    var q = ClosestOnSegment(p, line[i], line[i + 1]);
                    float d = Vector2.Distance(p, q);
                    // Sit on the café's side of the street centre line.
                    if (d < bestD) { bestD = d; best = q + (p - q).normalized * Mathf.Min(2.2f, d); }
                }
            }
            return best;
        }

        static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
            return a + ab * t;
        }

        // ---------------------------------------------------------------- primitives

        /// <summary>Axis-aligned (in local yaw frame) box with outward faces, all six sides.</summary>
        internal static void Box(MeshSink.Batch b, Vector3 centre, Vector3 size, float yaw, Color32 c)
        {
            var rot = Quaternion.Euler(0, yaw, 0);
            Vector3 h = size * 0.5f;
            Vector3 P(float x, float y, float z) => centre + rot * new Vector3(x * h.x, y * h.y, z * h.z);
            // Corners: bottom (y-1) ring CCW from above, then top.
            Vector3 b0 = P(-1, -1, -1), b1 = P(1, -1, -1), b2 = P(1, -1, 1), b3 = P(-1, -1, 1);
            Vector3 t0 = P(-1, 1, -1), t1 = P(1, 1, -1), t2 = P(1, 1, 1), t3 = P(-1, 1, 1);
            MeshSink.Quad(b, b0, t0, t1, b1, c); // -z side
            MeshSink.Quad(b, b1, t1, t2, b2, c); // +x
            MeshSink.Quad(b, b2, t2, t3, b3, c); // +z
            MeshSink.Quad(b, b3, t3, t0, b0, c); // -x
            MeshSink.Quad(b, t0, t3, t2, t1, c); // top
            MeshSink.Quad(b, b0, b1, b2, b3, c); // bottom
        }

        internal static void Cylinder(MeshSink.Batch b, Vector3 basePos, float r, float h, int sides, Color32 side, Color32 top, bool cap = true)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                var p0 = basePos + new Vector3(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r);
                var p1 = basePos + new Vector3(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r);
                MeshSink.Quad(b, p0, p0 + Vector3.up * h, p1 + Vector3.up * h, p1, side);
                if (cap) MeshSink.Tri(b, basePos + Vector3.up * h, p1 + Vector3.up * h, p0 + Vector3.up * h, top);
            }
        }

        static Vector3 V(Vector2 p, float y = 0f) => new Vector3(p.x, y, p.y);

        // ---------------------------------------------------------------- furniture

        static void Bench(MeshSink.Batch b, Vector2 p, float yaw)
        {
            var rot = Quaternion.Euler(0, yaw, 0);
            var c = V(p);
            Box(b, c + rot * new Vector3(0, 0.45f, 0), new Vector3(1.8f, 0.06f, 0.45f), yaw, Wood);                 // seat
            Box(b, c + rot * new Vector3(0, 0.78f, -0.24f), new Vector3(1.8f, 0.32f, 0.05f), yaw, Wood);           // backrest
            foreach (float x in new[] { -0.75f, 0.75f })
            {
                Box(b, c + rot * new Vector3(x, 0.22f, 0), new Vector3(0.07f, 0.44f, 0.42f), yaw, Iron);          // legs
                Box(b, c + rot * new Vector3(x, 0.62f, -0.24f), new Vector3(0.06f, 0.36f, 0.06f), yaw, Iron);
            }
        }

        static void PicnicTable(MeshSink.Batch b, Vector2 p, float yaw)
        {
            var rot = Quaternion.Euler(0, yaw, 0);
            var c = V(p);
            Box(b, c + rot * new Vector3(0, 0.74f, 0), new Vector3(1.8f, 0.06f, 0.75f), yaw, Wood);
            Box(b, c + rot * new Vector3(0, 0.44f, 0.7f), new Vector3(1.8f, 0.05f, 0.3f), yaw, Wood);
            Box(b, c + rot * new Vector3(0, 0.44f, -0.7f), new Vector3(1.8f, 0.05f, 0.3f), yaw, Wood);
            foreach (float x in new[] { -0.7f, 0.7f })
                Box(b, c + rot * new Vector3(x, 0.37f, 0), new Vector3(0.08f, 0.74f, 1.5f), yaw, Wood);
        }

        static void BikeStands(MeshSink.Batch b, Vector2 p, float yaw)
        {
            var rot = Quaternion.Euler(0, yaw, 0);
            for (int i = -2; i <= 2; i++)
            {
                var c = V(p) + rot * new Vector3(i * 0.9f, 0, 0);
                Box(b, c + rot * new Vector3(0, 0.42f, -0.35f), new Vector3(0.05f, 0.84f, 0.05f), yaw, Steel);
                Box(b, c + rot * new Vector3(0, 0.42f, 0.35f), new Vector3(0.05f, 0.84f, 0.05f), yaw, Steel);
                Box(b, c + rot * new Vector3(0, 0.84f, 0), new Vector3(0.05f, 0.05f, 0.75f), yaw, Steel);
            }
        }

        static void Bin(MeshSink.Batch b, Vector2 p)
        {
            Cylinder(b, V(p), 0.22f, 0.85f, 8, new Color32(70, 90, 80, 255), new Color32(40, 44, 46, 255));
        }

        static void Bollard(MeshSink.Batch b, Vector2 p, float h, Color32 c)
        {
            Cylinder(b, V(p), 0.1f, h, 6, c, c);
        }

        static void Flagpole(MeshSink.Batch b, Vector2 p)
        {
            Cylinder(b, V(p), 0.06f, 8f, 6, Cream, Cream);
            // Flag in brand colours.
            var top = V(p, 7.2f);
            Box(b, top + new Vector3(0.75f, 0, 0), new Vector3(1.4f, 0.45f, 0.02f), 0, Navy);
            Box(b, top + new Vector3(0.75f, -0.4f, 0), new Vector3(1.4f, 0.35f, 0.02f), 0, Mustard);
        }

        static void Statue(MeshSink.Batch b, Vector2 p, float yaw, System.Random rng)
        {
            var c = V(p);
            Box(b, c + Vector3.up * 0.6f, new Vector3(1.0f, 1.2f, 1.0f), yaw, Stone);              // plinth
            Box(b, c + Vector3.up * 1.25f, new Vector3(1.15f, 0.1f, 1.15f), yaw, StoneDark);
            var bronze = WorldStyle.Tint(new Color32(92, 112, 96, 255), rng, 0.08f);             // verdigris bronze
            Box(b, c + Vector3.up * 1.95f, new Vector3(0.42f, 1.2f, 0.3f), yaw, bronze);           // abstract figure
            Box(b, c + Vector3.up * 2.72f, new Vector3(0.28f, 0.32f, 0.28f), yaw, bronze);
        }

        static void Planter(MeshSink sink, Vector2 p, System.Random rng)
        {
            var b = sink.Get(WorldStyle.MatProps, p);
            Box(b, V(p, 0.3f), new Vector3(1.1f, 0.6f, 1.1f), 45f * (float)rng.NextDouble(), StoneDark);
            Box(b, V(p, 0.61f), new Vector3(0.95f, 0.02f, 0.95f), 0, new Color32(84, 62, 44, 255));
            // Small clipped tree or bush.
            var g = sink.Get(WorldStyle.MatTrees, p);
            var basePos = V(p, 0.6f);
            Cylinder(g, basePos, 0.06f, 1.4f, 5, WorldStyle.TreeTrunk, WorldStyle.TreeTrunk, false);
            var leaves = WorldStyle.Tint(PlanterGreen, rng, 0.1f);
            var local = PropPlacer.Fork(rng, 6); // same draw count as the former 6-sided crown
            PropPlacer.Blob(g, basePos + Vector3.up * 1.95f, 0.68f, 0.6f, 0f, leaves, local);
            PropPlacer.Blob(g, basePos + new Vector3(0.18f, 2.45f, -0.1f), 0.42f, 0.32f, 1f,
                Color32.Lerp(leaves, PropPlacer.LeavesLight, 0.3f), local);
        }

        static void CafeSet(MeshSink.Batch b, Vector2 p, float yaw, Color32 umbrella)
        {
            var c = V(p);
            Cylinder(b, c, 0.04f, 0.74f, 6, Iron, Iron, false);
            Cylinder(b, c + Vector3.up * 0.72f, 0.38f, 0.04f, 10, Cream, Cream);                 // table top
            var rot = Quaternion.Euler(0, yaw, 0);
            foreach (float s in new[] { -1f, 1f })
            {
                var ch = c + rot * new Vector3(0.75f * s, 0, 0);
                Box(b, ch + Vector3.up * 0.45f, new Vector3(0.42f, 0.05f, 0.42f), yaw, Iron);    // seat
                Box(b, ch + rot * new Vector3(0.2f * s, 0.7f, 0), new Vector3(0.04f, 0.5f, 0.42f), yaw, Iron); // back
                Box(b, ch + Vector3.up * 0.22f, new Vector3(0.38f, 0.44f, 0.38f) * 0.12f + new Vector3(0, 0.4f, 0), yaw, Iron);
            }
            // Umbrella: pole + low-poly canopy.
            Cylinder(b, c + Vector3.up * 0.76f, 0.025f, 1.6f, 5, Cream, Cream, false);
            var apex = c + Vector3.up * 2.65f;
            float rr = 1.3f, ry = 2.2f;
            for (int i = 0; i < 8; i++)
            {
                float a0 = i * Mathf.PI / 4f, a1 = (i + 1) * Mathf.PI / 4f;
                var r0 = c + new Vector3(Mathf.Cos(a0) * rr, ry, Mathf.Sin(a0) * rr);
                var r1 = c + new Vector3(Mathf.Cos(a1) * rr, ry, Mathf.Sin(a1) * rr);
                var col = i % 2 == 0 ? umbrella : Cream;
                MeshSink.Tri(b, r0, apex, r1, col);
                MeshSink.Tri(b, r1, apex, r0, col); // underside
            }
        }

        static void Fountain(MeshSink sink, Vector2 p, float radius)
        {
            var b = sink.Get(WorldStyle.MatProps, p);
            int sides = 8;
            float wall = 0.25f, h = 0.55f;
            var c = V(p);
            // Basin wall: outer + inner faces + rim top.
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                Vector3 o0 = c + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * radius, o1 = c + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * radius;
                Vector3 i0 = c + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * (radius - wall), i1 = c + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * (radius - wall);
                Vector3 up = Vector3.up * h;
                MeshSink.Quad(b, o0, o0 + up, o1 + up, o1, Stone);
                MeshSink.Quad(b, i1, i1 + up, i0 + up, i0, StoneDark);
                MeshSink.Quad(b, o0 + up, i0 + up, i1 + up, o1 + up, Cream);
            }
            // Centre column with a small top bowl.
            Cylinder(b, c, 0.22f, 1.6f, 8, Stone, Stone);
            Cylinder(b, c + Vector3.up * 1.6f, 0.65f, 0.15f, 8, StoneDark, Stone);
            // Water surface (animated water material).
            var w = sink.Get(WorldStyle.MatWater, p);
            var ring = new List<Vector2>();
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                ring.Add(new Vector2(p.x + Mathf.Cos(a) * (radius - wall), p.y + Mathf.Sin(a) * (radius - wall)));
            }
            if (PolygonTriangulator.Triangulate(ring, null, out var verts, out var tris))
                MeshSink.FlatPolygon(w, verts, tris, h - 0.12f, WorldStyle.Water);
        }
    }
}
