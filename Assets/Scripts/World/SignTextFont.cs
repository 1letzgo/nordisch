using UnityEngine;

namespace LeGamez.World
{
    /// <summary>
    /// Keeps the sign text material pointed at the built-in font's texture: the dynamic font atlas is created
    /// at runtime and rebuilt when new characters are requested.
    /// </summary>
    public class SignTextFont : MonoBehaviour
    {
        public Font font;
        public Material material;

        void Awake()
        {
            if (font == null || material == null) return;
            Font.textureRebuilt += OnRebuilt;
            // Request every glyph the signs use up front, so the atlas is stable before the first frame.
            var all = new System.Text.StringBuilder();
            foreach (var tm in GetComponentsInChildren<TextMesh>(true)) all.Append(tm.text);
            font.RequestCharactersInTexture(all.ToString(), 48, FontStyle.Bold);
            material.mainTexture = font.material.mainTexture;
        }

        void OnDestroy() => Font.textureRebuilt -= OnRebuilt;

        void OnRebuilt(Font f)
        {
            if (f == font && material != null) material.mainTexture = f.material.mainTexture;
        }
    }
}
