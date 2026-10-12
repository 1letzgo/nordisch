using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LeGamez.Core;
using LeGamez.Quests;
using LeGamez.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Turns Data/&lt;area&gt;.osm.json into a playable scene at Assets/Scenes/Areas/&lt;Name&gt;.unity.
    /// Menu: LeGamez/Import Area… and LeGamez/Import All Areas.
    /// Batch: Unity -batchmode -executeMethod LeGamez.Editor.OsmImport.OsmAreaImporter.ImportAllBatch -quit
    /// </summary>
    public static class OsmAreaImporter
    {
        const string GeneratedRoot = "Assets/Generated";
        const string MaterialsDir = GeneratedRoot + "/Materials";
        const string AreaScenesDir = "Assets/Scenes/Areas";
        const string ShaderName = "LeGamez/VertexColorLit";

        static string DataDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Data"));

        [MenuItem("LeGamez/Import Area…")]
        static void ImportAreaMenu()
        {
            var path = EditorUtility.OpenFilePanel("OSM area JSON", DataDir, "json");
            if (!string.IsNullOrEmpty(path)) ImportArea(path);
        }

        [MenuItem("LeGamez/Import All Areas")]
        public static void ImportAll()
        {
            CreateMenuScene();
            var files = Directory.GetFiles(DataDir, "*.osm.json").OrderBy(f => f).ToArray();
            for (int i = 0; i < files.Length; i++)
            {
                EditorUtility.DisplayProgressBar("LeGamez OSM Import", Path.GetFileName(files[i]), (float)i / files.Length);
                try { ImportArea(files[i]); }
                finally { EditorUtility.ClearProgressBar(); }
            }
        }

        public static void ImportAllBatch()
        {
            ImportAll();
            SceneSnapshot.RenderThumbnails();
            CreateMenuScene();
            AssetDatabase.SaveAssets();
        }

        const string MenuScenePath = "Assets/Scenes/MainMenu.unity";

        [MenuItem("LeGamez/Create Main Menu Scene")]
        public static void CreateMenuScene()
        {
            EnsureFolder("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cam.tag = "MainCamera";
            AddLighting(EnsureSkyMaterial());
            RenderSettings.fog = false;
            new GameObject("MainMenu").AddComponent<LeGamez.UI.MainMenu>();
            EditorSceneManager.SaveScene(scene, MenuScenePath);

            // Menu must be build index 0.
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != MenuScenePath && File.Exists(s.path)).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(MenuScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        static void UpdateCatalog(OsmAreaMeta meta, int poiCount)
        {
            EnsureFolder("Assets/Resources");
            string path = $"Assets/Resources/{AreaCatalog.ResourcePath}.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<AreaCatalog>(path);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<AreaCatalog>();
                AssetDatabase.CreateAsset(catalog, path);
            }
            catalog.areas.RemoveAll(a => a.id == meta.id);
            catalog.areas.Add(new AreaCatalog.Entry
            {
                id = meta.id,
                displayName = meta.name,
                sceneName = meta.name,
                lat = meta.lat,
                lon = meta.lon,
                poiCount = poiCount,
                vehicles = new List<string>(meta.vehicles),
            });
            catalog.areas.Sort((a, b) => b.lat.CompareTo(a.lat));
            EditorUtility.SetDirty(catalog);
        }

        public static string ImportArea(string jsonPath)
        {
            var sw = Stopwatch.StartNew();
            var data = OsmData.Load(jsonPath);
            var meta = data.meta;
            float half = meta.sizeM * 0.5f;
            var rng = new System.Random(meta.id.GetHashCode());

            var terrain = TerrainField.Load(Path.Combine(Path.GetDirectoryName(jsonPath), meta.id + ".dgm.bin"));
            var sink = new MeshSink();
            GroundBuilder.BuildBase(sink, half, WorldStyle.GroundColor(meta.ground), terrain);
            GroundBuilder.BuildAreas(data, sink, half);
            int lines = GroundBuilder.BuildLines(data, sink, half);
            // Prefer official LGLN LoD2 buildings (real roof shapes); fall back to OSM extrusion.
            string lod2Path = Path.Combine(Path.GetDirectoryName(jsonPath), meta.id + ".lod2.json");
            bool island = meta.ground == "sand";
            bool hasLod2 = Lod2Builder.TryBuild(lod2Path, data, sink, half, island, out int buildings);
            if (!hasLod2) buildings = BuildingBuilder.Build(data, sink, half, rng);
            int trees = PropPlacer.PlaceTrees(data, sink, half, rng);
            int lamps = PropPlacer.PlaceStreetLamps(data, sink, half);
            int barriers = GroundBuilder.BuildBarriers(data, sink, half);
            var furniture = FurniturePlacer.Place(data, sink, half, rng);
            var pois = PoiExtractor.Extract(data, half);
            foreach (var poi in pois) poi.position.y = terrain.Height(poi.position.x, poi.position.z);

            // Fit everything onto the terrain: overlays are cut & draped, small objects lifted.
            foreach (var kv in sink.Batches)
            {
                string m = kv.Key.mat;
                if (m == WorldStyle.MatGround || m == WorldStyle.MatWater) TerrainDrape.Drape(kv.Value, terrain);
                else if (m == WorldStyle.MatTrees || m == WorldStyle.MatTreeTrunks || m == WorldStyle.MatProps) TerrainDrape.Lift(kv.Value, terrain);
            }

            string areaDir = $"{GeneratedRoot}/{meta.id}";
            EnsureFolder(areaDir);
            EnsureFolder(AreaScenesDir);
            var materials = EnsureMaterials();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var worldRoot = new GameObject($"World_{meta.name}");
            worldRoot.isStatic = true;

            // Meshes go into a single asset file per area.
            string meshAssetPath = $"{areaDir}/{meta.id}_meshes.asset";
            AssetDatabase.DeleteAsset(meshAssetPath);
            var container = ScriptableObject.CreateInstance<GeneratedMeshes>();
            AssetDatabase.CreateAsset(container, meshAssetPath);

            var groups = new Dictionary<string, Transform>();
            foreach (var kv in sink.Batches)
            {
                var (mat, chunk) = kv.Key;
                if (kv.Value.vertices.Count == 0) continue;
                if (!groups.TryGetValue(mat, out var parent))
                {
                    parent = new GameObject(mat).transform;
                    parent.SetParent(worldRoot.transform, false);
                    groups[mat] = parent;
                }

                var mesh = MeshSink.ToMesh(kv.Value, $"{meta.id}_{mat}_{chunk.x}_{chunk.y}");
                MeshUtility.Optimize(mesh);
                AssetDatabase.AddObjectToAsset(mesh, container);

                var go = new GameObject($"{mat}_{chunk.x}_{chunk.y}");
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = materials[WorldStyle.MaterialOf(mat)];
                bool casts = mat != WorldStyle.MatGround && mat != WorldStyle.MatWater;
                mr.shadowCastingMode = casts ? ShadowCastingMode.On : ShadowCastingMode.Off;
                if (WorldStyle.HasCollider(mat))
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                // Thin props (lamps, trunks, fences) block the player but not the camera probe.
                if (mat == WorldStyle.MatProps || mat == WorldStyle.MatTreeTrunks) go.layer = 2;
                // Water: collider on the built-in "Water" layer so placement can detect it; the player walks through.
                if (mat == WorldStyle.MatWater)
                {
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                    go.layer = 4;
                }
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
            }

            AddGroundAndBounds(worldRoot.transform, half);
            AddLighting(EnsureSkyMaterial());

            // POI database asset.
            string poiPath = $"{areaDir}/{meta.id}_pois.asset";
            var poiDb = AssetDatabase.LoadAssetAtPath<PoiDatabase>(poiPath);
            if (poiDb == null)
            {
                poiDb = ScriptableObject.CreateInstance<PoiDatabase>();
                AssetDatabase.CreateAsset(poiDb, poiPath);
            }
            poiDb.entries = pois;
            EditorUtility.SetDirty(poiDb);

            var spawn = new GameObject("SpawnPoint").transform;
            var spawnXZ = FindSpawn(data, half);
            spawn.position = new Vector3(spawnXZ.x, terrain.Height(spawnXZ.x, spawnXZ.z) + 0.2f, spawnXZ.z);

            var areaGo = new GameObject("Area");
            var info = areaGo.AddComponent<AreaInfo>();
            info.areaId = meta.id;
            info.displayName = meta.name;
            info.projection = data.projection;
            info.sizeMeters = meta.sizeM;
            info.allowedVehicles = new List<string>(meta.vehicles);
            info.pois = poiDb;
            info.spawnPoint = spawn;
            info.bikeSpots = BikeSpots(data, half, terrain);
            var spawn2 = new Vector2(spawn.position.x, spawn.position.z);
            var bikeSpot = FreeBikeSpot(data, spawn2, NearbyBuildings(data, spawn2, 15f));
            info.spawnBikeSpot = bikeSpot.HasValue
                ? new Vector3(bikeSpot.Value.x, terrain.Height(bikeSpot.Value.x, bikeSpot.Value.y), bikeSpot.Value.y)
                : Vector3.zero;
            info.attribution = hasLod2 ? "© OpenStreetMap-Mitwirkende (ODbL) · 3D-Gebäude © LGLN (CC BY 4.0)" : "© OpenStreetMap-Mitwirkende (ODbL)";
            spawn.SetParent(areaGo.transform, true);

            new GameObject("GameBootstrap").AddComponent<GameBootstrap>();

            string scenePath = $"{AreaScenesDir}/{meta.name}.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            AddSceneToBuild(scenePath);
            UpdateCatalog(meta, pois.Count);
            AssetDatabase.SaveAssets();

            Debug.Log($"[OSM] {meta.name}: {buildings} buildings, {lines} lines, {trees} plants, {lamps} lamps, {barriers} barriers, terrain {(terrain.IsFlat ? "flat" : "DGM1")}, " +
                      $"furniture {furniture.mapped} mapped/{furniture.benches} benches/{furniture.planters} planters/{furniture.seating} café tables/{furniture.fountains} fountains, {pois.Count} POIs, " +
                      $"{sink.Batches.Count()} meshes in {sw.ElapsedMilliseconds} ms -> {scenePath}");
            return scenePath;
        }

        static Vector3 FindSpawn(OsmData data, float half)
        {
            // Walkable way vertex near the centre with open space around it (no narrow alleys,
            // never inside a building). Wider street types are preferred.
            var buildings = new List<List<Vector2>>();
            foreach (var p in data.polygons)
                if (p.tags.Has("building") && GeoUtil.Overlaps(p.outer, 200f))
                    buildings.Add(PolygonTriangulator.Clean(p.outer));

            bool Blocked(Vector2 p) => IsObstructed(data, p, 0f);

            Vector2 best = Vector2.zero;
            float bestScore = float.MaxValue;
            foreach (var w in data.ways)
            {
                float penalty;
                switch (w.tags.Get("highway"))
                {
                    case "pedestrian": case "living_street": penalty = 0f; break;
                    case "residential": case "footway": penalty = 40f; break;
                    case "path": case "cycleway": penalty = 60f; break;
                    default: continue;
                }
                foreach (var p in w.points)
                {
                    if (p.magnitude > 200f) continue;
                    float clearance = Clearance(p, buildings);
                    if (clearance < 6f || Blocked(p)) continue;
                    float score = p.magnitude + penalty - Mathf.Min(clearance, 20f);
                    if (score < bestScore) { bestScore = score; best = p; }
                }
            }
            return new Vector3(best.x, 0f, best.y);
        }

        static List<(Vector2 p, float r)> obstacleCache;
        static List<List<Vector2>> waterCache;
        static OsmData obstacleData;

        /// <summary>
        /// True if the point is inside/near street furniture (fountains, benches, statues, bike stands,
        /// picnic tables) or inside a water area. <paramref name="extra"/> widens every radius.
        /// </summary>
        static bool IsObstructed(OsmData data, Vector2 p, float extra)
        {
            if (obstacleData != data)
            {
                obstacleData = data;
                obstacleCache = new List<(Vector2, float)>();
                waterCache = new List<List<Vector2>>();
                foreach (var n in data.nodes)
                {
                    var t = n.tags;
                    float r = t.Is("amenity", "fountain") ? 5f
                            : t.Is("amenity", "bench") || t.Is("amenity", "bicycle_parking") || t.Is("tourism", "artwork")
                              || t.Is("historic", "memorial") || t.Is("leisure", "picnic_table") || t.Is("amenity", "waste_basket") ? 2.5f : 0f;
                    if (r > 0f) obstacleCache.Add((data.projection.ToLocal(n.lat, n.lon), r));
                }
                foreach (var poly in data.polygons)
                {
                    if (poly.tags.Is("amenity", "fountain"))
                    {
                        var bb = GeoUtil.Bounds(poly.outer);
                        obstacleCache.Add((bb.center, Mathf.Max(bb.width, bb.height) * 0.5f + 3f));
                    }
                    if (poly.tags.Is("natural", "water") || poly.tags.Has("water") || poly.tags.Is("leisure", "swimming_pool"))
                        waterCache.Add(PolygonTriangulator.Clean(poly.outer));
                }
            }
            foreach (var (o, r) in obstacleCache) if ((o - p).sqrMagnitude < (r + extra) * (r + extra)) return true;
            foreach (var o in FurniturePlacer.LastOccupied) if ((o - p).sqrMagnitude < (2f + extra) * (2f + extra)) return true;
            foreach (var w in waterCache) if (w.Count > 2 && OsmData.PointInRing(p, w)) return true;
            return false;
        }

        /// <summary>Free spot for a parked bike near <paramref name="around"/>: clear of furniture, water and buildings.</summary>
        static Vector2? FreeBikeSpot(OsmData data, Vector2 around, List<List<Vector2>> buildings)
        {
            for (float r = 1.8f; r <= 10f; r += 1.2f)
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI / 6f;
                var p = around + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                if (IsObstructed(data, p, 0f) || Clearance(p, buildings) < 2f) continue;
                return p;
            }
            return null;
        }

        static List<List<Vector2>> NearbyBuildings(OsmData data, Vector2 c, float radius)
        {
            var list = new List<List<Vector2>>();
            foreach (var poly in data.polygons)
                if (poly.tags.Has("building") && (GeoUtil.Centroid(poly.outer) - c).magnitude < radius + 60f)
                    list.Add(PolygonTriangulator.Clean(poly.outer));
            return list;
        }

        /// <summary>Up to 40 bicycle parkings, at least 40 m apart, as places for parked bikes.</summary>
        static List<Vector3> BikeSpots(OsmData data, float half, TerrainField terrain)
        {
            var spots = new List<Vector3>();
            foreach (var n in data.nodes)
            {
                if (!n.tags.Is("amenity", "bicycle_parking")) continue;
                var p = data.projection.ToLocal(n.lat, n.lon);
                if (Mathf.Abs(p.x) > half - 5 || Mathf.Abs(p.y) > half - 5) continue;
                bool near = false;
                foreach (var s in spots) if ((new Vector2(s.x, s.z) - p).sqrMagnitude < 40f * 40f) { near = true; break; }
                if (near) continue;
                // Beside the stands (not inside them, nor in a fountain or pond next to them).
                var free = FreeBikeSpot(data, p, NearbyBuildings(data, p, 15f));
                if (free == null) continue;
                var q = free.Value;
                spots.Add(new Vector3(q.x, terrain.Height(q.x, q.y), q.y));
                if (spots.Count >= 40) break;
            }
            return spots;
        }

        static float Clearance(Vector2 p, List<List<Vector2>> rings)
        {
            float min = float.MaxValue;
            foreach (var ring in rings)
            {
                if (ring.Count < 3) continue;
                if (OsmData.PointInRing(p, ring)) return 0f;
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                {
                    Vector2 a = ring[j], b = ring[i], ab = b - a;
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                    min = Mathf.Min(min, Vector2.Distance(p, a + ab * t));
                }
            }
            return min;
        }

        static void AddGroundAndBounds(Transform root, float half)
        {
            var ground = new GameObject("GroundCollider");
            ground.transform.SetParent(root, false);
            var box = ground.AddComponent<BoxCollider>();
            box.size = new Vector3(half * 2f + 120f, 1f, half * 2f + 120f);
            box.center = new Vector3(0f, -6f, 0f); // safety net below the terrain mesh collider

            var bounds = new GameObject("AreaBounds");
            bounds.transform.SetParent(root, false);
            const float height = 60f, thickness = 2f;
            float size = half * 2f;
            AddWall(bounds.transform, new Vector3(0, height / 2, half + thickness / 2), new Vector3(size + thickness * 2, height, thickness));
            AddWall(bounds.transform, new Vector3(0, height / 2, -half - thickness / 2), new Vector3(size + thickness * 2, height, thickness));
            AddWall(bounds.transform, new Vector3(half + thickness / 2, height / 2, 0), new Vector3(thickness, height, size));
            AddWall(bounds.transform, new Vector3(-half - thickness / 2, height / 2, 0), new Vector3(thickness, height, size));
        }

        static void AddWall(Transform parent, Vector3 center, Vector3 size)
        {
            var go = new GameObject("Wall");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.AddComponent<BoxCollider>().size = size;
        }

        static Material EnsureSkyMaterial()
        {
            string path = $"{MaterialsDir}/Sky.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/Procedural")) { name = "Sky" };
                AssetDatabase.CreateAsset(sky, path);
            }
            sky.SetFloat("_SunSize", 0.035f);
            sky.SetFloat("_SunSizeConvergence", 6f);
            sky.SetFloat("_AtmosphereThickness", 0.75f);
            sky.SetColor("_SkyTint", new Color(0.52f, 0.66f, 0.86f));
            sky.SetColor("_GroundColor", new Color(0.74f, 0.82f, 0.90f)); // = fog colour, hides the horizon seam
            sky.SetFloat("_Exposure", 1.15f);
            EditorUtility.SetDirty(sky);
            return sky;
        }

        static void AddLighting(Material sky)
        {
            RenderSettings.skybox = sky;
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);

            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.74f, 0.88f);
            RenderSettings.ambientEquatorColor = new Color(0.58f, 0.62f, 0.60f);
            RenderSettings.ambientGroundColor = new Color(0.35f, 0.33f, 0.30f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.74f, 0.82f, 0.90f);
            RenderSettings.fogStartDistance = 220f;
            RenderSettings.fogEndDistance = 650f;
        }

        static Dictionary<string, Material> EnsureMaterials()
        {
            EnsureFolder(MaterialsDir);
            var shader = Shader.Find(ShaderName);
            if (shader == null) throw new System.Exception($"Shader '{ShaderName}' not found – is URP installed?");
            var result = new Dictionary<string, Material>();
            foreach (var name in new[] { WorldStyle.MatBuildings, WorldStyle.MatGround, WorldStyle.MatWater, WorldStyle.MatTrees })
            {
                string path = $"{MaterialsDir}/World_{name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(shader) { name = $"World_{name}", enableInstancing = true };
                    AssetDatabase.CreateAsset(mat, path);
                }
                mat.shader = shader;
                if (name == WorldStyle.MatWater)
                {
                    mat.SetFloat("_WaterAnim", 1f);
                    mat.EnableKeyword("_WATER_ANIM");
                }
                if (name == WorldStyle.MatBuildings || name == WorldStyle.MatGround)
                {
                    mat.SetFloat("_Facade", 1f);
                    mat.EnableKeyword("_FACADE");
                }
                EditorUtility.SetDirty(mat);
                result[name] = mat;
            }
            return result;
        }

        static void AddSceneToBuild(string scenePath)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == scenePath)) return;
            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
