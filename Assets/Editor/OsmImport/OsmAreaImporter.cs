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
            SceneSnapshot.RenderThumbnails(args[i + 1]); // only this area
            AssetDatabase.SaveAssets();
        }

        const string MenuScenePath = "Assets/Scenes/MainMenu.unity";
        public const string WorldScenePath = "Assets/Scenes/World.unity";

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


            // The one world scene: lighting/sky + AreaLoader, which builds the downloaded (or, in the editor, local) area.
            var world = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AddLighting(EnsureSkyMaterial());
            new GameObject("AreaLoader").AddComponent<AreaLoader>();
            EditorSceneManager.SaveScene(world, WorldScenePath);

            // The app contains only menu + world scene (build index = AreaLoader.WorldSceneIndex); areas are downloads.
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(MenuScenePath, true), new EditorBuildSettingsScene(WorldScenePath, true) };
        }

        static void UpdateCatalog(OsmAreaMeta meta, int poiCount, List<AreaData.Start> starts)
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
                starts = starts.ConvertAll(x => x.name),
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

            // Large areas: only settlements + road corridors are built, the rest is coarse terrain (AreaMask, CoarseTerrain).
            var mask = AreaMask.Load(Path.GetDirectoryName(jsonPath), meta.id);
            var maskStats = mask?.Filter(data);
            var terrain = TerrainField.Load(Path.Combine(Path.GetDirectoryName(jsonPath), meta.id + ".dgm.bin"));
            var lidarTrees = PropPlacer.LoadLidarTrees(Path.Combine(Path.GetDirectoryName(jsonPath), meta.id + ".trees.json"));
            if (mask != null && lidarTrees != null) lidarTrees = lidarTrees.FindAll(t => mask.Inside(t.x, t.y));
            var aerial = AerialImage.Load(Path.GetDirectoryName(jsonPath), meta.id);
            aerial?.SetCanopy(lidarTrees);
            CoastBuilder.Prepare(data, area, terrain, aerial); // islands: sea level, water, tidal flats (rewrites the terrain)
            var sink = new MeshSink();
            CoarseTerrain.Build(sink, area, WorldStyle.GroundColor(meta.ground), terrain); // before BuildBase: simplifies the field
            GroundBuilder.BuildBase(sink, area, WorldStyle.GroundColor(meta.ground), terrain);
            GroundBuilder.BuildAreas(data, sink, area);
            int bridges = BridgeBuilder.Prepare(data, area, terrain); // decks instead of draped ribbons where there is a gap below
            int lines = GroundBuilder.BuildLines(data, sink, area);
            BridgeBuilder.Build(data, sink, terrain);
            var motorway = MotorwayBuilder.Build(data, sink, area); // lane markings, guard rails
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
            TerrainDrape.Lift(sink.instances, terrain); // instanced trees, bushes, lamps
            CoastBuilder.BuildSea(sink, area); // level sea surface, not draped

            string areaDir = $"{GeneratedRoot}/{meta.id}";
            EnsureFolder(areaDir);
            EditsPlacer.SaveRecords($"{areaDir}/{meta.id}_objects.json"); // object list for the map editor
            BuildingOverrides.SaveCatalog($"{areaDir}/{meta.id}_buildings.json"); // houses (id, address, footprint) for the map editor
            GardenBuilder.SaveCatalog($"{areaDir}/{meta.id}_gardens.json"); // hedges/fences/walls for the map editor
            string facadePath = $"{areaDir}/{meta.id}_facade.bytes";            // close-up building detail, built at runtime
            FacadeRecords.Save(facadePath);
            AssetDatabase.ImportAsset(facadePath);
            var materials = EnsureMaterials();

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

            // Everything besides the tiles goes into one AreaData asset; the world scene builds it at runtime (AreaLoader).
            string areaPath = AreaDataPath(meta.id);
            var areaData = AssetDatabase.LoadAssetAtPath<AreaData>(areaPath);
            if (areaData == null)
            {
                areaData = ScriptableObject.CreateInstance<AreaData>();
                AssetDatabase.CreateAsset(areaData, areaPath);
            }
            var d = areaData;
            d.areaId = meta.id;
            d.displayName = meta.name;
            d.projection = data.projection;
            // Only part of AreaInfo.MemoryKey (saved positions, own start points). An area that grew around the same
            // centre keeps its frame – "key_size" in areas.json keeps the old value, so nothing saved is lost.
            d.sizeMeters = KeySize(meta.id) ?? Mathf.Max(area.Width, area.Height);
            d.boundsMin = new Vector2(area.xMin, area.zMin);
            d.boundsMax = new Vector2(area.xMax, area.zMax);
            d.allowedVehicles = new List<string>(meta.vehicles);
            d.attribution = hasLod2 ? "© OpenStreetMap-Mitwirkende (ODbL) · 3D-Gebäude, Gelände, Luftbilder © LGLN (CC BY 4.0)" : "© OpenStreetMap-Mitwirkende (ODbL)";
            d.starts = Starts(data, area, terrain);
            d.spawn = d.starts[0].position;          // old apps only know the first start
            d.spawnBikeSpot = d.starts[0].bikeSpot;
            d.bikeSpots = BikeSpots(data, area, terrain);
            d.carSpots = CarSpots(data, area, terrain);
            d.pois = poiDb;
            d.streets = BuildStreets(data, $"{areaDir}/{meta.id}_streets.asset");
            d.facade = AssetDatabase.LoadAssetAtPath<TextAsset>(facadePath);
            TrafficBuilder.Export(terrain, d.trafficHeads, d.trafficSignals, d.signTexts);
            // World geometry: one asset per WorldStreamer tile (meshes as sub-assets), loaded around the camera at runtime.
            d.tileKeys = WriteTiles(sink, meta.id, areaDir);
            EditorUtility.SetDirty(d);
            UpdateCatalog(meta, pois.Count, d.starts);
            AssetDatabase.SaveAssets();

            // Leave a preview of the area open (unsaved scene with all tiles) for snapshot tools.
            AreaPreview.Open(d);

            Debug.Log($"[OSM] {meta.name}: motorway {motorway.carriageways} carriageway pieces/{motorway.ramps} ramps/{motorway.railKm:F1} km guard rail/{traffic.motorway} exit signs, {bridges} bridge decks");
            Debug.Log($"[OSM] {meta.name}: {buildings} buildings (+{osmExtra} OSM-only), {lines} lines, {trees} plants, {lamps} lamps, {barriers} barriers, gardens {gardens.plots} plots/{gardens.measured} measured hedges/{gardens.hedges} hedges/{gardens.fences} fences/{gardens.walls} walls/{gardens.drawn} drawn, terrain {(terrain.IsFlat ? "flat" : "DGM1")}, aerial {(aerial != null ? "DOP20" : "-")}, trees {(lidarTrees != null ? "LiDAR" : "OSM")}, " +
                      $"furniture {furniture.mapped} mapped/{furniture.benches} benches/{furniture.planters} planters/{furniture.seating} café tables/{furniture.fountains} fountains, {edits} hand-placed/{EditsPlacer.ChangedCount} edited, " +
                      $"facade {FacadeRecords.Windows} windows/{FacadeRecords.Doors} doors/{FacadeRecords.Gutters} gutters/{FacadeRecords.Verges} verges/{FacadeRecords.Ridges} ridges, traffic {traffic.junctionSignals} signal junctions/{traffic.crossingSignals} signal crossings/{traffic.heads} heads/{traffic.zebras} zebras/{traffic.lanes} lanes/{traffic.stops} stop/{traffic.giveWays} give way/{traffic.priority} priority/{traffic.speed} speed/{traffic.oneway} one-way/{traffic.names} names/{traffic.mapped} mapped, {pois.Count} POIs, " +
                      $"{(maskStats.HasValue ? $"mask -{maskStats.Value.nodes} nodes/-{maskStats.Value.polygons} areas/-{maskStats.Value.ways} ways/{maskStats.Value.pieces} cut, {CoarseTerrain.Blocks} coarse terrain blocks, " : "")}" +
                      $"{sink.Batches.Count()} meshes + {sink.instances.Count} instances in {sw.ElapsedMilliseconds} ms -> {areaPath}");
            return areaPath;
        }

        public static string AreaDataPath(string id) => $"{GeneratedRoot}/{id}/{id}_area.asset";

        /// <summary>
        /// Sorts the sink's batches (100 m chunks, 25 m for details) into <see cref="WorldStreamer.TileSize"/> tiles and
        /// writes one <see cref="WorldTile"/> asset per tile to &lt;areaDir&gt;/tiles (old tiles are removed first).
        /// Meshes without collider are not CPU-readable – halves their memory on the device.
        /// </summary>
        static List<Vector2Int> WriteTiles(MeshSink sink, string id, string areaDir)
        {
            string tileDir = $"{areaDir}/tiles";
            if (AssetDatabase.IsValidFolder(tileDir)) AssetDatabase.DeleteAsset(tileDir);
            AssetDatabase.DeleteAsset($"{areaDir}/{id}_meshes.asset"); // pre-streaming single mesh file
            EnsureFolder(tileDir);

            var tiles = new Dictionary<Vector2Int, WorldTile>();
            WorldTile TileAt(Vector2Int key)
            {
                if (!tiles.TryGetValue(key, out var tile))
                {
                    tile = ScriptableObject.CreateInstance<WorldTile>();
                    tile.key = key;
                    tile.name = WorldStreamer.TileName(id, key);
                    AssetDatabase.CreateAsset(tile, WorldStreamer.EditorTilePath(id, key));
                    tiles[key] = tile;
                }
                return tile;
            }
            {
                foreach (var kv in sink.Batches)
                {
                    var (mat, chunk) = kv.Key;
                    if (kv.Value.vertices.Count == 0) continue;
                    float chunkSize = mat == WorldStyle.MatDetails ? MeshSink.DetailChunkSize : MeshSink.ChunkSize;
                    var key = WorldStreamer.KeyOf((chunk.x + 0.5f) * chunkSize, (chunk.y + 0.5f) * chunkSize);
                    var tile = TileAt(key);

                    string style = WorldStyle.MaterialOf(mat);
                    bool facadeData = style == WorldStyle.MatBuildings || style == WorldStyle.MatGround;
                    var mesh = MeshSink.ToMesh(kv.Value, $"{id}_{mat}_{chunk.x}_{chunk.y}", facadeData);
                    MeshUtility.Optimize(mesh);
                    Release(kv.Value); // large areas: the geometry now lives in the mesh only
                    bool collider = WorldStyle.HasCollider(mat) || mat == WorldStyle.MatWater;
                    AssetDatabase.AddObjectToAsset(mesh, tile);
                    if (!collider)
                    {
                        var so = new SerializedObject(mesh);
                        so.FindProperty("m_IsReadable").boolValue = false;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }

                    int mi = System.Array.IndexOf(WorldTile.MaterialNames, style); // = GameMaterials.worldMaterials index
                    bool casts = mat != WorldStyle.MatGround && mat != WorldStyle.MatWater && mat != WorldStyle.MatDetails;
                    // Thin props (lamps, trunks, fences) block the player but not the camera probe; water sits on
                    // the built-in "Water" layer so placement can detect it, the player walks through.
                    int layer = mat == WorldStyle.MatProps || mat == WorldStyle.MatTreeTrunks ? 2
                              : mat == WorldStyle.MatDetails ? WorldStyle.DetailsLayer
                              : mat == WorldStyle.MatWater ? 4 : 0;
                    tile.parts.Add(new WorldTile.Part
                    {
                        mesh = mesh, material = mi, layer = layer, group = mat,
                        flags = (byte)((collider ? WorldTile.FlagCollider : 0) | (casts ? WorldTile.FlagShadows : 0)),
                    });
                }
                // Instanced trees/bushes/lamps go to the tile under their origin (crowns may overhang its edge a bit –
                // the renderer's bounds include them).
                foreach (var inst in sink.instances) TileAt(WorldStreamer.KeyOf(inst.position.x, inst.position.z)).instances.Add(inst);
                foreach (var t in tiles.Values) EditorUtility.SetDirty(t);
            }
            AssetDatabase.SaveAssets();
            var keys = tiles.Keys.ToList();
            keys.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            return keys;
        }

        static void Release(MeshSink.Batch b)
        {
            b.vertices.Clear(); b.vertices.TrimExcess();
            b.normals.Clear(); b.normals.TrimExcess();
            b.colors.Clear(); b.colors.TrimExcess();
            b.uvs.Clear(); b.uvs.TrimExcess();
            b.triangles.Clear(); b.triangles.TrimExcess();
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
            idx.names.Clear(); idx.segments.Clear(); idx.nameOf.Clear(); idx.fast.Clear();
            var ids = new Dictionary<string, int>();
            foreach (var w in data.ways)
            {
                string hw = w.tags.Get("highway");
                if (hw == null || w.tags.Is("area", "yes")) continue;
                string name = w.tags.Get("name");
                // Motorways are known by their number ("A 28"), their ramps rarely have a name at all.
                bool motorway = hw == "motorway" || hw == "motorway_link";
                if (string.IsNullOrEmpty(name) && motorway)
                    name = hw == "motorway" ? w.tags.Get("ref")?.Split(';')[0].Trim() : "Anschlussstelle";
                if (string.IsNullOrEmpty(name)) continue;
                if (!ids.TryGetValue(name, out int n)) { n = idx.names.Count; ids[name] = n; idx.names.Add(name); }
                if (motorway && !idx.fast.Contains(n)) idx.fast.Add(n);
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

        /// <summary>
        /// Default start points from "starts" of this area in Tools/osm/areas.json: [{name, lat?, lon?, yaw?}] – an entry
        /// without lat/lon is the area centre; no list = one start at the centre named like the area. Each gets a spawn
        /// (<see cref="FindSpawn"/> around it) and a checked spot for the starter bike. Never empty.
        /// </summary>
        static List<AreaData.Start> Starts(OsmData data, AreaRect area, TerrainField terrain)
        {
            var specs = new List<(string name, Vector2 at, float yaw)>();
            string areasPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "osm", "areas.json"));
            if (File.Exists(areasPath))
            {
                var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(areasPath));
                foreach (Dictionary<string, object> a in (List<object>)root["areas"])
                {
                    if ((string)a["id"] != data.meta.id || !a.TryGetValue("starts", out var startsObj)) continue;
                    foreach (Dictionary<string, object> st in (List<object>)startsObj)
                    {
                        string name = st.TryGetValue("name", out var n) ? (string)n : data.meta.name;
                        var at = st.ContainsKey("lat") && st.ContainsKey("lon")
                            ? data.projection.ToLocal(System.Convert.ToDouble(st["lat"]), System.Convert.ToDouble(st["lon"]))
                            : Vector2.zero;
                        float yaw = st.TryGetValue("yaw", out var y) ? (float)System.Convert.ToDouble(y) : 0f;
                        if (!area.Contains(at, 5f)) { Debug.LogWarning($"[OSM] start '{name}' lies outside the area – skipped"); continue; }
                        specs.Add((name, at, yaw));
                    }
                }
            }
            if (specs.Count == 0) specs.Add((data.meta.name, Vector2.zero, 0f));

            var result = new List<AreaData.Start>();
            foreach (var (name, at, yaw) in specs)
            {
                var xz = FindSpawn(data, at);
                var s2 = new Vector2(xz.x, xz.z);
                var bike = FreeBikeSpot(data, s2, NearbyBuildings(data, s2, 15f));
                result.Add(new AreaData.Start
                {
                    name = name,
                    position = new Vector3(xz.x, terrain.Height(xz.x, xz.z) + 0.2f, xz.z),
                    yaw = yaw,
                    bikeSpot = bike.HasValue ? new Vector3(bike.Value.x, terrain.Height(bike.Value.x, bike.Value.y), bike.Value.y) : Vector3.zero,
                });
                Debug.Log($"[OSM] start '{name}' at ({xz.x:F0}, {xz.z:F0}), {Vector2.Distance(s2, at):F0} m from the wanted spot{(bike.HasValue ? "" : ", no bike spot")}");
            }
            return result;
        }

        static Vector3 FindSpawn(OsmData data, Vector2 centre)
        {
            // Walkable way vertex within 200 m of the centre with open space around it (no narrow alleys,
            // never inside a building). Wider street types are preferred.
            var near = Rect.MinMaxRect(centre.x - 200f, centre.y - 200f, centre.x + 200f, centre.y + 200f);
            var buildings = new List<List<Vector2>>();
            foreach (var p in data.polygons)
                if (p.tags.Has("building") && GeoUtil.Bounds(p.outer).Overlaps(near))
                    buildings.Add(PolygonTriangulator.Clean(p.outer));

            bool Blocked(Vector2 p) => IsObstructed(data, p, 0f);

            Vector2 best = centre;
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
                    float dist = (p - centre).magnitude;
                    if (dist > 200f) continue;
                    float clearance = Clearance(p, buildings);
                    if (clearance < 6f || Blocked(p)) continue;
                    float score = dist + penalty - Mathf.Min(clearance, 20f);
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
        /// <summary>"key_size" of the area in Tools/osm/areas.json, if set.</summary>
        static float? KeySize(string id)
        {
            string areasPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "osm", "areas.json"));
            if (!File.Exists(areasPath)) return null;
            var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(areasPath));
            foreach (Dictionary<string, object> a in (List<object>)root["areas"])
                if ((string)a["id"] == id && a.TryGetValue("key_size", out var v)) return System.Convert.ToSingle(v);
            return null;
        }

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

        internal static Material EnsureSkyMaterial()
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

        internal static void AddLighting(Material sky)
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
                mat.enableInstancing = true; // trees/bushes/lamps: Graphics.RenderMeshInstanced (keeps the INSTANCING_ON variants in builds)
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
            // The app owns the world materials (maps reference them by index, WorldTile.MaterialNames).
            var gm = AssetDatabase.LoadAssetAtPath<GameMaterials>($"Assets/Resources/{GameMaterials.ResourcePath}.asset");
            if (gm != null)
            {
                gm.worldMaterials = WorldTile.MaterialNames.Select(n => result[n]).ToList();
                gm.signTextMaterial = TrafficBuilder.TextMaterial(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
                EditorUtility.SetDirty(gm);
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
