using System.Collections.Generic;
using LeGamez.Quests;
using LeGamez.World;
using UnityEngine;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>Shared navigation state: the player and the view camera.</summary>
    public static class NavState
    {
        public static Transform Player;
        public static Transform View;

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
    /// Round heading-up minimap: pre-rendered top-down images under a circular mask – the sharp 1 km region images
    /// (<see cref="RegionMaps"/>) around the player over the small overview of the whole area (shown where a region
    /// isn't loaded yet) – landmark dots, player arrow in the centre, "N" on the rim.
    /// </summary>
    public class MiniMap : MonoBehaviour
    {
        public float metersVisible = 130f;   // radius shown, in metres

        RectTransform rotator, map, regionLayer, playerArrow, north;
        readonly Dictionary<Vector2Int, RawImage> regionImages = new Dictionary<Vector2Int, RawImage>();
        readonly HashSet<Vector2Int> wantedRegions = new HashSet<Vector2Int>();
        float nextRegionCheck;
        float radius;
        float pixelsPerMeter;
        Vector2 mapCenter;
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
            // Tap the minimap to open the big map (overlay on the canvas root, outside the safe area).
            var canvasRoot = parent.GetComponentInParent<Canvas>().transform;
            root.gameObject.AddComponent<TapButton>().onPress = () => FullMap.Open(canvasRoot);
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
            var tex = area != null ? area.mapTexture : null;
            mapCenter = area != null ? area.BoundsCenter : Vector2.zero;
            Vector2 mapSize = area != null ? area.MapSize : new Vector2(1120f, 1120f);
            map = UiKit.Rect("Map", rotator, new Vector2(0.5f, 0.5f), Vector2.zero, mapSize * pixelsPerMeter);
            var raw = map.gameObject.AddComponent<RawImage>();
            raw.texture = tex;
            raw.color = tex != null ? Color.white : new Color(0.6f, 0.75f, 0.55f);
            raw.raycastTarget = false;
            regionLayer = UiKit.Rect("Regions", map, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero); // under the dots

            if (area != null && area.pois != null)
            {
                foreach (var poi in area.pois.entries)
                {
                    var d = UiKit.Rect("Poi", map, new Vector2(0.5f, 0.5f), (new Vector2(poi.position.x, poi.position.z) - mapCenter) * pixelsPerMeter, new Vector2(18, 18));
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
            map.anchoredPosition = -(new Vector2(p.x, p.z) - mapCenter) * pixelsPerMeter;
            playerArrow.localRotation = Quaternion.Euler(0, 0, viewYaw - NavState.Player.eulerAngles.y);

            foreach (var (_, dot) in dots)
                dot.rectTransform.localRotation = Quaternion.Euler(0, 0, -viewYaw);

            // North marker sits on the rim at the screen-angle of world north.
            north.anchoredPosition = RimPoint(-viewYaw, radius + 2f);

            if (Time.unscaledTime >= nextRegionCheck)
            {
                nextRegionCheck = Time.unscaledTime + 0.25f;
                UpdateRegions(p);
            }
        }

        /// <summary>Sharp images of the regions within reach of the visible circle (decoded a little ahead).</summary>
        void UpdateRegions(Vector3 p)
        {
            float reach = metersVisible + 120f, s = WorldStreamer.RegionSize;
            wantedRegions.Clear();
            for (int x = Mathf.FloorToInt((p.x - reach) / s); x <= Mathf.FloorToInt((p.x + reach) / s); x++)
            for (int z = Mathf.FloorToInt((p.z - reach) / s); z <= Mathf.FloorToInt((p.z + reach) / s); z++)
            {
                float dx = Mathf.Max(x * s - p.x, 0f, p.x - (x + 1) * s), dz = Mathf.Max(z * s - p.z, 0f, p.z - (z + 1) * s);
                if (dx * dx + dz * dz <= reach * reach) wantedRegions.Add(new Vector2Int(x, z));
            }
            RegionMaps.Show(regionLayer, regionImages, wantedRegions, (r, rt) =>
            {
                var b = RegionContent.Bounds(r);
                rt.anchoredPosition = (b.center - mapCenter) * pixelsPerMeter;
                rt.sizeDelta = b.size * pixelsPerMeter;
            });
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
