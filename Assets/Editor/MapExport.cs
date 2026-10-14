using System.Collections.Generic;
using System.IO;
using System.Linq;
using LeGamez.Core;
using LeGamez.World;
using UnityEditor;
using UnityEngine;

namespace LeGamez.Editor
{
    /// <summary>
    /// Writes the download files of each area (format 6, World/MapFormat – the same files for iOS and Android, independent
    /// of the Unity version) to Builds/Maps/&lt;id&gt;/: area.bin (AreaData + small overview map + index of the world pack) and
    /// world.bin (every tile, the facade records and minimap image per 1 km region as separate entries – the app fetches
    /// single entries with HTTP Range requests), plus index.json for Tools/release/publish_maps.py.
    /// From <see cref="MapFormat.ChunkedPackFormat"/> on the pack is split into 4 × 4 km chunk files (world-&lt;cx&gt;_&lt;cz&gt;.bin,
    /// region images in world-&lt;cx&gt;_&lt;cz&gt;-maps.bin), so a small change touches only one geometry chunk – the publisher
    /// uploads only files whose SHA-256 changed.
    /// Incremental: export.json next to the files remembers each entry's source (tile signature from the importer's
    /// TileManifest, hash of the facade records / region image) and where its bytes went; unchanged entries are copied
    /// from the previous files instead of loading and compressing the tile again. Entry order is fixed (regions west to
    /// east, south to north), so unchanged chunks come out byte for byte the same.
    /// Batch: Unity -batchmode -quit -projectPath . -executeMethod LeGamez.Editor.MapExport.ExportBatch [-areas a,b]
    /// </summary>
    public static class MapExport
    {
        public static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Maps"));

        [MenuItem("LeGamez/Export Maps")]
        public static void ExportMenu() => Export(null);

        public static void ExportBatch()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-areas");
            string[] only = i >= 0 && i + 1 < args.Length ? args[i + 1].Split(',') : null;
            if (!Export(only) && Application.isBatchMode) EditorApplication.Exit(1);
        }

        [System.Serializable]
        class IndexFile
        {
            public string name;   // area.bin or world.bin
            public string region; // unused since format 6 (was "rx_rz" for region files)
            public string path;
        }

        /// <summary>A default start point (AreaData.starts): menu card in the app (MapStore.StartInfo). thumb = local path
        /// of its card image (publish_maps.py uploads it and puts the URL in); empty = use the map's thumbnail.</summary>
        [System.Serializable]
        class IndexStart
        {
            public string name;
            public float x, z, yaw;
            public string thumb;
        }

        [System.Serializable]
        class IndexMap
        {
            public string id, name, thumb;
            public double lat, lon;
            public string[] vehicles;
            public int pois;
            public List<IndexFile> files = new List<IndexFile>();
            /// <summary>Regions "rx_rz" within <see cref="StartRadius"/> of any start point (catalog "start[]").</summary>
            public List<string> start = new List<string>();
            public List<IndexStart> starts = new List<IndexStart>();
        }

        /// <summary>Regions this close to a start point count as its start surroundings.</summary>
        const float StartRadius = 1200f;

        [System.Serializable]
        class Index
        {
            public int format;
            public List<IndexMap> maps = new List<IndexMap>();
        }

        /// <summary>export.json: per pack entry its source hash and location in the files of the last export.</summary>
        [System.Serializable]
        class ExportManifest
        {
            public int format;
            public List<ExportEntry> entries = new List<ExportEntry>();
        }

        [System.Serializable]
        class ExportEntry
        {
            public string name, source, file;
            public long offset;
            public int length;
            public string hash; // hex, MapFormat.Hash64 of the bytes
        }

        static bool Chunked => MapStore.Format >= MapFormat.ChunkedPackFormat;

