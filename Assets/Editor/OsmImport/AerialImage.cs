using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// LGLN aerial image (DOP20, written by Tools/dop/fetch_dop.py) in the local frame: RGB + near infrared, 1 m pixels.
    /// Gives measured roof colours and ground cover; both are snapped to the game palette so the low-poly look stays.
    /// </summary>
    public class AerialImage
    {
        public static AerialImage Current { get; private set; }

        Color32[] px;
        bool[] canopy;
        int w, h;
        float xMin, zMin, step;

        public enum Cover : byte { Unknown, Grass, Paving, Sand, Soil }

        public static AerialImage Load(string dataDir, string id)
        {
            Current = null;
            string png = Path.Combine(dataDir, id + ".dop.png"), meta = Path.Combine(dataDir, id + ".dop.json");
            if (!File.Exists(png) || !File.Exists(meta)) return null;
            var m = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(meta));
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!tex.LoadImage(File.ReadAllBytes(png))) { Object.DestroyImmediate(tex); return null; }
            var img = new AerialImage
            {
                px = tex.GetPixels32(), w = tex.width, h = tex.height, // row 0 = south after LoadImage
                xMin = System.Convert.ToSingle(m["xMin"]), zMin = System.Convert.ToSingle(m["zMin"]), step = System.Convert.ToSingle(m["step"]),
            };
            Object.DestroyImmediate(tex);
            return Current = img;
        }

        /// <summary>Marks tree crowns (LiDAR trees: x, z, height, radius) – the ground under them is not visible.</summary>
        public void SetCanopy(List<Vector4> trees)
        {
            canopy = new bool[w * h];
            if (trees == null) return;
            foreach (var t in trees)
            {
                float r = Mathf.Clamp(t.w, 1f, 9f) * 0.85f;
                int cx0 = Mathf.FloorToInt((t.x - r - xMin) / step), cx1 = Mathf.FloorToInt((t.x + r - xMin) / step);
                int cz0 = Mathf.FloorToInt((t.y - r - zMin) / step), cz1 = Mathf.FloorToInt((t.y + r - zMin) / step);
                for (int ix = Mathf.Max(0, cx0); ix <= Mathf.Min(w - 1, cx1); ix++)
                for (int iz = Mathf.Max(0, cz0); iz <= Mathf.Min(h - 1, cz1); iz++)
                {
                    float dx = xMin + (ix + 0.5f) * step - t.x, dz = zMin + (iz + 0.5f) * step - t.y;
                    if (dx * dx + dz * dz <= r * r) canopy[iz * w + ix] = true;
                }
            }
        }

        bool UnderCanopy(float x, float z)
        {
            if (canopy == null) return false;
            int ix = Mathf.FloorToInt((x - xMin) / step), iz = Mathf.FloorToInt((z - zMin) / step);
            return ix >= 0 && iz >= 0 && ix < w && iz < h && canopy[iz * w + ix];
        }

        bool TryPixel(float x, float z, out Color32 c)
        {
            int ix = Mathf.FloorToInt((x - xMin) / step), iz = Mathf.FloorToInt((z - zMin) / step);
            if (ix < 0 || iz < 0 || ix >= w || iz >= h) { c = default; return false; }
            c = px[iz * w + ix];
            return c.r + c.g + c.b > 0; // outside the flown tiles
        }

        static float Ndvi(Color32 c) => (c.a - c.r) / (c.a + c.r + 1f);

        // ------------------------------------------------------------------ roofs

        /// <summary>
        /// Median colour of the pixels inside the given roof rings (projected to the ground), keeping ~0.8 m away from the
        /// edges – the image is no true orthophoto, eaves are a little off. Null if too few pixels.
        /// </summary>
        public Color32? RoofColor(List<List<Vector2>> rings, bool flat)
        {
            var inner = new List<Color32>();
            var all = new List<Color32>();
            foreach (var ring in rings)
            {
                if (ring.Count < 3) continue;
                var b = GeoUtil.Bounds(ring);
                for (float x = Mathf.Floor(b.xMin / step) * step + step * 0.5f; x < b.xMax; x += step)
                for (float z = Mathf.Floor(b.yMin / step) * step + step * 0.5f; z < b.yMax; z += step)
                {
                    var p = new Vector2(x, z);
                    if (!OsmData.PointInRing(p, ring) || !TryPixel(x, z, out var c)) continue;
                    all.Add(c);
                    if (EdgeDistance(p, ring) >= 0.8f) inner.Add(c);
                }
            }
            var use = inner.Count >= 4 ? inner : all;
            if (use.Count < 3) return null;

            // Overhanging trees: ignore vegetation pixels unless the roof is mostly green (green roof / moss).
            int veg = 0;
            foreach (var c in use) if (Ndvi(c) > 0.25f) veg++;
            bool greenRoof = veg > use.Count * 0.6f;
            if (!greenRoof && veg > 0) use = use.FindAll(c => Ndvi(c) <= 0.25f);
            if (use.Count < 3) return null;

            // Sun-lit half only: shadowed slopes and shadows of neighbours would turn red tiles into anthracite.
            use.Sort((x, y) => (x.r + x.g + x.b).CompareTo(y.r + y.g + y.b));
            if (use.Count >= 6) use = use.GetRange(use.Count / 2, use.Count - use.Count / 2);
            var median = Median(use);
            return Stylize(median, flat, greenRoof);
        }

        static float EdgeDistance(Vector2 p, List<Vector2> ring)
        {
            float best = float.MaxValue;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 a = ring[i], ab = ring[(i + 1) % ring.Count] - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                best = Mathf.Min(best, (p - (a + ab * t)).sqrMagnitude);
            }
            return Mathf.Sqrt(best);
        }

        static Color32 Median(List<Color32> cs)
        {
            var r = new List<byte>(cs.Count); var g = new List<byte>(cs.Count); var b = new List<byte>(cs.Count);
            foreach (var c in cs) { r.Add(c.r); g.Add(c.g); b.Add(c.b); }
            r.Sort(); g.Sort(); b.Sort();
            int m = cs.Count / 2;
            return new Color32(r[m], g[m], b[m], 255);
        }

        // Game palette for roofs (matches Lod2Builder / WorldStyle).
        static readonly Color32 TileRed = new Color32(150, 58, 42, 255);
        static readonly Color32 TileDarkRed = new Color32(112, 52, 42, 255);
        static readonly Color32 TileBrown = new Color32(118, 84, 60, 255);
        static readonly Color32 Anthracite = new Color32(62, 62, 66, 255);
        static readonly Color32 GreyLight = new Color32(118, 118, 118, 255);
        static readonly Color32 Moss = new Color32(96, 114, 80, 255);
        static readonly Color32 SolarBlue = new Color32(52, 60, 76, 255);
        static readonly Color32 FlatBitumen = new Color32(84, 86, 90, 255);
        static readonly Color32 FlatGravel = new Color32(132, 130, 126, 255);
        static readonly Color32 FlatGreen = new Color32(98, 124, 78, 255);

        /// <summary>Snap a measured (hazy) aerial colour to the palette, keeping a bit of the real tone.</summary>
        static Color32 Stylize(Color32 m, bool flat, bool green)
        {
            Color.RGBToHSV(m, out float hue, out float s, out float v);
            float deg = hue * 360f;
            Color32 pick;
            if (flat)
                pick = green || (deg > 70 && deg < 170 && s > 0.15f) ? FlatGreen : v < 0.38f ? FlatBitumen : FlatGravel;
            else if (green || (deg > 70 && deg < 170 && s > 0.12f)) pick = Moss;
            else if (v < 0.25f) pick = Anthracite;
            else if (deg > 190 && deg < 260 && s > 0.35f) pick = SolarBlue; // haze makes dark roofs look bluish
            else if ((deg <= 28 || deg >= 330) && s > 0.22f) pick = v > 0.42f ? TileRed : TileDarkRed;
            else if (deg > 28 && deg <= 60 && s > 0.18f) pick = TileBrown;
            else pick = v < 0.42f ? Anthracite : GreyLight;

            // 30 % of the measured tone (saturation boosted against the haze) for natural variety; greys stay grey.
            bool neutral = pick.Equals(Anthracite) || pick.Equals(GreyLight) || pick.Equals(FlatBitumen) || pick.Equals(FlatGravel);
            var boosted = Color.HSVToRGB(hue, neutral ? 0.03f : Mathf.Clamp01(s * 1.25f), Mathf.Clamp(v, 0.2f, 0.6f));
            var mixed = Color.Lerp(pick, boosted, 0.3f);
            return new Color32((byte)(mixed.r * 255), (byte)(mixed.g * 255), (byte)(mixed.b * 255), 255);
        }

        // ------------------------------------------------------------------ ground

        static Cover Classify(Color32 c)
        {
            int max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (max < 45) return Cover.Unknown; // deep shadow
            if (Ndvi(c) > 0.15f) return Cover.Grass;
            Color.RGBToHSV(c, out float hue, out float s, out float v);
            float deg = hue * 360f;
            if (v > 0.55f && deg >= 30 && deg <= 60 && s > 0.12f) return Cover.Sand;
            if (s > 0.2f && deg >= 12 && deg < 45) return Cover.Soil;
            return Cover.Paving;
        }

        /// <summary>
        /// Paved (driveway, path, road, parking) around a point: most visible pixels within <paramref name="radius"/>
        /// are grey/non-vegetated. Shadows and tree-covered pixels don't vote.
        /// </summary>
        public bool IsPaved(Vector2 p, float radius = 0.8f)
        {
            int paved = 0, other = 0;
            float d = step <= 1f ? step : Mathf.Max(radius, 0.1f); // coarse images (large areas): still 3×3 samples
            for (float dx = -radius; dx <= radius + 1e-3f; dx += d)
            for (float dz = -radius; dz <= radius + 1e-3f; dz += d)
            {
                if (!TryPixel(p.x + dx, p.y + dz, out var c)) continue;
                var k = Classify(c);
                if (k == Cover.Unknown) continue;
                if (k == Cover.Paving) paved++; else other++;
            }
            return paved > other && paved >= 2;
        }

        /// <summary>Majority ground cover in a triangle of a terrain cell (lower = below the x0z0–x1z1 diagonal).</summary>
        public Cover CoverIn(float x0, float z0, float size, bool upper, bool wide = false)
        {
            var votes = new int[5];
            int n = Mathf.Max(2, Mathf.RoundToInt(size / step));
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                float u = (i + 0.5f) / n, t = (j + 0.5f) / n;
                if ((t >= u) != upper) continue; // z-offset ≥ x-offset → triangle (p00, p01, p11)
                float x = x0 + u * size, z = z0 + t * size;
                if (!UnderCanopy(x, z) && TryPixel(x, z, out var c)) votes[(int)Classify(c)]++;
            }
            int best = -1;
            for (int k = 1; k < votes.Length; k++) if (votes[k] > 0 && (best < 0 || votes[k] > votes[best])) best = k;
            if (best >= 0) return (Cover)best;
            // Hidden under trees or in shadow: let the surroundings decide (3×3 cells).
            return wide ? Cover.Unknown : Wider(x0, z0, size);
        }

        Cover Wider(float x0, float z0, float size)
        {
            var a = CoverIn(x0 - size, z0 - size, size * 3f, true, true);
            return a != Cover.Unknown ? a : CoverIn(x0 - size, z0 - size, size * 3f, false, true);
        }

        public static Color32 CoverColor(Cover cover, Color32 fallback) => cover switch
        {
            Cover.Grass => WorldStyle.Grass,
            Cover.Paving => WorldStyle.Residential,
            Cover.Sand => WorldStyle.Sand,
            Cover.Soil => new Color32(150, 122, 90, 255),
            _ => fallback,
        };
    }
}
