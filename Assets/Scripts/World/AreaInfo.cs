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
        [TextArea] public string attribution = "© OpenStreetMap-Mitwirkende";

        public static AreaInfo Current { get; private set; }

        public float HalfSize => sizeMeters * 0.5f;

        void Awake() => Current = this;

        void OnDestroy()
        {
            if (Current == this) Current = null;
        }

        public bool IsVehicleAllowed(string vehicleId) => allowedVehicles.Contains(vehicleId);
    }
}
