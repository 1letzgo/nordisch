using System.Collections.Generic;
using System.Linq;
using LeGamez.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LeGamez.Editor
{
    /// <summary>
    /// Editor stand-in for the per-area scenes of old: opens a new unsaved scene with the world lighting and the area built
    /// from its <see cref="AreaData"/> with all tiles loaded (snapshots, thumbnails, close-up checks).
    /// </summary>
    public static class AreaPreview
    {
        public static IEnumerable<AreaData> All() =>
            AssetDatabase.FindAssets("t:AreaData", new[] { "Assets/Generated" })
                .Select(g => AssetDatabase.LoadAssetAtPath<AreaData>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(d => d != null);

        public static AreaData Find(string id) => All().FirstOrDefault(d => d.areaId == id);

        /// <summary>Opens the preview; returns the area's AreaInfo (also findable via FindAnyObjectByType).
        /// <paramref name="allTiles"/> false: no tiles yet (big areas – load parts with WorldStreamer.*InEditor).</summary>
        public static AreaInfo Open(AreaData data, bool allTiles = true)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            OsmImport.OsmAreaImporter.AddLighting(OsmImport.OsmAreaImporter.EnsureSkyMaterial());
            var root = AreaLoader.Build(data, null, allTiles);
            return root.GetComponentInChildren<AreaInfo>(true);
        }
    }
}
