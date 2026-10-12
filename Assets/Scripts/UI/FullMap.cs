using System.Collections;
using System.Collections.Generic;
using LeGamez.Core;
using LeGamez.Player;
using LeGamez.Quests;
using LeGamez.World;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>
    /// Full-screen north-up area map (opened by tapping the minimap). Tap a spot or a POI to place a
    /// target pin, then "Hierhin beamen" teleports the player to the nearest free ground there.
    /// </summary>
    public class FullMap : MonoBehaviour, IPointerClickHandler
    {
        RectTransform mapRect, pin, playerMarker;
        Button beamButton;
        Text hint;
        CanvasGroup group;
        Vector2 mapCenter;     // world (x,z) at the map image centre
        Vector2 mapHalf;       // metres from centre to map edge (x, z)
        Vector3? target;
        PoiEntry targetPoi;
        AreaInfo area;
        PlayerController player;
        readonly List<(PoiEntry poi, Image dot)> dots = new List<(PoiEntry, Image)>();

        static FullMap instance;

        public static void Open(Transform canvasRoot)
        {
            if (instance == null) instance = Build(canvasRoot);
            instance.Show();
        }

        static FullMap Build(Transform canvasRoot)
        {
            var root = UiKit.Rect("FullMap", canvasRoot);
            UiKit.Stretch(root);
            var backdrop = root.gameObject.AddComponent<Image>();
            backdrop.color = new Color(UiKit.Navy.r * 0.6f, UiKit.Navy.g * 0.6f, UiKit.Navy.b * 0.6f, 0.92f);
            var fm = root.gameObject.AddComponent<FullMap>();
            fm.group = root.gameObject.AddComponent<CanvasGroup>();
            fm.area = AreaInfo.Current;
            fm.mapCenter = fm.area != null ? fm.area.BoundsCenter : Vector2.zero;
            fm.mapHalf = (fm.area != null ? fm.area.MapSize : new Vector2(1120f, 1120f)) * 0.5f;

            // Map fitted into a 940-unit box, keeping the area's aspect ratio.
            float size = 940f;
            float aspect = fm.mapHalf.x / Mathf.Max(1f, fm.mapHalf.y);
            var mapPx = aspect >= 1f ? new Vector2(size, size / aspect) : new Vector2(size * aspect, size);
            var frame = UiKit.Rect("Frame", root, new Vector2(0.5f, 0.5f), new Vector2(0, -10), mapPx + Vector2.one * 16f);
            UiKit.DropShadow(frame, 24f, -8f, 0.45f);
            UiKit.Panel(frame.gameObject, Color.white);
            var mask = UiKit.Rect("Mask", frame, new Vector2(0.5f, 0.5f), Vector2.zero, mapPx);
            UiKit.Panel(mask.gameObject, Color.white);
            mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            fm.mapRect = UiKit.Rect("Map", mask, new Vector2(0.5f, 0.5f), Vector2.zero, mapPx);
            fm.mapPixels = mapPx;
            var raw = fm.mapRect.gameObject.AddComponent<RawImage>();
            raw.texture = fm.area != null ? Resources.Load<Texture2D>("AreaMaps/" + fm.area.areaId) : null;
            raw.raycastTarget = true; // taps land here (handled by FullMap via bubbling)

            // POIs with names.
            if (fm.area != null && fm.area.pois != null)
            {
                foreach (var poi in fm.area.pois.entries)
                {
                    var d = UiKit.Rect("Poi", fm.mapRect, new Vector2(0.5f, 0.5f), fm.WorldToMap(poi.position), new Vector2(26, 26));
                    var img = UiKit.Panel(d.gameObject, Color.white, UiKit.Circle);
                    img.raycastTarget = false;
                    d.gameObject.AddComponent<Outline>().effectColor = UiKit.Navy;
                    fm.dots.Add((poi, img));
                }
            }

            fm.pin = UiKit.Rect("Pin", fm.mapRect, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(56, 56));
            var pinImg = fm.pin.gameObject.AddComponent<Image>();
            pinImg.sprite = MiniMap.ArrowSprite();
            pinImg.color = UiKit.Mustard;
            pinImg.raycastTarget = false;
            fm.pin.localRotation = Quaternion.Euler(0, 0, 180); // arrow points down onto the spot
            fm.pin.pivot = new Vector2(0.5f, 1f); // tip of the (rotated) arrow sits on the spot
            fm.pin.gameObject.AddComponent<Outline>().effectColor = UiKit.Navy;
            fm.pin.gameObject.SetActive(false);

            fm.playerMarker = UiKit.Rect("Player", fm.mapRect, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(44, 44));
            var pm = fm.playerMarker.gameObject.AddComponent<Image>();
            pm.sprite = MiniMap.ArrowSprite();
            pm.color = UiKit.Navy;
            pm.raycastTarget = false;
            fm.playerMarker.gameObject.AddComponent<Outline>().effectColor = Color.white;

            // Header + hint.
            UiKit.Label(root, fm.area != null ? fm.area.displayName : "Karte", 46, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold)
                .Place(new Vector2(0.5f, 0.5f), new Vector2(0, mapPx.y / 2 + 80), new Vector2(900, 60));
            fm.hint = UiKit.Label(root, "", 30, new Color(1, 1, 1, 0.85f), TextAnchor.MiddleCenter)
                .Place(new Vector2(0.5f, 0.5f), new Vector2(0, mapPx.y / 2 + 34), new Vector2(940, 44));

            // Close.
            var close = UiKit.Rect("Close", root, new Vector2(0.5f, 0.5f), new Vector2(mapPx.x / 2 - 30, mapPx.y / 2 + 80), new Vector2(84, 84));
            var closeImg = UiKit.Panel(close.gameObject, new Color(1, 1, 1, 0.95f), UiKit.Circle);
            var closeBtn = close.gameObject.AddComponent<Button>();
            closeBtn.targetGraphic = closeImg;
            closeBtn.onClick.AddListener(fm.Hide);
            UiKit.Stretch(UiKit.Label(close, "×", 54, UiKit.Navy, TextAnchor.MiddleCenter, FontStyle.Bold).rectTransform);

            // Beam button.
            var beam = UiKit.Rect("Beam", root, new Vector2(0.5f, 0.5f), new Vector2(0, -(mapPx.y / 2 + 90)), new Vector2(520, 104));
            UiKit.DropShadow(beam, 16f, -5f, 0.35f);
            var beamImg = UiKit.Panel(beam.gameObject, UiKit.Mustard);
            fm.beamButton = beam.gameObject.AddComponent<Button>();
            fm.beamButton.targetGraphic = beamImg;
            fm.beamButton.onClick.AddListener(fm.Beam);
            UiKit.Stretch(UiKit.Label(beam, "Hierhin beamen", 40, UiKit.Navy, TextAnchor.MiddleCenter, FontStyle.Bold).rectTransform);

            root.gameObject.SetActive(false);
            return fm;
        }

        Vector2 mapPixels;

        Vector2 WorldToMap(Vector3 w) =>
            Vector2.Scale((new Vector2(w.x, w.z) - mapCenter), new Vector2(mapPixels.x * 0.5f / mapHalf.x, mapPixels.y * 0.5f / mapHalf.y));

        Vector3 MapToWorld(Vector2 local)
        {
            var m = mapCenter + Vector2.Scale(local, new Vector2(mapHalf.x / (mapPixels.x * 0.5f), mapHalf.y / (mapPixels.y * 0.5f)));
            return new Vector3(m.x, 0f, m.y);
        }

        void Show()
        {
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
            player = NavState.Player != null ? NavState.Player.GetComponent<PlayerController>() : null;
            if (player != null) player.InputEnabled = false;
            target = null;
            pin.gameObject.SetActive(false);
            beamButton.interactable = false;
            hint.text = "Tippe auf einen Ort oder ein Ziel";
            foreach (var (poi, dot) in dots)
                dot.color = area != null && StampBook.Has(area.areaId, poi.id) ? UiKit.Done : NavState.Target == poi ? UiKit.Mustard : Color.white;
        }

        void Hide()
        {
            if (player != null) player.InputEnabled = true;
            gameObject.SetActive(false);
        }

        void LateUpdate()
        {
            if (NavState.Player == null) return;
            playerMarker.anchoredPosition = WorldToMap(NavState.Player.position);
            playerMarker.localRotation = Quaternion.Euler(0, 0, -NavState.Player.eulerAngles.y);
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (!RectTransformUtility.RectangleContainsScreenPoint(mapRect, e.position, e.pressEventCamera)) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(mapRect, e.position, e.pressEventCamera, out var local);

            // Snap to a POI if the tap is close to one.
            PoiEntry snapped = null;
            float best = 40f;
            foreach (var (poi, _) in dots)
            {
                float d = Vector2.Distance(WorldToMap(poi.position), local);
                if (d < best) { best = d; snapped = poi; }
            }
            Vector3 world = snapped != null ? snapped.position : MapToWorld(local);
            if (area != null) world = area.Clamp(world, 5f);
            target = world;
            targetPoi = snapped;
            pin.gameObject.SetActive(true);
            pin.anchoredPosition = WorldToMap(world);
            beamButton.interactable = true;
            hint.text = snapped != null ? $"Ziel: {snapped.name}" : "Ort gewählt";
        }

        void Beam()
        {
            if (target == null || player == null) return;
            Vector3 wanted = target.Value;
            if (targetPoi != null)
            {
                // Land a few metres in front of the landmark (inside the stamp radius), not in its marker.
                Vector3 away = player.transform.position - targetPoi.position;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = Vector3.back;
                wanted = targetPoi.position + away.normalized * 7f;
            }
            var spot = FindFreeGround(wanted);
            Vector3 face = targetPoi != null ? targetPoi.position - spot : Vector3.zero;
            face.y = 0f;
            if (face.sqrMagnitude > 0.01f) player.transform.rotation = Quaternion.LookRotation(face);
            var p = player;
            var flash = BeamFlash.Create(transform.parent);
            Hide();
            // Run on the flash object – this panel is now inactive and can't host coroutines.
            flash.StartCoroutine(BeamRoutine(flash, p, spot));
        }

        static IEnumerator BeamRoutine(BeamFlash flash, PlayerController player, Vector3 spot)
        {
            player.InputEnabled = false;
            yield return flash.Fade(0f, 1f, 0.18f);
            player.Teleport(spot + Vector3.up * 0.2f);
            var cam = NavState.View != null ? NavState.View.GetComponent<ThirdPersonCamera>() : null;
            if (cam != null) cam.SnapToTarget();
            yield return null;
            yield return flash.Fade(1f, 0f, 0.35f);
            Destroy(flash.gameObject);
            player.InputEnabled = true;
        }

        /// <summary>Terrain surface near the wanted point with room for the character (see <see cref="Placement"/>).</summary>
        Vector3 FindFreeGround(Vector3 wanted)
        {
            Placement.TryFindFreeGround(wanted, player != null ? player.transform : null, out var spot);
            return spot;
        }
    }

    /// <summary>Full-screen white flash used for the beam transition.</summary>
    public class BeamFlash : MonoBehaviour
    {
        Image img;

        public static BeamFlash Create(Transform canvasRoot)
        {
            var rt = UiKit.Rect("BeamFlash", canvasRoot);
            UiKit.Stretch(rt);
            rt.SetAsLastSibling();
            var f = rt.gameObject.AddComponent<BeamFlash>();
            f.img = rt.gameObject.AddComponent<Image>();
            f.img.color = new Color(1f, 0.97f, 0.88f, 0f);
            return f;
        }

        public IEnumerator Fade(float from, float to, float seconds)
        {
            for (float t = 0; t < seconds; t += Time.unscaledDeltaTime)
            {
                var c = img.color;
                c.a = Mathf.Lerp(from, to, t / seconds);
                img.color = c;
                yield return null;
            }
            var end = img.color;
            end.a = to;
            img.color = end;
        }
    }
}
