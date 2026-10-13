using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Ground outside the <see cref="AreaMask"/> (fields, meadows, forests between the villages). The laser terrain is far
    /// too rough there for the planar merging of <see cref="GroundBuilder.BuildBase"/> (furrows, ditches: ~20 M triangles
    /// for a municipality), so before anything is built the height field itself is simplified outside the detailed
    /// 40 m blocks: heights are smoothed onto a 40 m grid, nearly planar neighbours are merged up to 160 m, and every
    /// sample inside such a block is replaced by its two triangles (same diagonal as the terrain cells). The field and
    /// the coarse mesh are thus identical – draped water/railways and the detailed blocks next to it fit without cracks.
    /// Colours: a class field on a 20 m node grid (two 3×3 majority passes) – meadow / field from the smoothed aerial
    /// image (<see cref="AerialImage.FieldScore"/>, the same map the detailed open-country ground uses, so field shapes run
    /// on across the mask edge), forest and lakes from the mask. Blocks whose nodes all share one class stay whole (vertex
    /// colours carry the measured tone of each field); where classes change, the 20 m triangles are cut along the
    /// interpolated class border (marching triangles) – smooth field outlines instead of 10–16 m stair steps.
    /// Forests near the built corridors get instanced trees (own random generator, not editable objects).
    /// Call <see cref="Build"/> before GroundBuilder.BuildBase, which skips the blocks built here.
    /// </summary>
    public static class CoarseTerrain
    {
        const int Fine = 8;               // cells per 40 m block = GroundBuilder.MaxBlock
        const int MaxUnits = 4;           // largest merged block in 40 m units (160 m)
        const float Tolerance = 0.15f;    // m: merged blocks stay this close to the smoothed 40 m grid
        const int Smooth = 4;             // samples (±20 m) averaged for a 40 m grid point
        const int Node = 4;               // cells between colour nodes (20 m; 10 m doubles the triangles along borders)
        const byte Grass = 0, Field = 1, Forest = 2, Lake = 3;
        const float TreeReach = 100f;     // m from the detailed ground: forests get trees (visible from the roads)

        static byte[] nodeClass;          // per colour node
        static bool[] fine;               // per 40 m block: detailed (GroundBuilder)
        static int ncx, ncz, nbx, nbz, nnx, nnz; // cells, 40 m blocks, colour nodes
        static float startX, startZ;
        static Color32 baseColor;

        public static int Blocks { get; private set; }
        public static int Triangles { get; private set; }
        public static int Trees { get; private set; }

        public static void Build(MeshSink sink, AreaRect area, Color32 color, TerrainField t)
        {
            Blocks = Triangles = Trees = 0;
            var mask = AreaMask.Current;
            if (mask == null) return;
            baseColor = color;
            float cs = TerrainField.CellSize;
            var g = area.Expand(60f); // same cell grid as GroundBuilder.BuildBase
            startX = Mathf.Floor(g.xMin / cs) * cs; startZ = Mathf.Floor(g.zMin / cs) * cs;
            ncx = Mathf.CeilToInt((g.xMax - startX) / cs); ncz = Mathf.CeilToInt((g.zMax - startZ) / cs);
            nbx = (ncx + Fine - 1) / Fine; nbz = (ncz + Fine - 1) / Fine;

            fine = new bool[nbx * nbz];
            for (int bz = 0; bz < nbz; bz++)
            for (int bx = 0; bx < nbx; bx++)
                fine[bz * nbx + bx] = AreaMask.FineTerrain(startX + bx * Fine * cs, startZ + bz * Fine * cs, Fine * cs);

            nnx = ncx / Node + 1; nnz = ncz / Node + 1;
            var raw = new byte[nnx * nnz];
            var s = new float[4];
            for (int k = 0; k < nnz; k++)
            for (int i = 0; i < nnx; i++)
            {
                int bx = Mathf.Min(i * Node / Fine, nbx - 1), bz = Mathf.Min(k * Node / Fine, nbz - 1);
                if (fine[bz * nbx + bx] && (i * Node) % Fine != 0 && (k * Node) % Fine != 0) { raw[k * nnx + i] = 255; continue; } // inside a detailed block
                raw[k * nnx + i] = Classify(startX + i * Node * cs, startZ + k * Node * cs, s);
            }
            // 3×3 majority, twice: no single-node specks (each costs ~10 triangles), straighter borders.
            nodeClass = new byte[nnx * nnz];
            var votes = new int[4];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int k = 0; k < nnz; k++)
                for (int i = 0; i < nnx; i++)
                {
                    byte own = raw[k * nnx + i];
                    if (own == 255) { nodeClass[k * nnx + i] = 255; continue; }
                    System.Array.Clear(votes, 0, 4);
                    for (int b = Mathf.Max(0, k - 1); b <= Mathf.Min(nnz - 1, k + 1); b++)
                    for (int a = Mathf.Max(0, i - 1); a <= Mathf.Min(nnx - 1, i + 1); a++)
                        if (raw[b * nnx + a] != 255) votes[raw[b * nnx + a]]++;
                    byte best = own;
                    for (byte c = 0; c < 4; c++) if (votes[c] > votes[best]) best = c;
                    nodeClass[k * nnx + i] = votes[best] >= 5 ? best : own;
                }
                (raw, nodeClass) = (nodeClass, raw);
            }
            nodeClass = raw;
            for (int j = 0; j < nodeClass.Length; j++) if (nodeClass[j] == 255) nodeClass[j] = Grass; // never read

            // Simplify the field: only with a 5 m grid on the cell corners (fetch_dgm "dgm_step": 5), else emit cells as they are.
            var smp = t.Samples;
            int ox = smp == null ? 0 : Mathf.RoundToInt((startX - t.XMin) / cs), oz = smp == null ? 0 : Mathf.RoundToInt((startZ - t.ZMin) / cs);
            bool editable = smp != null && Mathf.Approximately(t.Step, cs) && ox >= 0 && oz >= 0 &&
                            Mathf.Abs(t.XMin + ox * cs - startX) < 0.01f && Mathf.Abs(t.ZMin + oz * cs - startZ) < 0.01f;

            var blocks = new List<(int bx, int bz, int n)>();
            var partial = new List<(int bx, int bz)>();
            if (editable || t.IsFlat)
            {
                float[] grid = editable ? SmoothedGrid(t, ox, oz) : new float[(nbx + 1) * (nbz + 1)];
                for (int bz = 0; bz < nbz; bz += MaxUnits)
                for (int bx = 0; bx < nbx; bx += MaxUnits)
                    Decide(grid, t, ox, oz, editable, bx, bz, MaxUnits, blocks, partial);
                if (editable)
                {
                    // Largest first: smaller blocks then read corners that lie on a bigger neighbour's edge from it.
                    blocks.Sort((a, b) => b.n.CompareTo(a.n));
                    foreach (var (bx, bz, n) in blocks) Flatten(t, ox + bx * Fine, oz + bz * Fine, n * Fine);
                }
            }
            else
            {
                for (int bz = 0; bz < nbz; bz++)
                for (int bx = 0; bx < nbx; bx++)
                    if (!fine[bz * nbx + bx]) partial.Add((bx, bz));
            }

            foreach (var (bx, bz, n) in blocks) Emit(sink, t, bx * Fine, bz * Fine, n * Fine);
            foreach (var (bx, bz) in partial)
                for (int iz = bz * Fine; iz < Mathf.Min((bz + 1) * Fine, ncz); iz++)
                for (int ix = bx * Fine; ix < Mathf.Min((bx + 1) * Fine, ncx); ix++)
                    Emit(sink, t, ix, iz, 1);
            Blocks = blocks.Count + partial.Count;
            ForestTrees(sink);
            nodeClass = null;
            fine = null;
        }

        // ------------------------------------------------------------------ classes

        /// <summary>Class scores at a point (grass, field, forest, lake) and the winning class.</summary>
        static byte Classify(float x, float z, float[] s)
        {
            var mask = AreaMask.Current;
            // Forest / lake share of a 3×3 neighbourhood of the 10 m mask: diagonal, not stepped, borders.
            int forest = 0, lake = 0;
            for (int a = -1; a <= 1; a++)
            for (int b = -1; b <= 1; b++)
            {
                float px = x + a * 8f, pz = z + b * 8f;
                if (mask.IsForest(px, pz)) forest++;
                else if (mask.IsLake(px, pz)) lake++;
            }
            float f = forest / 9f, l = lake / 9f, open = Mathf.Max(0f, 1f - f - l);
            var aerial = AerialImage.Current;
            float bare = aerial != null ? aerial.FieldScore(x, z) : 0f;
            s[Grass] = open * (1f - bare); s[Field] = open * bare; s[Forest] = f; s[Lake] = l;
            byte best = 0;
            for (byte c = 1; c < 4; c++) if (s[c] > s[best]) best = c;
            return best;
        }

        /// <summary>All colour nodes of an even-aligned square (incl. its border) share one class.</summary>
        static bool NodesUniform(int ix, int iz, int n, out byte c)
        {
            int i0 = ix / Node, k0 = iz / Node, i1 = Mathf.Min((ix + n) / Node, nnx - 1), k1 = Mathf.Min((iz + n) / Node, nnz - 1);
            c = nodeClass[k0 * nnx + i0];
            for (int k = k0; k <= k1; k++)
            for (int i = i0; i <= i1; i++)
                if (nodeClass[k * nnx + i] != c) return false;
            return true;
        }

        static Color32 ClassColour(byte c, Vector3 p)
        {
            Color32 col;
            var aerial = AerialImage.Current;
            if (c == Forest) col = WorldStyle.Forest;
            else if (c == Lake) col = WorldStyle.Water;
            else if (aerial != null) col = aerial.ColorAt(c == Field ? AerialImage.Cover.Field : AerialImage.Cover.Grass, p.x, p.z, baseColor);
            else col = c == Field ? AerialImage.FieldColor : baseColor;
            return Shade(col, p);
        }

        // ------------------------------------------------------------------ heights

        /// <summary>Heights on the 40 m grid: mean of ±20 m (corners of detailed blocks keep their exact height).</summary>
        static float[] SmoothedGrid(TerrainField t, int ox, int oz)
        {
            var s = t.Samples;
            int snx = t.Nx, snz = t.Nz;
            var grid = new float[(nbx + 1) * (nbz + 1)];
            for (int gz = 0; gz <= nbz; gz++)
            for (int gx = 0; gx <= nbx; gx++)
            {
                int ci = Mathf.Min(ox + gx * Fine, snx - 1), ck = Mathf.Min(oz + gz * Fine, snz - 1);
                if (FineCorner(gx, gz)) { grid[gz * (nbx + 1) + gx] = s[ck * snx + ci]; continue; }
                float sum = 0f; int cnt = 0;
                for (int k = Mathf.Max(0, ck - Smooth); k <= Mathf.Min(snz - 1, ck + Smooth); k++)
                for (int i = Mathf.Max(0, ci - Smooth); i <= Mathf.Min(snx - 1, ci + Smooth); i++) { sum += s[k * snx + i]; cnt++; }
                grid[gz * (nbx + 1) + gx] = sum / cnt;
            }
            return grid;
        }

        static bool FineCorner(int gx, int gz)
        {
            for (int dz = -1; dz <= 0; dz++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int bx = gx + dx, bz = gz + dz;
                if (bx >= 0 && bz >= 0 && bx < nbx && bz < nbz && fine[bz * nbx + bx]) return true;
            }
            return false;
        }

        /// <summary>Full 40 m block inside the height grid (edge blocks of the margin are emitted cell by cell).</summary>
        static bool Whole(TerrainField t, int ox, int oz, bool editable, int bx, int bz) =>
            (bx + 1) * Fine <= ncx && (bz + 1) * Fine <= ncz &&
            (!editable || ox + (bx + 1) * Fine < t.Nx && oz + (bz + 1) * Fine < t.Nz);

        static void Decide(float[] grid, TerrainField t, int ox, int oz, bool editable, int bx, int bz, int n,
            List<(int, int, int)> blocks, List<(int, int)> partial)
        {
            if (bx >= nbx || bz >= nbz) return;
            if (n == 1)
            {
                if (fine[bz * nbx + bx]) return;
                if (Whole(t, ox, oz, editable, bx, bz))
                {
                    blocks.Add((bx, bz, 1));
                    if (editable) // the block's corners: smoothed grid
                        foreach (var (gx, gz) in new[] { (bx, bz), (bx + 1, bz), (bx, bz + 1), (bx + 1, bz + 1) })
                            t.Samples[(oz + gz * Fine) * t.Nx + ox + gx * Fine] = grid[gz * (nbx + 1) + gx];
                }
                else partial.Add((bx, bz));
                return;
            }
            bool whole = bx + n <= nbx && bz + n <= nbz;
            for (int i = bx; whole && i < bx + n; i++)
            for (int k = bz; whole && k < bz + n; k++)
                whole = !fine[k * nbx + i] && Whole(t, ox, oz, editable, i, k);
            if (whole && Planar(grid, bx, bz, n)) // colour borders: Emit splits further
            {
                blocks.Add((bx, bz, n));
                if (editable)
                    foreach (var (gx, gz) in new[] { (bx, bz), (bx + n, bz), (bx, bz + n), (bx + n, bz + n) })
                        t.Samples[(oz + gz * Fine) * t.Nx + ox + gx * Fine] = grid[gz * (nbx + 1) + gx];
                return;
            }
            int h = n / 2;
            Decide(grid, t, ox, oz, editable, bx, bz, h, blocks, partial);
            Decide(grid, t, ox, oz, editable, bx + h, bz, h, blocks, partial);
            Decide(grid, t, ox, oz, editable, bx, bz + h, h, blocks, partial);
            Decide(grid, t, ox, oz, editable, bx + h, bz + h, h, blocks, partial);
        }

        static bool Planar(float[] grid, int bx, int bz, int n)
        {
            int w = nbx + 1;
            float h00 = grid[bz * w + bx], h10 = grid[bz * w + bx + n], h01 = grid[(bz + n) * w + bx];
            float dx = (h10 - h00) / n, dz = (h01 - h00) / n;
            for (int i = 0; i <= n; i++)
            for (int k = 0; k <= n; k++)
                if (Mathf.Abs(grid[(bz + k) * w + bx + i] - (h00 + dx * i + dz * k)) > Tolerance) return false;
            return true;
        }

        /// <summary>Replaces the samples of a block (incl. its border) by its two triangles (split x0z0–x1z1).</summary>
        static void Flatten(TerrainField t, int i0, int k0, int n)
        {
            var s = t.Samples;
            int w = t.Nx;
            float c00 = s[k0 * w + i0], c10 = s[k0 * w + i0 + n], c01 = s[(k0 + n) * w + i0], c11 = s[(k0 + n) * w + i0 + n];
            for (int k = 0; k <= n; k++)
            for (int i = 0; i <= n; i++)
            {
                float u = (float)i / n, v = (float)k / n;
                s[(k0 + k) * w + i0 + i] = u >= v ? c00 + (c10 - c00) * u + (c11 - c10) * v : c00 + (c11 - c01) * u + (c01 - c00) * v;
            }
        }

        // ------------------------------------------------------------------ mesh

        /// <summary>
        /// Two triangles per block of one class; mixed blocks are split down to the 20 m node squares, whose triangles
        /// are cut along the class borders.
        /// </summary>
        static void Emit(MeshSink sink, TerrainField t, int ix, int iz, int n)
        {
            bool aligned = n >= Node && ix % Node == 0 && iz % Node == 0;
            byte c = 0;
            bool uniform = aligned && NodesUniform(ix, iz, n, out c);
            if (aligned && !uniform && n > Node)
            {
                int h = n / 2;
                Emit(sink, t, ix, iz, h);
                Emit(sink, t, ix + h, iz, h);
                Emit(sink, t, ix, iz + h, h);
                Emit(sink, t, ix + h, iz + h, h);
                return;
            }
            float cs = TerrainField.CellSize;
            float x0 = startX + ix * cs, z0 = startZ + iz * cs, x1 = x0 + n * cs, z1 = z0 + n * cs;
            Vector3 p00 = new Vector3(x0, t.Raw(x0, z0), z0), p10 = new Vector3(x1, t.Raw(x1, z0), z0);
            Vector3 p01 = new Vector3(x0, t.Raw(x0, z1), z1), p11 = new Vector3(x1, t.Raw(x1, z1), z1);
            var batch = sink.Get(WorldStyle.MatTerrain, new Vector2(x0, z0));
            if (uniform)
            {
                Tri3(batch, p00, p01, p11, ClassColour(c, p00), ClassColour(c, p01), ClassColour(c, p11));
                Tri3(batch, p00, p11, p10, ClassColour(c, p00), ClassColour(c, p11), ClassColour(c, p10));
                Triangles += 2;
                return;
            }
            March(batch, p00, p01, p11);
            March(batch, p00, p11, p10);
        }

        /// <summary>
        /// One planar triangle (clockwise from above) cut into class regions: the border crosses each edge where the
        /// two end classes' scores are equal (depends on the edge only, so neighbours agree); three classes meet in the centroid.
        /// </summary>
        static void March(MeshSink.Batch batch, Vector3 a, Vector3 b, Vector3 c)
        {
            float[] sa = new float[4], sb = new float[4], sc = new float[4];
            byte ka = NodeAt(a, sa), kb = NodeAt(b, sb), kc = NodeAt(c, sc);
            if (ka == kb && kb == kc)
            {
                Poly(batch, ka, a, b, c);
                return;
            }
            if (ka != kb && kb != kc && ka != kc)
            {
                Vector3 ab = Cut(a, b, sa, sb, ka, kb), bc = Cut(b, c, sb, sc, kb, kc), ca = Cut(c, a, sc, sa, kc, ka);
                var g = (a + b + c) / 3f;
                Poly(batch, ka, a, ab, g, ca);
                Poly(batch, kb, b, bc, g, ab);
                Poly(batch, kc, c, ca, g, bc);
                return;
            }
            // Two corners agree: rotate so that the odd one is first (keeps the winding).
            if (kb != ka && kb != kc) { (a, b, c) = (b, c, a); (sa, sb, sc) = (sb, sc, sa); (ka, kb, kc) = (kb, kc, ka); }
            else if (kc != ka && kc != kb) { (a, b, c) = (c, a, b); (sa, sb, sc) = (sc, sa, sb); (ka, kb, kc) = (kc, ka, kb); }
            Vector3 cab = Cut(a, b, sa, sb, ka, kb), cca = Cut(c, a, sc, sa, kc, ka);
            Poly(batch, ka, a, cab, cca);
            Poly(batch, kb, cab, b, c, cca);
        }

        /// <summary>Class of the colour node at a triangle corner (filtered) and its raw scores (for the cut position).</summary>
        static byte NodeAt(Vector3 p, float[] s)
        {
            byte raw = Classify(p.x, p.z, s);
            float size = Node * TerrainField.CellSize;
            int i = Mathf.RoundToInt((p.x - startX) / size), k = Mathf.RoundToInt((p.z - startZ) / size);
            bool onNode = Mathf.Abs(startX + i * size - p.x) < 0.01f && Mathf.Abs(startZ + k * size - p.z) < 0.01f;
            return onNode && i >= 0 && k >= 0 && i < nnx && k < nnz ? nodeClass[k * nnx + i] : raw; // margin cells: raw
        }

        static Vector3 Cut(Vector3 p, Vector3 q, float[] sp, float[] sq, byte kp, byte kq)
        {
            float dp = sp[kp] - sp[kq], dq = sq[kp] - sq[kq]; // ≥ 0 at p, ≤ 0 at q
            float t = dp > 0f && dq < 0f ? dp / (dp - dq) : 0.5f; // filtered node against its scores: midway
            return Vector3.LerpUnclamped(p, q, Mathf.Clamp(t, 0.08f, 0.92f));
        }

        /// <summary>Convex polygon (3–4 corners, clockwise from above) in one class, fanned from the first corner.</summary>
        static void Poly(MeshSink.Batch batch, byte k, params Vector3[] v)
        {
            var col = new Color32[v.Length];
            for (int i = 0; i < v.Length; i++) col[i] = ClassColour(k, v[i]);
            for (int i = 1; i + 1 < v.Length; i++)
            {
                Tri3(batch, v[0], v[i], v[i + 1], col[0], col[i], col[i + 1]);
                Triangles++;
            }
        }

        // ------------------------------------------------------------------ forest edges

        /// <summary>
        /// Sparse instanced trees in forests within <see cref="TreeReach"/> of the detailed ground: dense along the forest
        /// edge (what one sees from the road), thinner inside, fading out with distance. Own generator – the shared
        /// sequence and all object ids stay unchanged; the trees are not editable objects.
        /// </summary>
        static void ForestTrees(MeshSink sink)
        {
            var mask = AreaMask.Current;
            float cs = TerrainField.CellSize, size = Node * cs;
            // Distance (m) to the nearest detailed block, chamfer over the 40 m blocks.
            var dist = new float[nbx * nbz];
            for (int j = 0; j < dist.Length; j++) dist[j] = fine[j] ? 0f : 1e9f;
            float bs = Fine * cs, diag = bs * 1.41421f;
            for (int pass = 0; pass < 2; pass++)
            for (int bz0 = 0; bz0 < nbz; bz0++)
            for (int bx0 = 0; bx0 < nbx; bx0++)
            {
                int bx = pass == 0 ? bx0 : nbx - 1 - bx0, bz = pass == 0 ? bz0 : nbz - 1 - bz0, d = pass == 0 ? -1 : 1;
                float best = dist[bz * nbx + bx];
                void Try(int x, int z, float add) { if (x >= 0 && z >= 0 && x < nbx && z < nbz) best = Mathf.Min(best, dist[z * nbx + x] + add); }
                Try(bx + d, bz, bs); Try(bx, bz + d, bs); Try(bx + d, bz + d, diag); Try(bx - d, bz + d, diag);
                dist[bz * nbx + bx] = best;
            }

            var rng = new System.Random(0x0F0E57);
            for (int k = 0; k < nnz - 1; k++)
            for (int i = 0; i < nnx - 1; i++)
            {
                int bx = i * Node / Fine, bz = k * Node / Fine;
                if (bx >= nbx || bz >= nbz || fine[bz * nbx + bx]) continue;
                float reach = dist[bz * nbx + bx] - bs * 0.5f; // block centre → roughly the cell
                if (reach > TreeReach) continue;
                float x0 = startX + i * size, z0 = startZ + k * size;
                if (!mask.IsForest(x0 + size * 0.5f, z0 + size * 0.5f)) continue;
                // Forest edge: a non-forest pixel within ~15 m.
                bool edge = false;
                for (int a = -1; a <= 1 && !edge; a++)
                for (int b = -1; b <= 1 && !edge; b++)
                    if ((a != 0 || b != 0) && !mask.IsForest(x0 + size * 0.5f + a * 15f, z0 + size * 0.5f + b * 15f)) edge = true;
                // Per 20 m cell: a closed edge (~1 tree per 70 m²), a looser stand behind it, fading out with distance.
                float expected = (edge ? 6f : 3.5f) * Mathf.Clamp01(1.2f - reach / TreeReach);
                int count = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
                for (int n = 0; n < count; n++)
                {
                    var p = new Vector2(x0 + (float)rng.NextDouble() * size, z0 + (float)rng.NextDouble() * size);
                    if (!mask.IsForest(p.x, p.y) || mask.Inside(p.x, p.y)) continue;
                    float height = 13f + (float)rng.NextDouble() * 10f;
                    bool conifer = rng.NextDouble() < 0.35;
                    float crown = (conifer ? 3.2f : 4.4f) + (float)rng.NextDouble() * 2f; // broad: a closed edge, not a plantation
                    var leaves = WorldStyle.Tint(WorldStyle.TreeLeaves, rng, 0.14f);
                    var local = new System.Random(rng.Next());
                    PropPlacer.TreeMesh(sink, p, height, crown, conifer ? 0.3f : 0.4f, conifer, (float)rng.NextDouble() * Mathf.PI * 2f, leaves, local);
                    Trees++;
                }
            }
        }

        /// <summary>All terrain triangles in the sink (detailed + coarse), for the import log.</summary>
        public static int TerrainTriangles(MeshSink sink)
        {
            int n = 0;
            foreach (var kv in sink.Batches) if (kv.Key.mat == WorldStyle.MatTerrain) n += kv.Value.triangles.Count / 3;
            return n;
        }

        /// <summary>Same soft colour variation as the detailed ground (GroundBuilder.Shade with Perlin noise).</summary>
        static Color32 Shade(Color32 color, Vector3 p)
        {
            float noise = Mathf.PerlinNoise(p.x * 0.012f + 31.7f, p.z * 0.012f + 11.3f) - 0.5f;
            float f = 1f + noise * 0.14f;
            return new Color32((byte)Mathf.Clamp(color.r * f, 0, 255), (byte)Mathf.Clamp(color.g * (f + noise * 0.04f), 0, 255),
                (byte)Mathf.Clamp(color.b * f, 0, 255), 255);
        }

        static void Tri3(MeshSink.Batch b, Vector3 a, Vector3 c1, Vector3 c2, Color32 ca, Color32 cb, Color32 cc)
        {
            var nrm = Vector3.Cross(c1 - a, c2 - a).normalized;
            int i = b.vertices.Count;
            b.vertices.Add(a); b.vertices.Add(c1); b.vertices.Add(c2);
            b.normals.Add(nrm); b.normals.Add(nrm); b.normals.Add(nrm);
            b.colors.Add(ca); b.colors.Add(cb); b.colors.Add(cc);
            b.uvs.Add(MeshSink.NoFacade); b.uvs.Add(MeshSink.NoFacade); b.uvs.Add(MeshSink.NoFacade);
            b.triangles.Add(i); b.triangles.Add(i + 1); b.triangles.Add(i + 2);
        }
    }
}
