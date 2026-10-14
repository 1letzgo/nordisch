using System.Collections.Generic;
using LeGamez.Quests;
using UnityEngine;

namespace LeGamez.World
{
    /// <summary>
    /// Everything an area needs besides its world tiles, written by the importer (Assets/Generated/&lt;id&gt;/&lt;id&gt;_area.asset)
    /// and turned into objects at runtime by <see cref="AreaLoader"/> inside the one world scene. Replaces the former
    /// per-area scene, so areas can be swapped without loading another scene and their data can be downloaded alone.
    /// </summary>
    public class AreaData : ScriptableObject
    {
        public string areaId;
        public string displayName;
        public GeoProjection projection;
        public float sizeMeters = 1000f;
        public Vector2 boundsMin, boundsMax;
        public List<string> allowedVehicles = new List<string>();
        [TextArea] public string attribution;

        [Header("Start")]
        public Vector3 spawn;
        public Vector3 spawnBikeSpot;
        /// <summary>Default start points (areas.json "starts"); the first one is <see cref="spawn"/>. Empty in data written
        /// before start lists existed – use <see cref="StartList"/>.</summary>
        public List<Start> starts = new List<Start>();
        public List<Vector3> bikeSpots = new List<Vector3>();
        public List<Vector4> carSpots = new List<Vector4>();

        [Header("Content")]
        public PoiDatabase pois;
        public StreetIndex streets;
        /// <summary>Overview of the whole area for the full map (small); the minimap uses the sharp region images.</summary>
        public Texture2D mapTexture;
        /// <summary>All facade records (editor only – downloads carry them per region, see <see cref="RegionContent"/>).</summary>
        public TextAsset facade;
        public List<Vector2Int> tileKeys = new List<Vector2Int>();
        /// <summary>Downloaded maps (format 6): index of the area's world pack – entry name → offset/length/hash.</summary>
        [System.NonSerialized] public Dictionary<string, MapFormat.PackEntry> pack;
        public List<TrafficLights.Head> trafficHeads = new List<TrafficLights.Head>();
        public List<TrafficLights.Signal> trafficSignals = new List<TrafficLights.Signal>();
        public List<SignTexts.Item> signTexts = new List<SignTexts.Item>();

        /// <summary>A default start point of the area: menu card / "Andere Orte" row, starter bike next to it.</summary>
        [System.Serializable]
        public class Start
        {
            public string name;
            public Vector3 position;
            public float yaw;
            public Vector3 bikeSpot; // zero = none found
        }

        /// <summary>The start points; older data without a list has exactly one: the spawn, named after the area.</summary>
        public List<Start> StartList()
        {
            if (starts != null && starts.Count > 0) return starts;
            return new List<Start> { new Start { name = displayName, position = spawn, bikeSpot = spawnBikeSpot } };
        }
    }
}
