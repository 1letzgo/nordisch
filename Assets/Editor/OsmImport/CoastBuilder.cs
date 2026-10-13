using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Sea, Wadden Sea and coast for island areas (ground "sand" with DGM + aerial image), shown at low tide like the
    /// aerial image (DOP20 is flown at low water):
    /// <list type="bullet">
    /// <item>Water = dark in near infrared (DOP NIR &lt; <see cref="NirWater"/>) and below 0 m NHN, or no DGM tile at all
    /// (fetch_dgm.py fills those with <see cref="FillNhn"/>); 3×3 majority filter against speckle.</item>
    /// <item>Sea level = median DGM height of the water pixels (LiDAR sees the water surface, ≈ −1.6 m NHN).</item>
    /// <item>The terrain is rewritten (<see cref="TerrainField.Remap"/>): water samples sink to a flat floor
    /// <see cref="WaterDepth"/> below the sea level (wadeable), tidal flats are low-passed onto a 40 m piecewise-planar
    /// lattice aligned with GroundBuilder's merge blocks (Watt = 2 triangles per 40 m instead of 128) and kept above the sea.
    /// Flats without DGM tile get the typical Watt height.</item>
    /// <item><see cref="BuildSea"/>: flat water quads at sea level (quadtree, up to 80 m), also <see cref="OuterSea"/> m beyond
    /// the area so the horizon is sea; added after draping, so they stay level.</item>
    /// <item><see cref="Classify"/>: island ground cover by height + DOP colour: Watt/wet sand, salt marsh, Queller (red),
    /// dry sand, dune grass, dune scrub, lawn.</item>
    /// </list>
    /// Nothing here draws from the shared random generator.
    /// </summary>
    public static class CoastBuilder
    {
        public static bool Active { get; private set; }
        /// <summary>Sea level in local units (height above <see cref="TerrainField.Base"/>).</summary>
        public static float SeaLevel { get; private set; }
        public static float SeaLevelNhn { get; private set; }

        const float FillNhn = -2.5f;     // fetch_dgm.py SEA_FLOOR
        const int NirWater = 50;
        const float WaterDepth = 0.8f;
        const float ShoreLift = 0.15f;   // land stays at least this much above the sea
        const float MudTop = 1.2f;       // m NHN: below ≈ mean high water → Watt / wet sand
        const float MarshTop = 2.6f;     // m NHN: salt marsh / beach plain, above → dunes, village
        const float Lattice = 40f;       // = GroundBuilder MaxBlock × TerrainField.CellSize
        const float OuterSea = 600f;
        const int MaxWaterBlock = 16;    // cells (80 m)

        public static readonly Color32 SeaColor = WorldStyle.Hex("#4F8797");
        public static readonly Color32 MudColor = WorldStyle.Hex("#A39E89");
        public static readonly Color32 MarshColor = WorldStyle.Hex("#93A374");
        public static readonly Color32 DuneGrassColor = WorldStyle.Hex("#9AA463");
        public static readonly Color32 ScrubColor = WorldStyle.Hex("#6B7C46");
        public static readonly Color32 QuellerColor = WorldStyle.Hex("#A08070");

        static TerrainField terrain;

        public static void Prepare(OsmData data, AreaRect area, TerrainField t, AerialImage aerial)
        {
            Active = false;
            terrain = null;
            if (data.meta.ground != "sand" || t.IsFlat || aerial == null) return;
            Active = true;
            terrain = t;
            coverCache.Clear();
            float b = t.Base;
            int nx = t.SamplesX, nz = t.SamplesZ;

            // 1. Water candidates per DGM sample.
            var cand = new bool[nx * nz];
            var filled = new bool[nx * nz];
            var waterH = new List<float>();
            for (int iz = 0; iz < nz; iz++)
            for (int ix = 0; ix < nx; ix++)
            {
                int i = iz * nx + ix;
                var p = t.SamplePos(ix, iz);
                float h = t.Sample(ix, iz) + b;
                filled[i] = Mathf.Abs(h - FillNhn) < 1e-3f;
                int nir = Nir(aerial, p, out bool covered);
                bool dark = covered && nir < NirWater;
                cand[i] = dark && (filled[i] || h < 0.5f) || !covered && filled[i];
                if (dark && !filled[i] && h < 0f && (ix & 1) == 0 && (iz & 1) == 0) waterH.Add(h);
            }
            SeaLevelNhn = waterH.Count > 100 ? Mathf.Clamp(Median(waterH), -2.2f, 0.5f) : -1.5f;
            SeaLevel = SeaLevelNhn - b;

            // 2. Majority filter (≥ 5 of 3×3).
            var water = new bool[nx * nz];
            for (int iz = 0; iz < nz; iz++)
            for (int ix = 0; ix < nx; ix++)
            {
                int n = 0;
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = Mathf.Clamp(ix + dx, 0, nx - 1), z = Mathf.Clamp(iz + dz, 0, nz - 1);
                    if (cand[z * nx + x]) n++;
                }
                water[iz * nx + ix] = n >= 5;
            }

            // 3. Typical tidal flat height (for flats without DGM tile).
            var flats = new List<float>();
            for (int iz = 0; iz < nz; iz += 3)
            for (int ix = 0; ix < nx; ix += 3)
            {
                int i = iz * nx + ix;
                float h = t.Sample(ix, iz) + b;
                if (!water[i] && !filled[i] && h > SeaLevelNhn + 0.1f && h < MudTop) flats.Add(h);
            }
            float flatNhn = flats.Count > 100 ? Median(flats) : SeaLevelNhn + 0.5f;

            // 4. Coarse lattice of Watt heights, aligned with GroundBuilder.BuildBase's blocks.
            var g = area.Expand(60f);
            float ox = Mathf.Floor(g.xMin / TerrainField.CellSize) * TerrainField.CellSize;
            float oz = Mathf.Floor(g.zMin / TerrainField.CellSize) * TerrainField.CellSize;
            int lx = Mathf.CeilToInt((g.xMax - ox) / Lattice) + 2, lz = Mathf.CeilToInt((g.zMax - oz) / Lattice) + 2;
            var sum = new float[lx * lz];
            var cnt = new int[lx * lz];
            for (int iz = 0; iz < nz; iz++)
            for (int ix = 0; ix < nx; ix++)
            {
                int i = iz * nx + ix;
                if (water[i]) continue;
                float h = filled[i] ? flatNhn : t.Sample(ix, iz) + b;
                if (h >= MudTop) continue;
                var p = t.SamplePos(ix, iz);
                int jx = Mathf.Clamp(Mathf.RoundToInt((p.x - ox) / Lattice), 0, lx - 1);
                int jz = Mathf.Clamp(Mathf.RoundToInt((p.y - oz) / Lattice), 0, lz - 1);
                sum[jz * lx + jx] += h;
                cnt[jz * lx + jx]++;
            }
            var node = new float[lx * lz];
            for (int j = 0; j < node.Length; j++)
                node[j] = Mathf.Max(cnt[j] > 0 ? sum[j] / cnt[j] : flatNhn, SeaLevelNhn + ShoreLift);

            float Planar(float x, float z)
            {
                float fu = (x - ox) / Lattice, fv = (z - oz) / Lattice;
                int cx = Mathf.Clamp(Mathf.FloorToInt(fu), 0, lx - 2), cz = Mathf.Clamp(Mathf.FloorToInt(fv), 0, lz - 2);
                float u = Mathf.Clamp01(fu - cx), v = Mathf.Clamp01(fv - cz);
                float h00 = node[cz * lx + cx], h10 = node[cz * lx + cx + 1], h01 = node[(cz + 1) * lx + cx], h11 = node[(cz + 1) * lx + cx + 1];
                // Same diagonal split as TerrainField.Height / GroundBuilder blocks (x0z0–x1z1).
                return u >= v ? h00 + (h10 - h00) * u + (h11 - h10) * v : h00 + (h11 - h01) * u + (h01 - h00) * v;
            }

            // 5. Rewrite the terrain.
            float floor = SeaLevelNhn - WaterDepth;
            int waterCount = 0;
            t.Remap((x, z, h, ix, iz) =>
            {
                int i = iz * nx + ix;
                if (water[i]) { waterCount++; return floor - b; }
                float nh = filled[i] ? flatNhn : h + b;
                float w = Mathf.Clamp01((MudTop + 0.1f - nh) / 0.4f);
                if (w > 0f) nh = Mathf.Lerp(nh, Planar(x, z), w);
                return Mathf.Max(nh, SeaLevelNhn + ShoreLift) - b;
            });
            Debug.Log($"[Coast] sea level {SeaLevelNhn:0.00} m NHN, tidal flats {flatNhn:0.00} m NHN, " +
                      $"water {100f * waterCount / (nx * nz):0}% of {nx}×{nz} samples");
        }

        /// <summary>Mean near-infrared over the 2×2 m around a DGM sample; covered = inside the flown tiles.</summary>
        static int Nir(AerialImage aerial, Vector2 p, out bool covered)
        {
            int s = 0, n = 0;
            for (int dx = 0; dx < 2; dx++)
            for (int dz = 0; dz < 2; dz++)
                if (aerial.TryColor(p.x - 0.5f + dx, p.y - 0.5f + dz, out var c)) { s += c.a; n++; }
            covered = n > 0;
            return n > 0 ? s / n : 255;
        }

        static float Median(List<float> v)
        {
            v.Sort();
            return v[v.Count / 2];
        }

        // ------------------------------------------------------------------ ground cover

        /// <summary>Island ground cover of one aerial pixel at (x, z), using the (rewritten) terrain height.</summary>
        public static AerialImage.Cover Classify(Color32 c, float x, float z)
        {
            float nhn = terrain.Raw(x, z) + terrain.Base;
            if (nhn < SeaLevelNhn + 0.05f) return AerialImage.Cover.Mud; // under water / water line
            int max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (max < 45) return AerialImage.Cover.Unknown; // deep shadow
            float ndvi = (c.a - c.r) / (c.a + c.r + 1f);
            Color.RGBToHSV(c, out float hue, out float s, out float v);
            float deg = hue * 360f;
            bool reddish = (deg < 25f || deg > 330f) && s > 0.12f;
            if (nhn < MudTop)
            {
                if (ndvi > 0.2f) return AerialImage.Cover.Marsh;
                return reddish ? AerialImage.Cover.Queller : AerialImage.Cover.Mud;
            }
            if (nhn < MarshTop)
            {
                if (ndvi > 0.4f && v > 0.35f) return AerialImage.Cover.Grass;
                if (reddish) return AerialImage.Cover.Queller;
                if (v > 0.66f && ndvi < 0.03f) return AerialImage.Cover.Sand;
                return AerialImage.Cover.Marsh;
            }
            if (ndvi > 0.15f)
            {
                if (ndvi > 0.45f && v > 0.33f) return AerialImage.Cover.Grass;
                return v < 0.3f ? AerialImage.Cover.Scrub : AerialImage.Cover.DuneGrass;
            }
            if (v > 0.6f) return AerialImage.Cover.Sand;
            if (s > 0.2f && deg >= 12f && deg < 45f) return AerialImage.Cover.Soil;
            return v > 0.45f && s < 0.12f ? AerialImage.Cover.Paving : AerialImage.Cover.DuneGrass;
        }

        static readonly Dictionary<long, AerialImage.Cover> coverCache = new Dictionary<long, AerialImage.Cover>();

        /// <summary>
        /// Ground cover of a terrain cell = majority over the 3×3 cells around it (2 m raster): smooth patches instead of
        /// a checkerboard, both triangle halves alike (more merged blocks). Cached per cell.
        /// </summary>
        public static AerialImage.Cover CoverOf(AerialImage aerial, float x0, float z0, float size)
        {
            long key = ((long)Mathf.RoundToInt(x0 * 4f) << 32) ^ (uint)Mathf.RoundToInt(z0 * 4f) ^ ((long)Mathf.RoundToInt(size) << 56);
            if (coverCache.TryGetValue(key, out var cached)) return cached;
            var votes = new int[10];
            for (float x = x0 - size + 1f; x < x0 + 2f * size; x += 2f)
            for (float z = z0 - size + 1f; z < z0 + 2f * size; z += 2f)
            {
                if (aerial.UnderCanopy(x, z) || !aerial.TryColor(x, z, out var c)) continue;
                // The centre cell counts double.
                int w = x >= x0 && x < x0 + size && z >= z0 && z < z0 + size ? 2 : 1;
                votes[(int)Classify(c, x, z)] += w;
            }
            int best = 0;
            for (int k = 1; k < votes.Length; k++) if (votes[k] > 0 && (best == 0 || votes[k] > votes[best])) best = k;
            var cover = best == 0 ? AerialImage.Cover.DuneGrass : (AerialImage.Cover)best;
            // The cell itself under water / at the water line: always Watt.
            float m = Mathf.Min(Mathf.Min(terrain.Raw(x0, z0), terrain.Raw(x0 + size, z0)), Mathf.Min(terrain.Raw(x0, z0 + size), terrain.Raw(x0 + size, z0 + size)));
            if (m < SeaLevel + 0.05f) cover = AerialImage.Cover.Mud;
            return coverCache[key] = cover;
        }

        /// <summary>
        /// Scattered OSM scrub/heath/dune bushes on islands: always on dark scrub, on a third of dune grass / salt marsh,
        /// never on sand, Watt or paving (the polygons often reach over beaches and tidal flats).
        /// </summary>
        public static bool BushFits(Vector2 p)
        {
            var aerial = AerialImage.Current;
            if (!Active || aerial == null) return true;
            float cs = TerrainField.CellSize;
            var cover = CoverOf(aerial, Mathf.Floor(p.x / cs) * cs, Mathf.Floor(p.y / cs) * cs, cs);
            if (cover == AerialImage.Cover.Scrub) return true;
            if (cover != AerialImage.Cover.DuneGrass && cover != AerialImage.Cover.Marsh) return false;
            uint hsh = (uint)(Mathf.RoundToInt(p.x * 10f) * 73856093 ^ Mathf.RoundToInt(p.y * 10f) * 19349663);
            return hsh % 3 == 0;
        }

        /// <summary>OSM areas the aerial image shows better on islands (natural cover, tidal water, meadows).</summary>
        public static bool SkipArea(Dictionary<string, string> t)
        {
            if (!Active) return false;
            if (t.Is("water", "tidal") || t.Is("tidal", "yes") || t.Is("natural", "bay") || t.Is("natural", "strait")) return true;
            var n = t.Get("natural");
            if (n != null && n != "water") return true;
            var l = t.Get("landuse");
            return l == "meadow" || l == "grass" || l == "farmland" || l == "forest";
        }

        // ------------------------------------------------------------------ sea surface

        /// <summary>Level water quads wherever the ground dips below the sea, plus open sea around the area.</summary>
        public static int BuildSea(MeshSink sink, AreaRect area)
        {
            if (!Active) return 0;
            float cs = TerrainField.CellSize;
            var land = area.Expand(60f);
            float landX = Mathf.Floor(land.xMin / cs) * cs, landZ = Mathf.Floor(land.zMin / cs) * cs;
            int landNx = Mathf.CeilToInt((land.xMax - landX) / cs), landNz = Mathf.CeilToInt((land.zMax - landZ) / cs);
            int pad = Mathf.CeilToInt(OuterSea / (cs * MaxWaterBlock)) * MaxWaterBlock; // keep blocks aligned with the land grid
            float startX = landX - pad * cs, startZ = landZ - pad * cs;
            int nx = landNx + 2 * pad, nz = landNz + 2 * pad;

            // Per cell: needs water (outside the terrain, or any corner below the sea).
            var need = new bool[nx * nz];
            for (int iz = 0; iz < nz; iz++)
            for (int ix = 0; ix < nx; ix++)
            {
                int lxi = ix - pad, lzi = iz - pad;
                if (lxi < 0 || lzi < 0 || lxi >= landNx || lzi >= landNz) { need[iz * nx + ix] = true; continue; }
                float x0 = startX + ix * cs, z0 = startZ + iz * cs;
                float m = Mathf.Min(Mathf.Min(terrain.Raw(x0, z0), terrain.Raw(x0 + cs, z0)),
                                    Mathf.Min(terrain.Raw(x0, z0 + cs), terrain.Raw(x0 + cs, z0 + cs)));
                need[iz * nx + ix] = m < SeaLevel;
            }
            // Summed-area table → O(1) "whole block is water" test.
            var sat = new int[(nx + 1) * (nz + 1)];
            for (int iz = 0; iz < nz; iz++)
            for (int ix = 0; ix < nx; ix++)
                sat[(iz + 1) * (nx + 1) + ix + 1] = (need[iz * nx + ix] ? 1 : 0) + sat[iz * (nx + 1) + ix + 1]
                                                    + sat[(iz + 1) * (nx + 1) + ix] - sat[iz * (nx + 1) + ix];
            int Count(int ix, int iz, int n)
            {
                int x1 = Mathf.Min(ix + n, nx), z1 = Mathf.Min(iz + n, nz);
                return sat[z1 * (nx + 1) + x1] - sat[iz * (nx + 1) + x1] - sat[z1 * (nx + 1) + ix] + sat[iz * (nx + 1) + ix];
            }

            int quads = 0;
            void Block(int ix, int iz, int n)
            {
                if (ix >= nx || iz >= nz) return;
                int w = Mathf.Min(n, nx - ix), h = Mathf.Min(n, nz - iz);
                int c = Count(ix, iz, n);
                if (c == 0) return;
                if (c < w * h && n > 1)
                {
                    int half = n / 2;
                    Block(ix, iz, half); Block(ix + half, iz, half); Block(ix, iz + half, half); Block(ix + half, iz + half, half);
                    return;
                }
                if (c < w * h) return;
                float x0 = startX + ix * cs, z0 = startZ + iz * cs, x1 = x0 + w * cs, z1 = z0 + h * cs;
                var batch = sink.Get(WorldStyle.MatWater, new Vector2((x0 + x1) * 0.5f, (z0 + z1) * 0.5f));
                Vector3 p00 = new Vector3(x0, SeaLevel, z0), p10 = new Vector3(x1, SeaLevel, z0);
                Vector3 p01 = new Vector3(x0, SeaLevel, z1), p11 = new Vector3(x1, SeaLevel, z1);
                MeshSink.Tri(batch, p00, p01, p11, SeaColor);
                MeshSink.Tri(batch, p00, p11, p10, SeaColor);
                quads++;
            }
            for (int bz = 0; bz < nz; bz += MaxWaterBlock)
            for (int bx = 0; bx < nx; bx += MaxWaterBlock)
                Block(bx, bz, MaxWaterBlock);
            Debug.Log($"[Coast] sea surface {quads} quads");
            return quads;
        }
    }
}
