using System.Collections.Generic;
using System.IO;
using LeGamez.World;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Incremental import: which tiles have to be built at all. Every builder still runs for the whole area (shared random
    /// sequence, object ids and catalogs stay exactly as in a full import – the builders take ~2 min of a large area),
    /// but everything after them – draping onto the terrain, meshes, asset writes, region images, export – only happens
    /// for tiles whose <b>signature</b> changed: a hash of the tile's geometry as the builders left it (before draping),
    /// the sea surface, its instances, the final terrain and <see cref="Version"/>. Same signature ⇒ the tile asset on
    /// disk is exactly what this import would write (the steps after the hash are deterministic).
    /// </summary>
    public static class TileBuild
    {
        /// <summary>Bump when code that runs <i>after</i> the signature changes its output (TerrainDrape, MeshSink.ToMesh,
        /// the part rules in OsmAreaImporter.WriteTiles, MapFormat tile fields …) – every tile is rebuilt once then.
        /// Builder changes need no bump: their output is part of the signature.</summary>
        public const int Version = 1;

        public class Plan
        {
            /// <summary>Signature per tile (pre-drape content; tiles that end up empty may be listed too).</summary>
            public readonly Dictionary<Vector2Int, ulong> signatures = new Dictionary<Vector2Int, ulong>();
            /// <summary>Tiles to drape, mesh and write; all others keep their asset.</summary>
            public readonly HashSet<Vector2Int> build = new HashSet<Vector2Int>();
            /// <summary>Signatures of the last import; null = unknown (first import, tiles deleted …) → everything is built.</summary>
            public Dictionary<Vector2Int, ulong> previous;
            public bool Builds(Vector2Int key) => previous == null || build.Contains(key);
        }

        /// <summary>The tile a sink batch belongs to (its chunk centre).</summary>
        public static Vector2Int KeyOf((string mat, Vector2Int chunk) batch)
        {
            float chunkSize = batch.mat == WorldStyle.MatDetails ? MeshSink.DetailChunkSize : MeshSink.ChunkSize;
            return WorldStreamer.KeyOf((batch.chunk.x + 0.5f) * chunkSize, (batch.chunk.y + 0.5f) * chunkSize);
        }

        /// <summary>
        /// Signatures of every tile and the tiles that changed since the last import (or whose asset is missing).
        /// Call after all builders, before draping; <paramref name="sea"/> = the level sea surface, added after draping.
        /// <paramref name="full"/>: build everything (forced rebuild).
        /// </summary>
        public static Plan Make(string id, MeshSink sink, MeshSink sea, TerrainField terrain, bool full)
        {
            var plan = new Plan { previous = full ? null : TileManifest.Load(id) };
            ulong salt = Mix(Mix(0x6E6F726469736368UL, (ulong)Version), TerrainHash(terrain));
            var hashers = new Dictionary<Vector2Int, Hasher>();
            Hasher At(Vector2Int key)
            {
                if (!hashers.TryGetValue(key, out var h)) hashers[key] = h = new Hasher(salt);
                return h;
            }
            foreach (var s in new[] { sink, sea })
                foreach (var kv in s.Batches)
                {
                    if (kv.Value.vertices.Count == 0) continue;
                    At(KeyOf(kv.Key)).Add(kv.Key.mat, kv.Key.chunk, kv.Value, s == sea);
                }
            foreach (var inst in sink.instances) At(WorldStreamer.KeyOf(inst.position.x, inst.position.z)).Add(inst);

            foreach (var kv in hashers)
            {
                ulong sig = kv.Value.Value;
                plan.signatures[kv.Key] = sig;
                if (plan.previous == null || !plan.previous.TryGetValue(kv.Key, out var old) || old != sig ||
                    !File.Exists(WorldStreamer.EditorTilePath(id, kv.Key)))
                    plan.build.Add(kv.Key);
            }
            return plan;
        }

        /// <summary>Appends the batches of <paramref name="from"/> to <paramref name="to"/> (same keys, indices shifted) –
        /// the same lists as if the geometry had been added to <paramref name="to"/> directly.</summary>
        public static void Append(MeshSink to, MeshSink from)
        {
            foreach (var kv in from.Batches)
            {
                var b = to.GetBatch(kv.Key.mat, kv.Key.chunk);
                int offset = b.vertices.Count;
                b.vertices.AddRange(kv.Value.vertices);
                b.normals.AddRange(kv.Value.normals);
                b.colors.AddRange(kv.Value.colors);
                b.uvs.AddRange(kv.Value.uvs);
                foreach (int i in kv.Value.triangles) b.triangles.Add(i + offset);
            }
            to.instances.AddRange(from.instances);
        }

        static ulong TerrainHash(TerrainField t)
        {
            var h = new Hasher(0);
            if (t == null || t.IsFlat) return 0;
            var s = t.Samples;
            for (int i = 0; i < s.Length; i++) h.Add(Bits(s[i]));
            h.Add((ulong)t.SamplesX); h.Add((ulong)t.SamplesZ);
            var o = t.SamplePos(0, 0);
            h.Add(Bits(o.x)); h.Add(Bits(o.y)); h.Add(Bits(t.Base));
            return h.Value;
        }

        static ulong Bits(float f) => (uint)System.BitConverter.SingleToInt32Bits(f);

        static ulong Mix(ulong h, ulong v)
        {
            h ^= v * 0x9E3779B97F4A7C15UL;
            h = (h << 31) | (h >> 33);
            return h * 0xC2B2AE3D27D4EB4FUL;
        }

        /// <summary>Fast 64-bit hash over the raw values (change detection, not security).</summary>
        class Hasher
        {
            ulong h;
            public Hasher(ulong seed) { h = seed ^ 0x27D4EB2F165667C5UL; }
            public ulong Value => Mix(h, 0xFF51AFD7ED558CCDUL);
            public void Add(ulong v) => h = Mix(h, v);

            void Add(string s)
            {
                Add((ulong)s.Length);
                foreach (char c in s) Add(c);
            }

            public void Add(string mat, Vector2Int chunk, MeshSink.Batch b, bool sea)
            {
                Add(sea ? 2UL : 1UL);
                Add(mat);
                Add((ulong)(uint)chunk.x << 32 | (uint)chunk.y);
                Add((ulong)b.vertices.Count); Add((ulong)b.triangles.Count);
                foreach (var v in b.vertices) { Add(Bits(v.x) << 32 | Bits(v.y)); Add(Bits(v.z)); }
                foreach (var n in b.normals) { Add(Bits(n.x) << 32 | Bits(n.y)); Add(Bits(n.z)); }
                foreach (var c in b.colors) Add((ulong)c.r | (ulong)c.g << 8 | (ulong)c.b << 16 | (ulong)c.a << 24);
                foreach (var u in b.uvs) { Add(Bits(u.x) << 32 | Bits(u.y)); Add(Bits(u.z) << 32 | Bits(u.w)); }
                foreach (int i in b.triangles) Add((uint)i);
            }

            public void Add(WorldTile.Instance i)
            {
                Add(3UL);
                Add(i.proto);
                Add(Bits(i.position.x) << 32 | Bits(i.position.y)); Add(Bits(i.position.z));
                Add(Bits(i.yaw));
                Add(Bits(i.scale.x) << 32 | Bits(i.scale.y)); Add(Bits(i.scale.z));
                Add((ulong)i.color.r | (ulong)i.color.g << 8 | (ulong)i.color.b << 16 | (ulong)i.color.a << 24);
            }
        }
    }
}
