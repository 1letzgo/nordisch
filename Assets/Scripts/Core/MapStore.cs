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
    /// Downloadable maps. The app ships without any area. Each area = map.bundle (World/AreaData: spawn, POIs, streets,
    /// traffic, facade data, minimap) + one bundle per 1 km region with its world tiles (r&lt;rx&gt;_&lt;rz&gt;.bundle), published
    /// by Tools/release/publish_maps.sh to GitHub releases and listed in maps.json (release "maps").
    ///
    /// Only what is needed lives on the device:
    /// - "Laden" fetches map.bundle + the regions around the start (build.start); everything else is fetched on demand
    ///   while playing (<see cref="RegionTileSource"/>: tiles the streamer needs + prefetch ~1.5 km ahead).
    /// - Region files are an LRU cache with <see cref="CacheLimit"/>; least recently used ones are dropped at the next start.
    ///
    /// Update logic:
    /// - The catalog is fetched once per app run (menu); the last copy is kept for offline starts.
    /// - A build fits this app only if platform, <see cref="Format"/> and Unity version match exactly. The catalog can hold
    ///   builds for several formats/engines side by side, so older apps keep finding theirs.
    /// - Newer fitting build: only files whose SHA-256 changed are fetched (area data + regions already on the device);
    ///   regions not on the device simply come from the new build when needed. On Wi-Fi updates run automatically.
    /// - Download → size + SHA-256 check → recompress to LZ4 (fast loading, single assets stream from file) → swap in.
    /// In the editor the local AreaData assets from Assets/Generated are listed instead (nothing to download).
    /// </summary>
    public static class MapStore
    {
        /// <summary>Bump when maps need something only a newer app has (new/changed serialized fields of AreaData, WorldTile,
        /// PoiDatabase, StreetIndex …) or the package layout changes. Then re-publish all maps.
        /// 1 = scene bundle; 2 = scene + tiles bundle; 3 = AreaData + tiles bundle; 4 = AreaData + one bundle per 1 km region.</summary>
        public const int Format = 4;

        public const string CatalogUrl = "https://github.com/1letzgo/nordisch/releases/download/maps/maps.json";
        /// <summary>Max. size of downloaded regions on the device (area data files don't count).</summary>
        public const long CacheLimit = 1024L * 1024 * 1024;
        const bool AutoUpdateOnWifi = true;
        const string AreaFile = "map.bundle";

        public static string Platform =>
            Application.platform == RuntimePlatform.Android ? "android" :
            Application.platform == RuntimePlatform.IPhonePlayer ? "ios" : "editor";

        // ------------------------------------------------------------------ catalog format (maps.json)

        [Serializable]
        public class FileEntry
        {
            public string name;   // file name in the map folder (map.bundle, r<rx>_<rz>.bundle)
            public string region; // "rx_rz" for region bundles, empty for the area bundle
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
            public string[] start = new string[0];       // regions around the start, fetched with "Laden"

            public FileEntry[] AllFiles => files != null && files.Length > 0
                ? files
                : new[] { new FileEntry { name = AreaFile, url = url, size = size, sha256 = sha256 } };

            public FileEntry File(string name)
            {
                foreach (var f in AllFiles) if (f.name == name) return f;
                return null;
            }

            /// <summary>Download size for a first install: area data + start regions.</summary>
            public long StartSize
            {
                get
                {
                    long n = 0;
                    foreach (var f in AllFiles)
                        if (string.IsNullOrEmpty(f.region) || Array.IndexOf(start ?? new string[0], f.region) >= 0) n += f.size;
                    return n;
                }
            }
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

        /// <summary>maps/&lt;id&gt;/info.json: catalog entry + build the files on disk belong to + what is on disk.</summary>
        [Serializable]
        internal class Installed
        {
            public Entry entry;
            public Build build;
            public List<FileEntry> files = new List<FileEntry>();
        }

        [Serializable] class Usage { public List<string> keys = new List<string>(); public List<long> times = new List<long>(); }

        // ------------------------------------------------------------------ state

        public enum Status
        {
            Available,       // in the catalog, not downloaded
            Queued,
            Downloading,
            Installing,      // checking + recompressing
            Installed,
            UpdateAvailable, // installed and playable, newer files exist
            UpdateRequired,  // installed build does not fit this app any more, a fitting one exists
            Unavailable,     // nothing fits: the catalog only has builds for a newer app (format/engine) – update the app
            Pending,         // nothing fits: only builds for older apps – maps not re-published for this version yet
            Failed,
        }

        public class Map
        {
            public Entry entry;
            public Build installed;
            internal Installed info; // files on disk (devices only)
            public Build latest;     // best build for this app, null if none
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
            /// <summary>What the next download costs: first install = area data + start regions, update = changed files.</summary>
            public long DownloadSize
            {
                get
                {
                    if (latest == null) return 0;
                    if (!Playable) return latest.StartSize;
                    long n = 0;
                    foreach (var f in ChangedFiles(this)) n += f.size;
                    return n;
                }
            }
        }

        public static readonly List<Map> Maps = new List<Map>();
        /// <summary>Increments whenever the list or a status changes (not on progress) – the menu rebuilds its cards then.</summary>
        public static int Revision { get; private set; }
        public static bool CatalogFetched { get; private set; }
        public static bool Fetching { get; private set; }
        /// <summary>The map being played (its regions are fetched on demand).</summary>
        public static Map Current { get; private set; }

        static string Root => Path.Combine(Application.persistentDataPath, "maps");
        static string MapDir(string id) => Path.Combine(Root, id);
        static string FilePath(string id, string name) => Path.Combine(MapDir(id), name);
        static string InfoPath(string id) => Path.Combine(MapDir(id), "info.json");
        static string ThumbDir => Path.Combine(Root, "thumbs");
        static string CatalogCache => Path.Combine(Root, "catalog.json");
        static string UsagePath => Path.Combine(Root, "usage.json");
        public static string RegionName(string region) => $"r{region}.bundle";

        static bool initialized;
        static Runner runner;
        static AssetBundle loaded;
        static RegionTileSource loadedSource;
        static readonly Queue<Map> queue = new Queue<Map>();
        static bool working;
        static bool starting; // a map is being loaded – ignore further taps
        static readonly Dictionary<string, long> usage = new Dictionary<string, long>();

        public static bool Fits(Build b) =>
            b != null && b.platform == Platform && b.format == Format && b.unity == Application.unityVersion;

        // ------------------------------------------------------------------ public API

        /// <summary>Main menu start: frees the last map's bundles, lists installed maps and fetches the catalog (once per run).</summary>
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
                LoadUsage();
                Catalog cached = null;
                try { if (File.Exists(CatalogCache)) cached = JsonUtility.FromJson<Catalog>(File.ReadAllText(CatalogCache)); }
                catch (Exception e) { Debug.LogWarning("[Maps] cached catalog unreadable: " + e.Message); }
                Merge(cached);
                TrimCache();
#endif
            }
#if !UNITY_EDITOR
            if (!CatalogFetched && !Fetching) Run(FetchCatalog());
#endif
        }

        public static void Download(Map m)
        {
            if (m == null || m.Busy || m.latest == null) return;
            if (m.Playable && ChangedFiles(m).Count == 0) return;
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
            m.info = null;
            if (m.entry.builds == null || m.entry.builds.Length == 0) Maps.Remove(m); // only known locally
            Evaluate(m);
            Changed();
        }

        /// <summary>Loads the area data and enters the world scene (regions follow on demand). Also switches areas while
        /// playing: the world scene is reloaded with the new area, the old one is released afterwards.</summary>
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

        /// <summary>Releases the bundles of the map that was played (the menu scene has replaced the world by now).</summary>
        public static void UnloadLoaded()
        {
            World.WorldStreamer.Source = null;
            Current = null;
            Release(ref loaded, ref loadedSource);
            SaveUsage();
        }

        static void Release(ref AssetBundle bundle, ref RegionTileSource source)
        {
            source?.UnloadAll();
            source = null;
            if (bundle != null) bundle.Unload(true);
            bundle = null;
        }

        // ------------------------------------------------------------------ regions on demand (RegionTileSource)

        static readonly Dictionary<string, IEnumerator> regionJobs = new Dictionary<string, IEnumerator>();
        static readonly Dictionary<string, float> regionFailedAt = new Dictionary<string, float>();

        public static string RegionPath(string id, string region) => FilePath(id, RegionName(region));
        public static bool HasRegion(string id, string region) => File.Exists(RegionPath(id, region));

        /// <summary>True if fetching the region failed in the last 20 s (offline, missing on the server).</summary>
        public static bool RegionFailed(string id, string region) =>
            regionFailedAt.TryGetValue($"{id}/{region}", out var t) && Time.realtimeSinceStartup - t < 20f;

        /// <summary>Downloads one region of the current build (waits for a download already running).</summary>
        public static IEnumerator FetchRegion(Map m, string region)
        {
            string key = $"{m.Id}/{region}";
            if (HasRegion(m.Id, region) || RegionFailed(m.Id, region)) yield break;
            while (regionJobs.ContainsKey(key)) yield return null; // someone else is fetching it
            if (HasRegion(m.Id, region)) yield break;
            var f = m.installed?.File(RegionName(region));
            if (f == null) { regionFailedAt[key] = Time.realtimeSinceStartup; yield break; } // not part of the map
            regionJobs[key] = null;
            string error = null;
            yield return DownloadFile(m, f, null, e => error = e);
            regionJobs.Remove(key);
            if (error != null)
            {
                regionFailedAt[key] = Time.realtimeSinceStartup;
                Debug.LogWarning($"[Maps] region {key}: {error}");
            }
        }

        internal static void TouchRegion(string id, string region) => usage[$"{id}/{region}"] = DateTime.UtcNow.Ticks;

        // ------------------------------------------------------------------ internals

        static void Changed() => Revision++;

        internal static void Run(IEnumerator routine)
        {
            if (runner == null)
            {
                runner = new GameObject("MapStore").AddComponent<Runner>();
                UnityEngine.Object.DontDestroyOnLoad(runner.gameObject);
            }
            runner.StartCoroutine(routine);
        }

        class Runner : MonoBehaviour
        {
            void OnApplicationPause(bool pause) { if (pause) SaveUsage(); }
            void OnApplicationQuit() => SaveUsage();
        }

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

        /// <summary>Files of the latest build that are on the device in an older version (area data always counts).</summary>
        static List<FileEntry> ChangedFiles(Map m)
        {
            var list = new List<FileEntry>();
            if (m.latest == null) return list;
            foreach (var f in m.latest.AllFiles)
            {
                bool area = string.IsNullOrEmpty(f.region);
                FileEntry have = null;
                if (m.info != null) foreach (var h in m.info.files) if (h.name == f.name) { have = h; break; }
                if (have == null) { if (area) list.Add(f); continue; }
                if (have.sha256 != f.sha256) list.Add(f);
            }
            return list;
        }

        static void Evaluate(Map m)
        {
            if (m.Busy) return;
            m.latest = BestBuild(m.entry);
            if (m.latest == null && (m.installed == null || !Fits(m.installed))) m.status = NewerAppNeeded(m.entry) ? Status.Unavailable : Status.Pending;
            else if (m.installed == null) m.status = Status.Available;
            else if (!Fits(m.installed)) m.status = Status.UpdateRequired;
            else if (m.latest != null && m.latest.version > m.installed.version)
            {
                if (ChangedFiles(m).Count > 0) m.status = Status.UpdateAvailable;
                else
                {
                    // Nothing on the device changed: adopt the new build, regions not yet fetched come from it.
                    m.installed = m.latest;
                    SaveInfo(m);
                    m.status = Status.Installed;
                }
            }
            else m.status = Status.Installed;
        }

        static void SaveInfo(Map m)
        {
            if (m.info == null) return;
            m.info.entry = m.entry;
            m.info.build = m.installed;
            try { File.WriteAllText(InfoPath(m.Id), JsonUtility.ToJson(m.info)); }
            catch (Exception e) { Debug.LogWarning($"[Maps] {m.Id}/info.json: {e.Message}"); }
        }

        /// <summary>Installed maps (from disk) + catalog entries → <see cref="Maps"/>, sorted north to south.</summary>
        static void Merge(Catalog catalog)
        {
            var byId = new Dictionary<string, Map>();
            foreach (var m in Maps) byId[m.Id] = m;

            // Installed maps first – they stay listed even when the catalog dropped them or is unreachable.
            if (Directory.Exists(Root))
                foreach (var dir in Directory.GetDirectories(Root))
                {
                    string id = Path.GetFileName(dir);
                    if (byId.ContainsKey(id) || !File.Exists(InfoPath(id)) || !File.Exists(FilePath(id, AreaFile))) continue;
                    try
                    {
                        var info = JsonUtility.FromJson<Installed>(File.ReadAllText(InfoPath(id)));
                        if (info?.entry == null || info.build == null) continue;
                        info.files ??= new List<FileEntry>();
                        info.files.RemoveAll(f => !File.Exists(FilePath(id, f.name))); // evicted regions
                        var m = new Map { entry = info.entry, installed = info.build, info = info };
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

        /// <summary>First install (area data + start regions) or update (files on the device whose content changed).</summary>
        static IEnumerator Install(Map m)
        {
            var b = m.latest;
            bool update = m.Playable;
            var files = new List<FileEntry>();
            if (update) files.AddRange(ChangedFiles(m));
            else
                foreach (var f in b.AllFiles)
                    if (string.IsNullOrEmpty(f.region) || Array.IndexOf(b.start ?? new string[0], f.region) >= 0) files.Add(f);
            long total = 0, done = 0;
            foreach (var f in files) total += Math.Max(1, f.size);

            if (!update)
            {
                // Fresh start: nothing of an older install (other format) must stay.
                try { if (Directory.Exists(MapDir(m.Id))) Directory.Delete(MapDir(m.Id), true); } catch (Exception) { }
                m.info = new Installed();
            }
            Directory.CreateDirectory(MapDir(m.Id));
            m.status = Status.Downloading;
            m.progress = 0f;
            Changed();
            foreach (var f in files)
            {
                string error = null;
                long before = done;
                yield return DownloadFile(m, f, p => m.progress = (before + p * Math.Max(1, f.size)) / total, e => error = e);
                if (error != null) { Fail(m, error.StartsWith("SHA") ? "Download beschädigt" : "Download fehlgeschlagen", error); yield break; }
                done += Math.Max(1, f.size);
                if (m.status != Status.Downloading) { m.status = Status.Downloading; Changed(); }
            }

            try
            {
                m.installed = b;
                string cached = ThumbCache(m.entry.thumb);
                if (cached != null && File.Exists(cached)) File.Copy(cached, Path.Combine(MapDir(m.Id), "thumb.png"), true);
                ExcludeFromBackup(MapDir(m.Id));
                SaveInfo(m);
            }
            catch (Exception e) { Fail(m, "Speichern fehlgeschlagen", e.Message); yield break; }

            m.status = Status.Installed; // leaves Busy so Evaluate runs
            Evaluate(m);
            Changed();
            Debug.Log($"[Maps] {m.Id} {b.version} {(update ? "updated" : "installed")} ({total / (1024 * 1024)} MB, {files.Count} file(s))");
        }

        /// <summary>Download → size/SHA-256 check → LZ4 → into the map folder, recorded in info.json.</summary>
        static IEnumerator DownloadFile(Map m, FileEntry f, Action<float> progress, Action<string> fail)
        {
            string tmpDir = Path.Combine(Root, ".download");
            Directory.CreateDirectory(tmpDir);
            string part = Path.Combine(tmpDir, $"{m.Id}.{f.name}.part"), lz4 = Path.Combine(tmpDir, $"{m.Id}.{f.name}.lz4");
            TryDelete(part); TryDelete(lz4);
            using (var req = new UnityWebRequest(f.url, UnityWebRequest.kHttpVerbGET))
            {
                req.downloadHandler = new DownloadHandlerFile(part) { removeFileOnAbort = true };
                var op = req.SendWebRequest();
                while (!op.isDone) { progress?.Invoke(req.downloadProgress); yield return null; }
                if (req.result != UnityWebRequest.Result.Success) { fail(req.error); yield break; }
            }
            long size = new FileInfo(part).Length;
            if (f.size > 0 && size != f.size) { TryDelete(part); fail($"size {f.name}: {size} != {f.size}"); yield break; }
            if (!string.IsNullOrEmpty(f.sha256))
            {
                var hash = Task.Run(() => Sha256(part));
                while (!hash.IsCompleted) yield return null;
                if (hash.IsFaulted || !string.Equals(hash.Result, f.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(part);
                    fail($"SHA-256 mismatch {f.name}");
                    yield break;
                }
            }
            // Published bundles are LZMA (small download); LZ4 on disk loads fast and single assets stream from file.
            var recompress = AssetBundle.RecompressAssetBundleAsync(part, lz4, BuildCompression.LZ4Runtime);
            yield return recompress;
            TryDelete(part);
            if (!recompress.success) { TryDelete(lz4); fail("unreadable " + recompress.humanReadableResult); yield break; }
            try
            {
                Directory.CreateDirectory(MapDir(m.Id));
                string target = FilePath(m.Id, f.name);
                TryDelete(target);
                File.Move(lz4, target);
                ExcludeFromBackup(target);
                m.info ??= new Installed();
                m.info.files.RemoveAll(x => x.name == f.name);
                m.info.files.Add(f);
                if (!string.IsNullOrEmpty(f.region)) TouchRegion(m.Id, f.region);
                if (m.installed != null) SaveInfo(m); // regions on demand: record right away
            }
            catch (Exception e) { TryDelete(lz4); fail(e.Message); }
        }

        static IEnumerator PlayRoutine(Map m)
        {
            starting = true;
            // Area switch while playing: the current area stays loaded until the new world scene has replaced it.
            var oldBundle = loaded;
            var oldSource = loadedSource;
            loaded = null;
            loadedSource = null;
            var req = AssetBundle.LoadFromFileAsync(FilePath(m.Id, AreaFile));
            yield return req;
            var bundle = req.assetBundle;
            World.AreaData data = null;
            if (bundle != null)
            {
                var areq = bundle.LoadAllAssetsAsync<World.AreaData>();
                yield return areq;
                data = areq.asset as World.AreaData;
            }
            if (data == null)
            {
                starting = false;
                if (bundle != null) bundle.Unload(true);
                loaded = oldBundle;
                loadedSource = oldSource;
                Fail(m, "Karte beschädigt – bitte löschen und neu laden", "bundle without AreaData");
                yield break;
            }
            Current = m;
            loaded = bundle;
            loadedSource = new RegionTileSource(m);
            World.WorldStreamer.Source = loadedSource;
            World.AreaLoader.Pending = data;
            yield return SceneManager.LoadSceneAsync(World.AreaLoader.WorldSceneIndex);
            Release(ref oldBundle, ref oldSource);
            SaveUsage();
            starting = false;
        }

        // ------------------------------------------------------------------ cache (LRU over region files)

        static void LoadUsage()
        {
            usage.Clear();
            try
            {
                if (!File.Exists(UsagePath)) return;
                var u = JsonUtility.FromJson<Usage>(File.ReadAllText(UsagePath));
                for (int i = 0; i < u.keys.Count && i < u.times.Count; i++) usage[u.keys[i]] = u.times[i];
            }
            catch (Exception) { }
        }

        static void SaveUsage()
        {
            if (Application.isEditor) return;
            var u = new Usage();
            foreach (var kv in usage) { u.keys.Add(kv.Key); u.times.Add(kv.Value); }
            try { File.WriteAllText(UsagePath, JsonUtility.ToJson(u)); } catch (Exception) { }
        }

        /// <summary>Drops least recently used region files until all regions together fit <see cref="CacheLimit"/>.</summary>
        static void TrimCache()
        {
            var regions = new List<(string id, string region, long size, long used)>();
            long total = 0;
            foreach (var m in Maps)
            {
                if (m.info == null) continue;
                foreach (var f in m.info.files)
                {
                    if (string.IsNullOrEmpty(f.region)) continue;
                    var fi = new FileInfo(FilePath(m.Id, f.name));
                    if (!fi.Exists) continue;
                    usage.TryGetValue($"{m.Id}/{f.region}", out long used);
                    regions.Add((m.Id, f.region, fi.Length, used));
                    total += fi.Length;
                }
            }
            if (total <= CacheLimit) return;
            regions.Sort((a, b) => a.used.CompareTo(b.used));
            foreach (var r in regions)
            {
                if (total <= CacheLimit * 0.9) break;
                TryDelete(FilePath(r.id, RegionName(r.region)));
                total -= r.size;
                usage.Remove($"{r.id}/{r.region}");
                var m = Maps.Find(x => x.Id == r.id);
                if (m?.info != null) { m.info.files.RemoveAll(f => f.region == r.region); SaveInfo(m); }
            }
            SaveUsage();
            Debug.Log($"[Maps] cache trimmed to {total / (1024 * 1024)} MB");
        }

        // ------------------------------------------------------------------ helpers

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
