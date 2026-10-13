using System.Collections.Generic;
using LeGamez.World;
using UnityEngine;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>
    /// Sharp top-down images of the 1 km regions (<see cref="RegionContent.mapImage"/>, ~1 m per pixel) for the minimap
    /// and the zoomed full map. Decoded on demand (at most one per frame), kept for a few regions, dropped when the
    /// streamer lets the region go. Only regions with tiles in the scene have an image – elsewhere the maps show the
    /// small area overview underneath.
    /// </summary>
    public static class RegionMaps
    {
        const int SoftLimit = 8;
        const float KeepSeconds = 2f; // in use = asked for within this time; never evicted

        class Entry
        {
            public Texture2D tex;
            public byte[] src;
            public float used;
        }

        static readonly Dictionary<Vector2Int, Entry> cache = new Dictionary<Vector2Int, Entry>();
        static readonly List<Vector2Int> scratch = new List<Vector2Int>();
        static WorldStreamer owner;
        static int lastDecodeFrame = -1;

        /// <summary>The region's image, or null when the region is not loaded or not decoded yet (try again later).</summary>
        public static Texture2D Get(Vector2Int r)
        {
            var s = Streamer();
            if (s == null || !s.Regions.TryGetValue(r, out var c) || c.mapImage == null) return null;
            if (cache.TryGetValue(r, out var e))
            {
                if (e.src == c.mapImage && e.tex != null) { e.used = Time.unscaledTime; return e.tex; }
                Object.Destroy(e.tex);
                cache.Remove(r);
            }
            if (lastDecodeFrame == Time.frameCount) return null; // spread the decoding (~10 ms per image on phones)
            lastDecodeFrame = Time.frameCount;
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false)
            {
                name = $"RegionMap_{r.x}_{r.y}", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            if (!tex.LoadImage(c.mapImage, true)) { Object.Destroy(tex); return null; }
            cache[r] = new Entry { tex = tex, src = c.mapImage, used = Time.unscaledTime };
            return tex;
        }

        /// <summary>Frees images of regions that left the scene and, above the soft limit, the longest unused ones.</summary>
        public static void Trim()
        {
            var s = Streamer();
            scratch.Clear();
            foreach (var kv in cache)
                if (s == null || !s.Regions.TryGetValue(kv.Key, out var c) || c.mapImage != kv.Value.src) scratch.Add(kv.Key);
            foreach (var k in scratch) Remove(k);
            while (cache.Count > SoftLimit)
            {
                Vector2Int? oldest = null;
                float t = Time.unscaledTime - KeepSeconds;
                foreach (var kv in cache)
                    if (kv.Value.used < t) { t = kv.Value.used; oldest = kv.Key; }
                if (oldest == null) break;
                Remove(oldest.Value);
            }
        }

        static void Remove(Vector2Int k)
        {
            if (cache.TryGetValue(k, out var e) && e.tex != null) Object.Destroy(e.tex);
            cache.Remove(k);
        }

        static WorldStreamer Streamer()
        {
            var s = WorldStreamer.Instance;
            if (s != owner)
            {
                foreach (var e in cache.Values) if (e.tex != null) Object.Destroy(e.tex);
                cache.Clear();
                owner = s;
            }
            return s;
        }

        /// <summary>
        /// Shows the images of <paramref name="wanted"/> regions as RawImages under <paramref name="layer"/> and hides the
        /// others. <paramref name="place"/> sets position/size of an image for a region.
        /// </summary>
        public static void Show(RectTransform layer, Dictionary<Vector2Int, RawImage> shown, ICollection<Vector2Int> wanted,
            System.Action<Vector2Int, RectTransform> place)
        {
            Trim();
            scratch.Clear();
            foreach (var kv in shown) if (!wanted.Contains(kv.Key)) scratch.Add(kv.Key);
            foreach (var k in scratch)
            {
                Object.Destroy(shown[k].gameObject);
                shown.Remove(k);
            }
            foreach (var r in wanted)
            {
                var tex = Get(r);
                shown.TryGetValue(r, out var img);
                if (tex == null)
                {
                    if (img != null) img.gameObject.SetActive(false);
                    continue;
                }
                if (img == null)
                {
                    var rt = UiKit.Rect($"Region {r.x}_{r.y}", layer, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
                    img = rt.gameObject.AddComponent<RawImage>();
                    img.raycastTarget = false;
                    shown[r] = img;
                }
                place(r, img.rectTransform);
                img.texture = tex;
                img.gameObject.SetActive(true);
            }
        }
    }
}
