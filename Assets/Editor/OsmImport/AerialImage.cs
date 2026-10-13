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

        // Mud… = islands (CoastBuilder); Field = bare/crop field in the open country (see SetBuiltUp)
        public enum Cover : byte { Unknown, Grass, Paving, Sand, Soil, Mud, Marsh, DuneGrass, Scrub, Queller, Field }

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

        public bool UnderCanopy(float x, float z)
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

        /// <summary>RGB + NIR (alpha) pixel at a local position; false outside the flown tiles.</summary>
        public bool TryColor(float x, float z, out Color32 c) => TryPixel(x, z, out c);

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
            if (CoastBuilder.Active && !wide) return CoastBuilder.CoverOf(this, x0, z0, size);
            if (!wide && built != null)
            {
                // Open country: smoothed field/meadow shapes instead of the per-pixel classes (bare fields read as paving).
                float cx = x0 + size * (upper ? 1f / 3f : 2f / 3f), cz = z0 + size * (upper ? 2f / 3f : 1f / 3f);
                if (Open(cx, cz)) return FieldScore(cx, cz) >= 0.5f ? Cover.Field : Cover.Grass;
            }
            var votes = new int[11];
            int n = Mathf.Max(2, Mathf.RoundToInt(size / step));
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                float u = (i + 0.5f) / n, t = (j + 0.5f) / n;
                if ((t >= u) != upper) continue; // z-offset ≥ x-offset → triangle (p00, p01, p11)
                float x = x0 + u * size, z = z0 + t * size;
                if (!UnderCanopy(x, z) && TryPixel(x, z, out var c)) votes[(int)(CoastBuilder.Active ? CoastBuilder.Classify(c, x, z) : Classify(c))]++;
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
            Cover.Mud => CoastBuilder.MudColor,
            Cover.Marsh => CoastBuilder.MarshColor,
            Cover.DuneGrass => CoastBuilder.DuneGrassColor,
            Cover.Scrub => CoastBuilder.ScrubColor,
            Cover.Queller => CoastBuilder.QuellerColor,
            Cover.Field => FieldColor,
            _ => fallback,
        };

        /// <summary>Bare or harvested field (ploughed soil with a little green), also the coarse terrain's field colour.</summary>
        public static readonly Color32 FieldColor = Color32.Lerp(WorldStyle.Grass, new Color32(150, 122, 90, 255), 0.65f);
        static readonly Color32 Straw = new Color32(190, 176, 128, 255);

        /// <summary>
        /// <see cref="CoverColor"/>, but meadows and fields in the open country get the measured brightness of the
        /// (smoothed) aerial image: every field its own tone, pale stubble vs. dark soil, lush vs. dry grass.
        /// </summary>
        public Color32 ColorAt(Cover cover, float x, float z, Color32 fallback)
        {
            var c = CoverColor(cover, fallback);
            if (built == null || cover != Cover.Grass && cover != Cover.Field || !Open(x, z)) return c;
            float t = Tone(x, z); // 0 dark … 1 bright
            if (cover == Cover.Field)
            {
                var f = Color32.Lerp(c, Straw, Mathf.Clamp01(t - 0.45f) * 0.9f);
                return Scale(f, 0.88f + 0.2f * Mathf.Min(t, 0.6f));
            }
            return Scale(Color32.Lerp(c, new Color32(150, 186, 104, 255), Mathf.Clamp01(t - 0.55f) * 0.8f), 0.9f + 0.16f * t);
        }

        static Color32 Scale(Color32 c, float f) =>
            new Color32((byte)Mathf.Clamp(c.r * f, 0, 255), (byte)Mathf.Clamp(c.g * f, 0, 255), (byte)Mathf.Clamp(c.b * f, 0, 255), 255);

        // ------------------------------------------------------------------ open country

        const float OpenCell = 10f;      // m: raster of the built-up test and the smoothed field map
        const float BuiltRadius = 30f;   // m around buildings: yards, driveways, terraces keep their measured cover
        bool[] built;                    // per cell: near buildings or inside built-up landuse
        float[] fieldMap, toneMap;       // per cell (smoothed): share of bare-field pixels, brightness 0…1
        int ow, oh;

        /// <summary>
        /// Prepares the open-country ground cover (call after loading; not for islands): marks cells near buildings and
        /// in built-up landuse, and builds a smoothed map of field vs. meadow from the image. Outside those cells
        /// <see cref="CoverIn"/> returns <see cref="Cover.Grass"/> or <see cref="Cover.Field"/> from that map – smooth
        /// field shapes (the image of large areas is only 16 m block means out there) and no false "paving" on bare
        /// fields. Areas without buildings near a field are unchanged in towns (everything is near a house there).
        /// </summary>
        public void SetBuiltUp(OsmData data)
        {
            if (CoastBuilder.Active) return;
            ow = Mathf.CeilToInt(w * step / OpenCell); oh = Mathf.CeilToInt(h * step / OpenCell);
            built = new bool[ow * oh];
            foreach (var p in data.polygons)
            {
                if (p.outer.Count < 3) continue;
                var t = p.tags;
                bool building = t.Has("building") || t.Has("building:part");
                string lu = t.Get("landuse");
                bool area = lu == "residential" || lu == "commercial" || lu == "industrial" || lu == "retail" || lu == "farmyard" ||
                            lu == "garages" || lu == "construction" || lu == "railway" || t.Is("amenity", "parking");
                if (!building && !area) continue;
                var b = GeoUtil.Bounds(p.outer);
                float r = building ? BuiltRadius : 0f;
                int c0 = Mathf.Max(0, Mathf.FloorToInt((b.xMin - r - xMin) / OpenCell)), c1 = Mathf.Min(ow - 1, Mathf.FloorToInt((b.xMax + r - xMin) / OpenCell));
                int r0 = Mathf.Max(0, Mathf.FloorToInt((b.yMin - r - zMin) / OpenCell)), r1 = Mathf.Min(oh - 1, Mathf.FloorToInt((b.yMax + r - zMin) / OpenCell));
                for (int k = r0; k <= r1; k++)
                for (int i = c0; i <= c1; i++)
                    if (building || OsmData.PointInRing(new Vector2(xMin + (i + 0.5f) * OpenCell, zMin + (k + 0.5f) * OpenCell), p.outer))
                        built[k * ow + i] = true;
            }

            // Per cell: share of bare pixels among the classified ones, mean brightness of the majority class.
            var field = new float[ow * oh]; var tone = new float[ow * oh]; var weight = new float[ow * oh];
            int n = Mathf.Max(2, Mathf.RoundToInt(OpenCell / step));
            for (int k = 0; k < oh; k++)
            for (int i = 0; i < ow; i++)
            {
                int bare = 0, green = 0; float vBare = 0f, vGreen = 0f;
                for (int a = 0; a < n; a++)
                for (int c = 0; c < n; c++)
                {
                    float x = xMin + (i + (a + 0.5f) / n) * OpenCell, z = zMin + (k + (c + 0.5f) / n) * OpenCell;
                    if (UnderCanopy(x, z) || !TryPixel(x, z, out var px)) continue;
                    var cl = Classify(px);
                    if (cl == Cover.Unknown) continue;
                    float v = Mathf.Max(px.r, Mathf.Max(px.g, px.b)) / 255f;
                    if (cl == Cover.Grass) { green++; vGreen += v; } else { bare++; vBare += v; }
                }
                int all = bare + green;
                if (all == 0) continue;
                int j = k * ow + i;
                weight[j] = 1f;
                field[j] = (float)bare / all;
                // Typical ranges in the DOP: meadows 0.27–0.51, bare fields 0.47–0.71 (max channel).
                tone[j] = bare > green ? Mathf.InverseLerp(0.47f, 0.71f, vBare / bare) : Mathf.InverseLerp(0.27f, 0.51f, vGreen / green);
            }
            for (int pass = 0; pass < 3; pass++) Blur(field, tone, weight);
            fieldMap = field; toneMap = tone;
        }

        /// <summary>Weighted [1 2 1]² blur (cells without pixels take their neighbours' values).</summary>
        void Blur(float[] a, float[] b, float[] wgt)
        {
            var ta = new float[a.Length]; var tb = new float[a.Length]; var tw = new float[a.Length];
            for (int axis = 0; axis < 2; axis++)
            {
                int stride = axis == 0 ? 1 : ow;
                for (int k = 0; k < oh; k++)
                for (int i = 0; i < ow; i++)
                {
                    int j = k * ow + i, pos = axis == 0 ? i : k, len = axis == 0 ? ow : oh;
                    float sa = 0f, sb = 0f, sw = 0f;
                    for (int d = -1; d <= 1; d++)
                    {
                        if (pos + d < 0 || pos + d >= len) continue;
                        int q = j + d * stride;
                        float f = (d == 0 ? 2f : 1f) * wgt[q];
                        sa += a[q] * f; sb += b[q] * f; sw += f;
                    }
                    ta[j] = sw > 0f ? sa / sw : 0f; tb[j] = sw > 0f ? sb / sw : 0.5f; tw[j] = sw > 0f ? 1f : 0f;
                }
                System.Array.Copy(ta, a, a.Length); System.Array.Copy(tb, b, b.Length); System.Array.Copy(tw, wgt, wgt.Length);
            }
        }

        /// <summary>No building within <see cref="BuiltRadius"/> and no built-up landuse (fields, meadows, forests).</summary>
        public bool Open(float x, float z)
        {
            if (built == null) return false;
            int i = Mathf.FloorToInt((x - xMin) / OpenCell), k = Mathf.FloorToInt((z - zMin) / OpenCell);
            return i < 0 || k < 0 || i >= ow || k >= oh || !built[k * ow + i];
        }

        /// <summary>Smoothed share of bare field (0 meadow … 1 field), bilinear between the cell centres.</summary>
        public float FieldScore(float x, float z) => fieldMap == null ? 0f : Bilinear(fieldMap, x, z);

        /// <summary>Smoothed brightness of the ground (0 dark … 1 bright, relative to its class).</summary>
        public float Tone(float x, float z) => toneMap == null ? 0.5f : Bilinear(toneMap, x, z);

        float Bilinear(float[] map, float x, float z)
        {
            float u = (x - xMin) / OpenCell - 0.5f, v = (z - zMin) / OpenCell - 0.5f;
            int i = Mathf.FloorToInt(u), k = Mathf.FloorToInt(v);
            float fu = u - i, fv = v - k;
            float S(int a, int b) => map[Mathf.Clamp(b, 0, oh - 1) * ow + Mathf.Clamp(a, 0, ow - 1)];
            return Mathf.Lerp(Mathf.Lerp(S(i, k), S(i + 1, k), fu), Mathf.Lerp(S(i, k + 1), S(i + 1, k + 1), fu), fv);
        }
    }
}
