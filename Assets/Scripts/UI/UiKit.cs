using UnityEngine;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>Tiny code-only UI toolkit: rounded panels, soft shadows, labels, palette.</summary>
    public static class UiKit
    {
        // Brand palette from the "NORDisch" logo: deep navy, mustard, white.
        public static readonly Color Navy = Hex(0x1A3A4E);
        public static readonly Color Mustard = Hex(0xE3A82B);
        public static readonly Color Ink = Navy;
        public static readonly Color InkSoft = Hex(0x5D7383);
        public static readonly Color Card = new Color(1f, 0.99f, 0.97f, 0.97f);
        public static readonly Color Accent = Mustard;
        public static readonly Color AccentSoft = Hex(0xF7E7C2);
        public static readonly Color Sea = Navy;
        public static readonly Color Island = Mustard;
        public static readonly Color City = Navy;
        public static readonly Color Track = Hex(0xE4E8EB);
        public static readonly Color Done = Hex(0x4E9A6B);

        public static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

        // ---------------------------------------------------------------- icons

        public enum IconKind { Jump, Run, Bike, Walk, BikeFast, Car, CarFast }

        static readonly System.Collections.Generic.Dictionary<IconKind, Sprite> icons = new System.Collections.Generic.Dictionary<IconKind, Sprite>();

        /// <summary>White pictogram, drawn as anti-aliased strokes on a 128 px canvas (coords in 0..64).</summary>
        public static Sprite Icon(IconKind kind)
        {
            if (icons.TryGetValue(kind, out var cached)) return cached;
            var strokes = new System.Collections.Generic.List<(Vector2 a, Vector2 b, float w)>();
            var dots = new System.Collections.Generic.List<(Vector2 c, float r)>();
            void S(float x0, float y0, float x1, float y1, float w) => strokes.Add((new Vector2(x0, y0), new Vector2(x1, y1), w));
            switch (kind)
            {
                case IconKind.Jump:
                    // Up arrow lifting off a ground line.
                    S(32, 20, 32, 52, 6.5f);
                    S(32, 54, 19, 41, 6.5f);
                    S(32, 54, 45, 41, 6.5f);
                    S(14, 11, 50, 11, 5f);
                    break;
                case IconKind.Run:
                    // Running figure leaning forward + speed lines.
                    dots.Add((new Vector2(42, 53), 5.5f));
                    S(38, 43, 31, 26, 6f);          // torso
                    S(37, 40, 47, 33, 4.5f);        // front arm
                    S(47, 33, 52, 38, 4.5f);
                    S(36, 40, 26, 41, 4.5f);        // back arm
                    S(26, 41, 22, 34, 4.5f);
                    S(31, 26, 41, 18, 5f);          // front leg
                    S(41, 18, 44, 8, 5f);
                    S(31, 26, 23, 16, 5f);          // back leg
                    S(23, 16, 13, 15, 5f);
                    S(6, 34, 17, 34, 3.5f);         // speed lines
                    S(3, 26, 14, 26, 3.5f);
                    break;
                case IconKind.Bike:
                    // Bicycle: two wheels as rings, step-through frame, handlebar, saddle.
                    Ring(strokes, new Vector2(16, 20), 10f, 4f);
                    Ring(strokes, new Vector2(48, 20), 10f, 4f);
                    S(16, 20, 28, 20, 4f);
                    S(28, 20, 22, 36, 4f);
                    S(28, 20, 42, 34, 4f);
                    S(42, 34, 48, 20, 4f);
                    S(42, 34, 40, 42, 4f);
                    S(36, 42, 46, 42, 4f);
                    S(18, 37, 27, 37, 5f);
                    break;
                case IconKind.BikeFast:
                    // Bicycle shifted right + speed lines: "fast gear".
                    Ring(strokes, new Vector2(24, 20), 10f, 4f);
                    Ring(strokes, new Vector2(54, 20), 10f, 4f);
                    S(24, 20, 35, 20, 4f);
                    S(35, 20, 30, 36, 4f);
                    S(35, 20, 48, 34, 4f);
                    S(48, 34, 54, 20, 4f);
                    S(48, 34, 46, 42, 4f);
                    S(42, 42, 52, 42, 4f);
                    S(26, 37, 34, 37, 5f);
                    S(2, 34, 11, 34, 3.5f);
                    S(4, 26, 12, 26, 3.5f);
                    break;
                case IconKind.Car:
                case IconKind.CarFast:
                {
                    // Side view hatchback: body outline, roof, two wheels; fast variant with speed lines.
                    float ox = kind == IconKind.CarFast ? 6f : 0f;
                    S(8 + ox, 22, 56 + ox, 22, 4f);           // sill
                    S(8 + ox, 22, 8 + ox, 32, 4f);            // rear
                    S(56 + ox, 22, 56 + ox, 30, 4f);          // front
                    S(8 + ox, 32, 18 + ox, 32, 4f);           // rear deck
                    S(18 + ox, 32, 24 + ox, 42, 4f);          // rear pillar
                    S(24 + ox, 42, 40 + ox, 42, 4f);          // roof
                    S(40 + ox, 42, 47 + ox, 32, 4f);          // windscreen
                    S(47 + ox, 32, 56 + ox, 30, 4f);          // bonnet
                    Ring(strokes, new Vector2(19 + ox, 20), 6f, 4f);
                    Ring(strokes, new Vector2(46 + ox, 20), 6f, 4f);
                    if (kind == IconKind.CarFast)
                    {
                        S(0, 36, 8, 36, 3.5f);
                        S(2, 28, 9, 28, 3.5f);
                    }
                    break;
                }
                case IconKind.Walk:
                    // Walking figure (calm, upright) – "get off the bike".
                    dots.Add((new Vector2(34, 54), 5.5f));
                    S(33, 44, 31, 26, 6f);
                    S(33, 41, 40, 32, 4.5f);
                    S(32, 41, 25, 33, 4.5f);
                    S(31, 26, 38, 16, 5f);
                    S(38, 16, 40, 7, 5f);
                    S(31, 26, 26, 16, 5f);
                    S(26, 16, 20, 9, 5f);
                    break;
            }
            // Centre the pictogram on its actual ink extent (strokes + dots), not on the drawing grid.
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var (a, b, w) in strokes)
            {
                minX = Mathf.Min(minX, Mathf.Min(a.x, b.x) - w / 2); maxX = Mathf.Max(maxX, Mathf.Max(a.x, b.x) + w / 2);
                minY = Mathf.Min(minY, Mathf.Min(a.y, b.y) - w / 2); maxY = Mathf.Max(maxY, Mathf.Max(a.y, b.y) + w / 2);
            }
            foreach (var (c, r) in dots)
            {
                minX = Mathf.Min(minX, c.x - r); maxX = Mathf.Max(maxX, c.x + r);
                minY = Mathf.Min(minY, c.y - r); maxY = Mathf.Max(maxY, c.y + r);
            }
            var shift = new Vector2(32f - (minX + maxX) * 0.5f, 32f - (minY + maxY) * 0.5f);
            for (int i = 0; i < strokes.Count; i++) strokes[i] = (strokes[i].a + shift, strokes[i].b + shift, strokes[i].w);
            for (int i = 0; i < dots.Count; i++) dots[i] = (dots[i].c + shift, dots[i].r);

            const int size = 128;
            const float scale = size / 64f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f) / scale;
                float d = float.MaxValue;
                foreach (var (a, b, w) in strokes)
                {
                    Vector2 ab = b - a;
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
                    d = Mathf.Min(d, Vector2.Distance(p, a + ab * t) - w * 0.5f);
                }
                foreach (var (c, r) in dots) d = Mathf.Min(d, Vector2.Distance(p, c) - r);
                float alpha = Mathf.Clamp01(0.5f - d * scale);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            var sprite = Sprite.Create(tex, new UnityEngine.Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            icons[kind] = sprite;
            return sprite;
        }

        static void Ring(System.Collections.Generic.List<(Vector2 a, Vector2 b, float w)> strokes, Vector2 c, float r, float w)
        {
            const int seg = 20;
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg;
                strokes.Add((c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, w));
            }
        }

        /// <summary>White "NORDisch" lettering without the badge (Resources/Branding/wordmark.png), with a soft
        /// navy shadow for contrast on the sky. Falls back to a text label.</summary>
        public static RectTransform Wordmark(Transform parent, Vector2 anchor, Vector2 pos, float height)
        {
            var sprite = Resources.Load<Sprite>("Branding/wordmark");
            float width = sprite != null ? height * sprite.rect.width / sprite.rect.height : height * 5f;
            var rt = Rect("Wordmark", parent, anchor, pos, new Vector2(width, height));
            if (sprite != null)
            {
                var img = rt.gameObject.AddComponent<Image>();
                img.sprite = sprite;
                img.raycastTarget = false;
                var sh = rt.gameObject.AddComponent<Shadow>();
                sh.effectColor = new Color(Navy.r, Navy.g, Navy.b, 0.55f);
                sh.effectDistance = new Vector2(0, -height * 0.05f);
            }
            else
            {
                var t = Label(rt, "<b>NORD</b>isch", Mathf.RoundToInt(height * 0.9f), Color.white, TextAnchor.MiddleCenter);
                Stretch(t.rectTransform);
                t.gameObject.AddComponent<Shadow>().effectColor = new Color(Navy.r, Navy.g, Navy.b, 0.55f);
            }
            return rt;
        }

        static Sprite rounded, shadow, circle;

        /// <summary>9-sliced rounded rectangle (radius 32 px).</summary>
        public static Sprite Rounded => rounded != null ? rounded : rounded = MakeRounded(128, 32, 0f);
        /// <summary>Blurred rounded rectangle for drop shadows.</summary>
        public static Sprite Shadow => shadow != null ? shadow : shadow = MakeRounded(160, 48, 20f);
        public static Sprite Circle => circle != null ? circle : circle = MakeRounded(128, 64, 0f, sliced: false);

        static Sprite MakeRounded(int size, float radius, float blur, bool sliced = true)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[size * size];
            float inset = blur;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Signed distance to a rounded box.
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f) - new Vector2(size, size) * 0.5f;
                Vector2 half = new Vector2(size, size) * 0.5f - Vector2.one * (radius + inset);
                Vector2 q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - half;
                float d = new Vector2(Mathf.Max(q.x, 0), Mathf.Max(q.y, 0)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0) - radius;
                float a = blur > 0 ? Mathf.Clamp01(0.5f - d / blur) : Mathf.Clamp01(0.5f - d);
                if (blur > 0) a = a * a * 0.55f;
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            float b = radius + inset + 1;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, sliced ? new Vector4(b, b, b, b) : Vector4.zero);
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rt.SetParent(parent, false);
            return rt;
        }

        public static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var rt = Rect(name, parent);
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            return rt;
        }

        public static void Stretch(RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.one * inset;
            rt.offsetMax = -Vector2.one * inset;
        }

        public static Image Panel(GameObject go, Color color, Sprite sprite = null)
        {
            var img = go.AddComponent<Image>();
            img.sprite = sprite != null ? sprite : Rounded;
            img.type = img.sprite == Circle ? Image.Type.Simple : Image.Type.Sliced;
            img.color = color;
            return img;
        }

        /// <summary>Soft drop shadow placed behind <paramref name="target"/>.</summary>
        public static void DropShadow(RectTransform target, float spread = 22f, float offsetY = -10f, float alpha = 0.35f)
        {
            var sh = Rect("Shadow", target.parent);
            sh.SetSiblingIndex(target.GetSiblingIndex());
            sh.anchorMin = target.anchorMin;
            sh.anchorMax = target.anchorMax;
            sh.pivot = target.pivot;
            sh.anchoredPosition = target.anchoredPosition + new Vector2(0, offsetY);
            sh.sizeDelta = target.sizeDelta + Vector2.one * spread * 2f;
            var img = sh.gameObject.AddComponent<Image>();
            img.sprite = Shadow;
            img.type = Image.Type.Sliced;
            img.color = new Color(0.05f, 0.1f, 0.2f, alpha);
            img.raycastTarget = false;
        }

        public static Text Label(Transform parent, string text, int size, Color color, TextAnchor align = TextAnchor.MiddleLeft,
            FontStyle style = FontStyle.Normal)
        {
            var rt = Rect("Label", parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Hud.UiFont;
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.alignment = align;
            t.raycastTarget = false;
            t.supportRichText = true;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        public static Text Place(this Text t, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return t;
        }
    }
}
