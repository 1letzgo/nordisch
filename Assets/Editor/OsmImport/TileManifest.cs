using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// What the last import wrote, in Assets/Generated/&lt;id&gt;/build~/ (Unity ignores "~" folders, not in the repo):
    /// - tiles.txt: signature (<see cref="TileBuild"/>) of every tile asset ("x z hash" per line). The builders always run
    ///   for the whole area, but only tiles whose signature changed are draped, meshed and written. Without the file (first
    ///   import, tiles deleted by hand) all tiles are written. MapExport uses the signatures to reuse unchanged pack entries.
    /// - maps-dirty.txt: tiles changed since the region images were rendered ("all" = everything) – SceneSnapshot renders
    ///   only the regions they touch and clears the list.
    /// </summary>
    public static class TileManifest
    {
        public static string Dir(string id) => $"Assets/Generated/{id}/build~";
        static string TilesPath(string id) => $"{Dir(id)}/tiles.txt";
        static string DirtyPath(string id) => $"{Dir(id)}/maps-dirty.txt";

        /// <summary>Tile hashes of the last import, null if unknown.</summary>
        public static Dictionary<Vector2Int, ulong> Load(string id)
        {
            string path = TilesPath(id);
            if (!File.Exists(path)) return null;
            var result = new Dictionary<Vector2Int, ulong>();
            foreach (var line in File.ReadAllLines(path))
            {
                var p = line.Split(' ');
                if (p.Length < 3) continue;
                result[new Vector2Int(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture))] =
                    ulong.Parse(p[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return result;
        }

        public static void Save(string id, Dictionary<Vector2Int, ulong> hashes)
        {
            Directory.CreateDirectory(Dir(id));
            var lines = hashes.OrderBy(kv => kv.Key.x).ThenBy(kv => kv.Key.y)
                .Select(kv => $"{kv.Key.x} {kv.Key.y} {kv.Value:x16}");
            File.WriteAllLines(TilesPath(id), lines);
        }

        /// <summary>Forgets the hashes (before the tiles are deleted/rewritten without them).</summary>
        public static void Clear(string id)
        {
            if (File.Exists(TilesPath(id))) File.Delete(TilesPath(id));
        }

        // ------------------------------------------------------------------ region images to re-render

        /// <summary>Adds changed tiles (null = all) to the list of tiles whose region images are out of date.</summary>
        public static void MarkMapsDirty(string id, IEnumerable<Vector2Int> tiles)
        {
            Directory.CreateDirectory(Dir(id));
            string path = DirtyPath(id);
            if (tiles == null) { File.WriteAllText(path, "all\n"); return; }
            if (File.Exists(path) && File.ReadAllText(path).StartsWith("all")) return;
            File.AppendAllLines(path, tiles.Select(k => $"{k.x} {k.y}"));
        }

        /// <summary>Tiles whose region images are out of date; null = all (also when nothing was recorded – unknown state).</summary>
        public static HashSet<Vector2Int> DirtyMaps(string id)
        {
            string path = DirtyPath(id);
            if (!File.Exists(path)) return null;
            var set = new HashSet<Vector2Int>();
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith("all")) return null;
                var p = line.Split(' ');
                if (p.Length < 2) continue;
                set.Add(new Vector2Int(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture)));
            }
            return set;
        }

        /// <summary>All region images are current (after SceneSnapshot rendered them).</summary>
        public static void ClearDirtyMaps(string id)
        {
            Directory.CreateDirectory(Dir(id));
            File.WriteAllText(DirtyPath(id), "");
        }
    }
}
