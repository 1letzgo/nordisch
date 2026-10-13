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
    /// Builds the download bundles of each area for the active platform into Builds/Maps/&lt;platform&gt;/, plus index.json for
    /// Tools/release/publish_maps.py: &lt;id&gt;.bundle = the area's AreaData with what it references (POIs, streets, facade
    /// data, minimap texture), &lt;id&gt;-r&lt;rx&gt;_&lt;rz&gt;.bundle = one per 1 km region with its world tiles – the unit
    /// the app downloads on demand and updates individually. Materials and
    /// shaders stay in the app (GameMaterials) – the world scene builds the area at runtime (AreaLoader). Bundles are LZMA (small download); the app recompresses them to LZ4.
    /// Batch: Unity -batchmode -quit -projectPath . -buildTarget iOS|Android -executeMethod LeGamez.Editor.MapBundles.BuildBatch [-areas a,b]
    /// </summary>
    public static class MapBundles
    {
        public static string Platform(BuildTarget t) =>
            t == BuildTarget.iOS ? "ios" : t == BuildTarget.Android ? "android" : throw new System.Exception($"No map bundles for {t}");

        public static string OutDir(BuildTarget t) =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Maps", Platform(t)));

        /// <summary>Manifest of the last bundle build: the player build reads it so engine code stripping keeps what maps use.</summary>
        public static string ManifestPath(BuildTarget t) => Path.Combine(OutDir(t), Platform(t) + ".manifest");

        [MenuItem("LeGamez/Build Map Bundles (active platform)")]
        public static void BuildActive() => Build(EditorUserBuildSettings.activeBuildTarget, null);

        public static void BuildBatch()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-areas");
            string[] only = i >= 0 && i + 1 < args.Length ? args[i + 1].Split(',') : null;
            if (!Build(EditorUserBuildSettings.activeBuildTarget, only) && Application.isBatchMode) EditorApplication.Exit(1);
        }

        [System.Serializable]
        class IndexFile
        {
            public string name;   // installed file name: map.bundle (area data) or r<rx>_<rz>.bundle (region)
            public string region; // "rx_rz" for regions, empty for the area bundle
            public string path;
            public uint crc;
        }

        [System.Serializable]
        class IndexMap
        {
            public string id, name, thumb;
            public double lat, lon;
            public string[] vehicles;
            public int pois;
            public List<IndexFile> files = new List<IndexFile>();
            /// <summary>Regions within <see cref="StartRadius"/> of the spawn: downloaded with the area ("Laden").</summary>
            public List<string> start = new List<string>();
        }

        [System.Serializable]
        class Index
        {
            public string platform, unity;
            public int format;
            public List<IndexMap> maps = new List<IndexMap>();
        }

        static string RegionBundle(string id, Vector2Int r) => $"{id}-r{r.x}_{r.y}.bundle";
        const float StartRadius = 1200f;

        static bool Build(BuildTarget target, string[] only)
        {
            string platform = Platform(target);
            var catalog = AssetDatabase.LoadAssetAtPath<AreaCatalog>(AreaCatalog.AssetPath);
            if (catalog == null) { Debug.LogError("[Maps] no AreaCatalog – run ImportAllBatch first"); return false; }
            KeepAllFogVariants();

            var areas = catalog.areas.Where(a => only == null || only.Contains(a.id)).ToList();
            var builds = new List<AssetBundleBuild>();
            var regionsOf = new Dictionary<string, List<Vector2Int>>();
            var spawnOf = new Dictionary<string, Vector3>();
            foreach (var a in areas)
            {
                string areaAsset = OsmImport.OsmAreaImporter.AreaDataPath(a.id);
                var data = AssetDatabase.LoadAssetAtPath<AreaData>(areaAsset);
                if (data == null) { Debug.LogError($"[Maps] {a.id}: {areaAsset} missing – re-import the area"); return false; }
                if (!File.Exists(AreaCatalog.MapPath(a.id)) || !File.Exists(AreaCatalog.ThumbPath(a.id)))
                    Debug.LogWarning($"[Maps] {a.id}: thumbnail/map texture missing – run SceneSnapshot.RenderThumbnails");
                builds.Add(new AssetBundleBuild { assetBundleName = a.id + ".bundle", assetNames = new[] { areaAsset } });
                // One bundle per 1 km region (= download unit), holding its 5 × 5 tiles.
                var byRegion = data.tileKeys.GroupBy(WorldStreamer.RegionOf).OrderBy(g => g.Key.x).ThenBy(g => g.Key.y).ToList();
                if (byRegion.Count == 0) { Debug.LogError($"[Maps] {a.id}: no world tiles – re-import the area"); return false; }
                regionsOf[a.id] = byRegion.Select(g => g.Key).ToList();
                spawnOf[a.id] = data.spawn;
                foreach (var g in byRegion)
                    builds.Add(new AssetBundleBuild
                    {
                        assetBundleName = RegionBundle(a.id, g.Key),
                        assetNames = g.Select(k => WorldStreamer.EditorTilePath(a.id, k)).ToArray(),
                    });
            }
            if (builds.Count == 0) { Debug.LogError("[Maps] nothing to build"); return false; }

            string outDir = OutDir(target);
            if (only == null && Directory.Exists(outDir)) Directory.Delete(outDir, true); // no stale regions
            Directory.CreateDirectory(outDir);
            var manifest = BuildPipeline.BuildAssetBundles(outDir, builds.ToArray(),
                BuildAssetBundleOptions.StrictMode, target);
            if (manifest == null) { Debug.LogError("[Maps] bundle build failed"); return false; }

            var index = new Index { platform = platform, unity = Application.unityVersion, format = MapStore.Format };
            foreach (var a in areas)
            {
                var m = new IndexMap
                {
                    id = a.id, name = a.displayName, lat = a.lat, lon = a.lon, vehicles = a.vehicles.ToArray(), pois = a.poiCount,
                    thumb = Path.GetFullPath(AreaCatalog.ThumbPath(a.id)),
                };
                void Add(string bundle, string name, string region)
                {
                    string file = Path.Combine(outDir, bundle);
                    BuildPipeline.GetCRCForAssetBundle(file, out uint crc);
                    m.files.Add(new IndexFile { name = name, region = region, path = file, crc = crc });
                }
                Add(a.id + ".bundle", "map.bundle", "");
                foreach (var r in regionsOf[a.id])
                {
                    Add(RegionBundle(a.id, r), $"r{r.x}_{r.y}.bundle", $"{r.x}_{r.y}");
                    var sp = spawnOf[a.id];
                    float x0 = r.x * WorldStreamer.RegionSize, z0 = r.y * WorldStreamer.RegionSize, size = WorldStreamer.RegionSize;
                    float dx = Mathf.Max(x0 - sp.x, 0f, sp.x - (x0 + size)), dz = Mathf.Max(z0 - sp.z, 0f, sp.z - (z0 + size));
                    if (dx * dx + dz * dz <= StartRadius * StartRadius) m.start.Add($"{r.x}_{r.y}");
                }
                index.maps.Add(m);
                long total = m.files.Sum(f => new FileInfo(f.path).Length);
                Debug.Log($"[Maps] {platform} {a.id}: area {new FileInfo(m.files[0].path).Length / (1024 * 1024)} MB + {m.files.Count - 1} regions, total {total / (1024 * 1024)} MB");
            }
            File.WriteAllText(Path.Combine(outDir, "index.json"), JsonUtility.ToJson(index, true));
            return true;
        }

        /// <summary>
        /// "Automatic" fog/lightmap stripping looks only at the scenes of the player build – the menu has no fog, so
        /// the fog variants would be missing for the downloaded map scenes. Keep them explicitly.
        /// </summary>
        static void KeepAllFogVariants()
        {
            var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (gs.Length == 0) return;
            var so = new SerializedObject(gs[0]);
            bool changed = false;
            void Set(string prop, int value)
            {
                var p = so.FindProperty(prop);
                if (p != null && p.intValue != value) { p.intValue = value; changed = true; }
            }
            Set("m_FogStripping", 1);
            Set("m_FogKeepLinear", 1);
            Set("m_FogKeepExp", 1);
            Set("m_FogKeepExp2", 1);
            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
            }
        }
    }
}
