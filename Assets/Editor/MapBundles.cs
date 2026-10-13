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
    /// Builds one download bundle per area (scene + everything it references: meshes, POIs, streets, facade data,
    /// minimap texture, materials, shaders) for the active platform into Builds/Maps/&lt;platform&gt;/, plus index.json
    /// for Tools/release/publish_maps.py. Bundles are LZMA (small download); the app recompresses them to LZ4.
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
        class IndexMap
        {
            public string id, name, file, thumb;
            public double lat, lon;
            public string[] vehicles;
            public int pois;
            public uint crc;
        }

        [System.Serializable]
        class Index
        {
            public string platform, unity;
            public int format;
            public List<IndexMap> maps = new List<IndexMap>();
        }

        static bool Build(BuildTarget target, string[] only)
        {
            string platform = Platform(target);
            var catalog = AssetDatabase.LoadAssetAtPath<AreaCatalog>(AreaCatalog.AssetPath);
            if (catalog == null) { Debug.LogError("[Maps] no AreaCatalog – run ImportAllBatch first"); return false; }
            KeepAllFogVariants();

            var areas = catalog.areas.Where(a => only == null || only.Contains(a.id)).ToList();
            var builds = new List<AssetBundleBuild>();
            foreach (var a in areas)
            {
                string scene = $"Assets/Scenes/Areas/{a.sceneName}.unity";
                if (!File.Exists(scene)) { Debug.LogError($"[Maps] {a.id}: {scene} missing"); return false; }
                if (!File.Exists(AreaCatalog.MapPath(a.id)) || !File.Exists(AreaCatalog.ThumbPath(a.id)))
                    Debug.LogWarning($"[Maps] {a.id}: thumbnail/map texture missing – run SceneSnapshot.RenderThumbnails");
                builds.Add(new AssetBundleBuild { assetBundleName = a.id + ".bundle", assetNames = new[] { scene } });
            }
            if (builds.Count == 0) { Debug.LogError("[Maps] nothing to build"); return false; }

            string outDir = OutDir(target);
            Directory.CreateDirectory(outDir);
            var manifest = BuildPipeline.BuildAssetBundles(outDir, builds.ToArray(),
                BuildAssetBundleOptions.StrictMode, target);
            if (manifest == null) { Debug.LogError("[Maps] bundle build failed"); return false; }

            var index = new Index { platform = platform, unity = Application.unityVersion, format = MapStore.Format };
            foreach (var a in areas)
            {
                string file = Path.Combine(outDir, a.id + ".bundle");
                BuildPipeline.GetCRCForAssetBundle(file, out uint crc);
                index.maps.Add(new IndexMap
                {
                    id = a.id, name = a.displayName, lat = a.lat, lon = a.lon, vehicles = a.vehicles.ToArray(), pois = a.poiCount,
                    file = file, thumb = Path.GetFullPath(AreaCatalog.ThumbPath(a.id)), crc = crc,
                });
                Debug.Log($"[Maps] {platform} {a.id}: {new FileInfo(file).Length / (1024 * 1024)} MB, crc {crc}");
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
