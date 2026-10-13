using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LeGamez.World;
using UnityEngine;

namespace LeGamez.Core
{
    /// <summary>
    /// Tiles for the <see cref="WorldStreamer"/> on devices: each 1 km region is one MapFormat file in the map folder,
    /// downloaded by <see cref="MapStore"/> the first time a tile of it is needed and prefetched when the player comes
    /// within <see cref="PrefetchRadius"/>. A tile is read and decompressed on a worker thread; only the mesh creation
    /// runs on the main thread (the streamer spaces that out to one tile per frame). The region's extras (facade records,
    /// minimap image) are read the same way once a tile of the region is in use.
    /// </summary>
    public class RegionTileSource : WorldStreamer.ITileSource
    {
        /// <summary>Regions this close (edge distance) are downloaded ahead of time – ~1 min by car beyond the view.</summary>
        public const float PrefetchRadius = 1600f;

        readonly MapStore.Map map;
        readonly HashSet<Vector2Int> known = new HashSet<Vector2Int>(); // regions the build has
        readonly Dictionary<Vector2Int, Dictionary<Vector2Int, (long offset, int length)>> indexes =
            new Dictionary<Vector2Int, Dictionary<Vector2Int, (long, int)>>();
        float nextPrefetch;
        bool prefetching;

        public RegionTileSource(MapStore.Map map)
        {
            this.map = map;
            if (map.installed == null) return;
            foreach (var f in map.installed.AllFiles)
                if (!string.IsNullOrEmpty(f.region) && TryParse(f.region, out var r)) known.Add(r);
        }

        static string Key(Vector2Int r) => $"{r.x}_{r.y}";

        static bool TryParse(string s, out Vector2Int r)
        {
            r = default;
            var p = s.Split('_');
            if (p.Length != 2 || !int.TryParse(p[0], out int x) || !int.TryParse(p[1], out int z)) return false;
            r = new Vector2Int(x, z);
            return true;
        }

        public IEnumerator Load(string areaId, Vector2Int key, Action<WorldTile> done)
        {
            var r = WorldStreamer.RegionOf(key);
            string rk = Key(r);
            if (!MapStore.HasRegion(map.Id, rk))
            {
                yield return MapStore.FetchRegion(map, rk);
                if (!MapStore.HasRegion(map.Id, rk)) { done(null); yield break; }
            }
            MapStore.TouchRegion(map.Id, rk);
            string path = MapStore.RegionPath(map.Id, rk);
            indexes.TryGetValue(r, out var index);
            var job = Task.Run(() =>
            {
                using var fs = File.OpenRead(path);
                index ??= MapFormat.ReadRegionIndex(fs);
                if (!index.TryGetValue(key, out var at)) return (index, (MapFormat.TileData)null);
                var blob = new byte[at.length];
                fs.Position = at.offset;
                int read = 0;
                while (read < blob.Length) { int n = fs.Read(blob, read, blob.Length - read); if (n <= 0) break; read += n; }
                return (index, MapFormat.DecodeTile(key, blob));
            });
            while (!job.IsCompleted) yield return null;
            if (job.IsFaulted)
            {
                Debug.LogWarning($"[World] region {map.Id}/{rk}: {job.Exception?.InnerException?.Message}");
                done(null);
                yield break;
            }
            indexes[r] = job.Result.index;
            var data = job.Result.Item2;
            done(data != null ? MapFormat.BuildTile(data, WorldStreamer.TileName(areaId, key)) : null);
        }

        public void Release(Vector2Int key, WorldTile tile) => MapFormat.DestroyTile(tile);

        /// <summary>Facade records + minimap image of a region (its file is on the device: one of its tiles is loaded).</summary>
        public IEnumerator LoadContent(string areaId, Vector2Int region, Action<RegionContent> done)
        {
            string rk = Key(region);
            if (!MapStore.HasRegion(map.Id, rk)) { done(null); yield break; }
            string path = MapStore.RegionPath(map.Id, rk);
            var job = Task.Run(() => MapFormat.ReadRegionContent(path, region));
            while (!job.IsCompleted) yield return null;
            if (job.IsFaulted)
            {
                Debug.LogWarning($"[World] region {map.Id}/{rk} content: {job.Exception?.InnerException?.Message}");
                done(null);
                yield break;
            }
            done(job.Result);
        }

        public void Prefetch(Vector3 focus)
        {
            if (prefetching || Time.unscaledTime < nextPrefetch) return;
            nextPrefetch = Time.unscaledTime + 1.5f;
            // Nearest missing region within reach, one at a time (tiles the streamer needs right now go first anyway).
            Vector2Int? best = null;
            float bestD = float.MaxValue;
            foreach (var r in known)
            {
                string rk = Key(r);
                if (MapStore.HasRegion(map.Id, rk) || MapStore.RegionFailed(map.Id, rk)) continue;
                float d = Distance(r, focus);
                if (d < PrefetchRadius && d < bestD) { bestD = d; best = r; }
            }
            if (best == null) return;
            prefetching = true;
            MapStore.Run(Fetch(Key(best.Value)));
        }

        IEnumerator Fetch(string rk)
        {
            yield return MapStore.FetchRegion(map, rk);
            prefetching = false;
        }

        static float Distance(Vector2Int r, Vector3 p)
        {
            float s = WorldStreamer.RegionSize, x0 = r.x * s, z0 = r.y * s;
            float dx = Mathf.Max(x0 - p.x, 0f, p.x - (x0 + s));
            float dz = Mathf.Max(z0 - p.z, 0f, p.z - (z0 + s));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
