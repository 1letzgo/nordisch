using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.World
{
    /// <summary>All playable areas, maintained by the OSM importer. Loaded from Resources at runtime.</summary>
    public class AreaCatalog : ScriptableObject
    {
        public const string ResourcePath = "AreaCatalog";

        [System.Serializable]
        public class Entry
        {
            public string id;
            public string displayName;
            public string sceneName;
            public double lat, lon;
            public int poiCount;
            public List<string> vehicles = new List<string>();
        }

        public List<Entry> areas = new List<Entry>();

        public static AreaCatalog Load() => Resources.Load<AreaCatalog>(ResourcePath);
    }
}
