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

        public enum IconKind { Jump, Run }

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
            }
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
