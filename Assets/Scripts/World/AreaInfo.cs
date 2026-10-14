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
        [Tooltip("Named streets for the HUD street display, filled by the importer.")]
        public StreetIndex streets;
        public Transform spawnPoint;
        [Tooltip("Parked bicycles (OSM bicycle_parking), filled by the importer.")]
        public List<Vector3> bikeSpots = new List<Vector3>();
        [Tooltip("Checked free spot next to the spawn for the starter bike (zero = none).")]
        public Vector3 spawnBikeSpot;
        [Tooltip("Default start points (the first = spawnPoint); GameBootstrap puts the starter bike at the nearest one.")]
        public List<AreaData.Start> starts = new List<AreaData.Start>();
        [Tooltip("Parked cars: (x, y, z, yaw), filled by the importer from car_spots in areas.json.")]
        public List<Vector4> carSpots = new List<Vector4>();
        [Tooltip("Top-down render of the area (+MapMargin) for minimap and full map, set by SceneSnapshot.RenderThumbnails.")]
        public Texture2D mapTexture;
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

        /// <summary>Index of the default start point closest to <paramref name="p"/> (x/z), -1 without start points.</summary>
        public int NearestStart(Vector3 p)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < starts.Count; i++)
            {
                float dx = starts[i].position.x - p.x, dz = starts[i].position.z - p.z, d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        public bool IsVehicleAllowed(string vehicleId) => allowedVehicles.Contains(vehicleId);
    }
}
