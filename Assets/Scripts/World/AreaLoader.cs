using LeGamez.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LeGamez.World
{
    /// <summary>
    /// Lives in the one world scene (Assets/Scenes/World.unity, build index 1). Builds the area set in <see cref="Pending"/>
    /// from its <see cref="AreaData"/>: tile streamer, traffic lights, sign texts, facade details, safety ground, area
    /// bounds, spawn and <see cref="AreaInfo"/> – then starts the gameplay (<see cref="GameBootstrap"/>).
    /// Materials and shaders come from the app (GameMaterials), map downloads carry only data and meshes.
    /// </summary>
    public class AreaLoader : MonoBehaviour
    {
        public const int WorldSceneIndex = 1;

        /// <summary>Area to show when the world scene starts (set by MapStore before loading the scene).</summary>
        public static AreaData Pending;

        void Start()
        {
            var data = Pending;
            Pending = null;
            if (data == null)
            {
                Debug.LogError("[World] no area to load – back to the menu");
                SceneManager.LoadScene(0);
                return;
            }
            Build(data, transform);
            new GameObject("GameBootstrap").AddComponent<GameBootstrap>();
        }

        /// <summary>Creates the area's objects under <paramref name="parent"/>. Editor tools pass allTiles to get every tile at once.</summary>
        public static GameObject Build(AreaData d, Transform parent, bool allTiles = false)
        {
            var gm = GameMaterials.Instance;
            var root = new GameObject($"World_{d.displayName}");
            root.transform.SetParent(parent, false);
            Material Mat(int i) => gm != null && i < gm.worldMaterials.Count ? gm.worldMaterials[i] : null;

            // Fields first, then activate: Awake/Start of these components read them.
            var streamer = Inactive<WorldStreamer>("WorldStreamer", root.transform);
            streamer.areaId = d.areaId;
            streamer.tileKeys = new System.Collections.Generic.List<Vector2Int>(d.tileKeys);
            streamer.materials = gm != null ? gm.worldMaterials : new System.Collections.Generic.List<Material>();
            streamer.gameObject.SetActive(true);

            AddGroundAndBounds(root.transform, d.boundsMin, d.boundsMax);

            if (d.trafficHeads.Count > 0)
            {
                var lights = Inactive<TrafficLights>("TrafficLights", root.transform);
                lights.material = Mat(WorldTile.MaterialTrees);
                lights.heads = new System.Collections.Generic.List<TrafficLights.Head>(d.trafficHeads);
                lights.signals = new System.Collections.Generic.List<TrafficLights.Signal>(d.trafficSignals);
                lights.gameObject.SetActive(true);
            }
            SignTexts.Build(root.transform, d.signTexts, gm != null ? gm.signTextMaterial : null);

            {
                // Records come per region from the streamer (RegionContent).
                var facade = Inactive<FacadeDetails>("FacadeDetails", root.transform);
                facade.streamer = streamer;
                facade.material = Mat(WorldTile.MaterialTrees);
                facade.gameObject.SetActive(true);
            }

            var spawn = new GameObject("SpawnPoint").transform;
            spawn.SetParent(root.transform, false);
            spawn.position = d.spawn;

            var info = Inactive<AreaInfo>("Area", root.transform);
            info.areaId = d.areaId;
            info.displayName = d.displayName;
            info.projection = d.projection;
            info.sizeMeters = d.sizeMeters;
            info.boundsMin = d.boundsMin;
            info.boundsMax = d.boundsMax;
            info.allowedVehicles = new System.Collections.Generic.List<string>(d.allowedVehicles);
            info.pois = d.pois;
            info.streets = d.streets;
            info.spawnPoint = spawn;
            info.bikeSpots = new System.Collections.Generic.List<Vector3>(d.bikeSpots);
            info.spawnBikeSpot = d.spawnBikeSpot;
            info.starts = new System.Collections.Generic.List<AreaData.Start>(d.StartList());
            info.carSpots = new System.Collections.Generic.List<Vector4>(d.carSpots);
            info.mapTexture = d.mapTexture;
            info.attribution = d.attribution;
            info.gameObject.SetActive(true);

#if UNITY_EDITOR
            if (allTiles) streamer.LoadAllInEditor();
#endif
            return root;
        }

        static T Inactive<T>(string name, Transform parent) where T : Component
        {
            var go = new GameObject(name);
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            return go.AddComponent<T>();
        }

        /// <summary>Safety net below the terrain collider + invisible walls around the playable rectangle.</summary>
        static void AddGroundAndBounds(Transform root, Vector2 min, Vector2 max)
        {
            var c = (min + max) * 0.5f;
            float w = max.x - min.x, h = max.y - min.y;
            var ground = new GameObject("GroundCollider");
            ground.transform.SetParent(root, false);
            var box = ground.AddComponent<BoxCollider>();
            box.size = new Vector3(w + 120f, 1f, h + 120f);
            box.center = new Vector3(c.x, -6f, c.y);

            var bounds = new GameObject("AreaBounds").transform;
            bounds.SetParent(root, false);
            const float height = 60f, t = 2f;
            Wall(bounds, new Vector3(c.x, height / 2, max.y + t / 2), new Vector3(w + t * 2, height, t));
            Wall(bounds, new Vector3(c.x, height / 2, min.y - t / 2), new Vector3(w + t * 2, height, t));
            Wall(bounds, new Vector3(max.x + t / 2, height / 2, c.y), new Vector3(t, height, h));
            Wall(bounds, new Vector3(min.x - t / 2, height / 2, c.y), new Vector3(t, height, h));
        }

        static void Wall(Transform parent, Vector3 center, Vector3 size)
        {
            var go = new GameObject("Wall");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.AddComponent<BoxCollider>().size = size;
        }
    }
}
