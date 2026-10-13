using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LeGamez.World;
using UnityEngine;
using UnityEngine.Networking;

namespace LeGamez.Core
{
    /// <summary>
    /// Tiles (and per-region facade records / minimap images) for the <see cref="WorldStreamer"/> on devices, fetched
    /// entry by entry out of the area's world pack on GitHub with HTTP Range requests (format 6) – only what the streamer
    /// needs, plus tiles within <see cref="PrefetchRadius"/> in the background. Every entry is cached under its content
    /// hash (MapStore.EntryPath) and checked against it. Reading and decompressing run on a worker thread; only the mesh
    /// creation is on the main thread (the streamer spaces that out to one tile per frame).
    /// </summary>
    public class PackTileSource : WorldStreamer.ITileSource
    {
        /// <summary>Tiles this close (edge distance) are fetched ahead of time – beyond the 760 m load radius.</summary>
        public const float PrefetchRadius = 1100f;
        /// <summary>Moving fast (car on the motorway, ~36 m/s): tiles around the point this many seconds ahead come first.</summary>
        const float LookAheadSeconds = 15f;
        const int MaxRequests = 6;

        readonly MapStore.Map map;
        readonly Dictionary<string, MapFormat.PackEntry> pack;
        readonly string url;
        readonly HashSet<Vector2Int> tiles = new HashSet<Vector2Int>();
        readonly Dictionary<string, float> failedAt = new Dictionary<string, float>();
        readonly HashSet<string> fetching = new HashSet<string>();
        int requests;
        float nextPrefetch;

        public PackTileSource(MapStore.Map map, AreaData area)
        {
            this.map = map;
            pack = area.pack ?? new Dictionary<string, MapFormat.PackEntry>();
            url = MapStore.PackUrl(map);
            foreach (var k in area.tileKeys) tiles.Add(k);
            MapStore.PrepareCache(map.Id);
        }

        bool Failed(string entry) => failedAt.TryGetValue(entry, out var t) && Time.realtimeSinceStartup - t < 20f;

        bool Cached(string entry) => pack.TryGetValue(entry, out var e) && File.Exists(MapStore.EntryPath(map.Id, entry, e.hash));

        /// <summary>Bytes of one pack entry: from the cache, else downloaded (Range request) and verified. done(null) if missing/offline.</summary>
        IEnumerator Fetch(string entry, Action<byte[]> done)
        {
            if (!pack.TryGetValue(entry, out var e)) { done(null); yield break; }
            string path = MapStore.EntryPath(map.Id, entry, e.hash);
            while (fetching.Contains(entry)) yield return null; // the same entry is on its way already
            if (File.Exists(path))
            {
                var read = Task.Run(() => File.ReadAllBytes(path));
                while (!read.IsCompleted) yield return null;
                if (!read.IsFaulted && read.Result.Length == e.length)
                {
                    MapStore.TouchEntry(map.Id, path);
                    done(read.Result);
                    yield break;
                }
            }
            if (url == null || Failed(entry)) { done(null); yield break; }

            fetching.Add(entry);
            while (requests >= MaxRequests) yield return null;
            requests++;
            byte[] bytes = null;
            string error = null;
            if (url.StartsWith("file://"))
            {
                // Local test catalogs (maps/catalog-url.txt): read the range straight from the file.
                string file = new Uri(url).LocalPath;
                var job = Task.Run(() =>
                {
                    using var fs = File.OpenRead(file);
                    var b = new byte[e.length];
                    fs.Position = e.offset;
                    int n = 0;
                    while (n < b.Length) { int k = fs.Read(b, n, b.Length - n); if (k <= 0) break; n += k; }
                    return b;
                });
                while (!job.IsCompleted) yield return null;
                if (job.IsFaulted) error = job.Exception?.InnerException?.Message; else bytes = job.Result;
            }
            else
            {
                using var req = UnityWebRequest.Get(url);
                req.SetRequestHeader("Range", $"bytes={e.offset}-{e.offset + e.length - 1}");
                req.timeout = 30;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    // A server ignoring the Range header would send the whole pack – stop right away.
                    if (req.downloadedBytes > (ulong)e.length + 4096) { req.Abort(); error = "range not supported"; break; }
                    yield return null;
                }
                if (error == null)
                {
                    if (req.result != UnityWebRequest.Result.Success) error = req.error;
                    else bytes = req.downloadHandler.data;
                }
            }
            requests--;

