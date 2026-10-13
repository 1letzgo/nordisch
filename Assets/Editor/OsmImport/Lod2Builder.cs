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
                if (kind == "W" && wantDoor && DoorWall(ring, normal, out var dw)) doorWalls.Add(dw);
                if (kind == "W" && style.windows) CollectSills(ring, holes, normal, sills);
            }
            if (custom != null && custom.gableWindows > 0)
                GableWindows(batch, wallsForWindows, custom.gableWindows, custom.gableWindowFloor);

            Vector2? door = null;
            if (doorWalls.Count > 0 && osm != null && Doors.Pick(osm, doorWalls, out var front, out float bay, doorSide == "street" ? null : doorSide))
            {
                Doors.Emit(batch, front, bay, custom?.doorColor);
                door = front.start + front.dir * bay;
            }
            if (sills.Count > 0)
            {
                var details = sink.Get(WorldStyle.MatDetails, new Vector2(centre.x, centre.z));
                int firstDetail = details.vertices.Count;
                var sillColor = WithCode(WorldStyle.Tint(new Color32(222, 220, 214, 255), rng, 0.03f), CodePlain);
                foreach (var sl in sills)
                {
                    if (sl.ground && door.HasValue && (new Vector2(sl.centre.x, sl.centre.z) - door.Value).sqrMagnitude < 0.5f) continue;
                    EmitSill(details, sl, sillColor);
                }
                TerrainDrape.Offset(details, firstDetail, groundY);
            }

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

        struct Sill { public Vector3 centre, along, normal; public bool ground; }

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
                if (floor == 0 && top > 7f) continue; // shop fronts: no sills
                float sillH = floor * 3f + 0.9f, headH = floor * 3f + 2.25f;
                for (int bay = 0; bay < bays; bay++)
                {
                    float cu = (bay + 0.5f) * WindowSpacing;
                    bool inside = true;
                    foreach (var corner in new[] { new Vector2(cu - 0.65f, sillH - 0.12f), new Vector2(cu + 0.65f, sillH - 0.12f),
                                                   new Vector2(cu - 0.65f, headH + 0.06f), new Vector2(cu + 0.65f, headH + 0.06f) })
                    {
                        if (!OsmData.PointInRing(corner, poly)) { inside = false; break; }
                        foreach (var h in holePolys) if (h.Count >= 3 && OsmData.PointInRing(corner, h)) { inside = false; break; }
                        if (!inside) break;
                    }
                    if (!inside) continue;
                    var c = origin + dir * cu + Vector3.up * (minY + (sillH - 0.1f) / k);
                    sills.Add(new Sill { centre = c, along = dir, normal = normal, ground = floor == 0 });
                }
            }
        }

        /// <summary>Stone sill: top and front edge of a slab 1.3 m wide, 13 cm deep (ends/underside are never seen).</summary>
        static void EmitSill(MeshSink.Batch b, Sill s, Color32 color)
        {
            Vector3 n = new Vector3(s.normal.x, 0f, s.normal.z).normalized, u = s.along * 0.65f;
            Vector3 back = s.centre, front = s.centre + n * 0.13f;
            Vector3 upB = Vector3.up * 0.05f, upF = Vector3.up * 0.035f, down = Vector3.down * 0.03f;
            var edge = new Color32((byte)(color.r * 0.86f), (byte)(color.g * 0.86f), (byte)(color.b * 0.86f), color.a);
            void Face(Vector3 a, Vector3 bb, Vector3 c, Vector3 d, Vector3 outward, Color32 col)
            {
                if (Vector3.Dot(Vector3.Cross(bb - a, c - a), outward) < 0f) MeshSink.Quad(b, d, c, bb, a, col);
                else MeshSink.Quad(b, a, bb, c, d, col);
            }
            Face(back - u + upB, back + u + upB, front + u + upF, front - u + upF, Vector3.up, color); // top
            Face(front - u + upF, front + u + upF, front + u + down, front - u + down, n, edge);      // front edge
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
