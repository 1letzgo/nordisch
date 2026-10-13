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
        /// Oblique bird's-eye thumbnails per area for the main menu cards, the sharp top-down image of every 1 km region
        /// (minimap, Generated/&lt;id&gt;/regions~/, into the region files on export) and the small overview of the whole area
        /// composed from them (full map, AreaData.mapTexture → area file). Only the tiles around the current view are
        /// loaded, so whole municipalities fit into memory. <paramref name="onlyId"/>: one area.
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
                if (world != null) world.LoadAroundInEditor(focus, 2500f);
                camGo.transform.position = focus + new Vector3(-90f, 170f, -200f);
                camGo.transform.LookAt(focus);
                bool fog = RenderSettings.fog;
                RenderSettings.fog = false;
                string file = AreaCatalog.ThumbPath(area.areaId);
                RenderTo(cam, file, 768, 512);

                string mapFile = AreaCatalog.MapPath(area.areaId);
                if (world != null) RenderMaps(area, data, world, cam, mapFile);
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
                AssetDatabase.ImportAsset(file);
                if (AssetImporter.GetAtPath(file) is TextureImporter ti)
                {
                    ti.textureType = TextureImporterType.Sprite;
                    ti.mipmapEnabled = false;
                    ti.maxTextureSize = 1024;
                    ti.SaveAndReimport();
                }

                // Texture imports above reload assets – fetch the AreaData again before writing to it.
                data = AssetDatabase.LoadAssetAtPath<AreaData>(dataPath);
                data.mapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(mapFile);
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();
            }
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
        static void RenderMaps(AreaInfo area, AreaData data, WorldStreamer world, Camera cam, string overviewFile)
        {
            string id = area.areaId;
            string dir = WorldStreamer.EditorRegionMapDir(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true); // no stale regions
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
            const int px = RegionContent.MapPixels;
            float k = px / WorldStreamer.RegionSize; // region pixels per metre
            long bytes = 0;
            for (int n = 0; n < regions.Count; n++)
            {
                var r = regions[n];
                var b = RegionContent.Bounds(r);
                const float margin = 30f; // trees and roofs reaching over the region edge
                world.KeepInEditor(new Rect(b.xMin - margin, b.yMin - margin, b.width + 2 * margin, b.height + 2 * margin));
                cam.transform.SetPositionAndRotation(new Vector3(b.center.x, 500f, b.center.y), Quaternion.Euler(90f, 0f, 0f));
                var tex = RenderToTexture(cam, px, px);
                var jpg = tex.EncodeToJPG(RegionJpegQuality);
                File.WriteAllBytes(WorldStreamer.EditorRegionMapPath(id, r), jpg);
                bytes += jpg.Length;

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
            Debug.Log($"[Snapshot] {id}: {regions.Count} region maps ({bytes / 1024} KB, {bytes / 1024 / Mathf.Max(1, regions.Count)} KB each), overview {ow}x{oh} ({ovJpg.Length / 1024} KB)");
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
