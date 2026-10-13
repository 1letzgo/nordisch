using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Front doors: one per house, on the ground-floor wall facing the nearest street, placed exactly over one of the
    /// shader's window bays so the procedural window behind it is covered. Geometry (frame, coloured leaf with a glazed
    /// top light, handle, stone step); shops get a glazed door instead.
    /// </summary>
    public static class Doors
    {
        public const float WindowSpacing = 2.6f;

        // ------------------------------------------------------------------ streets

        const float Cell = 30f;
        static OsmData indexed;
        static readonly Dictionary<Vector2Int, List<(Vector2 a, Vector2 b)>> roads = new Dictionary<Vector2Int, List<(Vector2, Vector2)>>();

        static void EnsureIndex(OsmData data)
        {
            if (indexed == data) return;
            indexed = data;
            roads.Clear();
            foreach (var w in data.ways)
            {
                var hw = w.tags.Get("highway");
                if (hw == null || hw == "motorway" || hw == "trunk" || hw == "steps" || hw == "proposed" || hw == "construction") continue;
                for (int i = 0; i < w.points.Count - 1; i++)
                {
                    Vector2 a = w.points[i], b = w.points[i + 1];
                    var min = Vector2.Min(a, b); var max = Vector2.Max(a, b);
                    for (int x = Mathf.FloorToInt(min.x / Cell); x <= Mathf.FloorToInt(max.x / Cell); x++)
                    for (int y = Mathf.FloorToInt(min.y / Cell); y <= Mathf.FloorToInt(max.y / Cell); y++)
                    {
                        var key = new Vector2Int(x, y);
                        if (!roads.TryGetValue(key, out var list)) roads[key] = list = new List<(Vector2, Vector2)>();
                        list.Add((a, b));
                    }
                }
            }
        }

        /// <summary>Nearest street point within ~60 m (searching outwards), or false.</summary>
        static bool NearestRoad(Vector2 p, out Vector2 best)
        {
            best = default;
            float bestD = float.MaxValue;
            int cx = Mathf.FloorToInt(p.x / Cell), cy = Mathf.FloorToInt(p.y / Cell);
            for (int ring = 0; ring <= 2; ring++)
            {
                for (int x = cx - ring; x <= cx + ring; x++)
                for (int y = cy - ring; y <= cy + ring; y++)
                {
                    if (Mathf.Max(Mathf.Abs(x - cx), Mathf.Abs(y - cy)) != ring) continue;
                    if (!roads.TryGetValue(new Vector2Int(x, y), out var list)) continue;
                    foreach (var (a, b) in list)
                    {
                        Vector2 ab = b - a;
                        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                        var q = a + ab * t;
                        float d = (p - q).sqrMagnitude;
                        if (d < bestD) { bestD = d; best = q; }
                    }
                }
                if (bestD < (ring * Cell) * (ring * Cell)) break; // nothing closer can be further out
            }
            return bestD < float.MaxValue;
        }

        // ------------------------------------------------------------------ choosing the wall

        /// <summary>A ground-floor wall that could take a door: start point, direction along it, outward normal, window bays.</summary>
        public struct Wall
        {
            public Vector2 start, dir, normal;
            public float bayOffset; // u of the first bay's left edge
            public int bays;
            public float heightScale; // shader units per metre (LoD2 fits whole storeys)
            public float top;         // wall height in shader units (shop fronts above 7)
        }

        /// <summary>Picks the wall facing the closest street (front side) and the bay nearest to it. False if none fits.</summary>
        public static bool Pick(OsmData data, List<Wall> walls, out Wall wall, out float bayCentre, string side = null)
        {
            wall = default; bayCentre = 0f;
            if (walls.Count == 0) return false;
            // Fixed side (map editor): the wall facing that compass direction best, middle bay.
            Vector2 compass = side == "N" ? Vector2.up : side == "S" ? Vector2.down : side == "E" ? Vector2.right : side == "W" ? Vector2.left : Vector2.zero;
            if (compass != Vector2.zero)
            {
                float best = 0.5f;
                foreach (var w in walls)
                {
                    if (w.bays <= 0) continue;
                    float d = Vector2.Dot(w.normal, compass) + w.bays * 0.01f; // ties: the wider wall
                    if (d <= best) continue;
                    best = d;
                    wall = w;
                    bayCentre = w.bayOffset + ((w.bays - 1) / 2 + 0.5f) * WindowSpacing;
                }
                return best > 0.5f;
            }
            EnsureIndex(data);
            float bestScore = float.MaxValue;
            foreach (var w in walls)
            {
                if (w.bays <= 0) continue;
                var mid = w.start + w.dir * (w.bayOffset + w.bays * WindowSpacing * 0.5f);
                float score;
                Vector2 road = default;
                if (NearestRoad(mid + w.normal * 0.5f, out road))
                {
                    var to = road - mid;
                    float facing = to.sqrMagnitude > 1e-4f ? Vector2.Dot(to.normalized, w.normal) : 0f;
                    score = to.magnitude + (facing > 0.3f ? 0f : 60f) - facing * 3f;
                }
                else score = 500f - w.bays; // no street around: longest wall
                if (score >= bestScore) continue;
                bestScore = score;
                wall = w;
                // Bay closest to where the street is.
                float along = road != default ? Vector2.Dot(road - w.start, w.dir) : w.bayOffset + w.bays * WindowSpacing * 0.5f;
                int i = Mathf.Clamp(Mathf.FloorToInt((along - w.bayOffset) / WindowSpacing), 0, w.bays - 1);
                bayCentre = w.bayOffset + (i + 0.5f) * WindowSpacing;
            }
            return bestScore < float.MaxValue;
        }

        // ------------------------------------------------------------------ geometry

        static readonly Color32[] LeafColors =
        {
            new Color32(38, 78, 60, 255),    // Friesengrün
            new Color32(30, 52, 78, 255),    // navy
            new Color32(112, 38, 34, 255),   // ochsenblut
            new Color32(232, 230, 222, 255), // white
            new Color32(110, 72, 44, 255),   // wood
            new Color32(78, 98, 112, 255),   // grey-blue
        };
        static readonly Color32 FrameWhite = new Color32(236, 233, 226, 255);
        static readonly Color32 FrameShop = new Color32(70, 72, 76, 255);
        static readonly Color32 Glass = new Color32(68, 82, 94, 255);
        static readonly Color32 Handle = new Color32(196, 170, 96, 255);
        static readonly Color32 Step = new Color32(150, 146, 140, 255);

        /// <summary>Emits a door centred at <paramref name="bayCentre"/> on the wall (local y = 0 at the wall foot).</summary>
        public static void Emit(MeshSink.Batch b, Wall w, float bayCentre, Color32? colour = null)
        {
            var c2 = w.start + w.dir * bayCentre;
            var c = new Vector3(c2.x, 0f, c2.y);
            var u = new Vector3(w.dir.x, 0f, w.dir.y);
            var n = new Vector3(w.normal.x, 0f, w.normal.y);
            var rng = new System.Random(Mathf.RoundToInt(c2.x * 10f) * 73856093 ^ Mathf.RoundToInt(c2.y * 10f) * 19349663);
            bool shop = w.top > 7f;
            float k = Mathf.Max(0.5f, w.heightScale);

            void Rect(float x0, float x1, float yA, float yB, float out_, Color32 col)
            {
                Vector3 o = c + n * out_;
                Vector3 p0 = o + u * x0 + Vector3.up * yA, p1 = o + u * x0 + Vector3.up * yB;
                Vector3 p2 = o + u * x1 + Vector3.up * yB, p3 = o + u * x1 + Vector3.up * yA;
                if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), n) < 0f) { var t = p0; p0 = p3; p3 = t; t = p1; p1 = p2; p2 = t; }
                MeshSink.Quad(b, p0, p1, p2, p3, col);
            }

            if (shop)
            {
                // Covers the shop window (shader: 1.9 wide, 0.5–2.6 high) with a glazed double door.
                float hw = 1.08f, top = 2.72f / k;
                Rect(-hw, hw, 0f, top, 0.03f, FrameShop);
                Rect(-hw + 0.1f, -0.03f, 0.08f, top - 0.1f, 0.04f, Glass);
                Rect(0.03f, hw - 0.1f, 0.08f, top - 0.1f, 0.04f, Glass);
                Rect(-0.12f, -0.07f, 0.9f, 1.3f, 0.05f, FrameShop);
                Rect(0.07f, 0.12f, 0.9f, 1.3f, 0.05f, FrameShop);
                return;
            }

            // Covers the residential window (shader: 1.04 wide + frame, sill 0.9, head 2.25 in shader units).
            float half = 0.66f, head = Mathf.Max(2.25f, 2.36f / k), f = 0.09f;
            var leaf = LeafColors[rng.Next(LeafColors.Length)];
            if (colour.HasValue) leaf = new Color32(colour.Value.r, colour.Value.g, colour.Value.b, 255);
            Rect(-half, half, 0f, head, 0.03f, FrameWhite);
            Rect(-half + f, half - f, 0.02f, head - f, 0.045f, leaf);
            // Top light (glazed band) and two panels.
            float lightY = head - f - 0.42f;
            Rect(-half + f, half - f, lightY - 0.05f, lightY, 0.05f, FrameWhite);
            Rect(-half + f + 0.06f, half - f - 0.06f, lightY + 0.05f, head - f - 0.05f, 0.055f, Glass);
            var panel = Color32.Lerp(leaf, new Color32(0, 0, 0, 255), 0.18f);
            Rect(-half + f + 0.1f, half - f - 0.1f, 0.25f, 0.95f, 0.055f, panel);
            Rect(-half + f + 0.1f, half - f - 0.1f, 1.1f, lightY - 0.15f, 0.055f, panel);
            Rect(half - f - 0.2f, half - f - 0.12f, 1.0f, 1.12f, 0.07f, Handle);

            // Stone step in front of the door.
            float sw = half + 0.18f, depth = 0.38f, sh = 0.14f;
            Vector3 a = c + n * 0.03f, front = a + n * depth;
            Face(b, a - u * sw + Vector3.up * sh, a + u * sw + Vector3.up * sh, front + u * sw + Vector3.up * sh, front - u * sw + Vector3.up * sh, Vector3.up, Step);
            Rect(-sw, sw, -0.3f, sh, 0.03f + depth, Step);
            foreach (float s in new[] { -1f, 1f }) // sides
                Face(b, a + u * (s * sw) + Vector3.down * 0.3f, a + u * (s * sw) + Vector3.up * sh,
                     front + u * (s * sw) + Vector3.up * sh, front + u * (s * sw) + Vector3.down * 0.3f, u * s, Step);
        }

        /// <summary>Quad facing <paramref name="facing"/> (winding fixed up as needed).</summary>
        static void Face(MeshSink.Batch b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 facing, Color32 col)
        {
            if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), facing) < 0f) { var t = p0; p0 = p3; p3 = t; t = p1; p1 = p2; p2 = t; }
            MeshSink.Quad(b, p0, p1, p2, p3, col);
        }
    }
}
