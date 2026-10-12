using System.Collections.Generic;
using LeGamez.Core;
using LeGamez.UI;
using LeGamez.World;
using UnityEngine;

namespace LeGamez.Quests
{
    /// <summary>Spawns floating markers for the area's POIs and awards stamps when the player arrives.</summary>
    public class PoiManager : MonoBehaviour
    {
        public float visitRadius = 14f;
        public Transform player;
        public Hud hud;

        readonly List<(PoiEntry poi, Transform marker)> markers = new List<(PoiEntry, Transform)>();
        AreaInfo area;
        Material openMat, doneMat;

        void Start()
        {
            area = AreaInfo.Current;
            if (area == null || area.pois == null) return;

            openMat = GameMaterials.Instance.poiOpen;
            doneMat = GameMaterials.Instance.poiDone;

            foreach (var poi in area.pois.entries)
            {
                var marker = CreateMarker(poi);
                markers.Add((poi, marker));
                SetVisited(marker, StampBook.Has(area.areaId, poi.id));
            }
            UpdateInfo();
        }

        Transform CreateMarker(PoiEntry poi)
        {
            var root = new GameObject($"POI {poi.name}").transform;
            root.SetParent(transform, false);
            root.position = poi.position;

            var pin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(pin.GetComponent<Collider>());
            pin.transform.SetParent(root, false);
            pin.transform.localPosition = new Vector3(0, 6f, 0);
            pin.transform.localRotation = Quaternion.Euler(45, 0, 45);
            pin.transform.localScale = Vector3.one * 1.4f;

            var beam = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(beam.GetComponent<Collider>());
            beam.transform.SetParent(root, false);
            beam.transform.localPosition = new Vector3(0, 2.5f, 0);
            beam.transform.localScale = new Vector3(0.12f, 2.5f, 0.12f);
            return root;
        }

        void SetVisited(Transform marker, bool visited)
        {
            foreach (var r in marker.GetComponentsInChildren<Renderer>())
            {
                r.sharedMaterial = visited ? doneMat : openMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        void Update()
        {
            if (area == null || player == null) return;
            float t = Time.time;
            PoiEntry nearestOpen = null;
            float nearestDist = float.MaxValue;

            foreach (var (poi, marker) in markers)
            {
                var pin = marker.GetChild(0);
                pin.localPosition = new Vector3(0, 6f + Mathf.Sin(t * 2f + poi.position.x) * 0.3f, 0);
                pin.Rotate(0, 60f * Time.deltaTime, 0, Space.World);

                Vector3 d = poi.position - player.position;
                d.y = 0;
                float dist = d.magnitude;
                bool visited = StampBook.Has(area.areaId, poi.id);
                if (!visited && dist < visitRadius && StampBook.Add(area.areaId, poi.id))
                {
                    SetVisited(marker, true);
                    hud?.ShowToast($"Stempel: {poi.name}!");
                    UpdateInfo();
                    visited = true;
                }
                if (!visited && dist < nearestDist) { nearestDist = dist; nearestOpen = poi; }
            }

            if (hud != null && nearestOpen != null && Time.frameCount % 15 == 0)
                UpdateInfo(nearestOpen, nearestDist);
        }

        void UpdateInfo(PoiEntry next = null, float dist = 0f)
        {
            if (hud == null || area == null) return;
            int total = area.pois.entries.Count;
            string text = $"Stempel {StampBook.CountForArea(area.areaId)}/{total}";
            if (next != null)
            {
                Vector3 dir = next.position - player.position;
                text += $"  ·  Nächstes Ziel: {next.name} ({Mathf.RoundToInt(dist)} m {Compass(dir)})";
            }
            hud.infoLabel.text = text;
        }

        static string Compass(Vector3 dir)
        {
            string[] names = { "N", "NO", "O", "SO", "S", "SW", "W", "NW" };
            float angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360f;
            return names[Mathf.RoundToInt(angle / 45f) % 8];
        }
    }
}
