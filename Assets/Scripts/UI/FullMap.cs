using System.Collections;
using System.Collections.Generic;
using LeGamez.Core;
using LeGamez.Player;
using LeGamez.Quests;
using LeGamez.World;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>
    /// Full-screen north-up area map (opened by tapping the minimap). Tap a spot or a POI to place a
    /// target pin, then "Hierhin beamen" teleports the player to the nearest free ground there.
    /// Zoom: two-finger pinch, mouse wheel or the +/− buttons; drag pans the zoomed map. The map image is a small overview
    /// of the whole area; zoomed in, the sharp region images (<see cref="RegionMaps"/>) of loaded regions lie on top.
    /// </summary>
    public class FullMap : MonoBehaviour, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        /// <summary>6× for town maps; whole municipalities (20 km) zoom further, down to ~1.3 km across.</summary>
        float MaxZoom => area != null ? Mathf.Clamp(Mathf.Max(area.MapSize.x, area.MapSize.y) / 1300f, 6f, 16f) : 6f;
        float zoom = 1f;
        bool gesture;          // pinch or drag in progress / just ended: the release is not a tap
        float pinchStartDist, pinchStartZoom;
        Vector2 pinchLastMid;
        Canvas canvas;

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
        RectTransform regionLayer;
        readonly Dictionary<Vector2Int, RawImage> regionImages = new Dictionary<Vector2Int, RawImage>();
        readonly HashSet<Vector2Int> wantedRegions = new HashSet<Vector2Int>();
        float nextRegionCheck;
        /// <summary>Region images only when the overview gets blurry (more than ~2.5 m per screen unit).</summary>
        const float RegionZoomMetersPerUnit = 2.5f;

        static FullMap instance;

        /// <summary>Closes the map if it is open (Android back button); false when there was nothing to close.</summary>
        public static bool CloseIfOpen()
        {
            if (instance == null || !instance.gameObject.activeSelf) return false;
            instance.Hide();
            return true;
        }

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
            raw.texture = fm.area != null ? fm.area.mapTexture : null;
            raw.raycastTarget = true; // taps land here (handled by FullMap via bubbling)
            fm.regionLayer = UiKit.Rect("Regions", fm.mapRect, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero); // under the markers

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

            // Other areas ("Andere Orte"): beam into another area (downloads it first if needed).
            var places = UiKit.Rect("Places", root, new Vector2(0.5f, 0.5f), new Vector2(-mapPx.x / 2 + 150, mapPx.y / 2 + 180), new Vector2(300, 84)); // above title + hint
            UiKit.DropShadow(places, 12f, -4f, 0.3f);
            var placesImg = UiKit.Panel(places.gameObject, new Color(1, 1, 1, 0.95f));
            var placesBtn = places.gameObject.AddComponent<Button>();
            placesBtn.targetGraphic = placesImg;
            placesBtn.onClick.AddListener(() => { fm.Hide(); PlaceSwitcher.Open(canvasRoot); });
            UiKit.Stretch(UiKit.Label(places, "Andere Orte", 34, UiKit.Navy, TextAnchor.MiddleCenter, FontStyle.Bold).rectTransform);

            // Zoom buttons at the right edge of the map.
            for (int i = 0; i < 2; i++)
            {
                bool plus = i == 0;
                var zb = UiKit.Rect(plus ? "ZoomIn" : "ZoomOut", root, new Vector2(0.5f, 0.5f),
                    new Vector2(mapPx.x / 2 - 56, -mapPx.y / 2 + (plus ? 150 : 56)), new Vector2(80, 80));
                UiKit.DropShadow(zb, 10f, -3f, 0.3f);
                var zi = UiKit.Panel(zb.gameObject, new Color(1, 1, 1, 0.95f), UiKit.Circle);
                var zbtn = zb.gameObject.AddComponent<Button>();
                zbtn.targetGraphic = zi;
                zbtn.onClick.AddListener(() => fm.ZoomAroundCentre(plus ? 1.6f : 1f / 1.6f));
                UiKit.Stretch(UiKit.Label(zb, plus ? "+" : "−", 52, UiKit.Navy, TextAnchor.MiddleCenter, FontStyle.Bold).rectTransform);
            }

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
            hint.text = "Tippe auf einen Ort oder ein Ziel · zwei Finger zoomen";
            if (canvas == null) canvas = GetComponentInParent<Canvas>();
            SetZoom(1f, Vector2.zero);
            UpdateRegions();
        }

        void Hide()
        {
            if (player != null) player.InputEnabled = true;
            gameObject.SetActive(false);
        }

        Camera UiCamera => canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;

        void Update()
        {
            // Pinch zoom (two fingers) – zooms about the midpoint and pans with it.
            var ts = Touchscreen.current;
            int down = 0;
            Vector2 a = default, b = default;
            if (ts != null)
                foreach (var t in ts.touches)
                {
                    if (!t.press.isPressed) continue;
                    if (down == 0) a = t.position.ReadValue(); else if (down == 1) b = t.position.ReadValue();
                    down++;
                }
            if (down >= 2)
            {
                float dist = Vector2.Distance(a, b);
                var mid = (a + b) * 0.5f;
                if (pinchStartDist <= 0f) { pinchStartDist = Mathf.Max(1f, dist); pinchStartZoom = zoom; pinchLastMid = mid; }
                gesture = true;
                ZoomAt(mid, pinchStartZoom * dist / pinchStartDist);
                PanBy(mid - pinchLastMid);
                pinchLastMid = mid;
            }
            else
            {
                pinchStartDist = 0f;
                if (down == 0 && !(Mouse.current != null && Mouse.current.leftButton.isPressed)) gesture = false;
            }
            // Mouse wheel (Mac / editor).
            var mouse = Mouse.current;
            if (mouse != null)
            {
                float wheel = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(wheel) > 0.01f) ZoomAt(mouse.position.ReadValue(), zoom * Mathf.Pow(1.0015f, wheel));
            }
        }

        public void OnBeginDrag(PointerEventData e) => gesture = true;

        public void OnDrag(PointerEventData e)
        {
            if (pinchStartDist > 0f) return; // the pinch handles panning
            PanBy(e.delta);
        }

        public void OnEndDrag(PointerEventData e) { }

        /// <summary>Screen-pixel delta → map offset.</summary>
        void PanBy(Vector2 screenDelta)
        {
            float k = canvas != null ? canvas.scaleFactor : 1f;
            SetZoom(zoom, mapRect.anchoredPosition + screenDelta / Mathf.Max(0.01f, k));
        }

        void ZoomAroundCentre(float factor)
        {
            var centre = RectTransformUtility.WorldToScreenPoint(UiCamera, mapRect.parent.position);
            ZoomAt(centre, zoom * factor);
        }

        /// <summary>Zooms so the map point under <paramref name="screenPoint"/> stays put.</summary>
        void ZoomAt(Vector2 screenPoint, float newZoom)
        {
            newZoom = Mathf.Clamp(newZoom, 1f, MaxZoom);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(mapRect, screenPoint, UiCamera, out var local)) return;
            var offset = mapRect.anchoredPosition + local * (zoom - newZoom);
            SetZoom(newZoom, offset);
        }

        void SetZoom(float z, Vector2 offset)
        {
            zoom = Mathf.Clamp(z, 1f, MaxZoom);
            // Keep the map covering its frame.
            var limit = mapPixels * (zoom - 1f) * 0.5f;
            offset.x = Mathf.Clamp(offset.x, -limit.x, limit.x);
            offset.y = Mathf.Clamp(offset.y, -limit.y, limit.y);
            mapRect.localScale = Vector3.one * zoom;
            mapRect.anchoredPosition = offset;
            // Markers keep their screen size.
            var inv = Vector3.one / zoom;
            foreach (var (_, dot) in dots) dot.rectTransform.localScale = inv;
            pin.localScale = inv;
            playerMarker.localScale = inv;
        }

        /// <summary>Region images of loaded regions inside the visible part of the map, once zoomed in far enough.</summary>
        void UpdateRegions()
        {
            wantedRegions.Clear();
            var streamer = WorldStreamer.Instance;
            float metersPerUnit = mapHalf.x * 2f / Mathf.Max(1f, mapPixels.x * zoom);
            if (streamer != null && metersPerUnit < RegionZoomMetersPerUnit)
            {
                // Visible map-local rectangle → world rectangle.
                var half = mapPixels * 0.5f / zoom;
                var c = -mapRect.anchoredPosition / zoom;
                var a = MapToWorld(c - half);
                var b = MapToWorld(c + half);
                foreach (var r in streamer.Regions.Keys)
                {
                    var rb = RegionContent.Bounds(r);
                    if (rb.xMax > a.x && rb.xMin < b.x && rb.yMax > a.z && rb.yMin < b.z) wantedRegions.Add(r);
                }
            }
            var scale = new Vector2(mapPixels.x * 0.5f / mapHalf.x, mapPixels.y * 0.5f / mapHalf.y);
            RegionMaps.Show(regionLayer, regionImages, wantedRegions, (r, rt) =>
            {
                var rb = RegionContent.Bounds(r);
                rt.anchoredPosition = WorldToMap(new Vector3(rb.center.x, 0f, rb.center.y));
                rt.sizeDelta = Vector2.Scale(rb.size, scale);
            });
        }

        void LateUpdate()
        {
            if (Time.unscaledTime >= nextRegionCheck)
            {
                nextRegionCheck = Time.unscaledTime + 0.2f;
                UpdateRegions();
            }
            if (NavState.Player == null) return;
            playerMarker.anchoredPosition = WorldToMap(NavState.Player.position);
            playerMarker.localRotation = Quaternion.Euler(0, 0, -NavState.Player.eulerAngles.y);
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (gesture || e.dragging) return;
            if (!RectTransformUtility.RectangleContainsScreenPoint(mapRect, e.position, e.pressEventCamera)) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(mapRect, e.position, e.pressEventCamera, out var local);

            // Snap to a POI if the tap is close to one.
            PoiEntry snapped = null;
            float best = 40f / zoom;
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
                // Land a few metres in front of the landmark, facing it.
                Vector3 away = player.transform.position - targetPoi.position;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = Vector3.back;
                wanted = targetPoi.position + away.normalized * 7f;
            }
            var p = player;
            var poi = targetPoi;
            var flash = BeamFlash.Create(transform.parent);
            Hide();
            // Run on the flash object – this panel is now inactive and can't host coroutines.
            flash.StartCoroutine(BeamRoutine(flash, p, wanted, poi));
        }

        static IEnumerator BeamRoutine(BeamFlash flash, PlayerController player, Vector3 wanted, PoiEntry poi)
        {
            player.InputEnabled = false;
            yield return flash.Fade(0f, 1f, 0.18f);
            // The target may be far outside the streamed surroundings: load it (behind the flash) before placing.
            yield return WorldStreamer.WaitFor(wanted, 220f);
            Placement.TryFindFreeGround(wanted, player.transform, out var spot);
            Vector3 face = poi != null ? poi.position - spot : Vector3.zero;
            face.y = 0f;
            if (face.sqrMagnitude > 0.01f) player.transform.rotation = Quaternion.LookRotation(face);
            player.Teleport(spot + Vector3.up * 0.2f);
            var cam = NavState.View != null ? NavState.View.GetComponent<ThirdPersonCamera>() : null;
            if (cam != null) cam.SnapToTarget();
            yield return null;
            yield return flash.Fade(1f, 0f, 0.35f);
            Destroy(flash.gameObject);
            player.InputEnabled = true;
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
