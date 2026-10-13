using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.World
{
    /// <summary>
    /// Street sign lettering (numbers, street names, brand names) as TextMesh objects, built at runtime from
    /// <see cref="AreaData.signTexts"/>. Layer 8 (details, culled near), shader LeGamez/SignText (depth-tested, front only).
    /// </summary>
    public static class SignTexts
    {
        public const int FontSize = 48;

        [System.Serializable]
        public struct Item
        {
            public string text;
            public Vector3 position;   // world space, terrain height already applied
            public Vector2 facing;     // direction the readable side faces (x/z)
            public float height;       // letter height in metres
            public Color32 color;
        }

        public static GameObject Build(Transform parent, List<Item> items, Material material, int layer = 8)
        {
            if (items == null || items.Count == 0 || material == null) return null;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var root = new GameObject("SignTexts");
            root.transform.SetParent(parent, false);
            foreach (var t in items)
            {
                var go = new GameObject(t.text) { layer = layer };
                go.transform.SetParent(root.transform, false);
                go.transform.position = t.position;
                // TextMesh reads correctly when looked at along its +Z: point it away from the viewer.
                go.transform.rotation = Quaternion.LookRotation(-new Vector3(t.facing.x, 0f, t.facing.y), Vector3.up);
                var tm = go.AddComponent<TextMesh>();
                tm.font = font;
                tm.fontSize = FontSize;
                tm.fontStyle = FontStyle.Bold;
                tm.characterSize = t.height / (FontSize * 0.1f);
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.color = t.color;
                tm.text = t.text;
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = material;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            // After the texts exist: requests their glyphs and keeps the material on the font atlas.
            var keeper = root.AddComponent<SignTextFont>();
            keeper.font = font;
            keeper.material = material;
            keeper.Init();
            return root;
        }
    }
}
