using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using LeGamez.Quests;
using UnityEngine;

namespace LeGamez.World
{
    /// <summary>
    /// Platform- and engine-independent map files (format 5) – the same files serve iOS and Android, and they don't
    /// depend on the Unity version (AssetBundles did on both).
    ///
    /// Area file "NWA1": named entries [string name, int length, bytes]: area.json (deflate) = <see cref="AreaJson"/>,
    /// map.jpg (small overview of the whole area for the full map, raw JPEG; older files: map.png).
    /// Region file "NWR2" (1 km, 5 × 5 tiles): int count, table [int x, int z, long offset, int length], int sections,
    /// table [string name, long offset, int length], then one deflate blob per tile (so a single tile can be read without
    /// the others) and the section blobs: <see cref="RegionContent.FacadeSection"/> (deflate, FacadeDetails records of the
    /// region), <see cref="RegionContent.MapSection"/> (raw JPEG, sharp top-down image of the region for the minimap).
    /// "NWR1" = the same without the section table.
    /// Tile blob: int parts; per part: byte material, byte flags, byte layer, string group, int vertexCount,
    /// int indexCount, bool uvs; positions (3 floats), normals (3 sbytes), colours (RGBA32), [uvs (4 floats)],
    /// indices (ushort if vertexCount ≤ 65535, else int).
    /// </summary>
    public static class MapFormat
    {
        // ------------------------------------------------------------------ area

        [Serializable]
        public class AreaJson
        {
            public string areaId, displayName, attribution;
            public GeoProjection projection;
            public float sizeMeters;
            public Vector2 boundsMin, boundsMax;
            public List<string> allowedVehicles = new List<string>();
            public Vector3 spawn, spawnBikeSpot;
            public List<Vector3> bikeSpots = new List<Vector3>();
            public List<Vector4> carSpots = new List<Vector4>();
            public List<Vector2Int> tileKeys = new List<Vector2Int>();
            public List<TrafficLights.Head> trafficHeads = new List<TrafficLights.Head>();
            public List<TrafficLights.Signal> trafficSignals = new List<TrafficLights.Signal>();
            public List<SignTexts.Item> signTexts = new List<SignTexts.Item>();
            public List<PoiEntry> pois = new List<PoiEntry>();
            public List<string> streetNames = new List<string>();
            public List<Vector4> streetSegments = new List<Vector4>();
            public List<int> streetNameOf = new List<int>();
        }

