using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Hand edits from Data/&lt;area&gt;.edits.json, written by the macOS map editor (Tools/MapEditor):
    /// <code>{ "version": 2,
    ///   "objects": [ { "type": "bench", "x": 12.5, "z": -40, "yaw": 90, "scale": 1, "seed": 7 } ],
    ///   "changes": { "tree@123,-456": { "x": 12.6, "z": -45.1, "yaw": 0, "scale": 1.4 }, "lamp@10,20": { "deleted": true } } }</code>
    /// <c>objects</c> are new items; <c>changes</c> move/rotate/scale/delete generated ones by their id
    /// (see <see cref="Emit"/>, listed in Assets/Generated/&lt;area&gt;/&lt;area&gt;_objects.json).
    /// Yaw is clockwise from north, like Quaternion.Euler(0, yaw, 0).
    /// </summary>
    public static class EditsPlacer
    {
        struct Change { public bool deleted; public float x, z, yaw, scale; }
        struct Record { public string id, type; public float x, z, yaw, size; }

        static Dictionary<string, Change> changes = new Dictionary<string, Change>();
        static readonly HashSet<string> usedChanges = new HashSet<string>();
        /// <summary>A change whose id no longer exists still applies to the same type within this distance (placement tweaks).</summary>
        const float RematchRadius = 1f;
        static readonly List<Record> records = new List<Record>();
        static readonly Dictionary<string, int> idCounts = new Dictionary<string, int>();
        static List<object> newObjects;
        public static int ChangedCount { get; private set; }

        /// <summary>Loads the edits file (if any) – call before the placers run.</summary>
        public static void Begin(string path)
        {
            changes = new Dictionary<string, Change>();
            records.Clear();
            idCounts.Clear();
            newObjects = null;
            ChangedCount = 0;
            usedChanges.Clear();
            if (!File.Exists(path)) return;
            var root = MiniJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
            if (root == null) return;
            if (root.TryGetValue("objects", out var list)) newObjects = list as List<object>;
            if (root.TryGetValue("changes", out var ch) && ch is Dictionary<string, object> dict)
                foreach (var kv in dict)
                {
                    var o = (Dictionary<string, object>)kv.Value;
                    changes[kv.Key] = new Change
                    {
                        deleted = o.TryGetValue("deleted", out var d) && d is bool b && b,
                        x = Num(o, "x", 0f), z = Num(o, "z", 0f), yaw = Num(o, "yaw", 0f), scale = Num(o, "scale", 1f),
                    };
                }
        }

        /// <summary>
        /// Every generated object goes through here. <paramref name="build"/>(sink, position, yaw) must create
        /// the object; it always runs (even for deleted ones) so the shared random sequence – and with it every
        /// other object – stays the same. The id is type + position (dm), stable as long as the OSM data is.
        /// </summary>
        public static void Emit(MeshSink sink, string type, Vector2 p, float yaw, float size, System.Action<MeshSink, Vector2, float> build)
        {
            string id = $"{type}@{Mathf.RoundToInt(p.x * 10f)},{Mathf.RoundToInt(p.y * 10f)}";
            idCounts.TryGetValue(id, out int n);
            idCounts[id] = n + 1;
            if (n > 0) id += "#" + n;
            records.Add(new Record { id = id, type = type, x = p.x, z = p.y, yaw = yaw, size = size });

            if (!changes.TryGetValue(id, out var c) && !TryRematch(type, p, out c)) { build(sink, p, yaw); return; }
            usedChanges.Add(id);
            ChangedCount++;
            var local = new MeshSink();
            build(local, Vector2.zero, 0f);
            if (!c.deleted) AppendAll(sink, local, c.x, c.z, c.yaw, Mathf.Clamp(c.scale, 0.1f, 20f));
        }

        /// <summary>Unused change of the same type whose id position ("type@x10,z10") is within <see cref="RematchRadius"/>.</summary>
        static bool TryRematch(string type, Vector2 p, out Change c)
        {
            c = default;
            string prefix = type + "@";
            foreach (var kv in changes)
            {
                if (!kv.Key.StartsWith(prefix) || usedChanges.Contains(kv.Key)) continue;
                var parts = kv.Key.Substring(prefix.Length).Split('#')[0].Split(',');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int z)) continue;
                if ((new Vector2(x / 10f, z / 10f) - p).sqrMagnitude > RematchRadius * RematchRadius) continue;
                usedChanges.Add(kv.Key);
                c = kv.Value;
                return true;
            }
            return false;
        }

        /// <summary>Hand-placed new objects; returns how many were built.</summary>
        public static int PlaceNew(MeshSink sink, AreaRect area)
        {
            if (newObjects == null) return 0;
            int count = 0;
            foreach (Dictionary<string, object> o in newObjects)
            {
                string type = o.TryGetValue("type", out var t) ? (string)t : null;
                float x = Num(o, "x", 0f), z = Num(o, "z", 0f);
                if (type == null || !area.Contains(new Vector2(x, z))) continue;
                var rng = new System.Random((int)Num(o, "seed", 0f));
                var local = new MeshSink();
                if (!FurniturePlacer.BuildSingle(type, local, rng) && !PropPlacer.BuildSingle(type, local, rng))
                {
                    Debug.LogWarning($"[OSM] edits: unknown object type '{type}'");
                    continue;
                }
                AppendAll(sink, local, x, z, Num(o, "yaw", 0f), Mathf.Clamp(Num(o, "scale", 1f), 0.1f, 20f));
                FurniturePlacer.LastOccupied.Add(new Vector2(x, z));
                count++;
            }
            return count;
        }

        /// <summary>Writes all generated objects (original placement) for the map editor.</summary>
        public static void SaveRecords(string path)
        {
            var sb = new StringBuilder("{\"objects\":[\n");
            var ci = CultureInfo.InvariantCulture;
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                sb.Append(string.Format(ci, "{{\"id\":\"{0}\",\"type\":\"{1}\",\"x\":{2:0.##},\"z\":{3:0.##},\"yaw\":{4:0.#},\"size\":{5:0.###}}}",
                    r.id, r.type, r.x, r.z, r.yaw, r.size));
                sb.Append(i < records.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("]}\n");
            File.WriteAllText(path, sb.ToString());
        }

        static float Num(Dictionary<string, object> o, string key, float fallback) =>
            o.TryGetValue(key, out var v) && v is double d ? (float)d : fallback;

        static void AppendAll(MeshSink sink, MeshSink local, float x, float z, float yaw, float scale)
        {
            var pos = new Vector3(x, 0f, z);
            var rot = Quaternion.Euler(0f, yaw, 0f);
            foreach (var kv in local.Batches)
            {
                var src = kv.Value;
                var dst = sink.Get(kv.Key.mat, new Vector2(x, z));
                int offset = dst.vertices.Count;
                foreach (var v in src.vertices) dst.vertices.Add(pos + rot * (v * scale));
                foreach (var n in src.normals) dst.normals.Add(rot * n);
                dst.colors.AddRange(src.colors);
                dst.uvs.AddRange(src.uvs);
                foreach (var i in src.triangles) dst.triangles.Add(i + offset);
            }
            foreach (var inst in local.instances)
            {
                var moved = inst;
                moved.position = pos + rot * (inst.position * scale);
                moved.yaw = inst.yaw + yaw;
                moved.scale = inst.scale * scale;
                sink.instances.Add(moved);
            }
        }
    }
}
