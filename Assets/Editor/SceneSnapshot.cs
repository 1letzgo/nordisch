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

        static void Render(Camera cam, string file)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(file, tex.EncodeToPNG());
            cam.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }
    }
}
