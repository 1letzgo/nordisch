using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.World
{
    /// <summary>
    /// All imported areas, maintained by the OSM importer. Editor only: the app lists downloadable maps
    /// (Core/MapStore); this asset feeds the editor's menu and the map export (Editor/MapExport).
    /// </summary>
    public class AreaCatalog : ScriptableObject
    {
        public const string AssetPath = "Assets/Generated/AreaCatalog.asset";
        /// <summary>Menu card image (rendered by SceneSnapshot.RenderThumbnails, published next to the bundle).</summary>
        public static string ThumbPath(string id) => $"Assets/Generated/{id}/{id}_thumb.png";
        /// <summary>Card image of default start point <paramref name="k"/>; the first start uses the area thumbnail.</summary>
        public static string ThumbPath(string id, int k) => k <= 0 ? ThumbPath(id) : $"Assets/Generated/{id}/{id}_thumb_{k}.png";
        /// <summary>Small top-down overview of the whole area (JPEG, max. 2048 px) for the full map; AreaData.mapTexture, travels
        /// in the area file. The minimap uses the sharp per-region images (WorldStreamer.EditorRegionMapPath).</summary>
        public static string MapPath(string id) => $"Assets/Generated/{id}/{id}_map.jpg";

        [System.Serializable]
        public class Entry
        {
            public string id;
            public string displayName;
            public string sceneName;
            public double lat, lon;
            public int poiCount;
            public List<string> vehicles = new List<string>();
            /// <summary>Names of the default start points (AreaData.starts; empty before start lists = one, named like the area).</summary>
            public List<string> starts = new List<string>();
        }

        public List<Entry> areas = new List<Entry>();
    }
}