        /// <param name="mapImage">Overview image of the area (JPEG or PNG), or null.</param>
        public static byte[] WriteArea(AreaData d, byte[] mapImage)
        {
            var j = new AreaJson
            {
                areaId = d.areaId, displayName = d.displayName, attribution = d.attribution, projection = d.projection,
                sizeMeters = d.sizeMeters, boundsMin = d.boundsMin, boundsMax = d.boundsMax,
                allowedVehicles = d.allowedVehicles, spawn = d.spawn, spawnBikeSpot = d.spawnBikeSpot,
                bikeSpots = d.bikeSpots, carSpots = d.carSpots, tileKeys = d.tileKeys,
                trafficHeads = d.trafficHeads, trafficSignals = d.trafficSignals, signTexts = d.signTexts,
                pois = d.pois != null ? d.pois.entries : new List<PoiEntry>(),
            };
            if (d.streets != null)
            {
                j.streetNames = d.streets.names;
                j.streetSegments = d.streets.segments;
                j.streetNameOf = d.streets.nameOf;
            }
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { (byte)'N', (byte)'W', (byte)'A', (byte)'1' });
                var entries = new List<(string, byte[])> { ("area.json", Deflate(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(j)))) };
                if (mapImage != null) entries.Add((IsJpeg(mapImage) ? "map.jpg" : "map.png", mapImage));
                w.Write(entries.Count);
                foreach (var (name, bytes) in entries) { w.Write(name); w.Write(bytes.Length); w.Write(bytes); }
            }
            return ms.ToArray();
        }

        /// <summary>Builds a runtime AreaData (with POI database, street index, overview map texture).</summary>
        public static AreaData ReadArea(byte[] file)
        {
            var entries = new Dictionary<string, byte[]>();
            using (var r = new BinaryReader(new MemoryStream(file)))
            {
                var magic = r.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != 'N' || magic[1] != 'W' || magic[2] != 'A') throw new InvalidDataException("not an area file");
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string name = r.ReadString();
                    entries[name] = r.ReadBytes(r.ReadInt32());
                }
            }
            var j = JsonUtility.FromJson<AreaJson>(System.Text.Encoding.UTF8.GetString(Inflate(entries["area.json"])));
            var d = ScriptableObject.CreateInstance<AreaData>();
            d.areaId = j.areaId; d.displayName = j.displayName; d.attribution = j.attribution; d.projection = j.projection;
            d.sizeMeters = j.sizeMeters; d.boundsMin = j.boundsMin; d.boundsMax = j.boundsMax;
            d.allowedVehicles = j.allowedVehicles; d.spawn = j.spawn; d.spawnBikeSpot = j.spawnBikeSpot;
            d.bikeSpots = j.bikeSpots; d.carSpots = j.carSpots; d.tileKeys = j.tileKeys;
            d.trafficHeads = j.trafficHeads; d.trafficSignals = j.trafficSignals; d.signTexts = j.signTexts;
            d.pois = ScriptableObject.CreateInstance<PoiDatabase>();
            d.pois.entries = j.pois;
            d.streets = ScriptableObject.CreateInstance<StreetIndex>();
            d.streets.names = j.streetNames; d.streets.segments = j.streetSegments; d.streets.nameOf = j.streetNameOf;
            if (entries.TryGetValue("map.jpg", out var img) || entries.TryGetValue("map.png", out img))
            {
                var tex = new Texture2D(2, 2, IsJpeg(img) ? TextureFormat.RGB24 : TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
                if (tex.LoadImage(img, true)) d.mapTexture = tex;
            }
            return d;
        }

        /// <summary>Destroys what <see cref="ReadArea"/> created.</summary>
        public static void Destroy(AreaData d)
        {
            if (d == null) return;
            if (d.mapTexture != null) UnityEngine.Object.Destroy(d.mapTexture);
            if (d.pois != null) UnityEngine.Object.Destroy(d.pois);
            if (d.streets != null) UnityEngine.Object.Destroy(d.streets);
            UnityEngine.Object.Destroy(d);
        }

        // ------------------------------------------------------------------ region / tiles

        /// <param name="sections">Extra named blobs of the region (see <see cref="RegionContent"/>), stored as given.</param>
        public static byte[] WriteRegion(List<WorldTile> tiles, List<(string name, byte[] bytes)> sections = null)
        {
            var blobs = new List<byte[]>();
            foreach (var t in tiles) blobs.Add(Deflate(EncodeTile(t)));
            sections ??= new List<(string, byte[])>();
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { (byte)'N', (byte)'W', (byte)'R', (byte)'2' });
                w.Write(tiles.Count);
                long offset = 4 + 4 + tiles.Count * (4 + 4 + 8 + 4) + SectionTableSize(sections);
                for (int i = 0; i < tiles.Count; i++)
                {
                    w.Write(tiles[i].key.x); w.Write(tiles[i].key.y); w.Write(offset); w.Write(blobs[i].Length);
                    offset += blobs[i].Length;
                }
                w.Write(sections.Count);
                foreach (var (name, bytes) in sections) { w.Write(name); w.Write(offset); w.Write(bytes.Length); offset += bytes.Length; }
                foreach (var b in blobs) w.Write(b);
                foreach (var (_, bytes) in sections) w.Write(bytes);
            }
            return ms.ToArray();
        }

        static long SectionTableSize(List<(string name, byte[] bytes)> sections)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(sections.Count);
                foreach (var (name, _) in sections) { w.Write(name); w.Write(0L); w.Write(0); }
            }
            return ms.ToArray().Length;
        }

        /// <summary>Section table of a region file: name → (offset, length); empty for "NWR1" files.</summary>
        public static Dictionary<string, (long offset, int length)> ReadRegionSections(Stream s)
        {
            var r = new BinaryReader(s);
            var magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != 'N' || magic[1] != 'W' || magic[2] != 'R') throw new InvalidDataException("not a region file");
            var result = new Dictionary<string, (long, int)>();
            int n = r.ReadInt32();
            if (magic[3] < '2') return result;
            s.Position += n * (4 + 4 + 8 + 4);
            int sections = r.ReadInt32();
            for (int i = 0; i < sections; i++) result[r.ReadString()] = (r.ReadInt64(), r.ReadInt32());
            return result;
        }

        /// <summary>Thread-safe: facade records + minimap image of a region file (no Unity objects touched).</summary>
        public static RegionContent ReadRegionContent(string path, Vector2Int region)
        {
            var c = new RegionContent { key = region };
            using var fs = File.OpenRead(path);
            var sections = ReadRegionSections(fs);
            byte[] Section(string name)
            {
                if (!sections.TryGetValue(name, out var at)) return null;
                var b = new byte[at.length];
                fs.Position = at.offset;
                int read = 0;
                while (read < b.Length) { int k = fs.Read(b, read, b.Length - read); if (k <= 0) break; read += k; }
                return b;
            }
            var facade = Section(RegionContent.FacadeSection);
            if (facade != null) c.facade = RegionContent.ToFloats(Inflate(facade));
            c.mapImage = Section(RegionContent.MapSection);
            return c;
        }

        /// <summary>Table of contents of a region file: tile key → (offset, length).</summary>
        public static Dictionary<Vector2Int, (long offset, int length)> ReadRegionIndex(Stream s)
        {
            var r = new BinaryReader(s);
            var magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != 'N' || magic[1] != 'W' || magic[2] != 'R') throw new InvalidDataException("not a region file");
            int n = r.ReadInt32();
            var index = new Dictionary<Vector2Int, (long, int)>(n);
            for (int i = 0; i < n; i++)
            {
                var k = new Vector2Int(r.ReadInt32(), r.ReadInt32());
                index[k] = (r.ReadInt64(), r.ReadInt32());
            }
            return index;
        }

        /// <summary>Decoded tile geometry (plain arrays – built on a worker thread, turned into meshes on the main thread).</summary>
        public class TileData
        {
            public Vector2Int key;
            public List<PartData> parts = new List<PartData>();
        }

        public class PartData
        {
            public byte material, flags, layer;
            public string group;
            public Vector3[] positions, normals;
            public Color32[] colors;
            public Vector4[] uvs;
            public int[] indices;
        }

        static byte[] EncodeTile(WorldTile t)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(t.parts.Count);
                foreach (var p in t.parts)
                {
                    var m = p.mesh;
                    var v = m.vertices; var nrm = m.normals; var col = m.colors32; var idx = m.triangles;
                    var uvs = new List<Vector4>();
                    m.GetUVs(0, uvs);
                    w.Write((byte)p.material); w.Write(p.flags); w.Write((byte)p.layer); w.Write(p.group ?? "");
                    w.Write(v.Length); w.Write(idx.Length); w.Write(uvs.Count == v.Length);
                    foreach (var x in v) { w.Write(x.x); w.Write(x.y); w.Write(x.z); }
                    for (int i = 0; i < v.Length; i++)
                    {
                        var n = i < nrm.Length ? nrm[i] : Vector3.up;
                        w.Write((sbyte)Mathf.RoundToInt(n.x * 127f)); w.Write((sbyte)Mathf.RoundToInt(n.y * 127f)); w.Write((sbyte)Mathf.RoundToInt(n.z * 127f));
                    }
                    for (int i = 0; i < v.Length; i++)
                    {
                        var c = i < col.Length ? col[i] : new Color32(255, 255, 255, 255);
                        w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a);
                    }
                    if (uvs.Count == v.Length) foreach (var u in uvs) { w.Write(u.x); w.Write(u.y); w.Write(u.z); w.Write(u.w); }
                    bool small = v.Length <= 65535;
                    foreach (var i in idx) { if (small) w.Write((ushort)i); else w.Write(i); }
                }
            }
            return ms.ToArray();
        }

        /// <summary>Thread-safe: no Unity objects are touched.</summary>
        public static TileData DecodeTile(Vector2Int key, byte[] blob)
        {
            var t = new TileData { key = key };
            using var r = new BinaryReader(new MemoryStream(Inflate(blob)));
            int parts = r.ReadInt32();
            for (int pi = 0; pi < parts; pi++)
            {
                var p = new PartData { material = r.ReadByte(), flags = r.ReadByte(), layer = r.ReadByte(), group = r.ReadString() };
                int vc = r.ReadInt32(), ic = r.ReadInt32();
                bool hasUv = r.ReadBoolean();
                p.positions = new Vector3[vc];
                for (int i = 0; i < vc; i++) p.positions[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                p.normals = new Vector3[vc];
                for (int i = 0; i < vc; i++) p.normals[i] = new Vector3(r.ReadSByte() / 127f, r.ReadSByte() / 127f, r.ReadSByte() / 127f);
                p.colors = new Color32[vc];
                for (int i = 0; i < vc; i++) p.colors[i] = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
                if (hasUv)
                {
                    p.uvs = new Vector4[vc];
                    for (int i = 0; i < vc; i++) p.uvs[i] = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                }
                p.indices = new int[ic];
                bool small = vc <= 65535;
                for (int i = 0; i < ic; i++) p.indices[i] = small ? r.ReadUInt16() : r.ReadInt32();
                t.parts.Add(p);
            }
            return t;
        }

        /// <summary>Main thread: meshes + a runtime WorldTile for the streamer. Meshes without collider are made GPU-only.</summary>
        public static WorldTile BuildTile(TileData d, string name)
        {
            var tile = ScriptableObject.CreateInstance<WorldTile>();
            tile.name = name;
            tile.key = d.key;
            foreach (var p in d.parts)
            {
                var mesh = new Mesh { name = p.group };
                if (p.positions.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(p.positions);
                mesh.SetNormals(p.normals);
                mesh.SetColors(p.colors);
                if (p.uvs != null) mesh.SetUVs(0, p.uvs);
                mesh.SetIndices(p.indices, MeshTopology.Triangles, 0);
                mesh.RecalculateBounds();
                bool collider = (p.flags & WorldTile.FlagCollider) != 0;
                mesh.UploadMeshData(!collider); // colliders need the CPU copy
                tile.parts.Add(new WorldTile.Part { mesh = mesh, material = p.material, flags = p.flags, layer = p.layer, group = p.group });
            }
            return tile;
        }

        public static void DestroyTile(WorldTile t)
        {
            foreach (var p in t.parts) if (p.mesh != null) UnityEngine.Object.Destroy(p.mesh);
            UnityEngine.Object.Destroy(t);
        }

        // ------------------------------------------------------------------ helpers

        public static bool IsJpeg(byte[] b) => b != null && b.Length > 2 && b[0] == 0xFF && b[1] == 0xD8;

        public static byte[] Deflate(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal)) z.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        public static byte[] Inflate(byte[] data)
        {
            using var src = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
