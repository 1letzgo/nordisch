using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.World
{
    /// <summary>
    /// Per-region extras besides the tile geometry (one 1 km region = <see cref="WorldStreamer.RegionSize"/>): the
    /// close-up facade records (<see cref="FacadeDetails"/>) and a sharp top-down image for the minimap. They travel in
    /// the region file (MapFormat "NWR2" sections), so the area file stays small. <see cref="WorldStreamer.Regions"/> holds
    /// the content of every region that has a tile in the scene; FacadeDetails and the maps read from there.
    /// </summary>
    public class RegionContent
    {
        public Vector2Int key;
        /// <summary>FacadeDetails records of buildings in this region (<see cref="FacadeDetails.Stride"/> floats each), or null.</summary>
        public float[] facade;
        /// <summary>Encoded image (JPEG) covering exactly the region, north up, or null.</summary>
        public byte[] mapImage;

        /// <summary>Region file sections.</summary>
        public const string FacadeSection = "facade.bin", MapSection = "map.jpg";

        /// <summary>Pixels per side of the region images (~1 m per pixel).</summary>
        public const int MapPixels = 1024;

        public static float[] ToFloats(byte[] bytes)
        {
            if (bytes == null) return null;
            var f = new float[bytes.Length / 4];
            System.Buffer.BlockCopy(bytes, 0, f, 0, f.Length * 4);
            return f;
        }

        public static byte[] ToBytes(float[] f)
        {
            var b = new byte[f.Length * 4];
            System.Buffer.BlockCopy(f, 0, b, 0, b.Length);
            return b;
        }

        /// <summary>Sorts all facade records of an area into regions (by the record's anchor point).</summary>
        public static Dictionary<Vector2Int, float[]> SplitFacade(float[] all)
        {
            var lists = new Dictionary<Vector2Int, List<float>>();
            if (all != null)
                for (int i = 0; i + FacadeDetails.Stride <= all.Length; i += FacadeDetails.Stride)
                {
                    var r = WorldStreamer.RegionAt(all[i + 1], all[i + 3]);
                    if (!lists.TryGetValue(r, out var l)) lists[r] = l = new List<float>();
                    for (int k = 0; k < FacadeDetails.Stride; k++) l.Add(all[i + k]);
                }
            var result = new Dictionary<Vector2Int, float[]>(lists.Count);
            foreach (var kv in lists) result[kv.Key] = kv.Value.ToArray();
            return result;
        }

        /// <summary>World rectangle (x, z) of a region.</summary>
        public static Rect Bounds(Vector2Int r) =>
            new Rect(r.x * WorldStreamer.RegionSize, r.y * WorldStreamer.RegionSize, WorldStreamer.RegionSize, WorldStreamer.RegionSize);
    }
}
