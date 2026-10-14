using System.Collections.Generic;
using System.IO;
using LeGamez.Editor.OsmImport;
using LeGamez.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LeGamez.Editor
{
    /// <summary>
    /// Guard rail check: a car-sized CharacterController (as VehicleManager sets it up) starts on every motorway
    /// carriageway every <c>-spacing</c> metres and "drives" straight ahead at 130 km/h, a few degrees off the lane,
    /// without steering. It must end against a rail or on another carriageway (ramp), never in the field.
    /// Batch: -executeMethod LeGamez.Editor.MotorwayDriveTest.Run -area westerstede [-spacing 150]
    /// Escapes are logged with position ("[DriveTest] ESCAPE …"); trees are not loaded (only rails may hold the car).
    /// -fresh: the tiles' props are skipped and the rails are built anew by MotorwayBuilder from the current code (no
    /// re-import needed to try a change; bridge decks still come from the tiles).
    /// </summary>
    public static class MotorwayDriveTest
    {
        public static void Run()
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string k, string d) { int i = System.Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string id = Arg("-area", "westerstede");
            float spacing = float.Parse(Arg("-spacing", "150"), System.Globalization.CultureInfo.InvariantCulture);
            var data = OsmData.Load(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Data", id + ".osm.json")));
            string dataDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Data"));
            AreaMask.Load(dataDir, id)?.Filter(data);
            bool fresh = System.Array.IndexOf(args, "-fresh") >= 0;
            string tr = Arg("-trace", null);
            bool trace = tr != null;
            var traceAt = trace ? new Vector2(float.Parse(tr.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(tr.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)) : Vector2.zero;

            // Every drivable carriageway (to accept ending on a ramp or the other carriageway), motorways as start lines.
            var roads = new List<(List<Vector2> pts, float hw)>();
            var motorways = new List<OsmWay>();
            foreach (var w in data.ways)
            {
                if (!GroundBuilder.Carriageway(w.tags, out float width, out _)) continue;
                roads.Add((w.points, width * 0.5f));
                if (w.tags.Is("highway", "motorway")) motorways.Add(w);
            }
            var starts = new List<(Vector2 p, Vector2 dir)>();
            foreach (var w in motorways)
            {
                float carry = 0f;
                for (int i = 0; i < w.points.Count - 1; i++)
                {
                    Vector2 a = w.points[i], b = w.points[i + 1];
                    float len = Vector2.Distance(a, b);
                    for (float s = spacing - carry; s < len; s += spacing) starts.Add((Vector2.Lerp(a, b, s / len), (b - a) / len));
                    carry = (carry + len) % spacing;
                }
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var keys = new HashSet<Vector2Int>();
            foreach (var (p, _) in starts)
                for (float dx = -400f; dx <= 400f; dx += 200f)
                for (float dz = -400f; dz <= 400f; dz += 200f)
                    keys.Add(WorldStreamer.KeyOf(p.x + dx, p.y + dz));
            int parts = 0;
            foreach (var k in keys)
            {
                var t = AssetDatabase.LoadAssetAtPath<WorldTile>(WorldStreamer.EditorTilePath(id, k));
                if (t == null) continue;
                foreach (var part in t.parts)
                {
                    if ((part.flags & WorldTile.FlagCollider) == 0 || fresh && part.group == WorldStyle.MatProps) continue;
                    var go = new GameObject(part.group) { layer = part.layer };
                    go.AddComponent<MeshCollider>().sharedMesh = part.mesh;
                    parts++;
                }
            }
            if (fresh)
            {
                var terrain = TerrainField.Load(Path.Combine(dataDir, id + ".dgm.bin"));
                BridgeBuilder.Prepare(data, data.meta.bounds, terrain);
                var sink = new MeshSink();
                var st = MotorwayBuilder.Build(data, sink, data.meta.bounds, terrain);
                BridgeBuilder.Build(data, sink, terrain); // decks again (on top of the tiles' ones): changes to them count too
                foreach (var kv in sink.Batches)
                {
                    if (kv.Key.mat != WorldStyle.MatProps && kv.Key.mat != WorldStyle.MatTerrain || kv.Value.vertices.Count == 0) continue;
                    if (kv.Key.mat == WorldStyle.MatProps) TerrainDrape.Lift(kv.Value, terrain);
                    var go = new GameObject("Rails") { layer = 2 };
                    go.AddComponent<MeshCollider>().sharedMesh = MeshSink.ToMesh(kv.Value, "rails", false);
                    parts++;
                }
                Debug.Log($"[DriveTest] fresh rails: {st.railKm:F1} km");
            }
            Physics.SyncTransforms();
            Debug.Log($"[DriveTest] {starts.Count} starts on {motorways.Count} motorway ways, {keys.Count} tiles, {parts} collider meshes");

            var player = new GameObject("Car") { layer = 2 };
            var cc = player.AddComponent<CharacterController>();
            cc.radius = 0.95f; cc.height = 1.95f; cc.center = new Vector3(0f, 0.975f, 0f); cc.stepOffset = 0.25f; cc.slopeLimit = 35f; // as VehicleManager sets it for cars
            const float step = 36.5f / 60f; // 130 km/h at 60 fps
            int runs = 0, escapes = 0, held = 0, onRoad = 0;
            var escaped = new List<Vector2>();
            foreach (var (p, dir) in starts)
            foreach (float angle in new[] { -25f, -10f, 10f, 25f })
            {
                if (!Physics.Raycast(new Vector3(p.x, 500f, p.y), Vector3.down, out var hit, 1000f)) continue;
                cc.enabled = false;
                player.transform.position = hit.point + Vector3.up * 0.1f;
                cc.enabled = true;
                Physics.SyncTransforms();
                var d3 = Quaternion.Euler(0f, angle, 0f) * new Vector3(dir.x, 0f, dir.y);
                bool blocked = false;
                float vy = 0f;
                for (int i = 0; i < 300; i++)
                {
                    vy = cc.isGrounded ? -2f / 60f : vy - 20f / 3600f;
                    var flags = cc.Move(d3 * step + Vector3.up * vy);
                    if ((flags & CollisionFlags.Sides) != 0) blocked = true;
                    if (trace && (p - traceAt).sqrMagnitude < 4f && i % 10 == 0)
                    {
                        var hits = Physics.OverlapSphere(player.transform.position + Vector3.up * 0.6f, 1.3f);
                        string names = "";
                        foreach (var h in hits) if (h.gameObject != player) names += h.name + " ";
                        Debug.Log($"[DriveTest] trace {angle} #{i}: {player.transform.position} sides {(flags & CollisionFlags.Sides) != 0} near {names}");
                    }
                }
                runs++;
                var q = new Vector2(player.transform.position.x, player.transform.position.z);
                bool road = false;
                foreach (var (pts, hw) in roads)
                {
                    for (int i = 0; i < pts.Count - 1 && !road; i++)
                    {
                        Vector2 a = pts[i], ab = pts[i + 1] - a;
                        float t = Mathf.Clamp01(Vector2.Dot(q - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                        road = Vector2.Distance(q, a + ab * t) < hw + 2.2f;
                    }
                    if (road) break;
                }
                if (road) { if (blocked) held++; else onRoad++; continue; }
                escapes++;
                escaped.Add(q);
                if (escapes <= 60) Debug.Log($"[DriveTest] ESCAPE start ({p.x:F0}, {p.y:F0}) angle {angle} -> ({q.x:F1}, {q.y:F1}) blocked-before {blocked}");
            }
            Object.DestroyImmediate(player);
            Debug.Log($"[DriveTest] {runs} runs: {held} held by a rail, {onRoad} still on a carriageway, {escapes} ESCAPED");
        }
    }
}
