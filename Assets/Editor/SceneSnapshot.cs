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
            foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets/Scenes/Areas" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                var area = Object.FindAnyObjectByType<AreaInfo>();
                if (area == null) continue;
                string id = area.areaId;

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
                Debug.Log($"[Snapshot] {id} -> {outDir}");
            }
        }

        const string ThumbDir = "Assets/Resources/AreaThumbs";
        const string MapDir = "Assets/Resources/AreaMaps";

        /// <summary>Oblique bird's-eye thumbnails per area for the main menu cards.</summary>
        [MenuItem("LeGamez/Render Menu Thumbnails")]
        public static void RenderThumbnails()
        {
            OsmImport.OsmAreaImporter.EnsureFolder(ThumbDir);
            var activeScene = EditorSceneManager.GetActiveScene().path;
            foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets/Scenes/Areas" }))
            {
                EditorSceneManager.OpenScene(AssetDatabase.GUIDToAssetPath(guid), OpenSceneMode.Single);
                var area = Object.FindAnyObjectByType<AreaInfo>();
                if (area == null) continue;

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
                camGo.transform.position = focus + new Vector3(-90f, 170f, -200f);
                camGo.transform.LookAt(focus);
                bool fog = RenderSettings.fog;
                RenderSettings.fog = false;
                string file = Path.Combine(ThumbDir, area.areaId + ".png");
                RenderTo(cam, file, 768, 512);
                RenderSettings.fog = fog;

                // Minimap: orthographic top-down render of the whole area (+60 m margin), north up.
                // Covers the (possibly non-square, off-centre) area rectangle; ~1 px per metre up to 2048 px.
                Vector2 mapSize = area.MapSize, mapCenter = area.BoundsCenter;
                cam.orthographic = true;
                cam.orthographicSize = mapSize.y * 0.5f;
                camGo.transform.SetPositionAndRotation(new Vector3(mapCenter.x, 500f, mapCenter.y), Quaternion.Euler(90f, 0f, 0f));
                RenderSettings.fog = false;
                OsmImport.OsmAreaImporter.EnsureFolder(MapDir);
                string mapFile = Path.Combine(MapDir, area.areaId + ".png");
                float longest = Mathf.Max(mapSize.x, mapSize.y);
                int texLong = longest > 1500f ? 2048 : 1024;
                int texW = Mathf.RoundToInt(texLong * mapSize.x / longest), texH = Mathf.RoundToInt(texLong * mapSize.y / longest);
                RenderTo(cam, mapFile, texW, texH); // camera aspect follows the render target
                RenderSettings.fog = fog;
                AssetDatabase.ImportAsset(mapFile);
                if (AssetImporter.GetAtPath(mapFile) is TextureImporter mi)
                {
                    mi.textureType = TextureImporterType.Default;
                    mi.mipmapEnabled = true;
                    mi.wrapMode = TextureWrapMode.Clamp;
                    mi.maxTextureSize = 2048;
                    mi.npotScale = TextureImporterNPOTScale.None;
                    mi.SaveAndReimport();
                }

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
            }
            if (!string.IsNullOrEmpty(activeScene)) EditorSceneManager.OpenScene(activeScene, OpenSceneMode.Single);
        }

        static void Render(Camera cam, string file) => RenderTo(cam, file, Width, Height);

        static void RenderTo(Camera cam, string file, int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(file, tex.EncodeToPNG());
            cam.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }
    }
}
