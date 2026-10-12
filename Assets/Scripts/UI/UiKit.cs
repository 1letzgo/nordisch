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

        static Sprite logoBadge;

        /// <summary>The mustard "NORDisch" badge cut out of Resources/Branding/logo.png (1024² app icon).</summary>
        public static Sprite LogoBadge
        {
            get
            {
                if (logoBadge != null) return logoBadge;
                var tex = Resources.Load<Texture2D>("Branding/logo");
                if (tex == null) return null;
                // Badge occupies x 112..912, y 362..662 of the 1024 px icon (symmetric).
                float s = tex.width / 1024f;
                logoBadge = Sprite.Create(tex, new UnityEngine.Rect(112 * s, 362 * s, 800 * s, 300 * s), new Vector2(0.5f, 0.5f), 100f);
                return logoBadge;
            }
        }

        /// <summary>Logo badge; falls back to a text recreation if the image is missing.</summary>
        public static RectTransform Logo(Transform parent, Vector2 anchor, Vector2 pos, float height)
        {
            float width = height * 800f / 300f;
            var badge = Rect("Logo", parent, anchor, pos, new Vector2(width, height));
            DropShadow(badge, height * 0.12f, -height * 0.05f, 0.35f);
            var sprite = LogoBadge;
            if (sprite != null)
            {
                // Mask with our rounded sprite so the icon's navy corners don't show.
                var img = Panel(badge.gameObject, Mustard);
                badge.gameObject.AddComponent<Mask>().showMaskGraphic = true;
                var pic = Rect("Image", badge);
                Stretch(pic, -2f);
                var picImg = pic.gameObject.AddComponent<Image>();
                picImg.sprite = sprite;
                picImg.raycastTarget = false;
                img.raycastTarget = false;
            }
            else
            {
                Panel(badge.gameObject, Mustard);
                var t = Label(badge, "<b>NORD</b>isch", Mathf.RoundToInt(height * 0.56f), Color.white, TextAnchor.MiddleCenter);
                Stretch(t.rectTransform);
            }
            return badge;
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
