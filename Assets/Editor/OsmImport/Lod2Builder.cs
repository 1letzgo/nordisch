using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Builds buildings from LGLN LoD2 data (Data/&lt;area&gt;.lod2.json, see Tools/lod2/fetch_lod2.py):
    /// real roof shapes and heights, styled with a north-west German palette (Klinker, white
    /// plaster, red/anthracite pantiles). Surface material codes go into vertex alpha for the shader.
    /// </summary>
    public static class Lod2Builder
    {
        // Material codes (vertex colour alpha) – keep in sync with VertexColorLit.shader.
        public const byte CodePlain = 255;
        public const byte CodeBrick = 230;
        public const byte CodePlaster = 200;
        public const byte CodeRoofTiles = 120;
        public const byte CodeRoofFlat = 60;

        const float WindowSpacing = 2.6f;

        enum Use { Residential, Commercial, Public, Church, Garage, Industrial, Other }

        struct Style
        {
            public Color32 wall;
            public byte wallCode;
            public Color32 roof;
            public bool windows;
            public bool chimney;
        }

        // Footprint hulls of every LoD2 building from the last TryBuild, bucketed on a grid for Covers().
        const float CoverCell = 25f;
        static readonly List<List<Vector2>> hulls = new List<List<Vector2>>();
        static readonly List<Rect> hullBounds = new List<Rect>();
        static readonly Dictionary<Vector2Int, List<int>> hullGrid = new Dictionary<Vector2Int, List<int>>();

        /// <summary>Ground outlines (or hulls) of every LoD2 building from the last TryBuild – trees keep clear of them.</summary>
        public static readonly List<List<Vector2>> Outlines = new List<List<Vector2>>();

        static OsmData osm; // streets for the door side

        public static bool TryBuild(string lod2Path, OsmData data, MeshSink sink, AreaRect area, bool island, out int count)
        {
            count = 0;
            osm = data;
            hulls.Clear();
            Outlines.Clear();
            hullBounds.Clear();
            hullGrid.Clear();
            if (!File.Exists(lod2Path)) return false;
            var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(lod2Path));
            foreach (Dictionary<string, object> b in (List<object>)root["buildings"])
                if (BuildOne(b, sink, area, island)) count++;
            return true;
        }

        static bool BuildOne(Dictionary<string, object> b, MeshSink sink, AreaRect area, bool island)
        {
            string id = b.TryGetValue("id", out var idObj) ? (string)idObj : "";
            var rng = new System.Random(id.GetHashCode());
            var surfaces = new List<(string kind, List<Vector3> ring, List<List<Vector3>> holes)>();
            Vector3 sum = Vector3.zero;
            int n = 0;
            float maxY = 0f;
            foreach (Dictionary<string, object> s in (List<object>)b["s"])
            {
                var ring = ReadRing((List<object>)s["r"]);
                if (ring.Count < 3) continue;
                var holes = new List<List<Vector3>>();
                foreach (List<object> h in (List<object>)s["h"]) holes.Add(ReadRing(h));
                surfaces.Add(((string)s["t"], ring, holes));
                foreach (var p in ring) { sum += p; n++; maxY = Mathf.Max(maxY, p.y); }
            }
            if (n == 0) return false;
            Vector3 centre = sum / n;
            if (!area.Contains(new Vector2(centre.x, centre.z), -20f)) return false;

            var use = Classify(b.TryGetValue("function", out var f) ? (string)f : null);
            var style = PickStyle(use, island, rng, maxY);
            // Measured roof colours from the aerial image (pitched and flat parts separately); hand overrides still win.
            Color32? aerialFlat = null;
            if (AerialImage.Current != null)
            {
                var pitchedRings = new List<List<Vector2>>();
                var flatRings = new List<List<Vector2>>();
                foreach (var (kind, ring, _) in surfaces)
                {
                    if (kind != "R") continue;
                    var nrm = Newell(ring);
                    if (nrm.sqrMagnitude < 1e-8f) continue;
                    var flat2d = ring.ConvertAll(q => new Vector2(q.x, q.z));
                    (Mathf.Abs(nrm.normalized.y) > 0.97f ? flatRings : pitchedRings).Add(flat2d);
                }
                if (pitchedRings.Count > 0 && AerialImage.Current.RoofColor(pitchedRings, false) is Color32 rc) style.roof = rc;
                if (flatRings.Count > 0) aerialFlat = AerialImage.Current.RoofColor(flatRings, true);
            }
            // Hand tweaks (Data/<area>.buildings.json) by gml id or official address.
            var addresses = new List<string>();
            if (b.TryGetValue("addr", out var addrObj) && addrObj is List<object> al)
                foreach (var a in al) if (a is string s) addresses.Add(s);
            var custom = BuildingOverrides.Find(id, addresses);
            if (custom?.wall != null) { style.wall = custom.wall.Value; style.wallCode = custom.brick ? CodeBrick : CodePlaster; }
            if (custom?.roof != null) style.roof = custom.roof.Value;
            if (custom?.windows != null) style.windows = custom.windows.Value;
            if (custom?.chimney != null) style.chimney = custom.chimney.Value;
            string doorSide = custom?.door;
            bool wantDoor = doorSide != "none" && (style.windows || doorSide != null);
            var batch = sink.Get(WorldStyle.MatBuildings, new Vector2(centre.x, centre.z));
            int firstVertex = batch.vertices.Count;
            // Stand on the lowest ground point of the footprint (no floating on slopes).
            var footprint = new List<Vector2>();
            foreach (var (_, ring, _) in surfaces) foreach (var p in ring) if (p.y < 0.5f) footprint.Add(new Vector2(p.x, p.z));
            if (footprint.Count == 0) footprint.Add(new Vector2(centre.x, centre.z));
            float groundY = TerrainField.Current.MinUnder(footprint);
            AddHull(footprint);
            // Footprint for the map editor: the ground surface if the model has one, else the hull.
            List<Vector2> outline = null;
            foreach (var (kind, ring, _) in surfaces)
                if (kind == "G") { outline = new List<Vector2>(); foreach (var p in ring) outline.Add(new Vector2(p.x, p.z)); break; }
            outline ??= ConvexHull(footprint);
            // Storeys up to the eaves (lowest roof edge); the map editor can change them.
            float eave = float.MaxValue;
            foreach (var (kind, ring, _) in surfaces) if (kind == "R") foreach (var p in ring) eave = Mathf.Min(eave, p.y);
            if (eave == float.MaxValue || eave < 1f) eave = maxY;
            if (custom != null && custom.floors > 0) Restorey(surfaces, eave, custom.floors * BuildingBuilder.LevelHeight + 0.3f);
            BuildingOverrides.Register(id, "lod2", addresses, outline, Mathf.Max(1, Mathf.RoundToInt(eave / BuildingBuilder.LevelHeight)));
            Outlines.Add(outline);
            // Removed in the map editor: keep the hull (no OSM stand-in), build nothing.
            if (custom != null && custom.hidden) return false;

            // Some datasets wind wall rings inwards – detect per building and flip if needed.
            int agree = 0, walls = 0;
            foreach (var (kind, ring, _) in surfaces)
            {
                if (kind != "W") continue;
                var nrm = Newell(ring);
                var fc = Centroid(ring) - centre;
                fc.y = 0; nrm.y = 0;
                if (fc.sqrMagnitude < 0.01f || nrm.sqrMagnitude < 1e-6f) continue;
                walls++;
                if (Vector3.Dot(nrm, fc) > 0) agree++;
            }
            bool flipWalls = walls > 0 && agree * 2 < walls;

            var wallsForWindows = new List<(List<Vector3> ring, Vector3 normal)>();
            var gutters = new List<(Vector3 a, Vector3 b, Vector3 outward, float drop)>();
            var pitchedRoofs = new List<List<Vector3>>();
            var sills = new List<Sill>();
            var doorWalls = new List<Doors.Wall>();
            Vector3 ridgeA = Vector3.zero, ridgeB = Vector3.zero;
            float ridgeY = float.MinValue;
            bool pitched = false;

            foreach (var (kind, ring, holes) in surfaces)
            {
                if (kind == "G" || kind == "C") continue; // ground/closure never visible
                var normal = Newell(ring);
                if (normal.sqrMagnitude < 1e-8f) continue;
                normal.Normalize();

                Color32 color;
                Vector4 uvMode;
                if (kind == "R")
                {
                    if (normal.y < 0) normal = -normal;
                    bool flat = normal.y > 0.97f;
                    pitched |= !flat;
                    color = flat ? WithCode(WorldStyle.Tint(new Color32(122, 124, 128, 255), rng, 0.05f), CodeRoofFlat)
                                 : WithCode(style.roof, CodeRoofTiles);
                    if (flat && aerialFlat != null) color = WithCode(aerialFlat.Value, CodeRoofFlat);
                    uvMode = new Vector4(0, 0, 1, 0); // roof: planar tile coordinates
                    if (!flat) { CollectGutters(ring, normal, gutters); pitchedRoofs.Add(ring); }
                    foreach (var p in ring)
                    {
                        if (p.y > ridgeY + 0.05f) { ridgeY = p.y; ridgeA = p; ridgeB = p; }
                        else if (Mathf.Abs(p.y - ridgeY) <= 0.05f && (p - ridgeA).sqrMagnitude > (ridgeB - ridgeA).sqrMagnitude) ridgeB = p;
                    }
                }
                else
                {
                    if (flipWalls) normal = -normal;
                    color = WithCode(style.wall, style.wallCode);
                    uvMode = new Vector4(0, 0, 0, style.windows ? 1 : 0); // wall: facade coordinates
                }
                EmitPolygon(batch, ring, holes, normal, color, uvMode);
                if (kind == "W") wallsForWindows.Add((ring, normal));
                if (kind == "W") CollectPlinth(ring, normal, style, groundY);
                if (kind == "W" && wantDoor && DoorWall(ring, normal, out var dw)) doorWalls.Add(dw);
                if (kind == "W" && style.windows) CollectSills(ring, holes, normal, sills);
            }
            if (custom != null && custom.gableWindows > 0)
                GableWindows(batch, wallsForWindows, custom.gableWindows, custom.gableWindowFloor);

            Vector2? door = null;
            (Vector3 pos, Vector3 normal, float head)? frontDoor = null;
            if (doorWalls.Count > 0 && osm != null && Doors.Pick(osm, doorWalls, out var front, out float bay, doorSide == "street" ? null : doorSide))
            {
                Doors.Emit(batch, front, bay, custom?.doorColor);
                door = front.start + front.dir * bay;
                if (front.top <= 7f)
                    frontDoor = (new Vector3(door.Value.x, 0f, door.Value.y), new Vector3(front.normal.x, 0f, front.normal.y),
                                 Mathf.Max(2.25f, 2.36f / Mathf.Max(0.5f, front.heightScale)));
            }
            // Close-up detail (frames, sills, lintels, door canopy, gutters) is built at runtime near the camera.
            var groundOffset = Vector3.up * groundY;
            foreach (var sl in sills)
            {
                if (sl.ground && door.HasValue && (new Vector2(sl.centre.x, sl.centre.z) - door.Value).sqrMagnitude < 0.5f) continue;
                var wn = new Vector3(sl.normal.x, 0f, sl.normal.z).normalized;
                if (sl.shop) FacadeRecords.Window(sl.centre + groundOffset, wn, 0.95f, 2.1f / sl.k, true, false, style.wall);
                else FacadeRecords.Window(sl.centre + groundOffset, wn, 0.52f, 1.35f / sl.k, false, style.wallCode == CodeBrick, style.wall);
            }
            foreach (var (a, b2, outward, dropY) in gutters) FacadeRecords.Gutter(a + groundOffset, b2 + groundOffset, outward, dropY);
            CollectRoofEdges(pitchedRoofs, style.roof, groundOffset);
            if (frontDoor.HasValue) { var fd = frontDoor.Value; FacadeRecords.Door(fd.pos + groundOffset, fd.normal, 0.66f, fd.head); }

            if (style.chimney && pitched && ridgeY > 4f && (ridgeB - ridgeA).magnitude > 4f)
                Chimney(batch, Vector3.Lerp(ridgeA, ridgeB, 0.25f + (float)rng.NextDouble() * 0.5f), rng);
            TerrainDrape.Offset(batch, firstVertex, groundY);
            return true;
        }

        /// <summary>
        /// True if a LoD2 building already stands on this OSM footprint (centroid inside a LoD2 hull, or a hull
        /// corner inside the outline). Used to add only OSM houses that LoD2 lacks, e.g. new estates.
        /// </summary>
        public static bool Covers(List<Vector2> osmOuter)
        {
            var ring = PolygonTriangulator.Clean(osmOuter);
            if (ring.Count < 3) return true;
            var c = GeoUtil.Centroid(ring);
            var bb = GeoUtil.Bounds(ring);
            var seen = new HashSet<int>();
            for (int gx = Mathf.FloorToInt(bb.xMin / CoverCell); gx <= Mathf.FloorToInt(bb.xMax / CoverCell); gx++)
            for (int gz = Mathf.FloorToInt(bb.yMin / CoverCell); gz <= Mathf.FloorToInt(bb.yMax / CoverCell); gz++)
            {
                if (!hullGrid.TryGetValue(new Vector2Int(gx, gz), out var list)) continue;
                foreach (int i in list)
                {
                    if (!seen.Add(i) || !hullBounds[i].Overlaps(bb)) continue;
                    if (OsmData.PointInRing(c, hulls[i])) return true;
                    foreach (var p in hulls[i]) if (OsmData.PointInRing(p, ring)) return true;
                }
            }
            return false;
        }

        static void AddHull(List<Vector2> pts)
        {
            var hull = ConvexHull(pts);
            if (hull.Count < 3) return;
            int index = hulls.Count;
            var bb = GeoUtil.Bounds(hull);
            hulls.Add(hull);
            hullBounds.Add(bb);
            for (int gx = Mathf.FloorToInt(bb.xMin / CoverCell); gx <= Mathf.FloorToInt(bb.xMax / CoverCell); gx++)
            for (int gz = Mathf.FloorToInt(bb.yMin / CoverCell); gz <= Mathf.FloorToInt(bb.yMax / CoverCell); gz++)
            {
                var key = new Vector2Int(gx, gz);
                if (!hullGrid.TryGetValue(key, out var list)) hullGrid[key] = list = new List<int>();
                list.Add(index);
            }
        }

        /// <summary>Andrew's monotone chain.</summary>
        static List<Vector2> ConvexHull(List<Vector2> pts)
        {
            var p = new List<Vector2>(pts);
            p.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            if (p.Count < 3) return p;
            float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            var h = new List<Vector2>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = h.Count;
                for (int k = 0; k < p.Count; k++)
                {
                    var q = pass == 0 ? p[k] : p[p.Count - 1 - k];
                    while (h.Count >= start + 2 && Cross(h[h.Count - 2], h[h.Count - 1], q) <= 0) h.RemoveAt(h.Count - 1);
                    h.Add(q);
                }
                h.RemoveAt(h.Count - 1);
            }
            return h;
        }

        /// <summary>
        /// New eave height: everything up to the old eaves is stretched, everything above (roof, gable tips)
        /// moves up by the difference – the roof keeps its shape.
        /// </summary>
        static void Restorey(List<(string kind, List<Vector3> ring, List<List<Vector3>> holes)> surfaces, float oldEave, float newEave)
        {
            float scale = newEave / Mathf.Max(0.5f, oldEave), shift = newEave - oldEave;
            Vector3 Move(Vector3 p) => new Vector3(p.x, p.y <= oldEave + 0.01f ? p.y * scale : p.y + shift, p.z);
            foreach (var (_, ring, holes) in surfaces)
            {
                for (int i = 0; i < ring.Count; i++) ring[i] = Move(ring[i]);
                foreach (var h in holes) for (int i = 0; i < h.Count; i++) h[i] = Move(h[i]);
            }
        }

        static List<Vector3> ReadRing(List<object> pts)
        {
            var ring = new List<Vector3>(pts.Count);
            foreach (List<object> p in pts)
                ring.Add(new Vector3((float)(double)p[0], (float)(double)p[1], (float)(double)p[2]));
            if (ring.Count > 1 && (ring[0] - ring[ring.Count - 1]).sqrMagnitude < 1e-6f) ring.RemoveAt(ring.Count - 1);
            return ring;
        }

        /// <summary>Outward normal from ring orientation. Points are (east, up, north), the GML ring is
        /// counter-clockwise in (east, north, up), so evaluate Newell in that frame and swap back.</summary>
        static Vector3 Newell(List<Vector3> ring)
        {
            Vector3 nn = Vector3.zero;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector3 a = new Vector3(ring[i].x, ring[i].z, ring[i].y);
                Vector3 c = new Vector3(ring[(i + 1) % ring.Count].x, ring[(i + 1) % ring.Count].z, ring[(i + 1) % ring.Count].y);
                nn.x += (a.y - c.y) * (a.z + c.z);
                nn.y += (a.z - c.z) * (a.x + c.x);
                nn.z += (a.x - c.x) * (a.y + c.y);
            }
            return new Vector3(nn.x, nn.z, nn.y);
        }

        static Vector3 Centroid(List<Vector3> ring)
        {
            Vector3 s = Vector3.zero;
            foreach (var p in ring) s += p;
            return s / ring.Count;
        }

        /// <summary>Triangulates a planar 3D polygon and emits it facing <paramref name="normal"/>.</summary>
        static void EmitPolygon(MeshSink.Batch batch, List<Vector3> ring, List<List<Vector3>> holes, Vector3 normal, Color32 color, Vector4 uvMode)
        {
            // Project onto the dominant plane.
            int drop = Mathf.Abs(normal.x) > Mathf.Abs(normal.y)
                ? (Mathf.Abs(normal.x) > Mathf.Abs(normal.z) ? 0 : 2)
                : (Mathf.Abs(normal.y) > Mathf.Abs(normal.z) ? 1 : 2);
            var lookup = new Dictionary<Vector2, Vector3>();
            List<Vector2> To2D(List<Vector3> pts)
            {
                var r = new List<Vector2>(pts.Count);
                foreach (var p in pts)
                {
                    var q = drop == 0 ? new Vector2(p.y, p.z) : drop == 1 ? new Vector2(p.x, p.z) : new Vector2(p.x, p.y);
                    lookup[q] = p;
                    r.Add(q);
                }
                return r;
            }
            var outer2 = To2D(ring);
            var holes2 = new List<List<Vector2>>();
            foreach (var h in holes) if (h.Count >= 3) holes2.Add(To2D(h));
            if (!PolygonTriangulator.Triangulate(outer2, holes2, out var verts, out var tris)) return;

            var uv = FacadeOrRoofUv(ring, normal, uvMode);
            for (int t = 0; t < tris.Count; t += 3)
            {
                if (!lookup.TryGetValue(verts[tris[t]], out var a) || !lookup.TryGetValue(verts[tris[t + 1]], out var b) ||
                    !lookup.TryGetValue(verts[tris[t + 2]], out var c)) continue;
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0) (b, c) = (c, b);
                MeshSink.TriUv(batch, a, b, c, normal, color, uv(a), uv(b), uv(c));
            }
        }

        /// <summary>Per-vertex shader data: walls get (u along wall, height, eave height, window span),
        /// roofs get (u along the eave, v up the slope) with z = 1 flag.</summary>
        static System.Func<Vector3, Vector4> FacadeOrRoofUv(List<Vector3> ring, Vector3 normal, Vector4 mode)
        {
            if (mode.z > 0.5f)
            {
                Vector3 down = Vector3.ProjectOnPlane(Vector3.down, normal);
                if (down.sqrMagnitude < 1e-6f) down = Vector3.forward;
                down.Normalize();
                Vector3 along = Vector3.Cross(normal, down).normalized;
                return p => new Vector4(Vector3.Dot(p, along), -Vector3.Dot(p, down), -1f, -1f);
            }

            // Wall: horizontal axis along the wall, windows centred over its length.
            Vector3 dir = Vector3.Cross(Vector3.up, normal);
            dir.y = 0;
            if (dir.sqrMagnitude < 1e-6f) return _ => MeshSink.NoFacade;
            dir.Normalize();
            float minU = float.MaxValue, maxU = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in ring)
            {
                float u = Vector3.Dot(p, dir);
                minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            // Eave: if the top edge is flat (≥2 vertices at the maximum) it is the maximum; a single
            // apex means a gable, so the eave is the next lower vertex height. Pure gable triangles
            // (LoD2 often splits them off) therefore get eave == bottom → no windows.
            float eave = maxY;
            int atMax = 0;
            foreach (var p in ring) if (p.y > maxY - 0.15f) atMax++;
            if (atMax < 2)
            {
                eave = minY;
                foreach (var p in ring) if (p.y < maxY - 0.15f) eave = Mathf.Max(eave, p.y);
            }
            float len = maxU - minU;
            float wallH = eave - minY;
            int windows = mode.w > 0.5f && wallH > 2.5f ? Mathf.FloorToInt((len - 0.6f) / WindowSpacing) : 0;
            // Fit whole storeys under the eave: heights are rescaled so every storey is exactly 3 shader units.
            int floors = Mathf.Max(1, Mathf.RoundToInt(wallH / 3f));
            float k = wallH > 0.1f ? floors * 3f / wallH : 1f;
            float baseY = minY;
            float top = wallH * k;
            // No windows: still pass wall coordinates (span 0) so brick/plaster patterns line up.
            if (windows <= 0) return p => new Vector4(Vector3.Dot(p, dir) - minU, (p.y - baseY) * k, top, 0f);
            float span = windows * WindowSpacing, offset = minU + (len - span) * 0.5f;
            return p => new Vector4(Vector3.Dot(p, dir) - offset, (p.y - baseY) * k, top, span);
        }

        /// <summary>Plinth band along the bottom edge of a wall (same colours as the shader's Sockel).</summary>
        static void CollectPlinth(List<Vector3> ring, Vector3 normal, Style style, float groundY)
        {
            var n = new Vector3(normal.x, 0f, normal.z);
            if (n.sqrMagnitude < 0.5f) return;
            n.Normalize();
            float low = float.MaxValue;
            foreach (var p in ring) low = Mathf.Min(low, p.y);
            if (low > 0.3f) return;
            var colour = style.wallCode == CodeBrick
                ? new Color32((byte)(style.wall.r * 0.68f), (byte)(style.wall.g * 0.68f), (byte)(style.wall.b * 0.68f), 255)
                : new Color32(133, 133, 128, 255);
            for (int i = 0; i < ring.Count; i++)
            {
                Vector3 a = ring[i], b = ring[(i + 1) % ring.Count];
                if (Mathf.Abs(a.y - low) > 0.05f || Mathf.Abs(b.y - low) > 0.05f) continue;
                var up = Vector3.up * groundY;
                FacadeRecords.Plinth(a + up, b + up, n, 0.45f, colour);
            }
        }

        /// <summary>
        /// Verge boards on unshared sloped roof edges (gable ends) and ridge caps on shared horizontal top edges.
        /// </summary>
        static void CollectRoofEdges(List<List<Vector3>> roofs, Color32 roofColour, Vector3 offset)
        {
            Vector3Int Q(Vector3 p) => new Vector3Int(Mathf.RoundToInt(p.x * 20f), Mathf.RoundToInt(p.y * 20f), Mathf.RoundToInt(p.z * 20f));
            var count = new Dictionary<(Vector3Int, Vector3Int), int>();
            (Vector3Int, Vector3Int) K(Vector3 a, Vector3 b)
            {
                var qa = Q(a); var qb = Q(b);
                return qa.x < qb.x || qa.x == qb.x && (qa.z < qb.z || qa.z == qb.z && qa.y <= qb.y) ? (qa, qb) : (qb, qa);
            }
            foreach (var r in roofs)
                for (int i = 0; i < r.Count; i++)
                {
                    var k = K(r[i], r[(i + 1) % r.Count]);
                    count.TryGetValue(k, out int c); count[k] = c + 1;
                }
            var ridgesDone = new HashSet<(Vector3Int, Vector3Int)>();
            foreach (var r in roofs)
            {
                var centre = Vector3.zero; float top = float.MinValue;
                foreach (var p in r) { centre += p; top = Mathf.Max(top, p.y); }
                centre /= r.Count;
                for (int i = 0; i < r.Count; i++)
                {
                    Vector3 a = r[i], b = r[(i + 1) % r.Count];
                    var e = new Vector3(b.x - a.x, 0f, b.z - a.z);
                    float horiz = e.magnitude, dy = Mathf.Abs(b.y - a.y);
                    if (horiz < 0.3f) continue;
                    var k = K(a, b);
                    int c = count[k];
                    if (c == 1 && dy > 0.4f)
                    {
                        var perp = new Vector3(-e.z, 0f, e.x) / horiz;
                        if (Vector3.Dot(perp, (a + b) * 0.5f - centre) < 0f) perp = -perp;
                        FacadeRecords.Verge(a + offset, b + offset, perp);
                    }
                    else if (c >= 2 && dy < 0.05f && a.y > top - 0.05f && ridgesDone.Add(k))
                        FacadeRecords.Ridge(a + offset, b + offset, roofColour);
                }
            }
        }

        /// <summary>Eaves of a pitched roof face: horizontal edges at its lowest height, running across the slope.</summary>
        static void CollectGutters(List<Vector3> ring, Vector3 normal, List<(Vector3, Vector3, Vector3, float)> gutters)
        {
            var downhill = new Vector3(normal.x, 0f, normal.z);
            if (downhill.sqrMagnitude < 0.01f) return;
            downhill.Normalize();
            float low = float.MaxValue;
            foreach (var p in ring) low = Mathf.Min(low, p.y);
            if (low < 2f) return;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector3 a = ring[i], b = ring[(i + 1) % ring.Count];
                if (Mathf.Abs(a.y - low) > 0.08f || Mathf.Abs(b.y - low) > 0.08f) continue;
                var e = new Vector3(b.x - a.x, 0f, b.z - a.z);
                float len = e.magnitude;
                if (len < 1.5f || Mathf.Abs(Vector3.Dot(e / len, downhill)) > 0.3f) continue;
                gutters.Add((a, b, downhill, low));
            }
        }

        /// <summary>One shader window: centre = bottom centre of the opening on the wall, k = shader units per metre.</summary>
        struct Sill { public Vector3 centre, along, normal; public bool ground, shop; public float k; }

        /// <summary>
        /// Window sills (Fensterbänke) under every procedural window of this wall – same layout as FacadeMask in
        /// VertexColorLit.shader; only where the whole window lies inside the wall polygon (gables, holes).
        /// </summary>
        static void CollectSills(List<Vector3> ring, List<List<Vector3>> holes, Vector3 normal, List<Sill> sills)
        {
            Vector3 dir = Vector3.Cross(Vector3.up, normal);
            dir.y = 0;
            if (dir.sqrMagnitude < 1e-6f || Mathf.Abs(normal.y) > 0.3f) return;
            dir.Normalize();
            var uv = FacadeOrRoofUv(ring, normal, new Vector4(0, 0, 0, 1));
            var probe = uv(ring[0]);
            float span = probe.w, top = probe.z;
            if (span <= 0f) return;
            // Shader-space outline of the wall (u, scaled height); scale k from any two vertices with different heights.
            var poly = ring.ConvertAll(p => { var q = uv(p); return new Vector2(q.x, q.y); });
            var holePolys = holes.ConvertAll(h => h.ConvertAll(p => { var q = uv(p); return new Vector2(q.x, q.y); }));
            float minY = float.MaxValue, k = 1f;
            foreach (var p in ring) minY = Mathf.Min(minY, p.y);
            for (int i = 0; i < ring.Count; i++)
                if (ring[i].y - minY > 0.5f) { k = uv(ring[i]).y / (ring[i].y - minY); break; }
            if (k <= 0f) return;
            // World position of shader u = 0 on the wall plane.
            var p0 = ring[0];
            float u0 = uv(p0).x;
            Vector3 origin = new Vector3(p0.x, 0f, p0.z) - dir * u0;
            int bays = Mathf.RoundToInt(span / WindowSpacing);
            for (int floor = 0; floor * 3f + 2.85f <= top; floor++) // same rule as the shader (lintel ≥ 0.6 under the eaves)
            {
                bool shop = floor == 0 && top > 7f; // shop fronts (same rule as the shader)
                float sillH = floor * 3f + (shop ? 0.5f : 0.9f), headH = floor * 3f + (shop ? 2.6f : 2.25f);
                float hw = shop ? 1.1f : 0.65f;
                for (int bay = 0; bay < bays; bay++)
                {
                    float cu = (bay + 0.5f) * WindowSpacing;
                    bool inside = true;
                    foreach (var corner in new[] { new Vector2(cu - hw, sillH - 0.12f), new Vector2(cu + hw, sillH - 0.12f),
                                                   new Vector2(cu - hw, headH + 0.06f), new Vector2(cu + hw, headH + 0.06f) })
                    {
                        if (!OsmData.PointInRing(corner, poly)) { inside = false; break; }
                        foreach (var h in holePolys) if (h.Count >= 3 && OsmData.PointInRing(corner, h)) { inside = false; break; }
                        if (!inside) break;
                    }
                    if (!inside) continue;
                    var c = origin + dir * cu + Vector3.up * (minY + sillH / k);
                    sills.Add(new Sill { centre = c, along = dir, normal = normal, ground = floor == 0, shop = shop, k = k });
                }
            }
        }

        /// <summary>Ground-floor wall with window bays, in the same u/height frame as FacadeOrRoofUv.</summary>
        static bool DoorWall(List<Vector3> ring, Vector3 normal, out Doors.Wall w)
        {
            w = default;
            Vector3 dir = Vector3.Cross(Vector3.up, normal);
            dir.y = 0;
            if (dir.sqrMagnitude < 1e-6f || Mathf.Abs(normal.y) > 0.3f) return false;
            dir.Normalize();
            float minU = float.MaxValue, maxU = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in ring)
            {
                float u = Vector3.Dot(p, dir);
                minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            if (minY > 0.3f) return false; // not standing on the ground
            float eave = maxY;
            int atMax = 0;
            foreach (var p in ring) if (p.y > maxY - 0.15f) atMax++;
            if (atMax < 2)
            {
                eave = minY;
                foreach (var p in ring) if (p.y < maxY - 0.15f) eave = Mathf.Max(eave, p.y);
            }
            float len = maxU - minU, wallH = eave - minY;
            if (wallH < 2.5f) return false;
            int windows = Mathf.FloorToInt((len - 0.6f) / WindowSpacing);
            if (windows <= 0) return false;
            int floors = Mathf.Max(1, Mathf.RoundToInt(wallH / 3f));
            float k = floors * 3f / wallH;
            if (minY + 2.6f / k > eave) return false; // door would not fit under the eave
            float span = windows * WindowSpacing;
            var n2 = new Vector2(normal.x, normal.z).normalized;
            var d2 = new Vector2(dir.x, dir.z);
            var p0 = new Vector2(ring[0].x, ring[0].z);
            w = new Doors.Wall
            {
                start = p0 - d2 * Vector2.Dot(p0, d2), dir = d2, normal = n2,
                bayOffset = minU + (len - span) * 0.5f, bays = windows, heightScale = k, top = wallH * k,
            };
            return true;
        }

        /// <summary>
        /// Windows centred under the apex of every gable-shaped wall (top in the middle, not at the ends), on floor
        /// <paramref name="floor"/>; each one only if it fits completely inside the wall. Coplanar wall polygons are
        /// merged first – LoD2 often splits a gable at the ridge line into two trapezoids.
        /// </summary>
        static void GableWindows(MeshSink.Batch b, List<(List<Vector3> ring, Vector3 normal)> walls, int count, int floor)
        {
            // Plane test relative to a point on the wall: coordinates are ~1 km from the origin, so a plane
            // constant n·p would turn tiny normal differences into metres.
            var groups = new List<(Vector3 n, Vector3 o, List<Vector3> pts)>();
            foreach (var (ring, normal) in walls)
            {
                var n = new Vector3(normal.x, 0f, normal.z);
                if (n.sqrMagnitude < 0.5f) continue; // not a vertical wall
                n.Normalize();
                var c = Centroid(ring);
                int gi = groups.FindIndex(g => Vector3.Dot(g.n, n) > 0.995f && Mathf.Abs(Vector3.Dot(c - g.o, g.n)) < 0.15f);
                if (gi < 0) groups.Add((n, new Vector3(c.x, 0f, c.z), new List<Vector3>(ring)));
                else groups[gi].pts.AddRange(ring);
            }
            foreach (var (n, o, pts) in groups) GableWall(b, n, o, pts, count, floor);
        }

        static void GableWall(MeshSink.Batch b, Vector3 n, Vector3 o, List<Vector3> pts, int count, int floor)
        {
            var u = new Vector3(-n.z, 0f, n.x);
            // Wall outline in its own (s = along, y = up) coordinates: convex hull of all its polygons (gables are convex).
            var sy = new List<Vector2>(pts.Count);
            foreach (var p in pts) sy.Add(new Vector2(Vector3.Dot(p - o, u), p.y));
            var poly = ConvexHull(sy);
            if (poly.Count < 3) return;
            float minS = float.MaxValue, maxS = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var q in poly)
            {
                minS = Mathf.Min(minS, q.x); maxS = Mathf.Max(maxS, q.x);
                minY = Mathf.Min(minY, q.y); maxY = Mathf.Max(maxY, q.y);
            }
            float width = maxS - minS;
            if (width < 3f || maxY - minY < 3f) return;
            float apexS = 0f; int apexN = 0, apexAtEnd = 0;
            foreach (var q in poly)
                if (q.y > maxY - 0.15f)
                {
                    apexS += q.x; apexN++;
                    if (q.x < minS + width * 0.2f || q.x > maxS - width * 0.2f) apexAtEnd++;
                }
            apexS /= apexN;
            if (apexAtEnd > 0) return; // flat top or shed slope: not a gable

            // Storeys count from the building's ground (LoD2 heights are relative to it).
            float sill = floor * BuildingBuilder.LevelHeight + BuildingBuilder.WinSill;
            const float margin = 0.15f;
            float Offset(int i) => apexS + (i - (count - 1) * 0.5f) * (BuildingBuilder.WinWidth + BuildingBuilder.WinGap);
            bool Fits(float s, float sl)
            {
                float h = BuildingBuilder.WinWidth * 0.5f + margin;
                // Top corners pushed out/up so the window keeps RoofGap to the gable slopes.
                float hTop = h + BuildingBuilder.RoofGap - margin, yTop = sl + BuildingBuilder.WinHeight + BuildingBuilder.RoofGap - 0.1f;
                foreach (var corner in new[] { new Vector2(s - h, sl - margin), new Vector2(s + h, sl - margin),
                                               new Vector2(s - hTop, yTop), new Vector2(s + hTop, yTop) })
                    if (!OsmData.PointInRing(corner, poly)) return false;
                return true;
            }
            // Too close to the slopes: the whole row moves down (up to 0.6 m, not below its storey).
            float rowSill = sill;
            for (float drop = 0f; drop <= 0.6f + 1e-3f; drop += 0.1f)
            {
                rowSill = Mathf.Max(floor * BuildingBuilder.LevelHeight + 0.3f, sill - drop);
                bool all = true;
                for (int i = 0; i < count && all; i++) all = Fits(Offset(i), rowSill);
                if (all) break;
            }
            for (int i = 0; i < count; i++)
                if (Fits(Offset(i), rowSill))
                    BuildingBuilder.Window(b, o + u * Offset(i) + n * 0.03f, u, n, rowSill);
        }

        internal static void Chimney(MeshSink.Batch b, Vector3 basePoint, System.Random rng)
        {
            float s = 0.32f, h = 1.3f + (float)rng.NextDouble() * 0.4f;
            var c = WithCode(WorldStyle.Tint(new Color32(150, 66, 50, 255), rng, 0.08f), CodeBrick);
            Vector3 o = basePoint + Vector3.down * 0.6f;
            Vector3[] q = { o + new Vector3(-s, 0, -s), o + new Vector3(s, 0, -s), o + new Vector3(s, 0, s), o + new Vector3(-s, 0, s) };
            Vector3 up = Vector3.up * h;
            for (int i = 0; i < 4; i++)
            {
                Vector3 a = q[i], d = q[(i + 1) % 4];
                // Ring is CCW from above, so (a, a+up, d+up, d) faces outwards.
                MeshSink.Quad(b, a, a + up, d + up, d, c);
            }
            MeshSink.Quad(b, q[0] + up, q[3] + up, q[2] + up, q[1] + up, WithCode(new Color32(70, 66, 64, 255), CodePlain));
        }

        static Color32 WithCode(Color32 c, byte code) => new Color32(c.r, c.g, c.b, code);

        // ---------------------------------------------------------------- regional style

        static Use Classify(string function)
        {
            if (string.IsNullOrEmpty(function)) return Use.Other;
            string code = function.Contains("_") ? function.Substring(function.IndexOf('_') + 1) : function;
            if (!int.TryParse(code, out int c)) return Use.Other;
            if (c >= 1000 && c < 2000) return Use.Residential;           // Wohngebäude
            if (c == 2463 || c == 2465 || c == 2500 || c == 2501 || c == 2523) return Use.Garage; // Garagen, Versorgung
            if (c >= 2000 && c < 2400) return Use.Commercial;            // Handel, Dienstleistung, Gastronomie
            if (c >= 2400 && c < 3000) return Use.Industrial;            // Gewerbe, Lager, Landwirtschaft
            if (c == 3041 || c == 3042 || c == 3043 || c == 3044 || c == 3046) return Use.Church;
            if (c >= 3000 && c < 4000) return Use.Public;                // Öffentliche Gebäude
            return Use.Other;
        }

        static readonly Color32 KlinkerRed = new Color32(158, 72, 52, 255);
        static readonly Color32 KlinkerDark = new Color32(112, 58, 46, 255);
        static readonly Color32 KlinkerYellow = new Color32(205, 170, 118, 255);
        static readonly Color32 PlasterWhite = new Color32(238, 234, 226, 255);
        static readonly Color32 PlasterCream = new Color32(232, 220, 190, 255);
        static readonly Color32 PlasterGrey = new Color32(196, 196, 190, 255);
        static readonly Color32 PlasterOchre = new Color32(222, 196, 140, 255);
        static readonly Color32 RoofRed = new Color32(150, 58, 42, 255);
        static readonly Color32 RoofAnthracite = new Color32(62, 62, 66, 255);
        static readonly Color32 RoofBrown = new Color32(112, 66, 50, 255);
        static readonly Color32 MetalGrey = new Color32(150, 156, 160, 255);

        static Style PickStyle(Use use, bool island, System.Random rng, float height)
        {
            double r = rng.NextDouble();
            var s = new Style { windows = true };
            switch (use)
            {
                case Use.Residential:
                case Use.Other:
                    if (island)
                    {
                        // Islands: whitewashed Friesenhäuser and red Klinker.
                        if (r < 0.5) Plaster(ref s, PlasterWhite);
                        else if (r < 0.88) Brick(ref s, KlinkerRed);
                        else Brick(ref s, KlinkerDark);
                    }
                    else
                    {
                        if (r < 0.55) Brick(ref s, KlinkerRed);
                        else if (r < 0.7) Brick(ref s, KlinkerDark);
                        else if (r < 0.78) Brick(ref s, KlinkerYellow);
                        else if (r < 0.93) Plaster(ref s, PlasterWhite);
                        else Plaster(ref s, PlasterCream);
                    }
                    s.chimney = rng.NextDouble() < 0.6;
                    break;
                case Use.Commercial:
                case Use.Public:
                    // Town centres: Gründerzeit plaster facades between brick buildings.
                    if (r < 0.35) Brick(ref s, KlinkerRed);
                    else if (r < 0.55) Plaster(ref s, PlasterCream);
                    else if (r < 0.72) Plaster(ref s, PlasterWhite);
                    else if (r < 0.86) Plaster(ref s, PlasterOchre);
                    else Plaster(ref s, PlasterGrey);
                    break;
                case Use.Church:
                    Brick(ref s, height > 20f ? KlinkerRed : KlinkerDark);
                    break;
                case Use.Garage:
                    if (r < 0.5) Brick(ref s, KlinkerRed); else Plaster(ref s, PlasterGrey);
                    s.windows = false;
                    break;
                case Use.Industrial:
                    s.wall = r < 0.5 ? MetalGrey : PlasterGrey;
                    s.wallCode = CodePlain;
                    s.windows = false;
                    break;
            }
            s.wall = WorldStyle.Tint(s.wall, rng, 0.06f);
            double rr = rng.NextDouble();
            s.roof = WorldStyle.Tint(rr < 0.55 ? RoofRed : rr < 0.9 ? RoofAnthracite : RoofBrown, rng, 0.07f);
            if (use == Use.Industrial || use == Use.Garage) s.roof = WorldStyle.Tint(MetalGrey, rng, 0.05f);
            return s;
        }

        static void Brick(ref Style s, Color32 c) { s.wall = c; s.wallCode = CodeBrick; }
        static void Plaster(ref Style s, Color32 c) { s.wall = c; s.wallCode = CodePlaster; }
    }
}
