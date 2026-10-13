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
    /// Colours: majority ground cover of the aerial image (blocks split down to 10 m along field borders), forest from
    /// the mask. Call <see cref="Build"/> before GroundBuilder.BuildBase, which skips the blocks built here.
    /// </summary>
    public static class CoarseTerrain
    {
        const int Fine = 8;               // cells per 40 m block = GroundBuilder.MaxBlock
        const int MaxUnits = 4;           // largest merged block in 40 m units (160 m)
        const float Tolerance = 0.15f;    // m: merged blocks stay this close to the smoothed 40 m grid
        const int Smooth = 4;             // samples (±20 m) averaged for a 40 m grid point
        const float Majority = 0.9f;      // share of cell halves with one cover for a block to stay whole
        const int MinCover = 2;           // cells: smallest block for cover borders (10 m)
        const byte Forest = 5;            // cover code next to AerialImage.Cover

        static byte[] cover;              // per cell: lower half | upper half << 4
        static bool[] fine;               // per 40 m block: detailed (GroundBuilder)
        static int ncx, ncz, nbx, nbz;    // cells, 40 m blocks
        static float startX, startZ;

        public static int Blocks { get; private set; }
        public static int Triangles { get; private set; }

        public static void Build(MeshSink sink, AreaRect area, Color32 color, TerrainField t)
        {
            Blocks = Triangles = 0;
            var mask = AreaMask.Current;
            if (mask == null) return;
            float cs = TerrainField.CellSize;
            var g = area.Expand(60f); // same cell grid as GroundBuilder.BuildBase
            startX = Mathf.Floor(g.xMin / cs) * cs; startZ = Mathf.Floor(g.zMin / cs) * cs;
            ncx = Mathf.CeilToInt((g.xMax - startX) / cs); ncz = Mathf.CeilToInt((g.zMax - startZ) / cs);
            nbx = (ncx + Fine - 1) / Fine; nbz = (ncz + Fine - 1) / Fine;

            fine = new bool[nbx * nbz];
            for (int bz = 0; bz < nbz; bz++)
            for (int bx = 0; bx < nbx; bx++)
                fine[bz * nbx + bx] = AreaMask.FineTerrain(startX + bx * Fine * cs, startZ + bz * Fine * cs, Fine * cs);

            var aerial = AerialImage.Current;
            cover = new byte[ncx * ncz];
            for (int iz = 0; iz < ncz; iz++)
            for (int ix = 0; ix < ncx; ix++)
            {
                if (fine[iz / Fine * nbx + ix / Fine]) continue;
                float x0 = startX + ix * cs, z0 = startZ + iz * cs;
                byte lo, up;
                if (mask.IsForest(x0 + cs * 0.5f, z0 + cs * 0.5f)) lo = up = Forest;
                else if (aerial == null) lo = up = 0;
                else { lo = (byte)aerial.CoverIn(x0, z0, cs, false); up = (byte)aerial.CoverIn(x0, z0, cs, true); }
                cover[iz * ncx + ix] = (byte)(lo | up << 4);
            }

            // Simplify the field: only with a 5 m grid on the cell corners (fetch_dgm "dgm_step": 5), else emit cells as they are.
            var s = t.Samples;
            int ox = s == null ? 0 : Mathf.RoundToInt((startX - t.XMin) / cs), oz = s == null ? 0 : Mathf.RoundToInt((startZ - t.ZMin) / cs);
            bool editable = s != null && Mathf.Approximately(t.Step, cs) && ox >= 0 && oz >= 0 &&
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

            foreach (var (bx, bz, n) in blocks) Emit(sink, t, color, bx * Fine, bz * Fine, n * Fine);
            foreach (var (bx, bz) in partial)
                for (int iz = bz * Fine; iz < Mathf.Min((bz + 1) * Fine, ncz); iz++)
                for (int ix = bx * Fine; ix < Mathf.Min((bx + 1) * Fine, ncx); ix++)
                    Emit(sink, t, color, ix, iz, 1);
            Blocks = blocks.Count + partial.Count;
            cover = null;
            fine = null;
        }

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
            if (whole && Planar(grid, bx, bz, n) && Uniform(bx * Fine, bz * Fine, n * Fine, out _))
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

        /// <summary>Two triangles per block; split (down to <see cref="MinCover"/> cells) where the ground cover changes.</summary>
        static void Emit(MeshSink sink, TerrainField t, Color32 color, int ix, int iz, int n)
        {
            if (n > MinCover && !Uniform(ix, iz, n, out _))
            {
                int h = n / 2;
                Emit(sink, t, color, ix, iz, h);
                Emit(sink, t, color, ix + h, iz, h);
                Emit(sink, t, color, ix, iz + h, h);
                Emit(sink, t, color, ix + h, iz + h, h);
                return;
            }
            float cs = TerrainField.CellSize;
            float x0 = startX + ix * cs, z0 = startZ + iz * cs, x1 = x0 + n * cs, z1 = z0 + n * cs;
            Vector3 p00 = new Vector3(x0, t.Raw(x0, z0), z0), p10 = new Vector3(x1, t.Raw(x1, z0), z0);
            Vector3 p01 = new Vector3(x0, t.Raw(x0, z1), z1), p11 = new Vector3(x1, t.Raw(x1, z1), z1);
            var batch = sink.Get(WorldStyle.MatTerrain, new Vector2(x0, z0));
            Color32 cu, cl;
            if (n == 1)
            {
                byte v = cover[iz * ncx + ix];
                cu = Colour((byte)(v >> 4), color); cl = Colour((byte)(v & 15), color);
            }
            else
            {
                Uniform(ix, iz, n, out byte c);
                cu = cl = Colour(c, color);
            }
            Tri3(batch, p00, p01, p11, Shade(cu, p00), Shade(cu, p01), Shade(cu, p11));
            Tri3(batch, p00, p11, p10, Shade(cl, p00), Shade(cl, p11), Shade(cl, p10));
            Triangles += 2;
        }

        /// <summary>At least <see cref="Majority"/> of the cell halves share one cover (returned in <paramref name="c"/>).</summary>
        static bool Uniform(int ix, int iz, int n, out byte c)
        {
            var votes = new int[6];
            for (int k = iz; k < Mathf.Min(iz + n, ncz); k++)
            for (int i = ix; i < Mathf.Min(ix + n, ncx); i++)
            {
                byte v = cover[k * ncx + i];
                votes[v & 15]++; votes[v >> 4]++;
            }
            int best = 0, total = 0;
            for (int j = 0; j < votes.Length; j++) { total += votes[j]; if (votes[j] > votes[best]) best = j; }
            c = (byte)best;
            return votes[best] >= total * Majority;
        }

        /// <summary>Bare field (ploughed / harvested): out in the country, "paving" and "sand" in the aerial image are soil.</summary>
        static readonly Color32 Field = Color32.Lerp(WorldStyle.Grass, new Color32(150, 122, 90, 255), 0.65f);

        static Color32 Colour(byte c, Color32 fallback)
        {
            if (c == Forest) return WorldStyle.Forest;
            var cover = (AerialImage.Cover)c;
            if (cover == AerialImage.Cover.Paving || cover == AerialImage.Cover.Sand || cover == AerialImage.Cover.Soil) return Field;
            return AerialImage.CoverColor(cover, fallback);
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
