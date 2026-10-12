using System.Collections.Generic;
using LeGamez.Quests;
using UnityEngine;

namespace LeGamez.World
{
    /// <summary>Static description of a playable area. Lives in the area scene, filled by the importer.</summary>
    public class AreaInfo : MonoBehaviour
    {
        public string areaId;
        public string displayName;
        public GeoProjection projection;
        public float sizeMeters = 1000f;
        public List<string> allowedVehicles = new List<string>();
        public PoiDatabase pois;
        public Transform spawnPoint;
        [Tooltip("Parked bicycles (OSM bicycle_parking), filled by the importer.")]
        public List<Vector3> bikeSpots = new List<Vector3>();
        [Tooltip("Checked free spot next to the spawn for the starter bike (zero = none).")]
        public Vector3 spawnBikeSpot;
        [TextArea] public string attribution = "© OpenStreetMap-Mitwirkende";

        public static AreaInfo Current { get; private set; }

        public float HalfSize => sizeMeters * 0.5f;

        /// <summary>Key for saved positions – changes when the area is re-centred or resized, so old spots are dropped.</summary>
        public string MemoryKey => $"{areaId}.{sizeMeters:F0}.{projection.originLat:F4}.{projection.originLon:F4}";

        void Awake() => Current = this;

        void OnDestroy()
        {
            if (Current == this) Current = null;
        }

        public bool IsVehicleAllowed(string vehicleId) => allowedVehicles.Contains(vehicleId);
    }
}
