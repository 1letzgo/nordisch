using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using LeGamez.World;
using UnityEngine;

namespace LeGamez.Editor
{
    /// <summary>Triangle/vertex counts per material group of every area: -executeMethod LeGamez.Editor.MeshStats.Print</summary>
    public static class MeshStats
    {
        public static void Print()
        {
            foreach (var path in AssetDatabase.GetSubFolders("Assets/Generated").Where(f => AssetDatabase.IsValidFolder(f + "/tiles")))
            {
                var byMat = new Dictionary<string, (long tris, long verts, int meshes)>();
                var tilePaths = AssetDatabase.FindAssets("t:WorldTile", new[] { path + "/tiles" }).Select(AssetDatabase.GUIDToAssetPath).ToList();
                Debug.Log($"[Stats] {path}: {tilePaths.Count} tiles");
                foreach (var m in tilePaths.SelectMany(t => AssetDatabase.LoadAllAssetsAtPath(t).OfType<Mesh>()))
                {
                    var parts = m.name.Split('_');
                    string mat = parts.Length >= 4 ? string.Join("_", parts.Skip(1).Take(parts.Length - 3)) : m.name;
                    byMat.TryGetValue(mat, out var s);
                    byMat[mat] = (s.tris + m.triangles.Length / 3, s.verts + m.vertexCount, s.meshes + 1);
                }
                // Instanced trees/bushes/lamps: records, drawn from shared prototype meshes (World/InstancePrototypes).
                var perProto = new long[InstancePrototypes.Count];
                int calls = 0, withInstances = 0;
                foreach (var t in tilePaths.Select(AssetDatabase.LoadAssetAtPath<WorldTile>).Where(t => t != null))
                {
                    var used = new HashSet<int>();
                    foreach (var i in t.instances) { if (i.proto < perProto.Length) perProto[i.proto]++; used.Add(i.proto); }
                    calls += used.Count;
                    if (used.Count > 0) withInstances++;
                }
                long instances = perProto.Sum(), drawn = Enumerable.Range(0, perProto.Length).Sum(p => perProto[p] * InstancePrototypes.Triangles(p));
                if (instances > 0)
                    Debug.Log($"[Stats] {path}: {instances} instances ({drawn / 1000}k tris when drawn, {calls} tile×prototype batches = " +
                              $"{(float)calls / Mathf.Max(1, withInstances):0.0} per tile with instances, {withInstances} such tiles): " +
                              string.Join(", ", Enumerable.Range(0, perProto.Length).Where(p => perProto[p] > 0).Select(p => $"#{p} {perProto[p]}×{InstancePrototypes.Triangles(p)}")));
                long total = byMat.Values.Sum(v => v.tris);
                Debug.Log($"[Stats] {path}: {total / 1000}k tris, {byMat.Values.Sum(v => v.verts) / 1000}k verts\n" +
                          string.Join("\n", byMat.OrderByDescending(kv => kv.Value.tris)
                              .Select(kv => $"[Stats]   {kv.Key,-16} {kv.Value.tris / 1000,7}k tris {kv.Value.verts / 1000,7}k verts {kv.Value.meshes,5} meshes")));
            }
        }
    }
}
