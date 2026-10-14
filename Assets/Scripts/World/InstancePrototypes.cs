using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.World
{
    /// <summary>
    /// The few meshes that every tree, bush and street lamp of the world is drawn from (GPU instancing, see
    /// <see cref="InstancedProps"/>). Built in code, deterministically, once per app start – map files carry only
    /// instance records (<see cref="WorldTile.Instance"/>: prototype, position, yaw, scale, colour), no geometry.
    ///
    /// Shapes are the importer's former per-tree geometry in normalised units: a crown of radius 1 (horizontal scale =
    /// measured crown radius), trunk radius/length 1, bush radius/height 1. Vertex colours are built with the
    /// prototype's <see cref="Reference"/> colour; an instance's colour becomes a per-instance tint
    /// (colour / reference, shader property <c>_InstanceTint</c>).
    /// Changing a shape changes how every map looks, but not the map files (old maps keep working).
    /// </summary>
    public static class InstancePrototypes
    {
        public const byte Trunk = 0;
        /// <summary>Broadleaf crowns: main lobe + 3 side lobes (4 seeds), and tall narrow ones with a second lobe on top (2 seeds).</summary>
        public const byte Crown = 1, CrownCount = 4, CrownStacked = 5, CrownStackedCount = 2;
        public const byte Conifer = 7, ConiferCount = 2;
        public const byte Bush = 9, BushCount = 3;
        public const byte Lamp = 12;
        public const int Count = 13;

        /// <summary>Normalised half height of a broadleaf crown prototype (radius 1).</summary>
        public const float CrownHalfHeight = 0.9f;

        // Palette (the importer's WorldStyle refers to these).
        public static readonly Color32 TreeLeaves = new Color32(0x4F, 0x8F, 0x3F, 255);
        public static readonly Color32 LeavesLight = new Color32(0x7D, 0xB0, 0x4E, 255);
        public static readonly Color32 ConiferLeaves = new Color32(0x3F, 0x6E, 0x3C, 255);
        public static readonly Color32 TreeTrunk = new Color32(0x7A, 0x56, 0x38, 255);
        public static readonly Color32 BushColor = new Color32(0x6E, 0x9A, 0x55, 255);
        public static readonly Color32 LampPole = new Color32(0x3E, 0x42, 0x47, 255);
        public static readonly Color32 LampGlow = new Color32(0xFF, 0xF4, 0xD6, 255);
        public static readonly Color32 Needles = Color32.Lerp(TreeLeaves, ConiferLeaves, 0.6f);

        /// <summary>Colour the prototype's vertex colours were built with (an instance in this colour gets tint 1).</summary>
        public static Color32 Reference(int proto) =>
            proto == Trunk ? TreeTrunk
            : proto >= Conifer && proto < Conifer + ConiferCount ? Needles
            : proto >= Bush && proto < Bush + BushCount ? BushColor
            : proto == Lamp ? new Color32(255, 255, 255, 255)
            : TreeLeaves;

        /// <summary>Material index (<see cref="WorldTile.MaterialNames"/>): plants use the tree material, lamps the ground/props one.</summary>
        public static int MaterialOf(int proto) => proto == Lamp ? WorldTile.MaterialGround : WorldTile.MaterialTrees;

        /// <summary>Things that block the player: vertical capsule (radius, height) in prototype units, scaled by the
        /// instance's x/y scale; zero radius = walk through.</summary>
        public static Vector2 Collider(int proto) =>
            proto == Trunk ? new Vector2(1f, 1f) : proto == Lamp ? new Vector2(0.12f, 4.85f) : Vector2.zero;

        /// <summary>Physics layer of the colliders (2 = thin props: block the player, not the camera probe).</summary>
        public const int ColliderLayer = 2;

        static Mesh[] meshes;

        /// <summary>Prototype meshes, built on first use.</summary>
        public static Mesh Get(int proto)
        {
            if (meshes == null) Build();
            return proto >= 0 && proto < meshes.Length ? meshes[proto] : null;
        }

        static Mesh[] farMeshes;

        /// <summary>
        /// Distant stand-in of a prototype (~250 m and beyond, see InstancedProps): crowns and bushes as a 6-sided double
        /// cone (12 tris) / conifers as a cone (6 tris) filling the prototype's bounds in its reference colour; null for
        /// trunks and lamps, which are simply skipped that far away.
        /// </summary>
        public static Mesh GetFar(int proto)
        {
            if (farMeshes == null)
            {
                farMeshes = new Mesh[Count];
                for (int p = 0; p < Count; p++)
                {
                    if (p == Trunk || p == Lamp) continue;
                    var full = Get(p);
                    if (full == null) continue;
                    bool cone = p >= Conifer && p < Conifer + ConiferCount;
                    farMeshes[p] = FarShape(full.bounds, Reference(p), cone);
                }
            }
            return proto >= 0 && proto < farMeshes.Length ? farMeshes[proto] : null;
        }

        static Mesh FarShape(Bounds b, Color32 color, bool cone)
        {
            const int sides = 6;
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var c = new List<Color32>();
            var t = new List<int>();
            Vector3 top = new Vector3(b.center.x, b.max.y, b.center.z);
            Vector3 bottom = new Vector3(b.center.x, b.min.y, b.center.z);
            float ringY = cone ? b.min.y + b.size.y * 0.08f : b.center.y;
            var shade = new Color32((byte)(color.r * 0.8f), (byte)(color.g * 0.8f), (byte)(color.b * 0.8f), color.a);
            void Tri(Vector3 a, Vector3 b1, Vector3 c1, Color32 col)
            {
                var nrm = Vector3.Cross(b1 - a, c1 - a).normalized;
                int i = v.Count;
                v.Add(a); v.Add(b1); v.Add(c1);
                n.Add(nrm); n.Add(nrm); n.Add(nrm);
                c.Add(col); c.Add(col); c.Add(col);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
            }
            for (int k = 0; k < sides; k++)
            {
                float a0 = k * Mathf.PI * 2f / sides, a1 = (k + 1) * Mathf.PI * 2f / sides;
                var p0 = new Vector3(b.center.x + Mathf.Cos(a0) * b.extents.x, ringY, b.center.z + Mathf.Sin(a0) * b.extents.z);
                var p1 = new Vector3(b.center.x + Mathf.Cos(a1) * b.extents.x, ringY, b.center.z + Mathf.Sin(a1) * b.extents.z);
                Tri(top, p1, p0, color);                 // upper half (clockwise seen from outside → faces out)
                if (!cone) Tri(bottom, p0, p1, shade);   // lower half, a little darker like the shaded underside
            }
            var mesh = new Mesh { name = "FarProto" };
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetColors(c);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        public static int Triangles(int proto)
        {
            var m = Get(proto);
            return m != null ? (int)m.GetIndexCount(0) / 3 : 0;
        }

        static void Build()
        {
            meshes = new Mesh[Count];
            meshes[Trunk] = Make("Trunk", b => Frustum(b, Vector3.zero, Vector3.up, 1f, 0.7f, 4, 0f, TreeTrunk));
            for (int i = 0; i < CrownCount; i++)
            {
                int seed = 101 + i;
                meshes[Crown + i] = Make($"Crown{i}", b => BroadleafCrown(b, false, new System.Random(seed)));
            }
            for (int i = 0; i < CrownStackedCount; i++)
            {
                int seed = 201 + i;
                meshes[CrownStacked + i] = Make($"CrownStacked{i}", b => BroadleafCrown(b, true, new System.Random(seed)));
            }
            for (int i = 0; i < ConiferCount; i++)
            {
                int seed = 301 + i;
                meshes[Conifer + i] = Make($"Conifer{i}", b => ConiferCrown(b, new System.Random(seed)));
            }
            for (int i = 0; i < BushCount; i++)
            {
                int seed = 401 + i, extra = i == 1 ? 2 : 1;
                meshes[Bush + i] = Make($"Bush{i}", b => BushShape(b, extra, new System.Random(seed)));
            }
            meshes[Lamp] = Make("Lamp", LampShape);
        }

        // ------------------------------------------------------------------ shapes (former importer geometry)

        /// <summary>Broadleaf crown, radius 1, centre at the origin: a faceted main lobe (+ one on top for tall crowns) and
        /// a ring of three smaller lobes – what PropPlacer built per tree for a crown that is not wider than tall.</summary>
        static void BroadleafCrown(Builder b, bool stacked, System.Random rng)
        {
            const float crownR = 1f, halfH = CrownHalfHeight;
            float mainR = crownR * 0.75f;
            var centre = Vector3.zero;
            var leaves = TreeLeaves;
            Blob(b, centre, mainR, halfH, 0f, leaves, rng);
            if (stacked)
            {
                float topR = mainR * 0.72f, topHalf = halfH * 0.72f;
                Blob(b, centre + new Vector3(((float)rng.NextDouble() - 0.5f) * mainR * 0.3f, halfH * 0.85f, ((float)rng.NextDouble() - 0.5f) * mainR * 0.3f),
                    topR, topHalf, 0.7f, Color32.Lerp(leaves, LeavesLight, 0.25f), rng);
            }
            float maxR = halfH * 1.35f;
            float subR = Mathf.Min(crownR * 0.5f, maxR * 0.8f);
            float d = Mathf.Max(crownR - subR, crownR * 0.35f);
            const int extra = 3;
            for (int i = 0; i < extra; i++)
            {
                float a = (i + 0.3f + (float)rng.NextDouble() * 0.4f) * Mathf.PI * 2f / extra;
                float dd = d * (0.85f + (float)rng.NextDouble() * 0.2f);
                float dy = halfH * (-0.25f + (float)rng.NextDouble() * 0.45f);
                var c = centre + new Vector3(Mathf.Cos(a) * dd, dy, Mathf.Sin(a) * dd);
                float r = subR * (0.85f + (float)rng.NextDouble() * 0.25f);
                var tint = Color32.Lerp(leaves, LeavesLight, 0.1f + (float)rng.NextDouble() * 0.3f);
                Lobe(b, c, r, Mathf.Min(r * 0.85f, halfH * 0.8f), a, Tint(tint, rng, 0.05f));
            }
        }

        /// <summary>Spruce/pine crown: three stacked cones, radius 1, height 1, base at the origin.</summary>
        static void ConiferCrown(Builder b, System.Random rng)
        {
            float y = 0f;
            for (int tier = 0; tier < 3; tier++)
            {
                float r = 1f - tier * 0.27f;
                float h = tier == 2 ? 0.5f : 0.42f;
                float y0 = y, y1 = Mathf.Min(1f, y + h);
                Cone(b, Vector3.up * y0, r, y1 - y0, 6, tier * 0.4f, Tint(Needles, rng, 0.06f), Tint(Needles, rng, 0.04f));
                y += 0.29f;
            }
        }

        /// <summary>Bush of radius 1 and height 1 on the ground: two or three overlapping lobes.</summary>
        static void BushShape(Builder b, int extra, System.Random rng)
        {
            const float r = 1f, h = 1f;
            var color = BushColor;
            Blob(b, Vector3.up * h * 0.45f, r, h * 0.55f, 0f, color, rng);
            for (int i = 0; i < extra; i++)
            {
                float a = 1.3f + i * 2.4f;
                float rr = r * (0.55f + (float)rng.NextDouble() * 0.2f);
                var c = new Vector3(Mathf.Cos(a) * r * 0.6f, rr * 0.5f, Mathf.Sin(a) * r * 0.6f);
                Lobe(b, c, rr, rr * 0.6f, a, Tint(Color32.Lerp(color, LeavesLight, 0.2f), rng, 0.05f));
            }
        }

        /// <summary>Street lamp: 4.6 m pole, head pointing along +X (yaw turns it towards the road).</summary>
        static void LampShape(Builder b)
        {
            Prism(b, Vector3.zero, 0.09f, 0f, 4.6f, 6, 0f, LampPole);
            var c = Vector3.up * 4.6f;
            const float s = 0.28f;
            Vector3 x = Vector3.right * s, z = Vector3.forward * s;
            Vector3 lo = Vector3.up * -0.05f, hi = Vector3.up * 0.25f;
            Vector3 p00 = c - x - z, p10 = c + x - z, p11 = c + x + z, p01 = c - x + z;
            b.Quad(p00 + hi, p01 + hi, p11 + hi, p10 + hi, LampPole);   // top
            b.Quad(p00 + lo, p10 + lo, p11 + lo, p01 + lo, LampGlow);   // bottom
            var ring = new[] { p00, p10, p11, p01 };
            for (int i = 0; i < 4; i++)
            {
                Vector3 a = ring[i], d = ring[(i + 1) % 4];
                b.Quad(a + lo, a + hi, d + hi, d + lo, LampPole);
                b.Quad(d + lo, d + hi, a + hi, a + lo, LampPole); // double-sided
            }
        }

        // ------------------------------------------------------------------ primitives

        /// <summary>Faceted lobe: pentagonal top cap, twisted lower ring, flat-ish bottom – 20 triangles.</summary>
        static void Blob(Builder b, Vector3 c, float r, float halfH, float rot, Color32 color, System.Random rng)
        {
            const int n = 5;
            var top = c + Vector3.up * halfH;
            var bottom = c + Vector3.down * halfH * 0.75f;
            var up = new Vector3[n];
            var lo = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = rot + i * Mathf.PI * 2f / n;
                float ra = r * (0.72f + (float)rng.NextDouble() * 0.1f);
                float rb = r * (0.92f + (float)rng.NextDouble() * 0.12f);
                up[i] = c + new Vector3(Mathf.Cos(a) * ra, halfH * 0.5f, Mathf.Sin(a) * ra);
                float b2 = a + Mathf.PI / n;
                lo[i] = c + new Vector3(Mathf.Cos(b2) * rb, -halfH * 0.2f, Mathf.Sin(b2) * rb);
            }
            var light = Color32.Lerp(color, LeavesLight, 0.18f);
            var dark = Shade(color, 0.82f);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                b.Tri(up[i], top, up[j], light);
                b.Tri(up[i], up[j], lo[i], color);
                b.Tri(lo[i], up[j], lo[j], Tint(color, rng, 0.04f));
                b.Tri(lo[j], bottom, lo[i], dark);
            }
        }

        /// <summary>Simple lobe: pentagonal ring with a low cap and a flat-ish bottom – 20 triangles.</summary>
        static void Lobe(Builder b, Vector3 c, float r, float halfH, float rot, Color32 color)
        {
            const int n = 5;
            var top = c + new Vector3(Mathf.Cos(rot) * r * 0.12f, halfH, Mathf.Sin(rot) * r * 0.12f);
            var bottom = c + Vector3.down * halfH * 0.55f;
            var light = Color32.Lerp(color, LeavesLight, 0.15f);
            var dark = Shade(color, 0.8f);
            var cap = new Vector3[n];
            var ring = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = rot + i * Mathf.PI * 2f / n;
                cap[i] = c + new Vector3(Mathf.Cos(a + 0.6f) * r * 0.6f, halfH * 0.6f, Mathf.Sin(a + 0.6f) * r * 0.6f);
                ring[i] = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                b.Tri(cap[i], top, cap[j], light);
                b.Tri(ring[j], cap[i], cap[j], color);
                b.Tri(ring[j], bottom, ring[i], dark);
                b.Tri(ring[i], cap[i], ring[j], color);
            }
        }

        static void Cone(Builder b, Vector3 baseC, float r, float h, int sides, float rot, Color32 side, Color32 under)
        {
            var tip = baseC + Vector3.up * h;
            var hollow = baseC + Vector3.up * h * 0.18f;
            for (int i = 0; i < sides; i++)
            {
                float a0 = rot + i * Mathf.PI * 2f / sides, a1 = rot + (i + 1) * Mathf.PI * 2f / sides;
                var r0 = baseC + new Vector3(Mathf.Cos(a0) * r, 0f, Mathf.Sin(a0) * r);
                var r1 = baseC + new Vector3(Mathf.Cos(a1) * r, 0f, Mathf.Sin(a1) * r);
                b.Tri(r0, tip, r1, side);
                b.Tri(r1, hollow, r0, Shade(under, 0.75f));
            }
        }

        static void Frustum(Builder b, Vector3 bottom, Vector3 top, float r0, float r1, int sides, float rot, Color32 c)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = rot + i * Mathf.PI * 2f / sides, a1 = rot + (i + 1) * Mathf.PI * 2f / sides;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                b.Quad(bottom + d0 * r0, top + d0 * r1, top + d1 * r1, bottom + d1 * r0, c);
            }
        }

        static void Prism(Builder b, Vector3 basePos, float r, float y0, float y1, int sides, float rot, Color32 c)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = rot + i * Mathf.PI * 2f / sides, a1 = rot + (i + 1) * Mathf.PI * 2f / sides;
                var p0 = basePos + new Vector3(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r);
                var p1 = basePos + new Vector3(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r);
                b.Quad(p0 + Vector3.up * y0, p0 + Vector3.up * y1, p1 + Vector3.up * y1, p1 + Vector3.up * y0, c);
            }
        }

        public static Color32 Tint(Color32 c, System.Random rng, float amount)
        {
            float f = 1f + ((float)rng.NextDouble() * 2f - 1f) * amount;
            return new Color32((byte)Mathf.Clamp(c.r * f, 0, 255), (byte)Mathf.Clamp(c.g * f, 0, 255), (byte)Mathf.Clamp(c.b * f, 0, 255), 255);
        }

        static Color32 Shade(Color32 c, float f) => new Color32((byte)(c.r * f), (byte)(c.g * f), (byte)(c.b * f), c.a);

        /// <summary>Flat-shaded triangle soup (same winding/normals as the importer's MeshSink).</summary>
        class Builder
        {
            public readonly List<Vector3> v = new List<Vector3>();
            public readonly List<Vector3> n = new List<Vector3>();
            public readonly List<Color32> c = new List<Color32>();
            public readonly List<int> t = new List<int>();

            public void Tri(Vector3 a, Vector3 b1, Vector3 b2, Color32 color)
            {
                var nn = Vector3.Cross(b1 - a, b2 - a).normalized;
                int i = v.Count;
                v.Add(a); v.Add(b1); v.Add(b2);
                n.Add(nn); n.Add(nn); n.Add(nn);
                c.Add(color); c.Add(color); c.Add(color);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
            }

            public void Quad(Vector3 a, Vector3 bb, Vector3 cc, Vector3 d, Color32 color)
            {
                var nn = Vector3.Cross(bb - a, cc - a).normalized;
                int i = v.Count;
                v.Add(a); v.Add(bb); v.Add(cc); v.Add(d);
                for (int k = 0; k < 4; k++) { n.Add(nn); c.Add(color); }
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
                t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }
        }

        static Mesh Make(string name, System.Action<Builder> build)
        {
            var b = new Builder();
            build(b);
            var mesh = new Mesh { name = "Proto_" + name, hideFlags = HideFlags.DontSave };
            mesh.SetVertices(b.v);
            mesh.SetNormals(b.n);
            mesh.SetColors(b.c);
            mesh.SetTriangles(b.t, 0);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false); // keep the CPU copy: editor previews bake instances into meshes
            return mesh;
        }

        // ------------------------------------------------------------------ instance helpers

        /// <summary>Object-to-world matrix of an instance (yaw clockwise from north, like Quaternion.Euler(0, yaw, 0)).</summary>
        public static Matrix4x4 Matrix(in WorldTile.Instance i) =>
            Matrix4x4.TRS(i.position, Quaternion.Euler(0f, i.yaw, 0f), i.scale);

        /// <summary>Per-instance tint (multiplies the prototype's vertex colours).</summary>
        public static Vector4 TintOf(in WorldTile.Instance i)
        {
            var r = Reference(i.proto);
            return new Vector4(i.color.r / (float)Mathf.Max(1, (int)r.r), i.color.g / (float)Mathf.Max(1, (int)r.g), i.color.b / (float)Mathf.Max(1, (int)r.b), 1f);
        }
    }
}
