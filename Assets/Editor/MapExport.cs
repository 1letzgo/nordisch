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

                // World pack: every entry back to back, grouped by region (neighbouring tiles lie close together).
                var facade = RegionContent.SplitFacade(data.facade != null ? RegionContent.ToFloats(data.facade.bytes) : null);
                var pack = new Dictionary<string, MapFormat.PackEntry>();
                string packFile = Path.Combine(dir, "world.bin");
                int noMap = 0;
                using (var fs = new FileStream(packFile, FileMode.Create, FileAccess.Write))
                {
                    void Put(string name, byte[] bytes)
                    {
                        pack[name] = new MapFormat.PackEntry { offset = fs.Position, length = bytes.Length, hash = MapFormat.Hash64(bytes) };
                        fs.Write(bytes, 0, bytes.Length);
                    }
                    foreach (var g in data.tileKeys.GroupBy(WorldStreamer.RegionOf).OrderBy(g => g.Key.x).ThenBy(g => g.Key.y))
                    {
                        foreach (var k in g.OrderBy(k => k.x).ThenBy(k => k.y))
                        {
                            var t = AssetDatabase.LoadAssetAtPath<WorldTile>(WorldStreamer.EditorTilePath(a.id, k));
                            if (t == null) continue;
                            Put(MapFormat.TileEntry(k), MapFormat.TileBlob(t));
                            Resources.UnloadAsset(t); // keep editor memory flat on big areas
                        }
                        if (facade.TryGetValue(g.Key, out var rec)) Put(MapFormat.FacadeEntry(g.Key), MapFormat.Deflate(RegionContent.ToBytes(rec)));
                        string mapFile = WorldStreamer.EditorRegionMapPath(a.id, g.Key);
                        if (File.Exists(mapFile)) Put(MapFormat.MapEntry(g.Key), File.ReadAllBytes(mapFile));
                        else noMap++;
                        if (g.Key.x % 4 == 0) EditorUtility.UnloadUnusedAssetsImmediate();
                    }
                }

                // The loop above unloads assets to keep memory flat – reload the area data (and its POIs/streets) fresh.
                data = AssetDatabase.LoadAssetAtPath<AreaData>(OsmImport.OsmAreaImporter.AreaDataPath(a.id));
                if (data.streets == null || data.pois == null) Debug.LogWarning($"[Maps] {a.id}: streets/POIs missing in AreaData");
                string areaFile = Path.Combine(dir, "area.bin");
                File.WriteAllBytes(areaFile, MapFormat.WriteArea(data, OverviewImage(a.id), pack));
                m.files.Add(new IndexFile { name = "area.bin", region = "", path = areaFile });
                m.files.Add(new IndexFile { name = "world.bin", region = "", path = packFile });
                AddStarts(m, a.id, data);
                long areaSize = new FileInfo(areaFile).Length, packSize = new FileInfo(packFile).Length;
                index.maps.Add(m);
                if (noMap > 0) Debug.LogWarning($"[Maps] {a.id}: {noMap} regions without minimap image – run SceneSnapshot.RenderThumbnailsBatch -area {a.id}");
                Debug.Log($"[Maps] {a.id}: area {areaSize / 1024} KB ({data.streets?.names.Count ?? 0} streets, {data.pois?.entries.Count ?? 0} POIs), world pack {packSize / (1024 * 1024)} MB ({pack.Count} entries)");
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
