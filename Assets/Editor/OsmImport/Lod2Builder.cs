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

        public static bool TryBuild(string lod2Path, OsmData data, MeshSink sink, float half, bool island, out int count)
        {
            count = 0;
            if (!File.Exists(lod2Path)) return false;
            var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(lod2Path));
            foreach (Dictionary<string, object> b in (List<object>)root["buildings"])
                if (BuildOne(b, sink, half, island)) count++;
            return true;
        }

        static bool BuildOne(Dictionary<string, object> b, MeshSink sink, float half, bool island)
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
            if (Mathf.Abs(centre.x) > half + 20f || Mathf.Abs(centre.z) > half + 20f) return false;

            var use = Classify(b.TryGetValue("function", out var f) ? (string)f : null);
            var style = PickStyle(use, island, rng, maxY);
            var batch = sink.Get(WorldStyle.MatBuildings, new Vector2(centre.x, centre.z));

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
            }

            if (style.chimney && pitched && ridgeY > 4f && (ridgeB - ridgeA).magnitude > 4f)
                Chimney(batch, Vector3.Lerp(ridgeA, ridgeB, 0.25f + (float)rng.NextDouble() * 0.5f), rng);
            return true;
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
            // Eave = lowest of the "top" vertices (gable walls peak higher in the middle).
            float eave = maxY;
            foreach (var p in ring) if (p.y > minY + 2f) eave = Mathf.Min(eave, p.y);
            float len = maxU - minU;
            int windows = mode.w > 0.5f && eave - minY > 2.5f ? Mathf.FloorToInt((len - 0.6f) / WindowSpacing) : 0;
            // No windows: still pass wall coordinates (span 0) so brick/plaster patterns line up.
            if (windows <= 0) return p => new Vector4(Vector3.Dot(p, dir) - minU, p.y, eave, 0f);
            float span = windows * WindowSpacing, offset = minU + (len - span) * 0.5f;
            return p => new Vector4(Vector3.Dot(p, dir) - offset, p.y, eave, span);
        }

        static void Chimney(MeshSink.Batch b, Vector3 basePoint, System.Random rng)
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
