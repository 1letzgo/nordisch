using System.Collections.Generic;
using System.IO;
using System.Linq;
using LeGamez.World;
using UnityEditor;
using UnityEngine;

namespace LeGamez.Editor
{
    /// <summary>
    /// Content hash of every tile of an area (what the map export puts into the pack), one line per tile:
    /// "x_z hash triangles instances". Compares two imports exactly:
    /// -executeMethod LeGamez.Editor.TileDigest.Run -area &lt;id&gt; -out &lt;file&gt;
    /// </summary>
    public static class TileDigest
    {
        public static void Run()
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string name) { int i = System.Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string id = Arg("-area"), output = Arg("-out");
            var data = AssetDatabase.LoadAssetAtPath<AreaData>(OsmImport.OsmAreaImporter.AreaDataPath(id));
            var lines = new List<string>();
            long tris = 0, instances = 0;
            int n = 0;
            foreach (var k in data.tileKeys.OrderBy(k => k.x).ThenBy(k => k.y))
            {
                var t = AssetDatabase.LoadAssetAtPath<WorldTile>(WorldStreamer.EditorTilePath(id, k));
                if (t == null) { lines.Add($"{k.x}_{k.y} missing"); continue; }
                if (t.parts.Any(p => p.mesh == null)) { lines.Add($"{k.x}_{k.y} broken {t.parts.Count(p => p.mesh == null)} of {t.parts.Count} meshes missing"); continue; }
                long tt = t.parts.Sum(p => p.mesh != null ? (long)p.mesh.triangles.Length / 3 : 0);
                lines.Add($"{k.x}_{k.y} {MapFormat.TileHash(t):x16} {tt} {t.instances.Count}");
                tris += tt;
                instances += t.instances.Count;
                Resources.UnloadAsset(t);
                if (++n % 500 == 0) EditorUtility.UnloadUnusedAssetsImmediate();
            }
            File.WriteAllLines(output, lines);
            Debug.Log($"[Digest] {id}: {data.tileKeys.Count} tiles, {tris} tris, {instances} instances -> {output}");
        }
    }
}
