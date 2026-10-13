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
        public List<Vector3> bikeSpots = new List<Vector3>();
        public List<Vector4> carSpots = new List<Vector4>();

        [Header("Content")]
        public PoiDatabase pois;
        public StreetIndex streets;
        public Texture2D mapTexture;
        public TextAsset facade;
        public List<Vector2Int> tileKeys = new List<Vector2Int>();
        public List<TrafficLights.Head> trafficHeads = new List<TrafficLights.Head>();
        public List<TrafficLights.Signal> trafficSignals = new List<TrafficLights.Signal>();
        public List<SignTexts.Item> signTexts = new List<SignTexts.Item>();
    }
}
