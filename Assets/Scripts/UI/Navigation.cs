using System.Collections.Generic;
using LeGamez.Quests;
using LeGamez.World;
using UnityEngine;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>Shared navigation state: the player, the view camera and the current target POI.</summary>
    public static class NavState
    {
        public static Transform Player;
        public static Transform View;
        public static PoiEntry Target;

        /// <summary>Camera heading in degrees, clockwise from north (+Z).</summary>
        public static float ViewYaw => View != null ? View.eulerAngles.y : 0f;

        public static float BearingTo(Vector3 world)
        {
            if (Player == null) return 0f;
            Vector3 d = world - Player.position;
            return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        public static float DistanceTo(Vector3 world)
        {
            if (Player == null) return 0f;
            Vector3 d = world - Player.position;
            d.y = 0f;
            return d.magnitude;
        }
    }

    /// <summary>
    /// Round heading-up minimap: pre-rendered top-down area image under a circular mask,
    /// POI dots (target pulses), player arrow in the centre, "N" on the rim.
    /// </summary>
    public class MiniMap : MonoBehaviour
    {
        public float metersVisible = 130f;   // radius shown, in metres

        RectTransform rotator, map, playerArrow, north, targetEdge;
        Text targetLabel;
        float radius;
        float pixelsPerMeter;
        readonly List<(PoiEntry poi, Image dot)> dots = new List<(PoiEntry, Image)>();
        AreaInfo area;

        public static MiniMap Create(Transform parent, float size)
        {
            var root = UiKit.Rect("MiniMap", parent, new Vector2(1, 1), new Vector2(-28, -28), new Vector2(size, size));
            UiKit.DropShadow(root, 16f, -6f, 0.35f);
            // White rim.
            UiKit.Panel(root.gameObject, new Color(1, 1, 1, 0.95f), UiKit.Circle);
            var mm = root.gameObject.AddComponent<MiniMap>();
            mm.Build(root, size);
            return mm;
        }

        void Build(RectTransform root, float size)
        {
            area = AreaInfo.Current;
            radius = size * 0.5f - 8f;
            pixelsPerMeter = radius / metersVisible;

            var mask = UiKit.Rect("Mask", root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(radius * 2, radius * 2));
            UiKit.Panel(mask.gameObject, new Color(0.55f, 0.7f, 0.5f), UiKit.Circle);
            mask.gameObject.AddComponent<Mask>().showMaskGraphic = true;

            rotator = UiKit.Rect("Rotator", mask, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            var tex = area != null ? Resources.Load<Texture2D>("AreaMaps/" + area.areaId) : null;
            float extent = area != null ? area.HalfSize + 60f : 560f;
            map = UiKit.Rect("Map", rotator, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.one * extent * 2f * pixelsPerMeter);
            var raw = map.gameObject.AddComponent<RawImage>();
            raw.texture = tex;
            raw.color = tex != null ? Color.white : new Color(0.6f, 0.75f, 0.55f);
            raw.raycastTarget = false;

            if (area != null && area.pois != null)
            {
                foreach (var poi in area.pois.entries)
                {
                    var d = UiKit.Rect("Poi", map, new Vector2(0.5f, 0.5f), new Vector2(poi.position.x, poi.position.z) * pixelsPerMeter, new Vector2(18, 18));
                    var img = UiKit.Panel(d.gameObject, Color.white, UiKit.Circle);
                    img.raycastTarget = false;
                    dots.Add((poi, img));
                }
            }

            // Player arrow (fixed in the centre, points to the player's facing relative to the view).
            playerArrow = UiKit.Rect("Player", mask, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(34, 34));
            var arrow = playerArrow.gameObject.AddComponent<Image>();
            arrow.sprite = ArrowSprite();
            arrow.color = UiKit.Navy;
            arrow.raycastTarget = false;
            playerArrow.gameObject.AddComponent<Outline>().effectColor = Color.white;

            // Target indicator on the rim when the target is outside the visible radius.
            targetEdge = UiKit.Rect("TargetEdge", root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(26, 26));
            var te = targetEdge.gameObject.AddComponent<Image>();
            te.sprite = ArrowSprite();
            te.color = UiKit.Mustard;
            te.raycastTarget = false;

            // Next target name + distance, as a pill under the map.
            var pill = UiKit.Rect("TargetPill", root, new Vector2(0.5f, 0f), new Vector2(0, -60), new Vector2(size + 20, 50));
            UiKit.Panel(pill.gameObject, new Color(UiKit.Navy.r, UiKit.Navy.g, UiKit.Navy.b, 0.88f));
            targetLabel = UiKit.Label(pill, "", 24, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(targetLabel.rectTransform, 6f);
            targetLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            targetLabel.resizeTextForBestFit = true;
            targetLabel.resizeTextMinSize = 16;
            targetLabel.resizeTextMaxSize = 24;

            north = UiKit.Rect("North", root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(40, 40));
            UiKit.Panel(north.gameObject, UiKit.Mustard, UiKit.Circle);
            UiKit.Stretch(UiKit.Label(north, "N", 24, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold).rectTransform);
        }

        void LateUpdate()
        {
            if (NavState.Player == null) return;
            float viewYaw = NavState.ViewYaw;
            Vector3 p = NavState.Player.position;

            // Heading-up: rotate the world opposite to the camera.
            rotator.localRotation = Quaternion.Euler(0, 0, viewYaw);
            map.anchoredPosition = -new Vector2(p.x, p.z) * pixelsPerMeter;
            playerArrow.localRotation = Quaternion.Euler(0, 0, viewYaw - NavState.Player.eulerAngles.y);

            float pulse = 1f + Mathf.Sin(Time.time * 6f) * 0.25f;
            foreach (var (poi, dot) in dots)
            {
                bool visited = area != null && StampBook.Has(area.areaId, poi.id);
                bool target = NavState.Target == poi;
                dot.color = target ? UiKit.Mustard : visited ? UiKit.Done : Color.white;
                dot.rectTransform.sizeDelta = Vector2.one * (target ? 26f * pulse : 16f);
                dot.rectTransform.localRotation = Quaternion.Euler(0, 0, -viewYaw);
            }

            // North marker sits on the rim at the screen-angle of world north.
            north.anchoredPosition = RimPoint(-viewYaw, radius + 2f);

            var t = NavState.Target;
            targetLabel.transform.parent.gameObject.SetActive(t != null);
            if (t != null) targetLabel.text = $"{t.name} · {Mathf.RoundToInt(NavState.DistanceTo(t.position))} m";

            if (NavState.Target != null && NavState.DistanceTo(NavState.Target.position) > metersVisible * 0.92f)
            {
                float rel = NavState.BearingTo(NavState.Target.position) - viewYaw;
                targetEdge.gameObject.SetActive(true);
                targetEdge.anchoredPosition = RimPoint(rel, radius - 16f);
                targetEdge.localRotation = Quaternion.Euler(0, 0, -rel);
            }
            else targetEdge.gameObject.SetActive(false);
        }

        /// <summary>Point on a circle for a bearing relative to "up" (clockwise degrees).</summary>
        static Vector2 RimPoint(float bearing, float r)
        {
            float rad = bearing * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(rad), Mathf.Cos(rad)) * r;
        }

        static Sprite arrowSprite;

        /// <summary>Upward-pointing chevron arrow, generated once.</summary>
        public static Sprite ArrowSprite()
        {
            if (arrowSprite != null) return arrowSprite;
            const int s = 64;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[s * s];
            Vector2 a = new Vector2(32, 60), b = new Vector2(6, 6), c = new Vector2(32, 18), d = new Vector2(58, 6);
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                bool inside = InTri(p, a, b, c) || InTri(p, a, c, d);
                px[y * s + x] = inside ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            }
            tex.SetPixels32(px);
            tex.Apply();
            return arrowSprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f);
        }

        static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float Cross(Vector2 o, Vector2 u, Vector2 v) => (u.x - o.x) * (v.y - o.y) - (u.y - o.y) * (v.x - o.x);
            float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }
    }
}
