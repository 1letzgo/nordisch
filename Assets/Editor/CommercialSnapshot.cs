using System.IO;
using LeGamez.Editor.OsmImport;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LeGamez.Editor
{
    /// <summary>
    /// Imports one area and renders close-ups of every petrol station (street level ~25 m away, oblique view,
    /// price pylon) to Builds/Snapshots/&lt;area&gt;_fuel_&lt;n&gt;_*.png.
    /// Batch: -executeMethod LeGamez.Editor.CommercialSnapshot.ImportAndRender -area &lt;id&gt;
    /// </summary>
    public static class CommercialSnapshot
    {
        public static void ImportAndRender()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-area");
            string id = i >= 0 && i + 1 < args.Length ? args[i + 1] : "westerstede";
            string dataDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Data"));
            OsmAreaImporter.ImportArea(Path.Combine(dataDir, id + ".osm.json"));

            string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Snapshots"));
            Directory.CreateDirectory(outDir);
            var camGo = new GameObject("SnapshotCam");
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.2f;
            cam.farClipPlane = 1500f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            var volume = new GameObject("SnapshotVolume").AddComponent<Volume>();
            volume.isGlobal = true;
            var gm = LeGamez.Core.GameMaterials.Instance;
            if (gm != null) volume.sharedProfile = gm.postProfile;
            var terrain = TerrainField.Current;
            float H(Vector2 p) => terrain.Height(p.x, p.y);

            int n = 0;
            foreach (var s in CommercialBuilder.LastSites)
            {
                string tag = $"{id}_fuel_{n++}_{Safe(s.name)}";
                var c = s.centre;
                var toStreet = s.street - c;
                var dir = toStreet.sqrMagnitude > 1f ? toStreet.normalized : Vector2.down;
                // Street level: from the street side, 25 m out, slightly off-axis.
                var from = c + Rotate(dir, 25f) * 25f;
                camGo.transform.position = new Vector3(from.x, H(from) + 2.2f, from.y);
                camGo.transform.LookAt(new Vector3(c.x, H(c) + 2.6f, c.y));
                SceneSnapshot.RenderTo(cam, Path.Combine(outDir, tag + "_street.png"), 1600, 900);
                // Oblique view from above.
                var ob = c + Rotate(dir, -50f) * 34f;
                camGo.transform.position = new Vector3(ob.x, H(ob) + 20f, ob.y);
                camGo.transform.LookAt(new Vector3(c.x, H(c), c.y));
                SceneSnapshot.RenderTo(cam, Path.Combine(outDir, tag + "_oblique.png"), 1600, 900);
                // Under the canopy, between the islands.
                var under = c + Rotate(dir, 90f) * (s.size * 0.75f);
                camGo.transform.position = new Vector3(under.x, H(under) + 1.7f, under.y);
                camGo.transform.LookAt(new Vector3(c.x, H(c) + 1.6f, c.y));
                SceneSnapshot.RenderTo(cam, Path.Combine(outDir, tag + "_pumps.png"), 1600, 900);
                // Price pylon from the street.
                if (s.pylon != Vector2.zero)
                {
                    var along = new Vector2(-dir.y, dir.x);
                    var pc = s.pylon + along * 10f + dir * 4f;
                    camGo.transform.position = new Vector3(pc.x, H(pc) + 1.8f, pc.y);
                    camGo.transform.LookAt(new Vector3(s.pylon.x, H(s.pylon) + 3.6f, s.pylon.y));
                    SceneSnapshot.RenderTo(cam, Path.Combine(outDir, tag + "_pylon.png"), 1600, 900);
                }
                Debug.Log($"[Snapshot] {tag}: centre {c}, pylon {s.pylon}, street {s.street}");
            }
            n = 0;
            foreach (var s in CommercialBuilder.LastMarkets)
            {
                string tag = $"{id}_market_{n++}_{Safe(s.name)}";
                var c = s.centre;
                var dir = (s.street - c).normalized;
                var from = c + Rotate(dir, 22f) * 28f;
                camGo.transform.position = new Vector3(from.x, H(from) + 2.2f, from.y);
                camGo.transform.LookAt(new Vector3(c.x, H(c) + 3f, c.y));
                SceneSnapshot.RenderTo(cam, Path.Combine(outDir, tag + "_street.png"), 1600, 900);
                var ob = c + Rotate(dir, -35f) * 45f;
                camGo.transform.position = new Vector3(ob.x, H(ob) + 26f, ob.y);
                camGo.transform.LookAt(new Vector3(c.x, H(c), c.y));
                SceneSnapshot.RenderTo(cam, Path.Combine(outDir, tag + "_oblique.png"), 1600, 900);
                Debug.Log($"[Snapshot] {tag}: front {c}");
            }
            Object.DestroyImmediate(volume.gameObject);
            Object.DestroyImmediate(camGo);
        }

        static Vector2 Rotate(Vector2 v, float deg)
        {
            float r = deg * Mathf.Deg2Rad, cs = Mathf.Cos(r), sn = Mathf.Sin(r);
            return new Vector2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
        }

        static string Safe(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s ?? "") if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.Length > 0 ? sb.ToString() : "site";
        }
    }
}
