using System.Globalization;
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
    /// Renders chosen views of one area (only the tiles around each view are loaded – fine for whole municipalities)
    /// to Builds/Snapshots/&lt;id&gt;_view_&lt;name&gt;.png.
    /// Batch: -executeMethod LeGamez.Editor.ViewSnapshot.Render -area &lt;id&gt; -views "name:x,z,yaw,height,pitch;…"
    /// (x, z local metres; yaw 0 = north, 90 = east; height above ground; pitch down in degrees).
    /// </summary>
    public static class ViewSnapshot
    {
        public static void Render()
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string k) { int i = System.Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string id = Arg("-area") ?? "westerstede", views = Arg("-views") ?? "centre:0,0,0,3,8";

            string scenePath = null;
            foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets/Scenes/Areas" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                EditorSceneManager.OpenScene(p, OpenSceneMode.Single);
                var a = Object.FindAnyObjectByType<AreaInfo>();
                if (a != null && a.areaId == id) { scenePath = p; break; }
            }
            if (scenePath == null) { Debug.LogError($"[View] no scene for {id}"); return; }
            var world = Object.FindAnyObjectByType<WorldStreamer>();
            string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Snapshots"));
            Directory.CreateDirectory(outDir);

            var camGo = new GameObject("ViewCam");
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 2500f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            var volume = new GameObject("ViewVolume").AddComponent<Volume>();
            volume.isGlobal = true;
            var gm = LeGamez.Core.GameMaterials.Instance;
            if (gm != null) volume.sharedProfile = gm.postProfile;

            foreach (var v in views.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                var parts = v.Split(':');
                var n = System.Array.ConvertAll(parts[1].Split(','), s => float.Parse(s, CultureInfo.InvariantCulture));
                var pos = new Vector3(n[0], 0f, n[1]);
                if (world != null) world.LoadAroundInEditor(pos, 900f);
                // Ground height under the camera: ray down onto the loaded meshes (colliders are not built in edit mode).
                float ground = GroundHeight(pos);
                camGo.transform.position = new Vector3(pos.x, ground + n[3], pos.z);
                camGo.transform.rotation = Quaternion.Euler(n[4], n[2], 0f);
                bool fog = RenderSettings.fog;
                if (n[3] > 150f) RenderSettings.fog = false; // overviews
                SceneSnapshot.RenderTo(cam, Path.Combine(outDir, $"{id}_view_{parts[0]}.png"), 1600, 900);
                RenderSettings.fog = fog;
                Debug.Log($"[View] {parts[0]} at {pos} ground {ground:F1}");
            }
            if (world != null) world.UnloadAllInEditor();
            Object.DestroyImmediate(volume.gameObject);
            Object.DestroyImmediate(camGo);
        }

        static float GroundHeight(Vector3 p)
        {
            float best = 0f;
            bool found = false;
            foreach (var mf in Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                if (!mf.name.StartsWith("Terrain") || mf.sharedMesh == null) continue;
                var b = mf.GetComponent<Renderer>().bounds;
                if (p.x < b.min.x || p.x > b.max.x || p.z < b.min.z || p.z > b.max.z) continue;
                var go = mf.gameObject;
                var col = go.GetComponent<MeshCollider>();
                if (col == null) col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mf.sharedMesh;
                if (col.Raycast(new Ray(new Vector3(p.x, 500f, p.z), Vector3.down), out var hit, 1000f))
                {
                    best = found ? Mathf.Max(best, hit.point.y) : hit.point.y;
                    found = true;
                }
            }
            return best;
        }
    }
}
