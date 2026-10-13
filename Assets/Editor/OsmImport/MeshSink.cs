using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Collects flat-shaded geometry per (material, 100 m chunk) so the area renders with
    /// few draw calls while still allowing per-chunk culling.
    /// </summary>
    public class MeshSink
    {
        public const float ChunkSize = 100f;
        /// <summary>Small chunks for <see cref="WorldStyle.MatDetails"/>, so distance culling works per block.</summary>
        public const float DetailChunkSize = 25f;

        public class Batch
        {
            public readonly List<Vector3> vertices = new List<Vector3>();
            public readonly List<Vector3> normals = new List<Vector3>();
            public readonly List<Color32> colors = new List<Color32>();
            /// <summary>Facade data for the shader: (u along wall, height, wall top, window span). y &lt; 0 = no facade.</summary>
            public readonly List<Vector4> uvs = new List<Vector4>();
            public readonly List<int> triangles = new List<int>();
        }

        readonly Dictionary<(string mat, Vector2Int chunk), Batch> batches = new Dictionary<(string, Vector2Int), Batch>();

        public IEnumerable<KeyValuePair<(string mat, Vector2Int chunk), Batch>> Batches => batches;

        public static Vector2Int ChunkOf(Vector2 p) =>
            new Vector2Int(Mathf.FloorToInt(p.x / ChunkSize), Mathf.FloorToInt(p.y / ChunkSize));

        public Batch Get(string material, Vector2 anchor)
        {
            var key = (material, material == WorldStyle.MatDetails
                ? new Vector2Int(Mathf.FloorToInt(anchor.x / DetailChunkSize), Mathf.FloorToInt(anchor.y / DetailChunkSize))
                : ChunkOf(anchor));
            if (!batches.TryGetValue(key, out var b)) batches[key] = b = new Batch();
            return b;
        }

        public static readonly Vector4 NoFacade = new Vector4(0f, -1f, 0f, 0f);

        /// <summary>Adds a triangle (clockwise seen from the front) with a flat normal.</summary>
        public static void Tri(Batch b, Vector3 a, Vector3 c1, Vector3 c2, Color32 color)
        {
            var n = Vector3.Cross(c1 - a, c2 - a).normalized;
            int i = b.vertices.Count;
            b.vertices.Add(a); b.vertices.Add(c1); b.vertices.Add(c2);
            b.normals.Add(n); b.normals.Add(n); b.normals.Add(n);
            b.colors.Add(color); b.colors.Add(color); b.colors.Add(color);
            b.uvs.Add(NoFacade); b.uvs.Add(NoFacade); b.uvs.Add(NoFacade);
            b.triangles.Add(i); b.triangles.Add(i + 1); b.triangles.Add(i + 2);
        }

        /// <summary>Triangle with explicit normal and per-vertex shader data.</summary>
        public static void TriUv(Batch b, Vector3 a, Vector3 c1, Vector3 c2, Vector3 normal, Color32 color, Vector4 uvA, Vector4 uvB, Vector4 uvC)
        {
            int i = b.vertices.Count;
            b.vertices.Add(a); b.vertices.Add(c1); b.vertices.Add(c2);
            b.normals.Add(normal); b.normals.Add(normal); b.normals.Add(normal);
            b.colors.Add(color); b.colors.Add(color); b.colors.Add(color);
            b.uvs.Add(uvA); b.uvs.Add(uvB); b.uvs.Add(uvC);
            b.triangles.Add(i); b.triangles.Add(i + 1); b.triangles.Add(i + 2);
        }

        /// <summary>Quad a-b-c-d (clockwise seen from the front), shared flat normal.</summary>
        public static void Quad(Batch b, Vector3 a, Vector3 bb, Vector3 c, Vector3 d, Color32 color) =>
            Quad(b, a, bb, c, d, color, NoFacade, NoFacade, NoFacade, NoFacade);

        public static void Quad(Batch b, Vector3 a, Vector3 bb, Vector3 c, Vector3 d, Color32 color,
            Vector4 uvA, Vector4 uvB, Vector4 uvC, Vector4 uvD)
        {
            var n = Vector3.Cross(bb - a, c - a).normalized;
            int i = b.vertices.Count;
            b.vertices.Add(a); b.vertices.Add(bb); b.vertices.Add(c); b.vertices.Add(d);
            for (int k = 0; k < 4; k++) { b.normals.Add(n); b.colors.Add(color); }
            b.uvs.Add(uvA); b.uvs.Add(uvB); b.uvs.Add(uvC); b.uvs.Add(uvD);
            b.triangles.Add(i); b.triangles.Add(i + 1); b.triangles.Add(i + 2);
            b.triangles.Add(i); b.triangles.Add(i + 2); b.triangles.Add(i + 3);
        }

        /// <summary>Horizontal polygon at height y, facing up. Indices come CCW from the triangulator.</summary>
        public static void FlatPolygon(Batch b, List<Vector2> verts, List<int> ccwTris, float y, Color32 color)
        {
            int baseIndex = b.vertices.Count;
            foreach (var v in verts)
            {
                b.vertices.Add(new Vector3(v.x, y, v.y));
                b.normals.Add(Vector3.up);
                b.colors.Add(color);
                b.uvs.Add(NoFacade);
            }
            // CCW in (x, z) seen from above == counter-clockwise on screen -> flip for Unity front faces.
            for (int t = 0; t < ccwTris.Count; t += 3)
            {
                b.triangles.Add(baseIndex + ccwTris[t]);
                b.triangles.Add(baseIndex + ccwTris[t + 2]);
                b.triangles.Add(baseIndex + ccwTris[t + 1]);
            }
        }

        /// <param name="withShaderData">False for materials without the facade keyword (trees, water): saves 16 bytes per vertex.</param>
        public static Mesh ToMesh(Batch b, string name, bool withShaderData = true)
        {
            var mesh = new Mesh { name = name };
            if (b.vertices.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(b.vertices);
            mesh.SetNormals(b.normals);
            mesh.SetColors(b.colors);
            if (withShaderData) mesh.SetUVs(0, b.uvs);
            mesh.SetTriangles(b.triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
