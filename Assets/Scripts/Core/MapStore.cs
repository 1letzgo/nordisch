using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace LeGamez.Core
{
    /// <summary>
    /// Downloadable maps. The app ships without any area: each town is an AssetBundle (scene + meshes + minimap) in the
    /// GitHub release "maps" of 1letzgo/nordisch, listed in its maps.json (Tools/release/publish_maps.sh writes both).
    ///
    /// Update logic:
    /// - The catalog is fetched once per app run (menu); the last copy is kept for offline starts.
    /// - A build fits this app only if platform, <see cref="Format"/> and Unity version match exactly – bundles are
    ///   tied to the engine version and to the serialized fields of our scripts. The catalog can hold builds for several
    ///   formats/engines side by side, so older apps keep finding theirs.
    /// - Newer fitting build than the installed one → "Update"; on Wi-Fi it is fetched automatically.
    ///   Installed build no longer fits (app updated) → must be re-downloaded before playing.
    /// - Download → size + SHA-256 check → recompress to LZ4 (fast loading) → swap in; the old version stays playable until then.
    /// In the editor the local scenes from Assets/Generated/AreaCatalog.asset are listed instead (nothing to download).
    /// </summary>
    public static class MapStore
    {
        /// <summary>Bump when map scenes need something only a newer app has (new script in map scenes, changed serialized
        /// fields of AreaInfo/TrafficLights/FacadeDetails/PoiDatabase/StreetIndex/WorldStreamer/WorldTile …). Then re-publish all maps.
        /// 1 = one scene bundle with all geometry; 2 = scene bundle + tiles bundle streamed by World/WorldStreamer;
        /// 3 = AreaData bundle (no scene, materials in the app) + tiles bundle, built in the one world scene.</summary>
        public const int Format = 3;

        public const string CatalogUrl = "https://github.com/1letzgo/nordisch/releases/download/maps/maps.json";
        const bool AutoUpdateOnWifi = true;

        public static string Platform =>
            Application.platform == RuntimePlatform.Android ? "android" :
            Application.platform == RuntimePlatform.IPhonePlayer ? "ios" : "editor";

        // ------------------------------------------------------------------ catalog format (maps.json)

        [Serializable]
        public class FileEntry
        {
            public string name; // file name in the map folder (map.bundle, tiles.bundle)
            public string url;
            public long size;
            public string sha256;
        }

        [Serializable]
        public class Build
        {
            public string platform;
            public int format;
            public string unity;
            public long version; // yyyyMMddHHmm of the build
            public string url;
            public long size;     // total of all files
            public string sha256;
            public FileEntry[] files = new FileEntry[0]; // format 2+; format 1 = just url/size/sha256 (map.bundle)

            public FileEntry[] AllFiles => files != null && files.Length > 0
                ? files
                : new[] { new FileEntry { name = "map.bundle", url = url, size = size, sha256 = sha256 } };
        }

        [Serializable]
        public class Entry
        {
            public string id;
            public string name;
            public double lat, lon;
            public string[] vehicles = new string[0];
            public int pois;
            public string thumb; // URL; the file name carries a version, so it doubles as cache key
            public Build[] builds = new Build[0];
        }

        [Serializable] class Catalog { public int schema; public Entry[] maps; }
        [Serializable] class Installed { public Entry entry; public Build build; }

        // ------------------------------------------------------------------ state

        public enum Status
        {
            Available,       // in the catalog, not downloaded
            Queued,
            Downloading,
            Installing,      // checking + recompressing
            Installed,
            UpdateAvailable, // installed and playable, newer build exists
            UpdateRequired,  // installed build does not fit this app any more, a fitting one exists
            Unavailable,     // nothing fits: the catalog only has builds for a newer app (format/engine) – update the app
            Pending,         // nothing fits: only builds for older apps – maps not re-published for this version yet
            Failed,
        }

        public class Map
        {
            public Entry entry;
            public Build installed;
            public Build latest; // best build for this app, null if none
            public Status status;
            public float progress;
            public string error;
            public Texture2D thumb;
            internal string thumbUrl;
            public string scenePath; // editor only: path of the AreaData asset

            public string Id => entry.id;
            /// <summary>Installed and fitting this app – also while a newer version downloads.</summary>
            public bool Playable => installed != null && Fits(installed);
            public bool Busy => status == Status.Queued || status == Status.Downloading || status == Status.Installing;
            public bool HasVehicle(string v) => Array.IndexOf(entry.vehicles ?? new string[0], v) >= 0;
        }

        public static readonly List<Map> Maps = new List<Map>();
        /// <summary>Increments whenever the list or a status changes (not on progress) – the menu rebuilds its cards then.</summary>
        public static int Revision { get; private set; }
        public static bool CatalogFetched { get; private set; }
        public static bool Fetching { get; private set; }

        static string Root => Path.Combine(Application.persistentDataPath, "maps");
        static string MapDir(string id) => Path.Combine(Root, id);
        static string BundlePath(string id) => Path.Combine(MapDir(id), "map.bundle");
        static string TilesPath(string id) => Path.Combine(MapDir(id), "tiles.bundle");
        static string InfoPath(string id) => Path.Combine(MapDir(id), "info.json");
        static string ThumbDir => Path.Combine(Root, "thumbs");
        static string CatalogCache => Path.Combine(Root, "catalog.json");

        static bool initialized;
        static Runner runner;
        static AssetBundle loaded, loadedTiles;
        static readonly Queue<Map> queue = new Queue<Map>();
        static bool working;
        static bool starting; // a map is being loaded – ignore further taps

        public static bool Fits(Build b) =>
            b != null && b.platform == Platform && b.format == Format && b.unity == Application.unityVersion;

        // ------------------------------------------------------------------ public API

        /// <summary>Main menu start: frees the last map's bundle, lists installed maps and fetches the catalog (once per run).</summary>
        public static void Init()
        {
            UnloadLoaded();
            if (!initialized)
            {
                initialized = true;
#if UNITY_EDITOR
                LoadEditorCatalog();
                return;
#else
                Directory.CreateDirectory(Root);
                ExcludeFromBackup(Root);
                Catalog cached = null;
                try { if (File.Exists(CatalogCache)) cached = JsonUtility.FromJson<Catalog>(File.ReadAllText(CatalogCache)); }
                catch (Exception e) { Debug.LogWarning("[Maps] cached catalog unreadable: " + e.Message); }
                Merge(cached);
#endif
            }
#if !UNITY_EDITOR
            if (!CatalogFetched && !Fetching) Run(FetchCatalog());
#endif
        }

        public static void Download(Map m)
        {
            if (m == null || m.Busy || m.latest == null) return;
            if (m.installed != null && m.latest.version <= m.installed.version && Fits(m.installed)) return;
            m.status = Status.Queued;
            m.error = null;
            queue.Enqueue(m);
            Changed();
            if (!working) Run(Worker());
        }

        public static void Delete(Map m)
        {
            if (m == null || m.Busy) return;
            try { if (Directory.Exists(MapDir(m.Id))) Directory.Delete(MapDir(m.Id), true); }
            catch (Exception e) { Debug.LogWarning($"[Maps] delete {m.Id}: {e.Message}"); }
            m.installed = null;
            if (m.entry.builds == null || m.entry.builds.Length == 0) Maps.Remove(m); // only known locally
            Evaluate(m);
            Changed();
        }

        /// <summary>Loads the map's scene (from its bundle on devices, from the project in the editor).</summary>
        public static void Play(Map m)
        {
            if (m == null || !m.Playable || starting) return;
#if UNITY_EDITOR
            World.AreaLoader.Pending = UnityEditor.AssetDatabase.LoadAssetAtPath<World.AreaData>(m.scenePath);
            SceneManager.LoadSceneAsync(World.AreaLoader.WorldSceneIndex);
#else
            Run(PlayRoutine(m));
#endif
        }

        /// <summary>Releases the bundle of the map that was played (the menu scene has replaced its scene by now).</summary>
        public static void UnloadLoaded()
        {
            World.WorldStreamer.Bundle = null;
            if (loadedTiles != null) { loadedTiles.Unload(true); loadedTiles = null; }
            if (loaded == null) return;
            loaded.Unload(true);
            loaded = null;
        }

        // ------------------------------------------------------------------ internals

        static void Changed() => Revision++;

        static void Run(IEnumerator routine)
        {
            if (runner == null)
            {
                runner = new GameObject("MapStore").AddComponent<Runner>();
                UnityEngine.Object.DontDestroyOnLoad(runner.gameObject);
            }
            runner.StartCoroutine(routine);
        }

        class Runner : MonoBehaviour { }

        /// <summary>Best build of an entry for this app.</summary>
        static Build BestBuild(Entry e)
        {
            Build best = null;
            if (e.builds != null)
                foreach (var b in e.builds)
                    if (Fits(b) && !string.IsNullOrEmpty(b.url) && (best == null || b.version > best.version)) best = b;
            return best;
        }

        static bool HasPlatform(Entry e)
        {
            if (e.builds != null) foreach (var b in e.builds) if (b != null && b.platform == Platform) return true;
            return false;
        }

        /// <summary>A build for this platform exists but needs a newer app (higher format).</summary>
        static bool NewerAppNeeded(Entry e)
        {
            if (e.builds != null)
                foreach (var b in e.builds)
                    if (b != null && b.platform == Platform && b.format > Format) return true;
            return false;
        }

        static void Evaluate(Map m)
        {
            if (m.Busy) return;
            m.latest = BestBuild(m.entry);
            if (m.latest == null && (m.installed == null || !Fits(m.installed))) m.status = NewerAppNeeded(m.entry) ? Status.Unavailable : Status.Pending;
            else if (m.installed == null) m.status = Status.Available;
            else if (!Fits(m.installed)) m.status = Status.UpdateRequired;
            else m.status = m.latest != null && m.latest.version > m.installed.version ? Status.UpdateAvailable : Status.Installed;
        }

        /// <summary>Installed maps (from disk) + catalog entries → <see cref="Maps"/>, sorted north to south like before.</summary>
        static void Merge(Catalog catalog)
        {
            var byId = new Dictionary<string, Map>();
            foreach (var m in Maps) byId[m.Id] = m;

            // Installed maps first – they stay listed even when the catalog dropped them or is unreachable.
            if (Directory.Exists(Root))
                foreach (var dir in Directory.GetDirectories(Root))
                {
                    string id = Path.GetFileName(dir);
                    if (byId.ContainsKey(id) || !File.Exists(InfoPath(id)) || !File.Exists(BundlePath(id))) continue;
                    try
                    {
                        var info = JsonUtility.FromJson<Installed>(File.ReadAllText(InfoPath(id)));
                        if (info?.entry == null || info.build == null) continue;
                        var m = new Map { entry = info.entry, installed = info.build };
                        m.entry.builds = new Build[0];
                        byId[id] = m;
                        Maps.Add(m);
                    }
                    catch (Exception e) { Debug.LogWarning($"[Maps] {id}/info.json unreadable: {e.Message}"); }
                }

            if (catalog?.maps != null)
                foreach (var e in catalog.maps)
                {
                    if (string.IsNullOrEmpty(e?.id)) continue;
                    if (!byId.TryGetValue(e.id, out var m))
                    {
                        if (!HasPlatform(e)) continue; // not built for this platform – don't advertise it
                        m = new Map();
                        byId[e.id] = m;
                        Maps.Add(m);
                    }
                    m.entry = e;
                }

            // Dropped from the catalog and not installed: gone.
            if (catalog?.maps != null)
            {
                var listed = new HashSet<string>();
                foreach (var e in catalog.maps) if (e?.id != null) listed.Add(e.id);
                Maps.RemoveAll(m => m.installed == null && !m.Busy && !listed.Contains(m.Id));
            }

            foreach (var m in Maps) Evaluate(m);
            Maps.Sort((a, b) => b.entry.lat.CompareTo(a.entry.lat));
            foreach (var m in Maps) if (m.thumb == null || m.thumbUrl != m.entry.thumb) Run(LoadThumb(m));
            Changed();

            if (AutoUpdateOnWifi && Application.internetReachability == NetworkReachability.ReachableViaLocalAreaNetwork)
                foreach (var m in Maps)
                    if (m.status == Status.UpdateAvailable || m.status == Status.UpdateRequired) Download(m);
        }

        static IEnumerator FetchCatalog()
        {
            Fetching = true;
            // Testing: maps/catalog-url.txt on the device points the app at another catalog (e.g. a file:// copy).
            string url = CatalogUrl;
            try { string o = Path.Combine(Root, "catalog-url.txt"); if (File.Exists(o)) url = File.ReadAllText(o).Trim(); }
            catch (Exception) { }
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("Cache-Control", "no-cache");
                req.timeout = 20;
                yield return req.SendWebRequest();
                Fetching = false;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[Maps] catalog: " + req.error); // offline: installed maps stay playable, retry next menu visit
                    Changed();
                    yield break;
                }
                Catalog catalog;
                try { catalog = JsonUtility.FromJson<Catalog>(req.downloadHandler.text); }
                catch (Exception e) { Debug.LogWarning("[Maps] catalog unreadable: " + e.Message); Changed(); yield break; }
                CatalogFetched = true;
                try { File.WriteAllText(CatalogCache, req.downloadHandler.text); } catch (Exception) { }
                Merge(catalog);
            }
        }

        static IEnumerator LoadThumb(Map m)
        {
            string url = m.entry.thumb;
            m.thumbUrl = url;
            string local = m.installed != null ? Path.Combine(MapDir(m.Id), "thumb.png") : null;
            string cached = ThumbCache(url);
            string path = cached != null && File.Exists(cached) ? cached : local != null && File.Exists(local) ? local : null;
            if (path == null && cached != null)
            {
                Directory.CreateDirectory(ThumbDir);
                using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET) { downloadHandler = new DownloadHandlerFile(cached) { removeFileOnAbort = true } };
                req.timeout = 20;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) path = cached;
            }
            if (path == null) yield break;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(File.ReadAllBytes(path)))
            {
                if (m.thumb != null) UnityEngine.Object.Destroy(m.thumb);
                m.thumb = tex;
                Changed();
            }
            else UnityEngine.Object.Destroy(tex);
        }

        static IEnumerator Worker()
        {
            working = true;
            while (queue.Count > 0)
            {
                var m = queue.Dequeue();
                yield return Install(m);
            }
            working = false;
        }

        static IEnumerator Install(Map m)
        {
            var b = m.latest;
            var files = b.AllFiles;
            string tmpDir = Path.Combine(Root, ".download");
            Directory.CreateDirectory(tmpDir);
            long total = 0, done = 0, downloaded = 0;
            foreach (var f in files) total += Math.Max(1, f.size);
            var ready = new List<(string lz4, string name)>();

            m.status = Status.Downloading;
            m.progress = 0f;
            Changed();
            foreach (var f in files)
            {
                string part = Path.Combine(tmpDir, $"{m.Id}.{f.name}.part"), lz4 = Path.Combine(tmpDir, $"{m.Id}.{f.name}.lz4");
                TryDelete(part); TryDelete(lz4);
                if (m.status != Status.Downloading) { m.status = Status.Downloading; Changed(); }
                using (var req = new UnityWebRequest(f.url, UnityWebRequest.kHttpVerbGET))
                {
                    req.downloadHandler = new DownloadHandlerFile(part) { removeFileOnAbort = true };
                    var op = req.SendWebRequest();
                    while (!op.isDone) { m.progress = (done + req.downloadProgress * Math.Max(1, f.size)) / total; yield return null; }
                    if (req.result != UnityWebRequest.Result.Success) { Fail(m, "Download fehlgeschlagen", req.error); Cleanup(ready); yield break; }
                }
                done += Math.Max(1, f.size);

                m.status = Status.Installing;
                Changed();
                long size = new FileInfo(part).Length;
                downloaded += size;
                if (f.size > 0 && size != f.size) { Fail(m, "Download unvollständig", $"{f.name}: {size} != {f.size}"); TryDelete(part); Cleanup(ready); yield break; }
                if (!string.IsNullOrEmpty(f.sha256))
                {
                    var hash = Task.Run(() => Sha256(part));
                    while (!hash.IsCompleted) yield return null;
                    if (hash.IsFaulted || !string.Equals(hash.Result, f.sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Fail(m, "Download beschädigt", $"{f.name}: SHA-256 mismatch");
                        TryDelete(part); Cleanup(ready);
                        yield break;
                    }
                }
                // Published bundles are LZMA (small download); LZ4 on disk loads fast and single assets stream from file.
                var recompress = AssetBundle.RecompressAssetBundleAsync(part, lz4, BuildCompression.LZ4Runtime);
                yield return recompress;
                TryDelete(part);
                if (!recompress.success) { Fail(m, "Karte nicht lesbar", recompress.humanReadableResult); TryDelete(lz4); Cleanup(ready); yield break; }
                ready.Add((lz4, f.name));
            }

            try
            {
                Directory.CreateDirectory(MapDir(m.Id));
                TryDelete(BundlePath(m.Id));
                TryDelete(TilesPath(m.Id)); // a format-1 map has none – don't leave an old one behind
                foreach (var (lz4, name) in ready)
                {
                    string target = Path.Combine(MapDir(m.Id), name);
                    TryDelete(target);
                    File.Move(lz4, target);
                    ExcludeFromBackup(target);
                }
                var info = new Installed { entry = m.entry, build = b };
                File.WriteAllText(InfoPath(m.Id), JsonUtility.ToJson(info));
                string cached = ThumbCache(m.entry.thumb);
                if (cached != null && File.Exists(cached)) File.Copy(cached, Path.Combine(MapDir(m.Id), "thumb.png"), true);
                ExcludeFromBackup(MapDir(m.Id));
            }
            catch (Exception e) { Fail(m, "Speichern fehlgeschlagen", e.Message); Cleanup(ready); yield break; }

            m.installed = b;
            m.status = Status.Installed; // leaves Busy so Evaluate runs
            Evaluate(m);
            Changed();
            Debug.Log($"[Maps] {m.Id} {b.version} installed ({downloaded / (1024 * 1024)} MB download, {files.Length} file(s))");
        }

        static void Cleanup(List<(string lz4, string name)> ready)
        {
            foreach (var (lz4, _) in ready) TryDelete(lz4);
        }

        static IEnumerator PlayRoutine(Map m)
        {
            starting = true;
            UnloadLoaded();
            // Tiles first: the scene's WorldStreamer asks for them as soon as it wakes up.
            if (File.Exists(TilesPath(m.Id)))
            {
                var tilesReq = AssetBundle.LoadFromFileAsync(TilesPath(m.Id));
                yield return tilesReq;
                loadedTiles = tilesReq.assetBundle;
                World.WorldStreamer.Bundle = loadedTiles;
            }
            var req = AssetBundle.LoadFromFileAsync(BundlePath(m.Id));
            yield return req;
            loaded = req.assetBundle;
            World.AreaData data = null;
            if (loaded != null)
            {
                var areq = loaded.LoadAllAssetsAsync<World.AreaData>();
                yield return areq;
                data = areq.asset as World.AreaData;
            }
            if (data == null)
            {
                starting = false;
                UnloadLoaded();
                Fail(m, "Karte beschädigt – bitte löschen und neu laden", "bundle without AreaData");
                yield break;
            }
            World.AreaLoader.Pending = data;
            yield return SceneManager.LoadSceneAsync(World.AreaLoader.WorldSceneIndex);
            starting = false;
        }

        static void Fail(Map m, string message, string detail)
        {
            Debug.LogWarning($"[Maps] {m.Id}: {message} ({detail})");
            m.status = Status.Failed;
            m.error = message;
            Changed();
        }

        /// <summary>Local cache file of a thumbnail URL (its file name carries the version).</summary>
        static string ThumbCache(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            return Path.Combine(ThumbDir, Path.GetFileName(uri.AbsolutePath));
        }

        static string Sha256(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        /// <summary>Re-downloadable data must not go into the iCloud backup (App Store rule).</summary>
        static void ExcludeFromBackup(string path)
        {
#if UNITY_IOS && !UNITY_EDITOR
            UnityEngine.iOS.Device.SetNoBackupFlag(path);
#endif
        }

        /// <summary>Human-readable size ("143 MB").</summary>
        public static string Megabytes(long bytes) => $"{Mathf.Max(1, Mathf.RoundToInt(bytes / (1024f * 1024f)))} MB";

#if UNITY_EDITOR
        static void LoadEditorCatalog()
        {
            var catalog = UnityEditor.AssetDatabase.LoadAssetAtPath<World.AreaCatalog>(World.AreaCatalog.AssetPath);
            if (catalog == null) { Changed(); return; }
            var editorBuild = new Build { platform = "editor", format = Format, unity = Application.unityVersion };
            foreach (var a in catalog.areas)
            {
                var m = new Map
                {
                    entry = new Entry { id = a.id, name = a.displayName, lat = a.lat, lon = a.lon, vehicles = a.vehicles.ToArray(), pois = a.poiCount },
                    installed = editorBuild,
                    status = Status.Installed,
                    scenePath = $"Assets/Generated/{a.id}/{a.id}_area.asset", // AreaData (OsmAreaImporter.AreaDataPath)
                    thumb = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(World.AreaCatalog.ThumbPath(a.id)),
                };
                Maps.Add(m);
            }
            Changed();
        }
#endif
    }
}
