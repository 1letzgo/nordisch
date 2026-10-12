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
        [Tooltip("Parked cars: (x, y, z, yaw), filled by the importer from car_spots in areas.json.")]
        public List<Vector4> carSpots = new List<Vector4>();
        [TextArea] public string attribution = "© OpenStreetMap-Mitwirkende";

        public static AreaInfo Current { get; private set; }

        [Tooltip("Playable rectangle in local metres (x/z). Areas need not be centred on the origin.")]
        public Vector2 boundsMin = new Vector2(-500f, -500f);
        public Vector2 boundsMax = new Vector2(500f, 500f);

        public Vector2 BoundsCenter => (boundsMin + boundsMax) * 0.5f;
        public Vector2 BoundsSize => boundsMax - boundsMin;

        /// <summary>Region covered by the pre-rendered map textures (area + 60 m margin).</summary>
        public const float MapMargin = 60f;
        public Vector2 MapSize => BoundsSize + Vector2.one * (2f * MapMargin);

        public bool Contains(Vector3 p, float margin = 0f) =>
            p.x >= boundsMin.x + margin && p.x <= boundsMax.x - margin && p.z >= boundsMin.y + margin && p.z <= boundsMax.y - margin;

        public Vector3 Clamp(Vector3 p, float margin = 0f) =>
            new Vector3(Mathf.Clamp(p.x, boundsMin.x + margin, boundsMax.x - margin), p.y, Mathf.Clamp(p.z, boundsMin.y + margin, boundsMax.y - margin));

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
