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

        /// <summary>Re-imports one area (map editor): Unity -batchmode … -executeMethod …ImportAreaBatch -area &lt;id&gt;</summary>
        public static void ImportAreaBatch()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-area");
            if (i < 0 || i + 1 >= args.Length) throw new System.ArgumentException("missing -area <id>");
            ImportArea(Path.Combine(DataDir, args[i + 1] + ".osm.json"));
            SceneSnapshot.RenderThumbnails();
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

            // The app contains only the menu; area scenes are downloaded as bundles (Core/MapStore, Editor/MapBundles).
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(MenuScenePath, true) };
        }

        static void UpdateCatalog(OsmAreaMeta meta, int poiCount)
        {
            EnsureFolder(GeneratedRoot);
            string path = AreaCatalog.AssetPath;
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
            var area = meta.bounds;
            var rng = new System.Random(meta.id.GetHashCode());

            var terrain = TerrainField.Load(Path.Combine(Path.GetDirectoryName(jsonPath), meta.id + ".dgm.bin"));
            var lidarTrees = PropPlacer.LoadLidarTrees(Path.Combine(Path.GetDirectoryName(jsonPath), meta.id + ".trees.json"));
            var aerial = AerialImage.Load(Path.GetDirectoryName(jsonPath), meta.id);
            aerial?.SetCanopy(lidarTrees);
            var sink = new MeshSink();
            GroundBuilder.BuildBase(sink, area, WorldStyle.GroundColor(meta.ground), terrain);
            GroundBuilder.BuildAreas(data, sink, area);
            int lines = GroundBuilder.BuildLines(data, sink, area);
            // Prefer official LGLN LoD2 buildings (real roof shapes); fall back to OSM extrusion.
            string lod2Path = Path.Combine(Path.GetDirectoryName(jsonPath), meta.id + ".lod2.json");
            bool island = meta.ground == "sand";
            FacadeRecords.Begin();
            BuildingOverrides.Load(Path.Combine(Path.GetDirectoryName(jsonPath), meta.id + ".buildings.json"));
            bool hasLod2 = Lod2Builder.TryBuild(lod2Path, data, sink, area, island, out int buildings);
            if (!hasLod2) buildings = BuildingBuilder.Build(data, sink, area, rng);
            // LoD2 lags behind reality (new estates): extrude the OSM houses it doesn't have. Own random
            // generator, so the shared sequence – and with it all generated object ids – stays unchanged.
            int osmExtra = hasLod2 ? BuildingBuilder.Build(data, sink, area, new System.Random(meta.id.GetHashCode() ^ 0x0B5D), Lod2Builder.Covers) : 0;
            BuildingOverrides.ReportUnused();
            EditsPlacer.Begin(Path.Combine(Path.GetDirectoryName(jsonPath), meta.id + ".edits.json"));
            int trees = PropPlacer.PlaceTrees(data, sink, area, rng, lidarTrees);
            int lamps = PropPlacer.PlaceStreetLamps(data, sink, area);
            int barriers = GroundBuilder.BuildBarriers(data, sink, area);
            var gardens = GardenBuilder.Build(Path.Combine(Path.GetDirectoryName(jsonPath), meta.id + ".parcels.json"), data, sink, area);
            var furniture = FurniturePlacer.Place(data, sink, area, rng);
            var traffic = TrafficBuilder.Build(Path.Combine(Path.GetDirectoryName(jsonPath), meta.id + ".traffic.json"), data, sink, area);
            CommercialBuilder.Build(data, sink, area); // petrol stations etc.; after traffic (adds sign texts)
            int edits = EditsPlacer.PlaceNew(sink, area);
            var pois = PoiExtractor.Extract(data, area);
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
            EditsPlacer.SaveRecords($"{areaDir}/{meta.id}_objects.json"); // object list for the map editor
            BuildingOverrides.SaveCatalog($"{areaDir}/{meta.id}_buildings.json"); // houses (id, address, footprint) for the map editor
            GardenBuilder.SaveCatalog($"{areaDir}/{meta.id}_gardens.json"); // hedges/fences/walls for the map editor
            string facadePath = $"{areaDir}/{meta.id}_facade.bytes";            // close-up building detail, built at runtime
            FacadeRecords.Save(facadePath);
            AssetDatabase.ImportAsset(facadePath);
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

                bool facadeData = WorldStyle.MaterialOf(mat) == WorldStyle.MatBuildings || WorldStyle.MaterialOf(mat) == WorldStyle.MatGround;
                var mesh = MeshSink.ToMesh(kv.Value, $"{meta.id}_{mat}_{chunk.x}_{chunk.y}", facadeData);
                MeshUtility.Optimize(mesh);
                AssetDatabase.AddObjectToAsset(mesh, container);

                var go = new GameObject($"{mat}_{chunk.x}_{chunk.y}");
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = materials[WorldStyle.MaterialOf(mat)];
                bool casts = mat != WorldStyle.MatGround && mat != WorldStyle.MatWater && mat != WorldStyle.MatDetails;
                mr.shadowCastingMode = casts ? ShadowCastingMode.On : ShadowCastingMode.Off;
                if (WorldStyle.HasCollider(mat))
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                // Thin props (lamps, trunks, fences) block the player but not the camera probe.
                if (mat == WorldStyle.MatProps || mat == WorldStyle.MatTreeTrunks) go.layer = 2;
                if (mat == WorldStyle.MatDetails) go.layer = WorldStyle.DetailsLayer;
                // Water: collider on the built-in "Water" layer so placement can detect it; the player walks through.
                if (mat == WorldStyle.MatWater)
                {
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                    go.layer = 4;
                }
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
            }

            AddGroundAndBounds(worldRoot.transform, area);
            TrafficBuilder.Spawn(worldRoot.transform, terrain, materials[WorldStyle.MatTrees]);
            var facade = new GameObject("FacadeDetails").AddComponent<FacadeDetails>();
            facade.transform.SetParent(worldRoot.transform, false);
            facade.data = AssetDatabase.LoadAssetAtPath<TextAsset>(facadePath);
            facade.material = materials[WorldStyle.MatTrees];
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
            var spawnXZ = FindSpawn(data, area);
            spawn.position = new Vector3(spawnXZ.x, terrain.Height(spawnXZ.x, spawnXZ.z) + 0.2f, spawnXZ.z);

            var areaGo = new GameObject("Area");
            var info = areaGo.AddComponent<AreaInfo>();
            info.areaId = meta.id;
            info.displayName = meta.name;
            info.projection = data.projection;
            info.sizeMeters = Mathf.Max(area.Width, area.Height);
            info.boundsMin = new Vector2(area.xMin, area.zMin);
            info.boundsMax = new Vector2(area.xMax, area.zMax);
            info.allowedVehicles = new List<string>(meta.vehicles);
            info.pois = poiDb;
            info.streets = BuildStreets(data, $"{areaDir}/{meta.id}_streets.asset");
            info.spawnPoint = spawn;
            info.bikeSpots = BikeSpots(data, area, terrain);
            info.carSpots = CarSpots(data, area, terrain);
            var spawn2 = new Vector2(spawn.position.x, spawn.position.z);
            var bikeSpot = FreeBikeSpot(data, spawn2, NearbyBuildings(data, spawn2, 15f));
            info.spawnBikeSpot = bikeSpot.HasValue
                ? new Vector3(bikeSpot.Value.x, terrain.Height(bikeSpot.Value.x, bikeSpot.Value.y), bikeSpot.Value.y)
                : Vector3.zero;
            info.attribution = hasLod2 ? "© OpenStreetMap-Mitwirkende (ODbL) · 3D-Gebäude, Gelände, Luftbilder © LGLN (CC BY 4.0)" : "© OpenStreetMap-Mitwirkende (ODbL)";
            spawn.SetParent(areaGo.transform, true);

            new GameObject("GameBootstrap").AddComponent<GameBootstrap>();

            string scenePath = $"{AreaScenesDir}/{meta.name}.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            UpdateCatalog(meta, pois.Count);
            AssetDatabase.SaveAssets();

            Debug.Log($"[OSM] {meta.name}: {buildings} buildings (+{osmExtra} OSM-only), {lines} lines, {trees} plants, {lamps} lamps, {barriers} barriers, gardens {gardens.plots} plots/{gardens.measured} measured hedges/{gardens.hedges} hedges/{gardens.fences} fences/{gardens.walls} walls/{gardens.drawn} drawn, terrain {(terrain.IsFlat ? "flat" : "DGM1")}, aerial {(aerial != null ? "DOP20" : "-")}, trees {(lidarTrees != null ? "LiDAR" : "OSM")}, " +
                      $"furniture {furniture.mapped} mapped/{furniture.benches} benches/{furniture.planters} planters/{furniture.seating} café tables/{furniture.fountains} fountains, {edits} hand-placed/{EditsPlacer.ChangedCount} edited, " +
                      $"facade {FacadeRecords.Windows} windows/{FacadeRecords.Doors} doors/{FacadeRecords.Gutters} gutters/{FacadeRecords.Verges} verges/{FacadeRecords.Ridges} ridges, traffic {traffic.junctionSignals} signal junctions/{traffic.crossingSignals} signal crossings/{traffic.heads} heads/{traffic.zebras} zebras/{traffic.lanes} lanes/{traffic.stops} stop/{traffic.giveWays} give way/{traffic.priority} priority/{traffic.speed} speed/{traffic.oneway} one-way/{traffic.names} names/{traffic.mapped} mapped, {pois.Count} POIs, " +
                      $"{sink.Batches.Count()} meshes in {sw.ElapsedMilliseconds} ms -> {scenePath}");
            return scenePath;
        }

        /// <summary>Named OSM streets/paths as segments (asset next to the meshes) for the HUD street display.</summary>
        static StreetIndex BuildStreets(OsmData data, string path)
        {
            var idx = AssetDatabase.LoadAssetAtPath<StreetIndex>(path);
            if (idx == null)
            {
                idx = ScriptableObject.CreateInstance<StreetIndex>();
                AssetDatabase.CreateAsset(idx, path);
            }
            idx.names.Clear(); idx.segments.Clear(); idx.nameOf.Clear();
            var ids = new Dictionary<string, int>();
            foreach (var w in data.ways)
            {
                if (w.tags.Get("highway") == null || w.tags.Is("area", "yes")) continue;
                string name = w.tags.Get("name");
                if (string.IsNullOrEmpty(name)) continue;
                if (!ids.TryGetValue(name, out int n)) { n = idx.names.Count; ids[name] = n; idx.names.Add(name); }
                for (int i = 0; i < w.points.Count - 1; i++)
                {
                    idx.segments.Add(new Vector4(w.points[i].x, w.points[i].y, w.points[i + 1].x, w.points[i + 1].y));
                    idx.nameOf.Add(n);
                }
            }
            // Named squares / pedestrian areas: their outline counts as street too.
            foreach (var poly in data.polygons)
            {
                if (!(poly.tags.Is("highway", "pedestrian") || poly.tags.Is("place", "square"))) continue;
                string name = poly.tags.Get("name");
                if (string.IsNullOrEmpty(name)) continue;
                if (!ids.TryGetValue(name, out int n)) { n = idx.names.Count; ids[name] = n; idx.names.Add(name); }
                for (int i = 0; i < poly.outer.Count; i++)
                {
                    var a = poly.outer[i]; var b = poly.outer[(i + 1) % poly.outer.Count];
                    idx.segments.Add(new Vector4(a.x, a.y, b.x, b.y));
                    idx.nameOf.Add(n);
                }
            }
            EditorUtility.SetDirty(idx);
            return idx;
        }

        static Vector3 FindSpawn(OsmData data, AreaRect area)
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

        /// <summary>
        /// Cars from "car_spots" ([{lat, lon}]) of this area in Tools/osm/areas.json – e.g. a house address.
        /// Each is moved to the edge of the nearest drivable street and lined up with it.
        /// </summary>
        static List<Vector4> CarSpots(OsmData data, AreaRect area, TerrainField terrain)
        {
            var result = new List<Vector4>();
            string areasPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "osm", "areas.json"));
            if (!File.Exists(areasPath)) return result;
            var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(areasPath));
            foreach (Dictionary<string, object> a in (List<object>)root["areas"])
            {
                if ((string)a["id"] != data.meta.id || !a.TryGetValue("car_spots", out var spotsObj)) continue;
                foreach (Dictionary<string, object> spot in (List<object>)spotsObj)
                {
                    var want = data.projection.ToLocal((double)spot["lat"], (double)spot["lon"]);
                    if (!area.Contains(want, 5f)) continue;
                    // Nearest drivable street segment within 60 m.
                    float best = 60f;
                    Vector2 bestQ = want, bestDir = Vector2.up;
                    float bestHalfWidth = 3f;
                    foreach (var w in data.ways)
                    {
                        var hw = w.tags.Get("highway");
                        if (hw != "residential" && hw != "unclassified" && hw != "living_street" && hw != "service" &&
                            hw != "tertiary" && hw != "secondary") continue;
                        float halfWidth = hw == "service" ? 2f : hw == "tertiary" || hw == "secondary" ? 4f : 3f;
                        for (int i = 0; i < w.points.Count - 1; i++)
                        {
                            Vector2 p0 = w.points[i], p1 = w.points[i + 1], ab = p1 - p0;
                            float t = Mathf.Clamp01(Vector2.Dot(want - p0, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                            var q = p0 + ab * t;
                            float d = Vector2.Distance(want, q);
                            if (d < best) { best = d; bestQ = q; bestDir = ab.normalized; bestHalfWidth = halfWidth; }
                        }
                    }
                    // Park at the kerb on the address' side of the street, nose along the street.
                    var side = want - bestQ;
                    var normal = new Vector2(-bestDir.y, bestDir.x);
                    if (side.sqrMagnitude > 0.01f && Vector2.Dot(normal, side) < 0) normal = -normal;
                    var pos = bestQ + normal * (bestHalfWidth - 1.0f);
                    float yaw = Mathf.Atan2(bestDir.x, bestDir.y) * Mathf.Rad2Deg;
                    result.Add(new Vector4(pos.x, terrain.Height(pos.x, pos.y), pos.y, yaw));
                }
            }
            return result;
        }

        /// <summary>Up to 40 bicycle parkings, at least 40 m apart, as places for parked bikes.</summary>
        static List<Vector3> BikeSpots(OsmData data, AreaRect area, TerrainField terrain)
        {
            var spots = new List<Vector3>();
            foreach (var n in data.nodes)
            {
                if (!n.tags.Is("amenity", "bicycle_parking")) continue;
                var p = data.projection.ToLocal(n.lat, n.lon);
                if (!area.Contains(p, 5f)) continue;
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

        static void AddGroundAndBounds(Transform root, AreaRect area)
        {
            var c = area.Center;
            var ground = new GameObject("GroundCollider");
            ground.transform.SetParent(root, false);
            var box = ground.AddComponent<BoxCollider>();
            box.size = new Vector3(area.Width + 120f, 1f, area.Height + 120f);
            box.center = new Vector3(c.x, -6f, c.y); // safety net below the terrain mesh collider

            var bounds = new GameObject("AreaBounds");
            bounds.transform.SetParent(root, false);
            const float height = 60f, t = 2f;
            float w = area.Width, h = area.Height;
            AddWall(bounds.transform, new Vector3(c.x, height / 2, area.zMax + t / 2), new Vector3(w + t * 2, height, t));
            AddWall(bounds.transform, new Vector3(c.x, height / 2, area.zMin - t / 2), new Vector3(w + t * 2, height, t));
            AddWall(bounds.transform, new Vector3(area.xMax + t / 2, height / 2, c.y), new Vector3(t, height, h));
            AddWall(bounds.transform, new Vector3(area.xMin - t / 2, height / 2, c.y), new Vector3(t, height, h));
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
            var shader = Shader.Find("LeGamez/Sky");
            if (sky == null)
            {
                sky = new Material(shader) { name = "Sky" };
                AssetDatabase.CreateAsset(sky, path);
            }
            sky.shader = shader;
            sky.SetColor("_ZenithColor", new Color(0.30f, 0.50f, 0.80f));
            sky.SetColor("_HorizonColor", new Color(0.74f, 0.82f, 0.90f)); // = fog colour, hides the horizon seam
            sky.SetColor("_GroundColor", new Color(0.74f, 0.82f, 0.90f));
            sky.SetColor("_SunColor", new Color(1f, 0.94f, 0.8f));
            sky.SetFloat("_CloudCover", 0.42f);
            EditorUtility.SetDirty(sky);
            return sky;
        }

        static void AddLighting(Material sky)
        {
            RenderSettings.skybox = sky;
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.94f, 0.84f);
            sun.intensity = 1.3f;
            sun.shadows = LightShadows.Soft;
            // Lower afternoon sun: longer shadows model the low-poly shapes better.
            sun.transform.rotation = Quaternion.Euler(38f, -35f, 0f);

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
                mat.SetFloat("_ShadowStrength", 0.68f);
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

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