        static bool Export(string[] only)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<AreaCatalog>(AreaCatalog.AssetPath);
            if (catalog == null) { Debug.LogError("[Maps] no AreaCatalog – run ImportAllBatch first"); return false; }
            Directory.CreateDirectory(OutDir);
            var index = new Index { format = MapStore.Format };
            foreach (var a in catalog.areas.Where(x => only == null || only.Contains(x.id)))
            {
                var data = AssetDatabase.LoadAssetAtPath<AreaData>(OsmImport.OsmAreaImporter.AreaDataPath(a.id));
                if (data == null) { Debug.LogError($"[Maps] {a.id}: AreaData missing – re-import the area"); return false; }
                string dir = Path.Combine(OutDir, a.id);
                // The previous export (if any) is read while the new one is written next to it, then replaced.
                string newDir = dir + ".new";
                if (Directory.Exists(newDir)) Directory.Delete(newDir, true);
                Directory.CreateDirectory(newDir);
                var old = new Dictionary<string, ExportEntry>();
                string oldManifest = Path.Combine(dir, "export.json");
                if (File.Exists(oldManifest))
                    foreach (var e in JsonUtility.FromJson<ExportManifest>(File.ReadAllText(oldManifest)).entries) old[e.name] = e;
                var tileHashes = OsmImport.TileManifest.Load(a.id); // what the importer wrote (null: unknown → encode every tile)
                var m = new IndexMap
                {
                    id = a.id, name = a.displayName, lat = a.lat, lon = a.lon, vehicles = a.vehicles.ToArray(), pois = a.poiCount,
                    thumb = Path.GetFullPath(AreaCatalog.ThumbPath(a.id)),
                };

                // World pack: every entry back to back, grouped by region (neighbouring tiles lie close together).
                var facade = RegionContent.SplitFacade(data.facade != null ? RegionContent.ToFloats(data.facade.bytes) : null);
                var pack = new Dictionary<string, MapFormat.PackEntry>();
                var manifest = new ExportManifest { format = MapStore.Format };
                var streams = new Dictionary<string, FileStream>();
                var oldStreams = new Dictionary<string, FileStream>();
                int noMap = 0, reused = 0, encoded = 0;
                try
                {
                    // Bytes of an unchanged entry out of the previous export, null if it can't be reused.
                    byte[] Reuse(string name, string source)
                    {
                        if (source == null || !old.TryGetValue(name, out var e) || e.source != source) return null;
                        string path = Path.Combine(dir, e.file);
                        if (!oldStreams.TryGetValue(e.file, out var fs))
                        {
                            if (!File.Exists(path)) return null;
                            oldStreams[e.file] = fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                        }
                        if (e.offset + e.length > fs.Length) return null;
                        var b = new byte[e.length];
                        fs.Position = e.offset;
                        int n = 0;
                        while (n < b.Length) { int k = fs.Read(b, n, b.Length - n); if (k <= 0) return null; n += k; }
                        if (MapFormat.Hash64(b).ToString("x16") != e.hash) return null;
                        reused++;
                        return b;
                    }
                    void Put(Vector2Int region, string name, string source, byte[] bytes)
                    {
                        string file = Chunked ? MapFormat.ChunkFile(region, maps: name.StartsWith("m/")) : MapStore.PackFile;
                        if (!streams.TryGetValue(file, out var fs))
                            streams[file] = fs = new FileStream(Path.Combine(newDir, file), FileMode.Create, FileAccess.Write);
                        ulong hash = MapFormat.Hash64(bytes);
                        pack[name] = new MapFormat.PackEntry { offset = fs.Position, length = bytes.Length, hash = hash, file = Chunked ? file : null };
                        manifest.entries.Add(new ExportEntry { name = name, source = source, file = file, offset = fs.Position, length = bytes.Length, hash = hash.ToString("x16") });
                        fs.Write(bytes, 0, bytes.Length);
                    }
                    foreach (var g in data.tileKeys.GroupBy(WorldStreamer.RegionOf).OrderBy(g => g.Key.x).ThenBy(g => g.Key.y))
                    {
                        foreach (var k in g.OrderBy(k => k.x).ThenBy(k => k.y))
                        {
                            string name = MapFormat.TileEntry(k);
                            string source = tileHashes != null && tileHashes.TryGetValue(k, out var th) ? "tile:" + th.ToString("x16") : null;
                            var bytes = Reuse(name, source);
                            if (bytes == null)
                            {
                                var t = AssetDatabase.LoadAssetAtPath<WorldTile>(WorldStreamer.EditorTilePath(a.id, k));
                                if (t == null) continue;
                                bytes = MapFormat.TileBlob(t);
                                Resources.UnloadAsset(t); // keep editor memory flat on big areas
                                encoded++;
                            }
                            Put(g.Key, name, source, bytes);
                        }
                        if (facade.TryGetValue(g.Key, out var rec))
                        {
                            var raw = RegionContent.ToBytes(rec);
                            string source = "facade:" + MapFormat.Hash64(raw).ToString("x16");
                            Put(g.Key, MapFormat.FacadeEntry(g.Key), source, Reuse(MapFormat.FacadeEntry(g.Key), source) ?? MapFormat.Deflate(raw));
                        }
                        string mapFile = WorldStreamer.EditorRegionMapPath(a.id, g.Key);
                        if (File.Exists(mapFile))
                        {
                            var jpg = File.ReadAllBytes(mapFile);
                            Put(g.Key, MapFormat.MapEntry(g.Key), "map:" + MapFormat.Hash64(jpg).ToString("x16"), jpg);
                        }
                        else noMap++;
                        if (g.Key.x % 4 == 0 && encoded > 0) EditorUtility.UnloadUnusedAssetsImmediate();
                    }
                }
                finally
                {
                    foreach (var fs in streams.Values) fs.Dispose();
                    foreach (var fs in oldStreams.Values) fs.Dispose();
                }
                File.WriteAllText(Path.Combine(newDir, "export.json"), JsonUtility.ToJson(manifest));
                if (Directory.Exists(dir)) Directory.Delete(dir, true); // no stale files
                Directory.Move(newDir, dir);

                // The loop above unloads assets to keep memory flat – reload the area data (and its POIs/streets) fresh.
                data = AssetDatabase.LoadAssetAtPath<AreaData>(OsmImport.OsmAreaImporter.AreaDataPath(a.id));
                if (data.streets == null || data.pois == null) Debug.LogWarning($"[Maps] {a.id}: streets/POIs missing in AreaData");
                string areaFile = Path.Combine(dir, "area.bin");
                File.WriteAllBytes(areaFile, MapFormat.WriteArea(data, OverviewImage(a.id), pack));
                m.files.Add(new IndexFile { name = "area.bin", region = "", path = areaFile });
                // Pack files are never downloaded whole (the app fetches entries by range); "pack" keeps chunks out of the
                // area download of apps that go by the region field.
                long packSize = 0;
                foreach (var file in manifest.entries.Select(e => e.file).Distinct().OrderBy(f => f))
                {
                    string path = Path.Combine(dir, file);
                    m.files.Add(new IndexFile { name = file, region = Chunked ? "pack" : "", path = path });
                    packSize += new FileInfo(path).Length;
                }
                AddStarts(m, a.id, data);
                long areaSize = new FileInfo(areaFile).Length;
                index.maps.Add(m);
                if (noMap > 0) Debug.LogWarning($"[Maps] {a.id}: {noMap} regions without minimap image – run SceneSnapshot.RenderThumbnailsBatch -area {a.id}");
                Debug.Log($"[Maps] {a.id}: area {areaSize / 1024} KB ({data.streets?.names.Count ?? 0} streets, {data.pois?.entries.Count ?? 0} POIs), world pack {packSize / (1024 * 1024)} MB " +
                          $"in {m.files.Count - 1} file(s) ({pack.Count} entries, {reused} reused from the last export, {encoded} tiles encoded)");
            }
            File.WriteAllText(Path.Combine(OutDir, "index.json"), JsonUtility.ToJson(index, true));
            return true;
        }

        /// <summary>Default start points with their card images, and the regions around all of them.</summary>
        static void AddStarts(IndexMap m, string id, AreaData data)
        {
            var list = data.StartList();
            for (int k = 0; k < list.Count; k++)
            {
                var st = list[k];
                string thumb = Path.GetFullPath(AreaCatalog.ThumbPath(id, k));
                if (k > 0 && !File.Exists(thumb))
                {
                    Debug.LogWarning($"[Maps] {id}: no card image for start '{st.name}' – run SceneSnapshot.RenderThumbnailsBatch -area {id}");
                    thumb = "";
                }
                m.starts.Add(new IndexStart { name = st.name, x = st.position.x, z = st.position.z, yaw = st.yaw, thumb = k == 0 ? m.thumb : thumb });
            }
            foreach (var r in data.tileKeys.Select(WorldStreamer.RegionOf).Distinct().OrderBy(r => r.x).ThenBy(r => r.y))
            {
                var b = RegionContent.Bounds(r);
                foreach (var st in list)
                {
                    float dx = Mathf.Max(b.xMin - st.position.x, 0f, st.position.x - b.xMax);
                    float dz = Mathf.Max(b.yMin - st.position.z, 0f, st.position.z - b.yMax);
                    if (dx * dx + dz * dz > StartRadius * StartRadius) continue;
                    m.start.Add($"{r.x}_{r.y}");
                    break;
                }
            }
        }

        /// <summary>
        /// Reads the exported files back like the app does (area.bin → pack index → every entry out of its pack file by
        /// offset/length, hash check, tiles decoded): -executeMethod LeGamez.Editor.MapExport.VerifyBatch [-areas a,b]
        /// </summary>
        public static void VerifyBatch()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-areas");
            string[] only = i >= 0 && i + 1 < args.Length ? args[i + 1].Split(',') : null;
            bool ok = true;
            foreach (var dir in Directory.GetDirectories(OutDir))
            {
                string id = Path.GetFileName(dir), areaFile = Path.Combine(dir, "area.bin");
                if ((only != null && !only.Contains(id)) || !File.Exists(areaFile)) continue;
                var area = MapFormat.ReadArea(File.ReadAllBytes(areaFile));
                int bad = 0, tiles = 0;
                var files = new Dictionary<string, FileStream>();
                var keyOf = area.tileKeys.ToDictionary(MapFormat.TileEntry);
                try
                {
                    foreach (var kv in area.pack)
                    {
                        string file = kv.Value.file ?? MapStore.PackFile;
                        if (!files.TryGetValue(file, out var fs)) files[file] = fs = File.OpenRead(Path.Combine(dir, file));
                        var b = new byte[kv.Value.length];
                        fs.Position = kv.Value.offset;
                        int n = 0;
                        while (n < b.Length) { int k = fs.Read(b, n, b.Length - n); if (k <= 0) break; n += k; }
                        if (n != b.Length || MapFormat.Hash64(b) != kv.Value.hash) { bad++; continue; }
                        if (kv.Key.StartsWith("t/"))
                        {
                            var tile = MapFormat.DecodeTile(keyOf[kv.Key], b);
                            if (tile.parts.Count == 0 && tile.instances.Count == 0) bad++;
                            tiles++;
                        }
                    }
                }
                finally { foreach (var fs in files.Values) fs.Dispose(); }
                int missing = area.tileKeys.Count(k => !area.pack.ContainsKey(MapFormat.TileEntry(k)));
                Debug.Log($"[Maps] verify {id}: {area.pack.Count} entries in {files.Count} pack file(s), {tiles} tiles decoded, {bad} bad, {missing} tiles without entry");
                ok &= bad == 0 && missing == 0;
                if (area.mapTexture != null) Object.DestroyImmediate(area.mapTexture);
                Object.DestroyImmediate(area.pois);
                Object.DestroyImmediate(area.streets);
                Object.DestroyImmediate(area);
            }
            if (!ok && Application.isBatchMode) EditorApplication.Exit(1);
        }

        /// <summary>Overview JPEG of the area (SceneSnapshot.RenderThumbnails); areas not re-rendered since the region
        /// images came still have the old large PNG – shrunk to an overview here.</summary>
        static byte[] OverviewImage(string id)
        {
            string jpg = AreaCatalog.MapPath(id);
            if (File.Exists(jpg)) return File.ReadAllBytes(jpg);
            string png = Path.ChangeExtension(jpg, ".png");
            if (!File.Exists(png)) return null;
            var src = new Texture2D(2, 2);
            if (!src.LoadImage(File.ReadAllBytes(png))) { Object.DestroyImmediate(src); return null; }
            float s = Mathf.Min(1f, (float)SceneSnapshot.OverviewMax / Mathf.Max(src.width, src.height));
            int w = Mathf.RoundToInt(src.width * s), h = Mathf.RoundToInt(src.height * s);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var dst = new Texture2D(w, h, TextureFormat.RGB24, false);
            dst.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            dst.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var bytes = dst.EncodeToJPG(85);
            Object.DestroyImmediate(src);
            Object.DestroyImmediate(dst);
            Debug.LogWarning($"[Maps] {id}: no {jpg} – overview made from the old PNG, region minimaps missing (re-import)");
            return bytes;
        }
    }
}
