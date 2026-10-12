using System.IO;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Height grid from Data/&lt;area&gt;.dgm.bin (see Tools/dgm/fetch_dgm.py). The visible ground is
    /// built from <see cref="CellSize"/> cells split along one diagonal; <see cref="Height"/> uses the
    /// exact same triangles, so draped overlays sit precisely on the faceted ground.
    /// Without a file every height is 0 (flat world, previous behaviour).
    /// </summary>
    public class TerrainField
    {
        public const float CellSize = 5f;

        readonly float[] samples;
        readonly int n;
        readonly float extent, step;
        public float Base { get; }
        public bool IsFlat { get; }

        public static TerrainField Current { get; private set; } = Flat();

        TerrainField(float[] samples, int n, float extent, float step, float baseHeight)
        {
            this.samples = samples;
            this.n = n;
            this.extent = extent;
            this.step = step;
            Base = baseHeight;
            IsFlat = samples == null;
        }

        static TerrainField Flat() => new TerrainField(null, 0, 0, 1, 0);

        public static TerrainField Load(string path)
        {
            if (!File.Exists(path)) return Current = Flat();
            using var br = new BinaryReader(File.OpenRead(path));
            int n = br.ReadInt32();
            float extent = br.ReadSingle(), step = br.ReadSingle(), baseH = br.ReadSingle();
            var s = new float[n * n];
            for (int i = 0; i < s.Length; i++) s[i] = br.ReadSingle();
            return Current = new TerrainField(s, n, extent, step, baseH);
        }

        /// <summary>Smooth (bilinear) DGM height – used to build the faceted ground vertices.</summary>
        public float Raw(float x, float z)
        {
            if (IsFlat) return 0f;
            float fx = Mathf.Clamp((x + extent) / step, 0, n - 1.001f);
            float fz = Mathf.Clamp((z + extent) / step, 0, n - 1.001f);
            int ix = (int)fx, iz = (int)fz;
            float tx = fx - ix, tz = fz - iz;
            float h00 = samples[iz * n + ix], h10 = samples[iz * n + ix + 1];
            float h01 = samples[(iz + 1) * n + ix], h11 = samples[(iz + 1) * n + ix + 1];
            return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
        }

        /// <summary>Height of the faceted ground surface (cell corners from <see cref="Raw"/>, split x0z0–x1z1).</summary>
        public float Height(float x, float z)
        {
            if (IsFlat) return 0f;
            float cx = Mathf.Floor(x / CellSize) * CellSize, cz = Mathf.Floor(z / CellSize) * CellSize;
            float u = (x - cx) / CellSize, v = (z - cz) / CellSize;
            float h00 = Raw(cx, cz), h11 = Raw(cx + CellSize, cz + CellSize);
            if (u >= v)
            {
                float h10 = Raw(cx + CellSize, cz);
                return h00 + (h10 - h00) * u + (h11 - h10) * v;
            }
            float h01 = Raw(cx, cz + CellSize);
            return h00 + (h11 - h01) * u + (h01 - h00) * v;
        }

        /// <summary>Lowest ground height among the given footprint points (buildings sit on this).</summary>
        public float MinUnder(System.Collections.Generic.IEnumerable<Vector2> pts)
        {
            if (IsFlat) return 0f;
            float m = float.MaxValue;
            foreach (var p in pts) m = Mathf.Min(m, Height(p.x, p.y));
            return m == float.MaxValue ? 0f : m;
        }
    }
}