            if (bytes != null && bytes.Length == e.length)
            {
                var check = Task.Run(() =>
                {
                    if (MapFormat.Hash64(bytes) != e.hash) return false;
                    File.WriteAllBytes(path, bytes);
                    return true;
                });
                while (!check.IsCompleted) yield return null;
                if (check.IsFaulted || !check.Result) { error = "hash mismatch"; bytes = null; }
            }
            else if (error == null) { error = $"length {bytes?.Length} != {e.length}"; bytes = null; }
            fetching.Remove(entry);
            if (error != null)
            {
                failedAt[entry] = Time.realtimeSinceStartup;
                Debug.LogWarning($"[World] {map.Id} {entry}: {error}");
                done(null);
                yield break;
            }
            MapStore.TouchEntry(map.Id, path);
            done(bytes);
        }

        public IEnumerator Load(string areaId, Vector2Int key, Action<WorldTile> done)
        {
            byte[] blob = null;
            yield return Fetch(MapFormat.TileEntry(key), b => blob = b);
            if (blob == null) { done(null); yield break; }
            var job = Task.Run(() => MapFormat.DecodeTile(key, blob));
            while (!job.IsCompleted) yield return null;
            if (job.IsFaulted)
            {
                Debug.LogWarning($"[World] {map.Id} tile {key}: {job.Exception?.InnerException?.Message}");
                done(null);
                yield break;
            }
            done(MapFormat.BuildTile(job.Result, WorldStreamer.TileName(areaId, key)));
        }

        public void Release(Vector2Int key, WorldTile tile) => MapFormat.DestroyTile(tile);

        public IEnumerator LoadContent(string areaId, Vector2Int region, Action<RegionContent> done)
        {
            byte[] facade = null, image = null;
            yield return Fetch(MapFormat.FacadeEntry(region), b => facade = b);
            yield return Fetch(MapFormat.MapEntry(region), b => image = b);
            if (facade == null && image == null) { done(null); yield break; }
            var job = Task.Run(() => MapFormat.RegionContentFrom(region, facade, image));
            while (!job.IsCompleted) yield return null;
            done(job.IsFaulted ? null : job.Result);
        }

        public void Prefetch(Vector3 focus, Vector3 velocity)
        {
            // Streamer requests go first (two slots stay free for them).
            bool fast = velocity.sqrMagnitude > 10f * 10f;
            if (Time.unscaledTime < nextPrefetch || requests >= MaxRequests - 2) return;
            nextPrefetch = Time.unscaledTime + (fast ? 0.1f : 0.5f);
            // At speed the disc moves with the car: around the point a few seconds ahead, nearest to it first.
            var ahead = fast ? focus + Vector3.ClampMagnitude(velocity * LookAheadSeconds, 700f) : focus;
            Vector2Int? best = null;
            float bestD = float.MaxValue;
            int r = Mathf.CeilToInt(PrefetchRadius / WorldStreamer.TileSize) + 1;
            var c = WorldStreamer.KeyOf(ahead.x, ahead.z);
            for (int x = c.x - r; x <= c.x + r; x++)
            for (int z = c.y - r; z <= c.y + r; z++)
            {
                var k = new Vector2Int(x, z);
                if (!tiles.Contains(k)) continue;
                string entry = MapFormat.TileEntry(k);
                if (fetching.Contains(entry) || Failed(entry) || Cached(entry)) continue;
                float d = Distance(k, ahead);
                if (d < PrefetchRadius && d < bestD) { bestD = d; best = k; }
            }
            if (best != null) MapStore.Run(Fetch(MapFormat.TileEntry(best.Value), _ => { }));
        }

        static float Distance(Vector2Int k, Vector3 p)
        {
            float s = WorldStreamer.TileSize, x0 = k.x * s, z0 = k.y * s;
            float dx = Mathf.Max(x0 - p.x, 0f, p.x - (x0 + s));
            float dz = Mathf.Max(z0 - p.z, 0f, p.z - (z0 + s));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
