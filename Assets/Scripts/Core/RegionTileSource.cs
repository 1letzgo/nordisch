using System;
using System.Collections;
using System.Collections.Generic;
using LeGamez.World;
using UnityEngine;

namespace LeGamez.Core
{
    /// <summary>
    /// Tiles for the <see cref="WorldStreamer"/> on devices: each 1 km region is its own bundle in the map folder,
    /// downloaded by <see cref="MapStore"/> the first time a tile of it is needed and prefetched when the player comes
    /// within <see cref="PrefetchRadius"/>. A region bundle stays open while any of its tiles is in the scene.
    /// </summary>
    public class RegionTileSource : WorldStreamer.ITileSource
    {
        /// <summary>Regions this close (edge distance) are downloaded ahead of time – ~1 min by car beyond the view.</summary>
        public const float PrefetchRadius = 1600f;

        class Region
        {
            public AssetBundle bundle;
            public int refs;
            public bool loading;
        }

        readonly MapStore.Map map;
        readonly HashSet<Vector2Int> known = new HashSet<Vector2Int>(); // regions the build has
        readonly Dictionary<Vector2Int, Region> open = new Dictionary<Vector2Int, Region>();
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
            if (!open.TryGetValue(r, out var region))
            {
                region = new Region { loading = true };
                open[r] = region;
                var req = AssetBundle.LoadFromFileAsync(MapStore.RegionPath(map.Id, rk));
                yield return req;
                region.bundle = req.assetBundle;
                region.loading = false;
            }
            else
                while (region.loading) yield return null;
            if (region.bundle == null)
            {
                Debug.LogWarning($"[World] region {map.Id}/{rk} unreadable");
                if (open.TryGetValue(r, out var o) && o == region) open.Remove(r);
                done(null);
                yield break;
            }
            region.refs++;
            MapStore.TouchRegion(map.Id, rk);
            var tileReq = region.bundle.LoadAssetAsync<WorldTile>(WorldStreamer.TileName(areaId, key));
            yield return tileReq;
            var tile = tileReq.asset as WorldTile;
            if (tile == null) Unref(r);
            done(tile);
        }

        public void Release(Vector2Int key, WorldTile tile)
        {
            // Assets from a bundle can be dropped one by one; they load again on the next request.
            foreach (var part in tile.parts) if (part.mesh != null) Resources.UnloadAsset(part.mesh);
            Resources.UnloadAsset(tile);
            Unref(WorldStreamer.RegionOf(key));
        }

        void Unref(Vector2Int r)
        {
            if (!open.TryGetValue(r, out var region) || region.loading) return;
            if (--region.refs > 0) return;
            if (region.bundle != null) region.bundle.Unload(true);
            open.Remove(r);
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

        public void UnloadAll()
        {
            foreach (var region in open.Values) if (region.bundle != null) region.bundle.Unload(true);
            open.Clear();
        }
    }
}
