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
    /// of the Unity version) to Builds/Maps/&lt;id&gt;/: area.bin (AreaData + small overview map) and one r&lt;rx&gt;_&lt;rz&gt;.bin per
    /// 1 km region with its tiles, facade records and minimap image (<see cref="RegionContent"/>), plus index.json for
    /// Tools/release/publish_maps.py.
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

                string areaFile = Path.Combine(dir, "area.bin");
                File.WriteAllBytes(areaFile, MapFormat.WriteArea(data, OverviewImage(a.id)));
                m.files.Add(new IndexFile { name = "area.bin", region = "", path = areaFile });
                var facade = RegionContent.SplitFacade(data.facade != null ? RegionContent.ToFloats(data.facade.bytes) : null);
                int noMap = 0;

                long total = new FileInfo(areaFile).Length;
                foreach (var g in data.tileKeys.GroupBy(WorldStreamer.RegionOf).OrderBy(g => g.Key.x).ThenBy(g => g.Key.y))
                {
                    var tiles = g.Select(k => AssetDatabase.LoadAssetAtPath<WorldTile>(WorldStreamer.EditorTilePath(a.id, k)))
                        .Where(t => t != null).ToList();
                    string rk = $"{g.Key.x}_{g.Key.y}";
                    string file = Path.Combine(dir, $"r{rk}.bin");
                    var sections = new List<(string, byte[])>();
                    if (facade.TryGetValue(g.Key, out var rec)) sections.Add((RegionContent.FacadeSection, MapFormat.Deflate(RegionContent.ToBytes(rec))));
                    string mapFile = WorldStreamer.EditorRegionMapPath(a.id, g.Key);
                    if (File.Exists(mapFile)) sections.Add((RegionContent.MapSection, File.ReadAllBytes(mapFile)));
                    else noMap++;
                    File.WriteAllBytes(file, MapFormat.WriteRegion(tiles, sections));
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
                if (noMap > 0) Debug.LogWarning($"[Maps] {a.id}: {noMap} regions without minimap image – run SceneSnapshot.RenderThumbnailsBatch -area {a.id}");
                Debug.Log($"[Maps] {a.id}: area {new FileInfo(areaFile).Length / 1024} KB + {m.files.Count - 1} regions, total {total / (1024 * 1024)} MB, start {m.start.Count} regions");
            }
            File.WriteAllText(Path.Combine(OutDir, "index.json"), JsonUtility.ToJson(index, true));
            return true;
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
