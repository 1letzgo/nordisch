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
        readonly int nx, nz;
        readonly float xMin, zMin, step;
        public float Base { get; }
        public bool IsFlat { get; }

        public static TerrainField Current { get; private set; } = Flat();

        TerrainField(float[] samples, int nx, int nz, float xMin, float zMin, float step, float baseHeight)
        {
            this.samples = samples;
            this.nx = nx;
            this.nz = nz;
            this.xMin = xMin;
            this.zMin = zMin;
            this.step = step;
            Base = baseHeight;
            IsFlat = samples == null;
        }

        /// <summary>Sample grid for in-place edits (<see cref="CoarseTerrain"/> simplifies the ground outside the area mask).</summary>
        internal float[] Samples => samples;
        internal int Nx => nx;
        internal int Nz => nz;
        internal float XMin => xMin;
        internal float ZMin => zMin;
        internal float Step => step;

        static TerrainField Flat() => new TerrainField(null, 0, 0, 0, 0, 1, 0);

        public static TerrainField Load(string path)
        {
            if (!File.Exists(path)) return Current = Flat();
            Stream stream = File.OpenRead(path);
            if (stream.ReadByte() == 0x1f && stream.ReadByte() == 0x8b) // gzip (large areas, see fetch_dgm.py)
            {
                stream.Position = 0;
                stream = new BufferedStream(new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress), 1 << 20);
            }
            else stream.Position = 0;
            using var br = new BinaryReader(stream);
            int nx, nz;
            float xMin, zMin, step, baseH;
            var magic = br.ReadBytes(4);
            if (magic[0] == 'D' && magic[1] == 'G' && magic[2] == 'M' && magic[3] == 'Q')
            {
                // Compact grid: same header, heights as uint16 centimetres.
                nx = br.ReadInt32(); nz = br.ReadInt32();
                xMin = br.ReadSingle(); zMin = br.ReadSingle();
                step = br.ReadSingle(); baseH = br.ReadSingle();
                var q = new float[nx * nz];
                var bytes = br.ReadBytes(q.Length * 2);
                for (int i = 0; i < q.Length; i++) q[i] = (bytes[2 * i] | bytes[2 * i + 1] << 8) * 0.01f;
                return Current = new TerrainField(q, nx, nz, xMin, zMin, step, baseH);
            }
            if (magic[0] == 'D' && magic[1] == 'G' && magic[2] == 'M' && magic[3] == '2')
            {
                // Rectangular grid (current format).
                nx = br.ReadInt32(); nz = br.ReadInt32();
                xMin = br.ReadSingle(); zMin = br.ReadSingle();
                step = br.ReadSingle(); baseH = br.ReadSingle();
            }
            else
            {
                // Legacy square grid: int32 n, float extent, step, base.
                br.BaseStream.Position = 0;
                nx = nz = br.ReadInt32();
                float extent = br.ReadSingle();
                xMin = zMin = -extent;
                step = br.ReadSingle(); baseH = br.ReadSingle();
            }
            var s = new float[nx * nz];
            for (int i = 0; i < s.Length; i++) s[i] = br.ReadSingle();
            return Current = new TerrainField(s, nx, nz, xMin, zMin, step, baseH);
        }

        /// <summary>Smooth (bilinear) DGM height – used to build the faceted ground vertices.</summary>
        public float Raw(float x, float z)
        {
            if (IsFlat) return 0f;
            float fx = Mathf.Clamp((x - xMin) / step, 0, nx - 1.001f);
            float fz = Mathf.Clamp((z - zMin) / step, 0, nz - 1.001f);
            int ix = (int)fx, iz = (int)fz;
            float tx = fx - ix, tz = fz - iz;
            float h00 = samples[iz * nx + ix], h10 = samples[iz * nx + ix + 1];
            float h01 = samples[(iz + 1) * nx + ix], h11 = samples[(iz + 1) * nx + ix + 1];
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
