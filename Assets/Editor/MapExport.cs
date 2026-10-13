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
    /// Writes the download files of each area (format 5, World/MapFormat – the same files for iOS and Android, independent
    /// of the Unity version) to Builds/Maps/: &lt;id&gt;.area (AreaData + minimap + facade records) and one
    /// &lt;id&gt;-r&lt;rx&gt;_&lt;rz&gt;.region per 1 km region with its tiles, plus index.json for Tools/release/publish_maps.py.
    /// Batch: Unity -batchmode -quit -projectPath . -executeMethod LeGamez.Editor.MapExport.ExportBatch [-areas a,b]
    /// </summary>
    public static class MapExport
    {
        public static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Maps"));
        const float StartRadius = 1200f;

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
            public string name;   // installed file name: area.bin or r<rx>_<rz>.bin
            public string region; // "rx_rz" for regions, empty for the area file
            public string path;
        }

        [System.Serializable]
        class IndexMap
        {
            public string id, name, thumb;
            public double lat, lon;
            public string[] vehicles;
            public int pois;
            public List<IndexFile> files = new List<IndexFile>();
            public List<string> start = new List<string>();
        }

        [System.Serializable]
        class Index
        {
            public int format;
            public List<IndexMap> maps = new List<IndexMap>();
        }

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
                if (Directory.Exists(dir)) Directory.Delete(dir, true); // no stale regions
                Directory.CreateDirectory(dir);
                var m = new IndexMap
                {
                    id = a.id, name = a.displayName, lat = a.lat, lon = a.lon, vehicles = a.vehicles.ToArray(), pois = a.poiCount,
                    thumb = Path.GetFullPath(AreaCatalog.ThumbPath(a.id)),
                };

                string mapPng = AreaCatalog.MapPath(a.id);
                byte[] png = File.Exists(mapPng) ? File.ReadAllBytes(mapPng) : null;
                byte[] facade = data.facade != null ? data.facade.bytes : null;
                string areaFile = Path.Combine(dir, "area.bin");
                File.WriteAllBytes(areaFile, MapFormat.WriteArea(data, png, facade));
                m.files.Add(new IndexFile { name = "area.bin", region = "", path = areaFile });

                long total = new FileInfo(areaFile).Length;
                foreach (var g in data.tileKeys.GroupBy(WorldStreamer.RegionOf).OrderBy(g => g.Key.x).ThenBy(g => g.Key.y))
                {
                    var tiles = g.Select(k => AssetDatabase.LoadAssetAtPath<WorldTile>(WorldStreamer.EditorTilePath(a.id, k)))
                        .Where(t => t != null).ToList();
                    string rk = $"{g.Key.x}_{g.Key.y}";
                    string file = Path.Combine(dir, $"r{rk}.bin");
                    File.WriteAllBytes(file, MapFormat.WriteRegion(tiles));
                    total += new FileInfo(file).Length;
                    m.files.Add(new IndexFile { name = $"r{rk}.bin", region = rk, path = file });
                    var sp = data.spawn;
                    float s = WorldStreamer.RegionSize, x0 = g.Key.x * s, z0 = g.Key.y * s;
                    float dx = Mathf.Max(x0 - sp.x, 0f, sp.x - (x0 + s)), dz = Mathf.Max(z0 - sp.z, 0f, sp.z - (z0 + s));
                    if (dx * dx + dz * dz <= StartRadius * StartRadius) m.start.Add(rk);
                    foreach (var t in tiles) Resources.UnloadAsset(t); // keep editor memory flat on big areas
                    if (g.Key.x % 4 == 0) EditorUtility.UnloadUnusedAssetsImmediate();
                }
                index.maps.Add(m);
                Debug.Log($"[Maps] {a.id}: area {new FileInfo(areaFile).Length / (1024 * 1024)} MB + {m.files.Count - 1} regions, total {total / (1024 * 1024)} MB, start {m.start.Count} regions");
            }
            File.WriteAllText(Path.Combine(OutDir, "index.json"), JsonUtility.ToJson(index, true));
            return true;
        }
    }
}
