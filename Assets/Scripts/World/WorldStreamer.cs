using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LeGamez.World
{
    /// <summary>
    /// Keeps only the world tiles (<see cref="WorldTile"/>) around the camera in memory: loads those within
    /// <see cref="loadRadius"/> (nearest first, a few at a time, one instantiated per frame) and drops tiles beyond
    /// <see cref="unloadRadius"/>. Fog ends at 650 m, so nothing further away is ever visible. Lets areas grow far beyond
    /// what a phone could hold as one scene. On devices the tiles come from 1 km region bundles (Core/RegionTileSource, downloaded on demand),
    /// in the editor straight from Assets/Generated/&lt;id&gt;/tiles/.
    /// </summary>
    public class WorldStreamer : MonoBehaviour
    {
        public const float TileSize = 200f;

        public string areaId;
        public List<Material> materials = new List<Material>();
        public List<Vector2Int> tileKeys = new List<Vector2Int>();
        public float loadRadius = 760f;
        public float unloadRadius = 960f;

        public static WorldStreamer Instance { get; private set; }
        /// <summary>Where the NEXT streamer gets its tiles on devices (Core/RegionTileSource: region bundles, downloaded on
        /// demand); taken over in Awake, so an area switch can't mix up old and new tiles. Null in the editor: tiles load
        /// straight from Assets/Generated.</summary>
        public static ITileSource Source;
        ITileSource source;

        public interface ITileSource
        {
            /// <summary>Loads a tile (fetching its region first if needed); done(null) if it is not available right now.</summary>
            IEnumerator Load(string areaId, Vector2Int key, System.Action<WorldTile> done);
            /// <summary>The tile's objects are gone – free its assets.</summary>
            void Release(Vector2Int key, WorldTile tile);
            /// <summary>Called with the focus a few times per second: fetch what will be needed soon.</summary>
            void Prefetch(Vector3 focus);
        }

        public static string TileName(string id, Vector2Int k) => $"{id}_{k.x}_{k.y}";
        public static string EditorTilePath(string id, Vector2Int k) => $"Assets/Generated/{id}/tiles/{TileName(id, k)}.asset";
        public static Vector2Int KeyOf(float x, float z) => new Vector2Int(Mathf.FloorToInt(x / TileSize), Mathf.FloorToInt(z / TileSize));

        /// <summary>Download unit: 1 km (5 × 5 tiles), one bundle per region ("r&lt;rx&gt;_&lt;rz&gt;.bundle").</summary>
        public const float RegionSize = 1000f;
        public const int TilesPerRegion = 5;
        public static Vector2Int RegionOf(Vector2Int tile) =>
            new Vector2Int(Mathf.FloorToInt((float)tile.x / TilesPerRegion), Mathf.FloorToInt((float)tile.y / TilesPerRegion));
        public static Vector2Int RegionAt(float x, float z) => RegionOf(KeyOf(x, z));

        class Slot
        {
            public WorldTile tile;
            public GameObject root;
            public bool loading;
        }

        readonly Dictionary<Vector2Int, Slot> slots = new Dictionary<Vector2Int, Slot>();
        /// <summary>Tiles whose region could not be fetched (offline): not asked again before this time, and not waited for.</summary>
        readonly Dictionary<Vector2Int, float> retryAt = new Dictionary<Vector2Int, float>();
        HashSet<Vector2Int> exists;
        Vector3? focusOverride;
        int inFlight;
        int lastInstantiateFrame = -1;
        float nextSchedule;
        const int MaxInFlight = 3;

        public int LoadedCount { get { int n = 0; foreach (var s in slots.Values) if (s.root != null) n++; return n; } }

        void Awake()
        {
            Instance = this;
            exists = new HashSet<Vector2Int>(tileKeys);
            source = Source;
            Source = null;
        }

        void OnDestroy()
        {
            foreach (var kv in slots) Release(kv.Key, kv.Value);
            slots.Clear();
            if (Instance == this) Instance = null;
        }

        Vector3 Focus
        {
            get
            {
                if (focusOverride.HasValue) return focusOverride.Value;
                var cam = Camera.main;
                return cam != null ? cam.transform.position : Vector3.zero;
            }
        }

        void Update()
        {
            if (Time.unscaledTime < nextSchedule) return;
            nextSchedule = Time.unscaledTime + 0.2f;
            Schedule(Focus);
        }

        static float Distance(Vector2Int k, Vector3 p)
        {
            float x0 = k.x * TileSize, z0 = k.y * TileSize;
            float dx = Mathf.Max(x0 - p.x, 0f, p.x - (x0 + TileSize));
            float dz = Mathf.Max(z0 - p.z, 0f, p.z - (z0 + TileSize));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        readonly List<Vector2Int> scratch = new List<Vector2Int>();

        void Schedule(Vector3 p)
        {
            // Drop far tiles (not ones still loading – they are released when they arrive).
            scratch.Clear();
            foreach (var kv in slots)
                if (!kv.Value.loading && Distance(kv.Key, p) > unloadRadius) scratch.Add(kv.Key);
            foreach (var k in scratch) { Release(k, slots[k]); slots.Remove(k); }
            source?.Prefetch(p);

            // Wanted tiles, nearest first.
            scratch.Clear();
            int r = Mathf.CeilToInt(loadRadius / TileSize) + 1;
            var c = KeyOf(p.x, p.z);
            for (int x = c.x - r; x <= c.x + r; x++)
            for (int z = c.y - r; z <= c.y + r; z++)
            {
                var k = new Vector2Int(x, z);
                if (exists.Contains(k) && !slots.ContainsKey(k) && Distance(k, p) <= loadRadius && !Waiting(k)) scratch.Add(k);
            }
            scratch.Sort((a, b) => Distance(a, p).CompareTo(Distance(b, p)));
            foreach (var k in scratch)
            {
                if (inFlight >= MaxInFlight) break;
                var slot = new Slot { loading = true };
                slots[k] = slot;
                StartCoroutine(Load(k, slot));
            }
        }

        IEnumerator Load(Vector2Int k, Slot slot)
        {
            inFlight++;
            string name = TileName(areaId, k);
            if (source != null)
            {
                WorldTile loadedTile = null;
                yield return source.Load(areaId, k, t => loadedTile = t);
                slot.tile = loadedTile;
            }
#if UNITY_EDITOR
            else slot.tile = UnityEditor.AssetDatabase.LoadAssetAtPath<WorldTile>(EditorTilePath(areaId, k));
#endif
            // Spread the main-thread work: one tile per frame (mesh upload + collider cooking).
            while (lastInstantiateFrame == Time.frameCount) yield return null;
            lastInstantiateFrame = Time.frameCount;
            inFlight--;
            slot.loading = false;
            if (slot.tile == null)
            {
                if (source != null) retryAt[k] = Time.unscaledTime + 10f; // region not downloadable right now (offline)
                else
                {
                    Debug.LogWarning($"[World] tile {name} missing");
                    exists.Remove(k); // don't wait for it or ask again
                }
                slots.Remove(k);
                yield break;
            }
            if (!slots.TryGetValue(k, out var current) || current != slot || Distance(k, Focus) > unloadRadius)
            {
                Release(k, slot); // no longer wanted
                if (slots.TryGetValue(k, out current) && current == slot) slots.Remove(k);
                yield break;
            }
            slot.root = Build(slot.tile, transform);
        }

        GameObject Build(WorldTile tile, Transform parent, HideFlags hide = HideFlags.None)
        {
            var root = new GameObject($"Tile {tile.key.x}_{tile.key.y}") { hideFlags = hide };
            root.transform.SetParent(parent, false);
            for (int i = 0; i < tile.parts.Count; i++)
            {
                var part = tile.parts[i];
                if (part.mesh == null) continue;
                // Name starts with the group: Placement recognises walkable ground by "Terrain…".
                var go = new GameObject($"{part.group}_{i}") { hideFlags = hide, layer = part.layer };
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = part.mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = part.material >= 0 && part.material < materials.Count ? materials[part.material] : null;
                mr.shadowCastingMode = (part.flags & WorldTile.FlagShadows) != 0 ? ShadowCastingMode.On : ShadowCastingMode.Off;
                if ((part.flags & WorldTile.FlagCollider) != 0) go.AddComponent<MeshCollider>().sharedMesh = part.mesh;
            }
            return root;
        }

        void Release(Vector2Int key, Slot slot)
        {
            if (slot.root != null) DestroyImmediate(slot.root);
            slot.root = null;
            if (source != null && slot.tile != null) source.Release(key, slot.tile);
            slot.tile = null;
        }

        bool Waiting(Vector2Int k) => retryAt.TryGetValue(k, out var t) && Time.unscaledTime < t;

        /// <summary>True when every tile touching the circle is in the scene.</summary>
        public bool IsReady(Vector3 p, float radius)
        {
            int r = Mathf.CeilToInt(radius / TileSize) + 1;
            var c = KeyOf(p.x, p.z);
            for (int x = c.x - r; x <= c.x + r; x++)
            for (int z = c.y - r; z <= c.y + r; z++)
            {
                var k = new Vector2Int(x, z);
                if (!exists.Contains(k) || Distance(k, p) > radius || Waiting(k)) continue; // offline: don't wait forever
                if (!slots.TryGetValue(k, out var s) || s.root == null) return false;
            }
            return true;
        }

        /// <summary>Loads the surroundings of <paramref name="p"/> first (spawn, beaming) and waits for them.</summary>
        public IEnumerator Preload(Vector3 p, float radius = 150f)
        {
            focusOverride = p;
            nextSchedule = 0f;
            while (!IsReady(p, radius))
            {
                Schedule(p);
                yield return null;
            }
            focusOverride = null;
        }

        /// <summary>Waits until the surroundings are loaded; no-op without a streamer (older scenes).</summary>
        public static IEnumerator WaitFor(Vector3 p, float radius = 150f)
        {
            if (Instance != null) yield return Instance.Preload(p, radius);
        }

#if UNITY_EDITOR
        /// <summary>Editor tools (snapshots, thumbnails): every tile at once, not saved with the scene.</summary>
        public void LoadAllInEditor()
        {
            UnloadAllInEditor();
            foreach (var k in tileKeys)
            {
                var t = UnityEditor.AssetDatabase.LoadAssetAtPath<WorldTile>(EditorTilePath(areaId, k));
                if (t != null) slots[k] = new Slot { tile = t, root = Build(t, transform, HideFlags.DontSave) };
            }
        }

        /// <summary>Editor tools: only the tiles within <paramref name="radius"/> (views into very large areas).</summary>
        public void LoadAroundInEditor(Vector3 p, float radius)
        {
            UnloadAllInEditor();
            foreach (var k in tileKeys)
            {
                if (Distance(k, p) > radius) continue;
                var t = UnityEditor.AssetDatabase.LoadAssetAtPath<WorldTile>(EditorTilePath(areaId, k));
                if (t != null) slots[k] = new Slot { tile = t, root = Build(t, transform, HideFlags.DontSave) };
            }
        }

        public void UnloadAllInEditor()
        {
            foreach (var s in slots.Values) if (s.root != null) DestroyImmediate(s.root);
            slots.Clear();
        }
#endif
    }
}
