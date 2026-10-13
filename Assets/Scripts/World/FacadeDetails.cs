using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.World
{
    /// <summary>
    /// Close-up building detail that would be far too heavy to bake for a whole town: 3D window frames with
    /// Sprossen, stone sills, brick lintels (Rollschicht), door canopies and steps, gutters and downpipes.
    /// The importer writes a compact record list (Assets/Generated/&lt;id&gt;/&lt;id&gt;_facade.bytes); here the records are
    /// sorted into 32 m cells and meshes are built only for cells near the camera (and dropped again behind it).
    /// Far away the shader-painted windows of the building mesh remain.
    /// </summary>
    public class FacadeDetails : MonoBehaviour
    {
        public TextAsset data;
        /// <summary>Same records from a downloaded map (format 5); used when <see cref="data"/> is null.</summary>
        [System.NonSerialized] public byte[] bytes;
        public Material material;
        public float radius = 95f;

        // Record layout (10 floats): kind, x, y, z, ux, uz, a, b, c, colour (RGB packed).
        public const int Stride = 10;
        public const int KindWindow = 1, KindShop = 2, KindDoor = 3, KindGutter = 4, KindVerge = 5, KindRidge = 6, KindPlinth = 7;
        public const int FlagBrick = 1, FlagMullion = 2;

        const float Cell = 32f;
        float[] rec;
        readonly Dictionary<Vector2Int, List<int>> cells = new Dictionary<Vector2Int, List<int>>();
        readonly Dictionary<Vector2Int, GameObject> built = new Dictionary<Vector2Int, GameObject>();
        float nextCheck;
        Transform cam;

        readonly List<Vector3> v = new List<Vector3>();
        readonly List<Vector3> nrm = new List<Vector3>();
        readonly List<Color32> col = new List<Color32>();
        readonly List<int> tri = new List<int>();

        static readonly Color32 FrameWhite = new Color32(238, 236, 230, 255);
        static readonly Color32 FrameShop = new Color32(62, 66, 70, 255);
        static readonly Color32 SillStone = new Color32(214, 212, 205, 255);
        static readonly Color32 Zinc = new Color32(150, 156, 160, 255);
        static readonly Color32 StepStone = new Color32(170, 166, 158, 255);
        static readonly Color32 Canopy = new Color32(232, 230, 224, 255);

        void Start()
        {
            var src = data != null ? data.bytes : bytes;
            if (src == null || material == null) { enabled = false; return; }
            rec = new float[src.Length / 4];
            System.Buffer.BlockCopy(src, 0, rec, 0, rec.Length * 4);
            for (int i = 0; i + Stride <= rec.Length; i += Stride)
            {
                var k = new Vector2Int(Mathf.FloorToInt(rec[i + 1] / Cell), Mathf.FloorToInt(rec[i + 3] / Cell));
                if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<int>();
                l.Add(i);
            }
        }

        void Update()
        {
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + 0.25f;
            if (cam == null) { var c = Camera.main; if (c == null) return; cam = c.transform; }
            Refresh(cam.position, 3);
        }

        /// <summary>Builds up to <paramref name="budget"/> missing cells around <paramref name="position"/> and drops far ones.</summary>
        public void Refresh(Vector3 position, int budget)
        {
            if (rec == null) Start();
            var p = new Vector2(position.x, position.z);
            // Drop cells that fell behind.
            List<Vector2Int> drop = null;
            foreach (var kv in built)
                if (Distance(kv.Key, p) > radius + 25f) (drop ??= new List<Vector2Int>()).Add(kv.Key);
            if (drop != null)
                foreach (var k in drop)
                {
                    Destroy(built[k].GetComponent<MeshFilter>().sharedMesh);
                    Destroy(built[k]);
                    built.Remove(k);
                }
            // Build the nearest missing cells, a few per tick so walking never hitches.
            int r = Mathf.CeilToInt(radius / Cell);
            var centre = new Vector2Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell));
            var todo = new List<(float d, Vector2Int k)>();
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    var k = new Vector2Int(centre.x + dx, centre.y + dz);
                    if (built.ContainsKey(k) || !cells.ContainsKey(k)) continue;
                    float d = Distance(k, p);
                    if (d <= radius) todo.Add((d, k));
                }
            todo.Sort((a, b) => a.d.CompareTo(b.d));
            for (int i = 0; i < todo.Count && i < budget; i++) Build(todo[i].k);
        }

        static float Distance(Vector2Int k, Vector2 p)
        {
            // Distance from p to the cell rectangle.
            float x = Mathf.Max(k.x * Cell - p.x, 0f, p.x - (k.x + 1) * Cell);
            float z = Mathf.Max(k.y * Cell - p.y, 0f, p.y - (k.y + 1) * Cell);
            return Mathf.Sqrt(x * x + z * z);
        }

        void Build(Vector2Int k)
        {
            v.Clear(); nrm.Clear(); col.Clear(); tri.Clear();
            foreach (int i in cells[k])
            {
                int kind = (int)rec[i];
                var o = new Vector3(rec[i + 1], rec[i + 2], rec[i + 3]);
                var u = new Vector3(rec[i + 4], 0f, rec[i + 5]);
                var n = new Vector3(-u.z, 0f, u.x);            // outward
                var colour = Unpack(rec[i + 9]);
                switch (kind)
                {
                    case KindWindow: Window(o, u, n, rec[i + 6], rec[i + 7], (int)rec[i + 8], colour); break;
                    case KindShop: Shop(o, u, n, rec[i + 6], rec[i + 7]); break;
                    case KindDoor: Door(o, u, n, rec[i + 6], rec[i + 7]); break;
                    case KindGutter: Gutter(o, u, n, rec[i + 6], rec[i + 7], rec[i + 8]); break;
                    case KindVerge: Verge(o, u, n, rec[i + 6], rec[i + 7]); break;
                    case KindRidge: Box(o, u, n, -0.04f, rec[i + 6] + 0.04f, -0.03f, 0.09f, -0.11f, 0.11f, Shade(colour, 0.78f)); break;
                    case KindPlinth: Box(o, u, n, 0f, rec[i + 6], -0.3f, rec[i + 7], 0f, 0.035f, colour); break;
                }
            }
            var mesh = new Mesh { name = $"Facade_{k.x}_{k.y}" };
            if (v.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(v); mesh.SetNormals(nrm); mesh.SetColors(col); mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            var go = new GameObject(mesh.name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            built[k] = go;
        }

        static Color32 Unpack(float f)
        {
            int c = (int)f;
            return new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, 255);
        }

        public static float Pack(Color32 c) => (c.r << 16) | (c.g << 8) | c.b;

        // ------------------------------------------------------------------ parts

        /// <summary>Residential window. o = bottom centre of the opening on the wall plane, w = half width, h = height.</summary>
        void Window(Vector3 o, Vector3 u, Vector3 n, float w, float h, int flags, Color32 wall)
        {
            const float f = 0.08f, depth = 0.04f;
            // Frame (Blendrahmen) around the opening, standing 4 cm proud of the wall.
            Box(o, u, n, -w - f, -w, -0.02f, h + 0.06f, 0f, depth, FrameWhite);
            Box(o, u, n, w, w + f, -0.02f, h + 0.06f, 0f, depth, FrameWhite);
            Box(o, u, n, -w, w, h, h + 0.06f, 0f, depth, FrameWhite);
            Box(o, u, n, -w, w, -0.02f, 0.04f, 0f, depth, FrameWhite);
            if ((flags & FlagMullion) != 0)
            {
                float t = h * 0.62f;
                Box(o, u, n, -0.025f, 0.025f, 0.04f, h, 0f, 0.028f, FrameWhite);
                Box(o, u, n, -w, w, t - 0.025f, t + 0.025f, 0f, 0.028f, FrameWhite);
            }
            // Stone sill under the window, 13 cm deep.
            Box(o, u, n, -w - 0.13f, w + 0.13f, -0.09f, -0.02f, 0f, 0.13f, SillStone);
            // Rollschicht: band of upright bricks over the opening on Klinker houses.
            if ((flags & FlagBrick) != 0)
            {
                var dark = new Color32((byte)(wall.r * 0.7f), (byte)(wall.g * 0.7f), (byte)(wall.b * 0.72f), 255);
                Box(o, u, n, -w - f - 0.08f, w + f + 0.08f, h + 0.06f, h + 0.26f, 0f, 0.025f, dark);
            }
        }

        void Shop(Vector3 o, Vector3 u, Vector3 n, float w, float h)
        {
            const float f = 0.1f, depth = 0.05f;
            Box(o, u, n, -w - f, -w, -0.05f, h + 0.08f, 0f, depth, FrameShop);
            Box(o, u, n, w, w + f, -0.05f, h + 0.08f, 0f, depth, FrameShop);
            Box(o, u, n, -w, w, h, h + 0.08f, 0f, depth, FrameShop);
            Box(o, u, n, -w - f, w + f, -0.12f, 0.02f, 0f, 0.08f, FrameShop);
            Box(o, u, n, -0.03f, 0.03f, 0.02f, h, 0f, 0.03f, FrameShop);
        }

        /// <summary>Front door: canopy on two brackets and a stone step.</summary>
        void Door(Vector3 o, Vector3 u, Vector3 n, float w, float h)
        {
            float cw = w + 0.3f;
            Box(o, u, n, -cw, cw, h + 0.22f, h + 0.3f, 0f, 0.85f, Canopy);
            Box(o, u, n, -cw + 0.05f, -cw + 0.1f, h - 0.1f, h + 0.22f, 0f, 0.5f, Canopy);
            Box(o, u, n, cw - 0.1f, cw - 0.05f, h - 0.1f, h + 0.22f, 0f, 0.5f, Canopy);
            Box(o, u, n, -w - 0.2f, w + 0.2f, -0.05f, 0.15f, 0f, 0.45f, StepStone);
        }

        /// <summary>Eave gutter along u (length len) and a downpipe dropping to the ground at the far end.</summary>
        void Gutter(Vector3 o, Vector3 u, Vector3 n, float len, float drop, float pipeEnd)
        {
            // Rinne: open box 14 cm wide hanging just under the roof edge.
            Box(o, u, n, -0.05f, len + 0.05f, -0.13f, -0.02f, 0.02f, 0.16f, Zinc);
            if (drop > 1f)
            {
                float at = pipeEnd > 0.5f ? len - 0.2f : 0.2f;
                Box(o, u, n, at - 0.045f, at + 0.045f, -drop, -0.12f, 0.03f, 0.12f, Zinc);
            }
        }

        /// <summary>White verge board (Ortgangbrett) under a sloped gable edge: runs len along u while rising dy.</summary>
        void Verge(Vector3 o, Vector3 u, Vector3 n, float len, float dy)
        {
            const float board = 0.22f, out0 = 0.0f, out1 = 0.05f;
            Vector3 P(float a, float y, float b) => o + u * a + Vector3.up * (y + dy * Mathf.Clamp01(a / Mathf.Max(0.01f, len))) + n * b;
            float a0 = -0.05f, a1 = len + 0.05f;
            Quad(P(a0, -board, out1), P(a0, 0.03f, out1), P(a1, 0.03f, out1), P(a1, -board, out1), n, FrameWhite);
            Quad(P(a0, -board, out0), P(a0, -board, out1), P(a1, -board, out1), P(a1, -board, out0), Vector3.down, Shade(FrameWhite, 0.72f));
            Quad(P(a0, 0.03f, out0), P(a0, 0.03f, out1), P(a1, 0.03f, out1), P(a1, 0.03f, out0), Vector3.up, FrameWhite);
        }

        /// <summary>Box in the wall frame: u from u0..u1, y from y0..y1 (relative to o), n from n0..n1. Back face omitted.</summary>
        void Box(Vector3 o, Vector3 u, Vector3 n, float u0, float u1, float y0, float y1, float n0, float n1, Color32 c)
        {
            Vector3 P(float a, float y, float b) => o + u * a + Vector3.up * y + n * b;
            Quad(P(u0, y0, n1), P(u0, y1, n1), P(u1, y1, n1), P(u1, y0, n1), n, c);            // front
            Quad(P(u0, y1, n0), P(u0, y1, n1), P(u1, y1, n1), P(u1, y1, n0), Vector3.up, Shade(c, 1.06f)); // top
            Quad(P(u0, y0, n0), P(u0, y0, n1), P(u1, y0, n1), P(u1, y0, n0), Vector3.down, Shade(c, 0.7f));
            Quad(P(u0, y0, n0), P(u0, y1, n0), P(u0, y1, n1), P(u0, y0, n1), -u, Shade(c, 0.88f));
            Quad(P(u1, y0, n0), P(u1, y1, n0), P(u1, y1, n1), P(u1, y0, n1), u, Shade(c, 0.88f));
        }

        static Color32 Shade(Color32 c, float k) =>
            new Color32((byte)Mathf.Min(255, c.r * k), (byte)Mathf.Min(255, c.g * k), (byte)Mathf.Min(255, c.b * k), 255);

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color32 colour)
        {
            // Wind so the face looks along 'normal' (Unity: clockwise seen from the front).
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f) { var t = b; b = d; d = t; }
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            for (int k = 0; k < 4; k++) { nrm.Add(normal); col.Add(colour); }
            tri.Add(i); tri.Add(i + 1); tri.Add(i + 2);
            tri.Add(i); tri.Add(i + 2); tri.Add(i + 3);
        }
    }
}
