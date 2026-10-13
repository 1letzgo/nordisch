using System.Globalization;
using System.IO;
using LeGamez.Editor.OsmImport;
using LeGamez.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LeGamez.Editor
{
    /// <summary>
    /// Renders hand-picked views of one area to Builds/Snapshots/&lt;id&gt;_&lt;name&gt;.png (for checking coasts, dunes, harbours …).
    /// Batch: -executeMethod LeGamez.Editor.ViewSnapshot.Render -area &lt;id&gt; -views "beach:x,z,h,lx,lz;…"
    /// Camera at local (x, z), h metres above the terrain, looking at (lx, lz) at ground level (+1.5 m).
    /// "top" as name renders an orthographic, fog-free top view of the whole area instead (other values ignored).
    /// </summary>
    public static class ViewSnapshot
    {
        public static void Render()
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string k) { int i = System.Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string id = Arg("-area");
            string views = Arg("-views") ?? "top";

            var data = AreaPreview.Find(id);
            if (data == null) throw new System.ArgumentException("area not found: " + id);
            var area = AreaPreview.Open(data); // all tiles loaded
            Physics.SyncTransforms();
            var terrain = TerrainField.Load(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Data", id + ".dgm.bin")));
            // Sea level etc. change the terrain; use the scene's colliders for heights instead of the raw field.
            float H(float x, float z)
            {
                if (Physics.Raycast(new Vector3(x, 500f, z), Vector3.down, out var hit, 1000f, ~(1 << 4))) return hit.point.y;
                return terrain.Height(x, z);
            }

            string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Snapshots"));
            Directory.CreateDirectory(outDir);
            var camGo = new GameObject("SnapshotCam");
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 3000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            var volume = new GameObject("SnapshotVolume").AddComponent<Volume>();
            volume.isGlobal = true;
            var gm = LeGamez.Core.GameMaterials.Instance;
            if (gm != null) volume.sharedProfile = gm.postProfile;

            foreach (var view in views.Split(';'))
            {
                var parts = view.Split(':');
                string name = parts[0].Trim();
                if (name.Length == 0) continue;
                string file = Path.Combine(outDir, $"{id}_{name}.png");
                if (name == "top")
                {
                    Vector2 size = area.MapSize, c = area.BoundsCenter;
                    cam.orthographic = true;
                    cam.orthographicSize = size.y * 0.5f;
                    camGo.transform.SetPositionAndRotation(new Vector3(c.x, 800f, c.y), Quaternion.Euler(90f, 0f, 0f));
                    bool fog = RenderSettings.fog;
                    RenderSettings.fog = false;
                    int w = 2400, h = Mathf.RoundToInt(w * size.y / size.x);
                    SceneSnapshot.RenderTo(cam, file, w, h);
                    RenderSettings.fog = fog;
                    cam.orthographic = false;
                    continue;
                }
                var v = System.Array.ConvertAll(parts[1].Split(','), s => float.Parse(s, CultureInfo.InvariantCulture));
                cam.fieldOfView = 55f;
                camGo.transform.position = new Vector3(v[0], H(v[0], v[1]) + v[2], v[1]);
                camGo.transform.LookAt(new Vector3(v[3], H(v[3], v[4]) + 1.5f, v[4]));
                SceneSnapshot.RenderTo(cam, file, 1600, 900);
            }
            Object.DestroyImmediate(volume.gameObject);
            Object.DestroyImmediate(camGo);
            Debug.Log($"[Snapshot] views {id} -> {outDir}");
        }
    }
}
