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

            var sink = new MeshSink();
            GroundBuilder.BuildBase(sink, half, WorldStyle.GroundColor(meta.ground));
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
            spawn.position = FindSpawn(data, half) + Vector3.up * 0.2f;

            var areaGo = new GameObject("Area");
            var info = areaGo.AddComponent<AreaInfo>();
            info.areaId = meta.id;
            info.displayName = meta.name;
            info.projection = data.projection;
            info.sizeMeters = meta.sizeM;
            info.allowedVehicles = new List<string>(meta.vehicles);
            info.pois = poiDb;
            info.spawnPoint = spawn;
            info.attribution = hasLod2 ? "© OpenStreetMap-Mitwirkende (ODbL) · 3D-Gebäude © LGLN (CC BY 4.0)" : "© OpenStreetMap-Mitwirkende (ODbL)";
            spawn.SetParent(areaGo.transform, true);

            new GameObject("GameBootstrap").AddComponent<GameBootstrap>();

            string scenePath = $"{AreaScenesDir}/{meta.name}.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            AddSceneToBuild(scenePath);
            UpdateCatalog(meta, pois.Count);
            AssetDatabase.SaveAssets();

            Debug.Log($"[OSM] {meta.name}: {buildings} buildings, {lines} lines, {trees} plants, {lamps} lamps, {barriers} barriers, " +
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
                    if (clearance < 6f) continue;
                    float score = p.magnitude + penalty - Mathf.Min(clearance, 20f);
                    if (score < bestScore) { bestScore = score; best = p; }
                }
            }
            return new Vector3(best.x, 0f, best.y);
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
            box.center = new Vector3(0f, -0.5f, 0f);

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
