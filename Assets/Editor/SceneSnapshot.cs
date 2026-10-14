using System.Linq;
using System.IO;
using LeGamez.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LeGamez.Editor
{
    /// <summary>
    /// Renders preview PNGs (aerial + street level) of every area scene to Builds/Snapshots.
    /// Batch: -executeMethod LeGamez.Editor.SceneSnapshot.RenderAll
    /// </summary>
    public static class SceneSnapshot
    {
        const int Width = 1600, Height = 900;

        [MenuItem("LeGamez/Render Area Snapshots")]
        public static void RenderAll()
        {
            string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Snapshots"));
            Directory.CreateDirectory(outDir);
            foreach (var data in AreaPreview.All().ToList())
            {
                var area = AreaPreview.Open(data); // all tiles loaded
                if (area == null) continue;
                string id = area.areaId;
                var world = Object.FindAnyObjectByType<WorldStreamer>();

                var camGo = new GameObject("SnapshotCam");
                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = 55f;
                cam.farClipPlane = 2000f;
                cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                cam.backgroundColor = RenderSettings.fogColor;
                cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
                var gm = LeGamez.Core.GameMaterials.Instance;
                var volume = new GameObject("SnapshotVolume").AddComponent<Volume>();
                volume.isGlobal = true;
                if (gm != null) volume.sharedProfile = gm.postProfile;

                // Aerial: from the south-west corner looking at the centre.
                camGo.transform.position = new Vector3(-380f, 320f, -420f);
                camGo.transform.LookAt(Vector3.zero);
                Render(cam, Path.Combine(outDir, $"{id}_aerial.png"));

                // Top-down overview without fog.
                bool fog = RenderSettings.fog;
                RenderSettings.fog = false;
                camGo.transform.position = new Vector3(0, 900f, 0);
                camGo.transform.rotation = Quaternion.Euler(90, 0, 0);
                cam.fieldOfView = 62f;
                Render(cam, Path.Combine(outDir, $"{id}_top.png"));
                RenderSettings.fog = fog;
                cam.fieldOfView = 55f;

                // Street level: behind the spawn, looking at the nearest POI (3rd-person framing).
                Vector3 spawn = area.spawnPoint != null ? area.spawnPoint.position : Vector3.zero;
                Vector3 look = spawn + Vector3.forward * 20f;
                if (area.pois != null && area.pois.entries.Count > 0)
                {
                    float best = float.MaxValue;
                    foreach (var p in area.pois.entries)
                    {
                        float d = Vector3.Distance(p.position, spawn);
                        if (d < best && d > 15f) { best = d; look = p.position; }
                    }
                }
                Vector3 dir = Vector3.ProjectOnPlane(look - spawn, Vector3.up).normalized;
                camGo.transform.position = spawn - dir * 6f + Vector3.up * 3.2f;
                camGo.transform.rotation = Quaternion.LookRotation(spawn + dir * 30f + Vector3.up * 1.5f - camGo.transform.position);
                Render(cam, Path.Combine(outDir, $"{id}_street.png"));

                // Landmark: the top-ranked POI seen from 35 m away at eye level.
                if (area.pois != null && area.pois.entries.Count > 0)
                {
                    var poi = area.pois.entries[0].position;
                    Vector3 from = poi + new Vector3(-22f, 0f, -28f);
                    camGo.transform.position = from + Vector3.up * 2.5f;
                    camGo.transform.rotation = Quaternion.LookRotation(poi + Vector3.up * 6f - camGo.transform.position);
                    Render(cam, Path.Combine(outDir, $"{id}_landmark.png"));
                }

                Object.DestroyImmediate(volume.gameObject);
                Object.DestroyImmediate(camGo);
                if (world != null) world.UnloadAllInEditor();
                Debug.Log($"[Snapshot] {id} -> {outDir}");
            }
        }

        /// <summary>
        /// Oblique bird's-eye thumbnails per area for the main menu cards (one more per further default start point,
        /// <see cref="AreaCatalog.ThumbPath(string, int)"/>), the sharp top-down image of every 1 km region
        /// (minimap, Generated/&lt;id&gt;/regions~/, into the region files on export) and the small overview of the whole area
        /// composed from them (full map, AreaData.mapTexture → area file). Only the tiles around the current view are
        /// loaded, so whole municipalities fit into memory. <paramref name="onlyId"/>: one area.
        /// Incremental: only images that tiles changed since the last rendering can show are made again
        /// (<see cref="OsmImport.TileManifest.DirtyMaps"/>, written by the importer); unknown state = everything.
        /// </summary>
        [MenuItem("LeGamez/Render Menu Thumbnails")]
        public static void RenderThumbnails() => RenderThumbnails(null);

        /// <summary>Batch: -executeMethod LeGamez.Editor.SceneSnapshot.RenderThumbnailsBatch [-area &lt;id&gt;]</summary>
        public static void RenderThumbnailsBatch()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-area");
            RenderThumbnails(i >= 0 && i + 1 < args.Length ? args[i + 1] : null);
        }

        public static void RenderThumbnails(string onlyId)
        {
            var paths = AreaPreview.All().Where(d => onlyId == null || d.areaId == onlyId).Select(AssetDatabase.GetAssetPath).ToList();
            foreach (var dataPath in paths)
            {
                var data = AssetDatabase.LoadAssetAtPath<AreaData>(dataPath);
                var dirty = OsmImport.TileManifest.DirtyMaps(data.areaId); // null = render everything
                var area = AreaPreview.Open(data, allTiles: false);
                if (area == null) continue;
                var world = Object.FindAnyObjectByType<WorldStreamer>();

                var camGo = new GameObject("ThumbCam");
                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = 40f;
                cam.farClipPlane = 3000f;
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
                var volume = new GameObject("ThumbVolume").AddComponent<Volume>();
                volume.isGlobal = true;
                var gm = LeGamez.Core.GameMaterials.Instance;
                if (gm != null) volume.sharedProfile = gm.postProfile;

                // Look at the best POI (or the centre) from the south at ~40° down.
                Vector3 focus = area.pois != null && area.pois.entries.Count > 0 ? area.pois.entries[0].position : Vector3.zero;
                focus = Vector3.Lerp(focus, Vector3.zero, 0.4f);
                string file = AreaCatalog.ThumbPath(area.areaId);
                bool thumb = Affects(dirty, focus, ThumbReach) || !File.Exists(file);
                if (world != null && thumb) world.LoadAroundInEditor(focus, 2500f);
                camGo.transform.position = focus + new Vector3(-90f, 170f, -200f);
                camGo.transform.LookAt(focus);
                bool fog = RenderSettings.fog;
                RenderSettings.fog = false;
                if (thumb) RenderTo(cam, file, 768, 512);
                var startThumbs = RenderStartThumbs(area, data, world, cam, dirty);

                string mapFile = AreaCatalog.MapPath(area.areaId);
                if (world != null) RenderMaps(area, data, world, cam, mapFile, dirty);
                RenderSettings.fog = fog;
                AssetDatabase.ImportAsset(mapFile);
                if (AssetImporter.GetAtPath(mapFile) is TextureImporter mi)
                {
                    mi.textureType = TextureImporterType.Default;
                    mi.mipmapEnabled = true;
                    mi.wrapMode = TextureWrapMode.Clamp;
                    mi.maxTextureSize = OverviewMax;
                    mi.npotScale = TextureImporterNPOTScale.None;
                    mi.SaveAndReimport();
                }
                string oldPng = Path.ChangeExtension(mapFile, ".png"); // before region images: 4096 px PNG
                if (File.Exists(oldPng)) AssetDatabase.DeleteAsset(oldPng);

                Object.DestroyImmediate(volume.gameObject);
                Object.DestroyImmediate(camGo);
                if (thumb) startThumbs.Insert(0, file);
                foreach (var thumbFile in startThumbs)
                {
                    AssetDatabase.ImportAsset(thumbFile);
                    if (AssetImporter.GetAtPath(thumbFile) is TextureImporter ti)
                    {
                        ti.textureType = TextureImporterType.Sprite;
                        ti.mipmapEnabled = false;
                        ti.maxTextureSize = 1024;
                        ti.SaveAndReimport();
                    }
                }

                // Texture imports above reload assets – fetch the AreaData again before writing to it.
                data = AssetDatabase.LoadAssetAtPath<AreaData>(dataPath);
                data.mapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(mapFile);
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();
                if (world != null) OsmImport.TileManifest.ClearDirtyMaps(data.areaId);
            }
        }

        /// <summary>Card images look this far (m) – changes further away don't show.</summary>
        const float ThumbReach = 1500f;

        /// <summary>True if a changed tile (null set = everything changed) lies within <paramref name="reach"/> of the point.</summary>
        static bool Affects(System.Collections.Generic.HashSet<Vector2Int> dirty, Vector3 at, float reach)
        {
            if (dirty == null) return true;
            foreach (var k in dirty)
            {
                float s = WorldStreamer.TileSize, x0 = k.x * s, z0 = k.y * s;
                float dx = Mathf.Max(x0 - at.x, 0f, at.x - (x0 + s)), dz = Mathf.Max(z0 - at.z, 0f, at.z - (z0 + s));
                if (dx * dx + dz * dz < reach * reach) return true;
            }
            return false;
        }

        /// <summary>
        /// Card images of the default start points after the first (the first uses the area thumbnail): same view as the
        /// area thumbnail, centred between the start and the best POI near it. Removes images of starts that are gone.
        /// Returns the files written.
        /// </summary>
        static System.Collections.Generic.List<string> RenderStartThumbs(AreaInfo area, AreaData data, WorldStreamer world, Camera cam,
            System.Collections.Generic.HashSet<Vector2Int> dirty)
        {
            var files = new System.Collections.Generic.List<string>();
            var starts = data.StartList();
            for (int k = 1; k < starts.Count; k++)
            {
                Vector3 at = starts[k].position, poi = at;
                if (area.pois != null)
                    foreach (var p in area.pois.entries) // ranked: the first one close enough
                        if (Vector2.Distance(new Vector2(p.position.x, p.position.z), new Vector2(at.x, at.z)) < 600f) { poi = p.position; break; }
                Vector3 focus = Vector3.Lerp(poi, at, 0.4f);
                if (!Affects(dirty, focus, ThumbReach) && File.Exists(AreaCatalog.ThumbPath(area.areaId, k))) continue; // unchanged
                if (world != null) world.LoadAroundInEditor(focus, 2500f);
                cam.transform.position = focus + new Vector3(-90f, 170f, -200f);
                cam.transform.LookAt(focus);
                string file = AreaCatalog.ThumbPath(area.areaId, k);
                RenderTo(cam, file, 768, 512);
                files.Add(file);
                Debug.Log($"[Snapshot] {area.areaId}: start '{starts[k].name}' -> {file}");
            }
            for (int k = starts.Count; File.Exists(AreaCatalog.ThumbPath(area.areaId, k)); k++)
                AssetDatabase.DeleteAsset(AreaCatalog.ThumbPath(area.areaId, k));
            return files;
        }

        /// <summary>Longest side of the area overview (full map); sharp detail comes from the region images.</summary>
        public const int OverviewMax = 2048;
        const int RegionJpegQuality = 80, OverviewJpegQuality = 85;
        /// <summary>Where no region has an image (outside the tiles) – same as the minimap's background.</summary>
        static readonly Color32 MapBackground = new Color32(140, 178, 128, 255);

        /// <summary>
        /// Orthographic top-down render of every region (exactly its 1 km square, <see cref="RegionContent.MapPixels"/> px,
        /// north up) to <see cref="WorldStreamer.EditorRegionMapPath"/>, walking the regions in a snake so neighbouring
        /// tiles stay loaded, and box-filtered into the overview image of the whole area (+ MapMargin), ~1 px/m up to
        /// <see cref="OverviewMax"/> px.
        /// </summary>
        static void RenderMaps(AreaInfo area, AreaData data, WorldStreamer world, Camera cam, string overviewFile,
            System.Collections.Generic.HashSet<Vector2Int> dirty)
        {
            string id = area.areaId;
            string dir = WorldStreamer.EditorRegionMapDir(id);
            if (dirty == null && Directory.Exists(dir)) Directory.Delete(dir, true); // no stale regions
            Directory.CreateDirectory(dir);

            Vector2 mapSize = area.MapSize, mapCenter = area.BoundsCenter, mapMin = mapCenter - mapSize * 0.5f;
            float longest = Mathf.Max(mapSize.x, mapSize.y);
            int texLong = Mathf.Clamp(Mathf.CeilToInt(longest), 1024, OverviewMax); // ~1 px/m at most
            int ow = Mathf.RoundToInt(texLong * mapSize.x / longest), oh = Mathf.RoundToInt(texLong * mapSize.y / longest);
            var overview = new Color32[ow * oh];
            for (int i = 0; i < overview.Length; i++) overview[i] = MapBackground;
            float mx = mapSize.x / ow, mz = mapSize.y / oh; // metres per overview pixel

            bool wasOrtho = cam.orthographic;
            var camClear = cam.clearFlags;
            var camBg = cam.backgroundColor;
            cam.orthographic = true;
            cam.orthographicSize = WorldStreamer.RegionSize * 0.5f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = MapBackground;
            // No vignette: it would darken every region image towards its corners (visible grid in the maps).
            var noVignette = new GameObject("MapVolume").AddComponent<Volume>();
            noVignette.isGlobal = true;
            noVignette.priority = 100f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.Add<Vignette>(true).intensity.Override(0f);
            noVignette.sharedProfile = profile;

            var regions = data.tileKeys.Select(WorldStreamer.RegionOf).Distinct()
                .OrderBy(r => r.x).ThenBy(r => (r.x & 1) == 0 ? r.y : -r.y).ToList();
            if (dirty != null)
            {
                // Images of regions that are gone (incremental runs keep the folder).
                var keep = new System.Collections.Generic.HashSet<string>(regions.Select(r => Path.GetFileName(WorldStreamer.EditorRegionMapPath(id, r))));
                foreach (var f in Directory.GetFiles(dir, "*.jpg")) if (!keep.Contains(Path.GetFileName(f))) File.Delete(f);
            }
            const int px = RegionContent.MapPixels;
            float k = px / WorldStreamer.RegionSize; // region pixels per metre
            long bytes = 0;
            int rendered = 0;
            for (int n = 0; n < regions.Count; n++)
            {
                var r = regions[n];
                var b = RegionContent.Bounds(r);
                const float margin = 30f; // trees and roofs reaching over the region edge
                string regionFile = WorldStreamer.EditorRegionMapPath(id, r);
                Texture2D tex = null;
                if (dirty != null && File.Exists(regionFile) && !TouchesRegion(dirty, b, margin))
                {
                    // Unchanged region: keep its image, only add it to the overview.
                    tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    if (!tex.LoadImage(File.ReadAllBytes(regionFile)) || tex.width != px || tex.height != px) { Object.DestroyImmediate(tex); tex = null; }
                }
                if (tex == null)
                {
                    world.KeepInEditor(new Rect(b.xMin - margin, b.yMin - margin, b.width + 2 * margin, b.height + 2 * margin));
                    cam.transform.SetPositionAndRotation(new Vector3(b.center.x, 500f, b.center.y), Quaternion.Euler(90f, 0f, 0f));
                    tex = RenderToTexture(cam, px, px);
                    var jpg = tex.EncodeToJPG(RegionJpegQuality);
                    File.WriteAllBytes(regionFile, jpg);
                    bytes += jpg.Length;
                    rendered++;
                }

                // Box filter into the overview (rows bottom-up = south to north in both images).
                var src = tex.GetPixels32();
                int ox0 = Mathf.Max(0, Mathf.FloorToInt((b.xMin - mapMin.x) / mx)), ox1 = Mathf.Min(ow - 1, Mathf.CeilToInt((b.xMax - mapMin.x) / mx));
                int oz0 = Mathf.Max(0, Mathf.FloorToInt((b.yMin - mapMin.y) / mz)), oz1 = Mathf.Min(oh - 1, Mathf.CeilToInt((b.yMax - mapMin.y) / mz));
                for (int oz = oz0; oz <= oz1; oz++)
                for (int ox = ox0; ox <= ox1; ox++)
                {
                    float wx = mapMin.x + (ox + 0.5f) * mx, wz = mapMin.y + (oz + 0.5f) * mz;
                    if (!b.Contains(new Vector2(wx, wz))) continue;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt((wx - mx * 0.5f - b.xMin) * k), 0, px - 1);
                    int x1 = Mathf.Clamp(Mathf.CeilToInt((wx + mx * 0.5f - b.xMin) * k) - 1, x0, px - 1);
                    int z0 = Mathf.Clamp(Mathf.FloorToInt((wz - mz * 0.5f - b.yMin) * k), 0, px - 1);
                    int z1 = Mathf.Clamp(Mathf.CeilToInt((wz + mz * 0.5f - b.yMin) * k) - 1, z0, px - 1);
                    int sr = 0, sg = 0, sb = 0, cnt = 0;
                    for (int y = z0; y <= z1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var c = src[y * px + x];
                        sr += c.r; sg += c.g; sb += c.b; cnt++;
                    }
                    overview[oz * ow + ox] = new Color32((byte)(sr / cnt), (byte)(sg / cnt), (byte)(sb / cnt), 255);
                }
                Object.DestroyImmediate(tex);
                if (n % 8 == 7) EditorUtility.UnloadUnusedAssetsImmediate(); // keep editor memory flat on big areas
                if (n % 20 == 0) Debug.Log($"[Snapshot] {id}: region maps {n + 1}/{regions.Count}");
            }
            world.UnloadAllInEditor();
            EditorUtility.UnloadUnusedAssetsImmediate();

            var ov = new Texture2D(ow, oh, TextureFormat.RGB24, false);
            ov.SetPixels32(overview);
            ov.Apply();
            var ovJpg = ov.EncodeToJPG(OverviewJpegQuality);
            File.WriteAllBytes(overviewFile, ovJpg);
            Object.DestroyImmediate(ov);

            Object.DestroyImmediate(noVignette.gameObject);
            Object.DestroyImmediate(profile);
            cam.orthographic = wasOrtho;
            cam.clearFlags = camClear;
            cam.backgroundColor = camBg;
            Debug.Log($"[Snapshot] {id}: {rendered} of {regions.Count} region maps rendered ({bytes / 1024} KB, {bytes / 1024 / Mathf.Max(1, rendered)} KB each), overview {ow}x{oh} ({ovJpg.Length / 1024} KB)");
        }

        /// <summary>A changed tile overlaps the region grown by <paramref name="margin"/> (what its image shows).</summary>
        static bool TouchesRegion(System.Collections.Generic.HashSet<Vector2Int> dirty, Rect region, float margin)
        {
            float s = WorldStreamer.TileSize;
            foreach (var t in dirty)
                if (t.x * s < region.xMax + margin && (t.x + 1) * s > region.xMin - margin &&
                    t.y * s < region.yMax + margin && (t.y + 1) * s > region.yMin - margin) return true;
            return false;
        }

        static void Render(Camera cam, string file) => RenderTo(cam, file, Width, Height);

        internal static void RenderTo(Camera cam, string file, int width, int height)
        {
            var tex = RenderToTexture(cam, width, height);
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        /// <summary>Renders the camera into a new readable RGB24 texture (caller destroys it).</summary>
        internal static Texture2D RenderToTexture(Camera cam, int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            cam.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            return tex;
        }
    }
}
