using UnityEngine;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>Tiny code-only UI toolkit: rounded panels, soft shadows, labels, palette.</summary>
    public static class UiKit
    {
        // Palette: north-sea blues, warm brick accent, off-white cards.
        public static readonly Color Ink = new Color(0.11f, 0.16f, 0.24f);
        public static readonly Color InkSoft = new Color(0.36f, 0.42f, 0.50f);
        public static readonly Color Card = new Color(1f, 0.99f, 0.97f, 0.96f);
        public static readonly Color Accent = new Color(0.86f, 0.38f, 0.24f);
        public static readonly Color AccentSoft = new Color(0.98f, 0.88f, 0.82f);
        public static readonly Color Sea = new Color(0.20f, 0.47f, 0.68f);
        public static readonly Color Island = new Color(0.90f, 0.78f, 0.45f);
        public static readonly Color City = new Color(0.45f, 0.62f, 0.40f);
        public static readonly Color Track = new Color(0.88f, 0.89f, 0.91f);

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
