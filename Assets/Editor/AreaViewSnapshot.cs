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
    /// Renders chosen views of one area (only the tiles around each view are loaded – fine for whole municipalities)
    /// to Builds/Snapshots/&lt;id&gt;_view_&lt;name&gt;.png.
    /// Batch: -executeMethod LeGamez.Editor.AreaViewSnapshot.Render -area &lt;id&gt; -views "name:x,z,yaw,height,pitch;…"
    /// (x, z local metres; yaw 0 = north, 90 = east; height above ground; pitch down in degrees).
    /// </summary>
    public static class AreaViewSnapshot
    {
        public static void Render()
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string k) { int i = System.Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string id = Arg("-area") ?? "westerstede", views = Arg("-views") ?? "centre:0,0,0,3,8";

            var data = AreaPreview.Find(id);
            if (data == null) { Debug.LogError($"[View] no area {id}"); return; }
            // Like AreaPreview.Open, but without loading every tile (whole municipalities are too big for that).
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            OsmImport.OsmAreaImporter.AddLighting(OsmImport.OsmAreaImporter.EnsureSkyMaterial());
            AreaLoader.Build(data, null, allTiles: false);
            var world = Object.FindAnyObjectByType<WorldStreamer>();
            field = OsmImport.OsmAreaImporter.PrepareTerrain(id); // as imported: road profiles, bridge pits, coarse terrain
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
                // Close-up facade details (window frames …) as the game builds them near the camera.
                var facade = Object.FindAnyObjectByType<FacadeDetails>();
                if (facade != null) facade.Refresh(pos, 1000);
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

        static TerrainField field;

        /// <summary>Ground height of the imported terrain field (bridge decks are not in it).</summary>
        static float GroundHeight(Vector3 p) => field.Height(p.x, p.z);
    }
}
