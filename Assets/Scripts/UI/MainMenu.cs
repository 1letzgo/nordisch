using System.Collections.Generic;
using LeGamez.Core;
using LeGamez.Player;
using LeGamez.Quests;
using LeGamez.World;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace LeGamez.UI
{
    /// <summary>
    /// Main menu: 3D character preview (turntable, swipe/arrows to change) and area cards with
    /// rendered thumbnails and the ways to get around. Separate layouts for portrait and landscape.
    /// </summary>
    public class MainMenu : MonoBehaviour
    {
        const float PreviewFov = 30f;

        GameObject canvasGo;
        int builtRevision = -1;
        readonly List<CardUi> cards = new List<CardUi>();
        readonly Dictionary<Texture2D, Sprite> thumbSprites = new Dictionary<Texture2D, Sprite>();

        /// <summary>Live parts of an area card, refreshed every frame from <see cref="MapStore"/>.</summary>
        class CardUi
        {
            public MapStore.Map map;
            public StartPoints.Point point; // own start point (null = the area's default start)
            public Text status;
            public RectTransform progressFill;
            public Text deleteLabel;
            public float deleteArmedUntil;
        }
        bool? builtPortrait;
        Camera cam;
        CharacterPreview preview;
        Text characterLabel;

        void Start()
        {
            Application.targetFrameRate = 60;
            MapStore.Init();
            cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                cam.backgroundColor = UiKit.Sea;
                cam.fieldOfView = PreviewFov;
                cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(6f, 0f, 0f));
            }
            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
                es.AddComponent<InputSystemUIInputModule>();
#else
                es.AddComponent<StandaloneInputModule>();
#endif
            }
            preview = new GameObject("CharacterPreview").AddComponent<CharacterPreview>();
            preview.Show(PlayerAvatar.SelectedIndex);
            AppUpdater.CheckOnce();
        }

        void Update()
        {
            // Android back button (arrives as Escape) on the root screen: send the app to the background.
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (Application.platform == RuntimePlatform.Android && kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                activity.Call<bool>("moveTaskToBack", true);
            }
            bool portrait = Screen.height > Screen.width;
            if (builtPortrait != portrait || builtRevision != MapStore.Revision + StartPoints.Revision * 100000)
            {
                builtPortrait = portrait;
                builtRevision = MapStore.Revision + StartPoints.Revision * 100000;
                if (canvasGo != null) Destroy(canvasGo);
                BuildUi(portrait);
            }
            RefreshCards();
            PlacePreview(portrait);
            RefreshUpdateUi();
        }

        /// <summary>Puts the character where the layout leaves room for it, sized to that region.</summary>
        void PlacePreview(bool portrait)
        {
            if (cam == null || preview == null) return;
            Vector2 viewport = portrait ? new Vector2(0.5f, 0.66f) : new Vector2(0.21f, 0.45f);
            float screenFraction = portrait ? 0.2f : 0.5f; // character height as fraction of screen height
            float distance = 1.75f / (screenFraction * 2f * Mathf.Tan(PreviewFov * 0.5f * Mathf.Deg2Rad));
            var ray = cam.ViewportPointToRay(new Vector3(viewport.x, viewport.y, 0f));
            preview.SetAnchor(ray.GetPoint(distance) - Vector3.up * 0.9f, cam.transform.position);
        }

        void BuildUi(bool portrait)
        {
            cards.Clear();
            canvasGo = new GameObject("Menu", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1080);
            scaler.matchWidthOrHeight = portrait ? 0f : 1f; // short side = 1080 units

            // Safe area root (notch / home indicator).
            var safe = UiKit.Rect("SafeArea", canvasGo.transform);
            var sa = Screen.safeArea;
            safe.anchorMin = new Vector2(sa.xMin / Screen.width, sa.yMin / Screen.height);
            safe.anchorMax = new Vector2(sa.xMax / Screen.width, sa.yMax / Screen.height);
            safe.offsetMin = safe.offsetMax = Vector2.zero;

            float longSide = 1080f * Mathf.Max(Screen.width, Screen.height) / Mathf.Max(1, Mathf.Min(Screen.width, Screen.height));
            if (portrait) BuildPortrait(safe, longSide);
            else BuildLandscape(safe, longSide);
            updateLabel = null;
            updateBanner = null;
            if (BuildExpiry.IsExpired) ExpiredOverlay(canvasGo.transform);
            else if (ShowsUpdate) UpdateBanner(safe, portrait);
        }

        static bool ShowsUpdate => AppUpdater.Current >= AppUpdater.State.Available;

        RectTransform updateBanner;
        Text updateLabel;

        /// <summary>Android release builds: "update available" pill under the header; tap downloads and installs.</summary>
        void UpdateBanner(RectTransform root, bool portrait)
        {
            var anchor = portrait ? new Vector2(0.5f, 1f) : new Vector2(0f, 1f);
            var pos = portrait ? new Vector2(0, -250) : new Vector2(60, -215);
            updateBanner = UiKit.Rect("Update", root, anchor, pos, new Vector2(760, 96));
            UiKit.DropShadow(updateBanner, 18f, -6f, 0.3f);
            var img = UiKit.Panel(updateBanner.gameObject, UiKit.Mustard, UiKit.Rounded);
            var button = updateBanner.gameObject.AddComponent<Button>();
            button.targetGraphic = img;
            button.onClick.AddListener(AppUpdater.Install);
            updateLabel = UiKit.Label(updateBanner, AppUpdater.Label(), 34, UiKit.Navy, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(updateLabel.rectTransform, 12f);
        }

        /// <summary>Release build past its date: covers the whole menu; the button installs the new version in place
        /// (Android, once the update check found it) or opens the GitHub release page.</summary>
        void ExpiredOverlay(Transform canvas)
        {
            var root = UiKit.Rect("Expired", canvas);
            UiKit.Stretch(root);
            UiKit.Panel(root.gameObject, new Color(UiKit.Navy.r, UiKit.Navy.g, UiKit.Navy.b, 0.96f)); // blocks all taps below
            var card = UiKit.Rect("Card", root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(860, 520));
            UiKit.Panel(card.gameObject, UiKit.Card, UiKit.Rounded);
            UiKit.Label(card, "Diese Version ist abgelaufen", 52, UiKit.Navy, TextAnchor.UpperCenter, FontStyle.Bold)
                .Place(new Vector2(0.5f, 1f), new Vector2(0, -60), new Vector2(780, 70));
            string date = BuildExpiry.Expires.HasValue ? BuildExpiry.Expires.Value.ToLocalTime().ToString("dd.MM.yyyy") : "";
            UiKit.Label(card, $"Testversionen laufen 15 Tage (bis {date}).\nDie aktuelle Version gibt es auf GitHub.", 34, UiKit.InkSoft,
                    TextAnchor.UpperCenter)
                .Place(new Vector2(0.5f, 1f), new Vector2(0, -160), new Vector2(780, 120));
            var btn = UiKit.Rect("Download", card, new Vector2(0.5f, 0f), new Vector2(0, 110), new Vector2(680, 110));
            var img = UiKit.Panel(btn.gameObject, UiKit.Mustard, UiKit.Rounded);
            var button = btn.gameObject.AddComponent<Button>();
            button.targetGraphic = img;
            button.onClick.AddListener(() =>
            {
                if (ShowsUpdate) AppUpdater.Install();
                else Application.OpenURL(BuildExpiry.ReleasesUrl);
            });
            updateLabel = UiKit.Label(btn, ShowsUpdate ? AppUpdater.Label() : "Neue Version laden", 36, UiKit.Navy, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(updateLabel.rectTransform, 12f);
        }

        /// <summary>Keeps banner/button text in step with the updater; adds the banner once the check found an update.</summary>
        void RefreshUpdateUi()
        {
            if (!AppUpdater.Supported || canvasGo == null) return;
            if (ShowsUpdate && updateLabel == null && !BuildExpiry.IsExpired)
            {
                var safe = (RectTransform)canvasGo.transform.Find("SafeArea");
                if (safe != null) UpdateBanner(safe, builtPortrait ?? true);
            }
            if (updateLabel != null && ShowsUpdate) updateLabel.text = AppUpdater.Label();
        }

        // ---------------------------------------------------------------- layouts

        void BuildPortrait(RectTransform root, float height)
        {
            Header(root, new Vector2(0.5f, 1f), new Vector2(0, -40), TextAnchor.UpperCenter);

            // Character zone between header and sheet.
            float sheetHeight = Mathf.Min(height * 0.47f, 1150f);
            var zone = UiKit.Rect("CharacterZone", root);
            zone.anchorMin = new Vector2(0, 0); zone.anchorMax = new Vector2(1, 1);
            zone.offsetMin = new Vector2(0, sheetHeight + 20); zone.offsetMax = new Vector2(0, -230);
            CharacterControls(zone);

            var sheet = UiKit.Rect("Sheet", root);
            sheet.anchorMin = new Vector2(0, 0); sheet.anchorMax = new Vector2(1, 0);
            sheet.pivot = new Vector2(0.5f, 0f);
            sheet.sizeDelta = new Vector2(0, sheetHeight + 60); // extends below the safe area
            sheet.anchoredPosition = new Vector2(0, -60);
            UiKit.DropShadow(sheet, 30f, 0f, 0.3f);
            UiKit.Panel(sheet.gameObject, UiKit.Card);
            AreaGrid(sheet, new Vector2(1080, sheetHeight + 60), 60f);
        }

        void BuildLandscape(RectTransform root, float width)
        {
            Header(root, new Vector2(0f, 1f), new Vector2(60, -30), TextAnchor.UpperLeft);

            float sheetWidth = Mathf.Min(width * 0.56f, 1350f);
            var zone = UiKit.Rect("CharacterZone", root);
            zone.anchorMin = new Vector2(0, 0); zone.anchorMax = new Vector2(1, 1);
            zone.offsetMin = new Vector2(0, 40); zone.offsetMax = new Vector2(-sheetWidth - 40, -200);
            CharacterControls(zone);

            var sheet = UiKit.Rect("Sheet", root);
            sheet.anchorMin = new Vector2(1, 0); sheet.anchorMax = new Vector2(1, 1);
            sheet.pivot = new Vector2(1f, 0.5f);
            sheet.sizeDelta = new Vector2(sheetWidth, -60);
            sheet.anchoredPosition = new Vector2(-30, 0);
            UiKit.DropShadow(sheet, 30f, -8f, 0.3f);
            UiKit.Panel(sheet.gameObject, UiKit.Card);
            AreaGrid(sheet, new Vector2(sheetWidth, 1020f), 40f);
        }

        // ---------------------------------------------------------------- parts

        static void Header(RectTransform root, Vector2 anchor, Vector2 pos, TextAnchor align)
        {
            UiKit.Wordmark(root, anchor, pos, 110f);
            var sub = UiKit.Label(root, "Entdecke den Nordwesten", 38, Color.white, align, FontStyle.Bold)
                .Place(anchor, pos + new Vector2(0, -130), new Vector2(900, 50));
            sub.gameObject.AddComponent<Shadow>().effectColor = new Color(0.1f, 0.23f, 0.31f, 0.6f);
        }

        void CharacterControls(RectTransform zone)
        {
            // Drag anywhere in the zone to spin the figure.
            var drag = UiKit.Rect("Spin", zone);
            UiKit.Stretch(drag);
            drag.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0);
            drag.gameObject.AddComponent<SpinArea>().preview = preview;

            ArrowButton(zone, "<", new Vector2(0f, 0.5f), new Vector2(60, 0), -1);
            ArrowButton(zone, ">", new Vector2(1f, 0.5f), new Vector2(-60, 0), +1);

            var pill = UiKit.Rect("NamePill", zone, new Vector2(0.5f, 0f), new Vector2(0, 0), new Vector2(330, 76));
            UiKit.DropShadow(pill, 14f, -4f, 0.25f);
            UiKit.Panel(pill.gameObject, new Color(1, 1, 1, 0.92f));
            characterLabel = UiKit.Label(pill, "", 34, UiKit.Ink, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(characterLabel.rectTransform);
            RefreshCharacterLabel();
        }

        void ArrowButton(RectTransform parent, string glyph, Vector2 anchor, Vector2 pos, int step)
        {
            var rt = UiKit.Rect($"Arrow{step}", parent, anchor, pos, new Vector2(104, 104));
            rt.pivot = new Vector2(anchor.x, 0.5f);
            UiKit.DropShadow(rt, 14f, -4f, 0.25f);
            var img = UiKit.Panel(rt.gameObject, new Color(1, 1, 1, 0.92f), UiKit.Circle);
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => ChangeCharacter(step));
            var t = UiKit.Label(rt, glyph, 60, UiKit.Navy, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(t.rectTransform);
        }

        void ChangeCharacter(int step)
        {
            var gm = GameMaterials.Instance;
            int count = gm != null ? gm.characters.Count : 0;
            if (count == 0) return;
            PlayerAvatar.SelectedIndex = (PlayerAvatar.SelectedIndex + step + count) % count;
            preview.Show(PlayerAvatar.SelectedIndex);
            RefreshCharacterLabel();
        }

        void RefreshCharacterLabel()
        {
            var gm = GameMaterials.Instance;
            int count = gm != null ? gm.characters.Count : 0;
            if (characterLabel != null) characterLabel.text = count > 0 ? $"Figur {PlayerAvatar.SelectedIndex + 1} / {count}" : "";
        }

        void AreaGrid(RectTransform sheet, Vector2 size, float padding)
        {
            UiKit.Label(sheet, "Wo startest du?", 50, UiKit.Ink, TextAnchor.UpperLeft, FontStyle.Bold)
                .Place(new Vector2(0, 1), new Vector2(padding, -padding * 0.8f), new Vector2(size.x - 2 * padding, 64));
            UiKit.Label(sheet, "Karte © OpenStreetMap-Mitwirkende · 3D-Gebäude © LGLN · Figuren: Kenney (CC0)", 20, UiKit.InkSoft, TextAnchor.LowerCenter)
                .Place(new Vector2(0.5f, 0f), new Vector2(0, 74), new Vector2(size.x, 30));

            var maps = MapStore.Maps;
            if (maps.Count == 0)
            {
                string msg = MapStore.Fetching ? "Lade Kartenliste …"
                    : Application.isEditor ? "Noch keine Gebiete importiert."
                    : "Keine Karten geladen.\nFür den Download Internet einschalten.";
                UiKit.Label(sheet, msg, 36, UiKit.InkSoft, TextAnchor.MiddleCenter)
                    .Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size.x - 2 * padding, 120));
                return;
            }

            // Default start of every area, then the player's own start points (of areas we know).
            var entries = new List<(MapStore.Map map, StartPoints.Point point)>();
            foreach (var m in maps) entries.Add((m, null));
            foreach (var p in StartPoints.All)
            {
                var m = maps.Find(x => x.Id == p.areaId);
                if (m != null) entries.Add((m, p));
            }

            const float gap = 28f;
            float top = padding * 0.8f + 90f, bottom = 120f;
            int cols = 2, rows = Mathf.CeilToInt(entries.Count / 2f);
            float cardW = (size.x - 2 * padding - gap * (cols - 1)) / cols;
            // Cards keep their size; more than fit scroll (new areas and own start points keep coming).
            float viewH = size.y - top - bottom;
            float cardH = Mathf.Min(cardW * 0.95f, Mathf.Max(cardW * 0.78f, (viewH - gap * (Mathf.Min(rows, 2) - 1)) / Mathf.Min(rows, 2)));
            var view = UiKit.Rect("Places", sheet);
            view.anchorMin = new Vector2(0, 1); view.anchorMax = new Vector2(1, 1);
            view.pivot = new Vector2(0.5f, 1f);
            view.sizeDelta = new Vector2(0, viewH + 30f);
            view.anchoredPosition = new Vector2(0, -top + 15f);
            view.gameObject.AddComponent<RectMask2D>().padding = new Vector4(0, -30, 0, 0);
            view.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0);
            var content = UiKit.Rect("Content", view);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = new Vector2(0, rows * (cardH + gap) + 30f);
            var scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            for (int i = 0; i < entries.Count; i++)
            {
                int c = i % cols, r = i / cols;
                var pos = new Vector2(padding + c * (cardW + gap), -(15f + r * (cardH + gap)));
                AreaCard(content, entries[i].map, entries[i].point, pos, new Vector2(cardW, cardH));
            }
        }

        void AreaCard(RectTransform parent, MapStore.Map map, StartPoints.Point point, Vector2 pos, Vector2 size)
        {
            var ui = new CardUi { map = map, point = point };
            cards.Add(ui);
            var card = UiKit.Rect($"Card {point?.name ?? map.entry.name}", parent, new Vector2(0, 1), pos, size);
            UiKit.DropShadow(card, 16f, -6f, 0.28f);
            var bg = UiKit.Panel(card.gameObject, Color.white);
            var btn = card.gameObject.AddComponent<Button>();
            btn.targetGraphic = bg;
            var colors = btn.colors;
            colors.pressedColor = new Color(0.9f, 0.92f, 0.95f);
            btn.colors = colors;
            // Installed → play (also while an update downloads); otherwise the card starts the download.
            btn.onClick.AddListener(() =>
            {
                MapStore.Start(map, point); // fetches the small area file if needed, then goes – tiles follow on demand
            });

            // Thumbnail, clipped to rounded corners.
            float thumbH = size.y * 0.58f;
            var mask = UiKit.Rect("ThumbMask", card);
            mask.anchorMin = new Vector2(0, 1); mask.anchorMax = new Vector2(1, 1);
            mask.pivot = new Vector2(0.5f, 1f);
            mask.sizeDelta = new Vector2(-16, thumbH);
            mask.anchoredPosition = new Vector2(0, -8);
            UiKit.Panel(mask.gameObject, Color.white);
            mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            var thumb = UiKit.Rect("Thumb", mask);
            UiKit.Stretch(thumb);
            var thumbImg = thumb.gameObject.AddComponent<Image>();
            thumbImg.raycastTarget = false;
            var sprite = ThumbSprite(map.thumb);
            if (sprite != null)
            {
                thumbImg.sprite = sprite;
                var fit = thumb.gameObject.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
                fit.aspectRatio = sprite.rect.width / sprite.rect.height;
            }
            else thumbImg.color = UiKit.Sea;

            // Type badge on the thumbnail.
            bool island = map.HasVehicle("carriage");
            var badge = UiKit.Rect("Badge", mask, new Vector2(0, 1), new Vector2(16, -16), new Vector2(116, 46));
            UiKit.Panel(badge.gameObject, island ? UiKit.Island : UiKit.City);
            UiKit.Stretch(UiKit.Label(badge, island ? "Insel" : "Stadt", 26, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold).rectTransform);

            // Download state: pill on the thumbnail (bottom), progress bar along its lower edge.
            var pill = UiKit.Rect("Status", mask, new Vector2(1, 0), new Vector2(-14, 14), new Vector2(Mathf.Min(size.x - 44, 420), 50));
            var pillImg = UiKit.Panel(pill.gameObject, UiKit.Mustard);
            ui.status = UiKit.Label(pill, "", 26, UiKit.Navy, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(ui.status.rectTransform, 6f);
            var track = UiKit.Rect("Progress", mask);
            track.anchorMin = new Vector2(0, 0); track.anchorMax = new Vector2(1, 0);
            track.pivot = new Vector2(0.5f, 0f);
            track.sizeDelta = new Vector2(0, 8);
            track.anchoredPosition = Vector2.zero;
            track.gameObject.AddComponent<Image>().color = new Color(UiKit.Navy.r, UiKit.Navy.g, UiKit.Navy.b, 0.35f);
            ui.progressFill = UiKit.Rect("Fill", track);
            ui.progressFill.anchorMin = Vector2.zero; ui.progressFill.anchorMax = new Vector2(0, 1);
            ui.progressFill.offsetMin = ui.progressFill.offsetMax = Vector2.zero;
            ui.progressFill.gameObject.AddComponent<Image>().color = UiKit.Mustard;

            float textTop = thumbH + 22;
            UiKit.Label(card, point != null ? point.name : map.entry.name, 40, UiKit.Ink, TextAnchor.UpperLeft, FontStyle.Bold)
                .Place(new Vector2(0, 1), new Vector2(26, -textTop), new Vector2(size.x - 52, 50));

            // How you get around there.
            var ways = new List<string> { "zu Fuß" };
            if (map.HasVehicle("bicycle")) ways.Add("Fahrrad");
            if (map.HasVehicle("car")) ways.Add("Auto");
            if (map.HasVehicle("carriage")) ways.Add("Kutsche");
            UiKit.Label(card, point != null ? $"{map.entry.name} · eigener Startpunkt" : string.Join(" · ", ways), 26, UiKit.InkSoft, TextAnchor.UpperLeft)
                .Place(new Vector2(0, 1), new Vector2(26, -textTop - 54), new Vector2(size.x - 52, 34));

            // Remove from the device (tap twice). Not in the editor – there the scenes are the project's.
            if (point != null || (map.Playable && !map.Busy && !Application.isEditor))
            {
                var del = UiKit.Rect("Delete", card, new Vector2(1, 0), new Vector2(-14, 10), new Vector2(170, 52));
                var delImg = del.gameObject.AddComponent<Image>();
                delImg.color = new Color(0, 0, 0, 0);
                var delBtn = del.gameObject.AddComponent<Button>();
                delBtn.targetGraphic = delImg;
                ui.deleteLabel = UiKit.Label(del, "Löschen", 24, UiKit.InkSoft, TextAnchor.MiddleRight);
                UiKit.Stretch(ui.deleteLabel.rectTransform);
                delBtn.onClick.AddListener(() =>
                {
                    if (Time.unscaledTime < ui.deleteArmedUntil)
                    {
                        if (point != null) StartPoints.Remove(point);
                        else MapStore.Delete(map);
                    }
                    else ui.deleteArmedUntil = Time.unscaledTime + 3f;
                });
            }
            RefreshCard(ui);
        }

        Sprite ThumbSprite(Texture2D tex)
        {
            if (tex == null) return null;
            if (!thumbSprites.TryGetValue(tex, out var sprite))
                thumbSprites[tex] = sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            return sprite;
        }

        void RefreshCards()
        {
            foreach (var ui in cards) RefreshCard(ui);
        }

        static void RefreshCard(CardUi ui)
        {
            var m = ui.map;
            string size = m.latest != null ? MapStore.Megabytes(m.DownloadSize) : "";
            string text;
            switch (m.status)
            {
                case MapStore.Status.Available: text = ""; break; // tap = go; only the small area file is fetched
                case MapStore.Status.Queued: text = "Wartet …"; break;
                case MapStore.Status.Downloading: text = $"Lädt … {Mathf.RoundToInt(m.progress * 100f)} %"; break;
                case MapStore.Status.Installing: text = "Wird eingerichtet …"; break;
                case MapStore.Status.UpdateAvailable: text = ""; break; // refreshed automatically on start
                case MapStore.Status.UpdateRequired: text = ""; break;
                case MapStore.Status.Unavailable: text = "Neue App-Version nötig"; break;
                case MapStore.Status.Pending: text = "Karte wird aktualisiert"; break;
                case MapStore.Status.Failed: text = (m.error ?? "Fehler") + " – nochmal"; break;
                default: text = ""; break;
            }
            ui.status.text = text;
            ui.status.transform.parent.gameObject.SetActive(text.Length > 0);
            bool loading = m.status == MapStore.Status.Downloading || m.status == MapStore.Status.Installing;
            ui.progressFill.parent.gameObject.SetActive(loading);
            ui.progressFill.anchorMax = new Vector2(Mathf.Clamp01(m.progress), 1f);
            if (ui.deleteLabel != null)
            {
                bool armed = Time.unscaledTime < ui.deleteArmedUntil;
                ui.deleteLabel.text = armed ? "Wirklich löschen?" : "Löschen";
                ui.deleteLabel.color = armed ? new Color(0.75f, 0.2f, 0.15f) : UiKit.InkSoft;
            }
        }

        /// <summary>Drag horizontally to spin the preview.</summary>
        class SpinArea : MonoBehaviour, IDragHandler
        {
            public CharacterPreview preview;
            public void OnDrag(PointerEventData e) { if (preview != null) preview.Spin(-e.delta.x * 0.4f); }
        }
    }

    /// <summary>Animated character on a little pedestal, slowly turning.</summary>
    public class CharacterPreview : MonoBehaviour
    {
        GameObject model;
        Transform pedestal;
        float angle = 20f;
        float spinVelocity = 12f;
        Vector3 anchor;
        Vector3 cameraPos;

        public void Show(int index)
        {
            var gm = GameMaterials.Instance;
            if (gm == null || gm.characters.Count == 0) return;
            if (model != null) Destroy(model);
            index = Mathf.Clamp(index, 0, gm.characters.Count - 1);
            model = Instantiate(gm.characters[index], transform, false);
            model.transform.localScale = Vector3.one * gm.characterScale;
            model.transform.localPosition = Vector3.up * gm.characterFootOffset;
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = gm.characterMaterial;
                r.sharedMaterials = mats;
            }
            var anim = model.GetComponent<Animator>();
            if (anim == null) anim = model.AddComponent<Animator>();
            anim.runtimeAnimatorController = gm.characterController;
            anim.SetBool("Grounded", true);
            anim.SetTrigger("Jump"); // small hop as feedback when switching

            if (pedestal == null && gm.pedestalMaterial != null)
            {
                var p = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(p.GetComponent<Collider>());
                p.transform.SetParent(transform, false);
                p.transform.localScale = new Vector3(1.6f, 0.06f, 1.6f);
                p.transform.localPosition = Vector3.down * 0.06f;
                p.GetComponent<Renderer>().sharedMaterial = gm.pedestalMaterial;
                pedestal = p.transform;
            }
        }

        public void Spin(float degrees)
        {
            angle += degrees;
            spinVelocity = degrees / Mathf.Max(Time.deltaTime, 0.001f);
        }

        public void SetAnchor(Vector3 position, Vector3 cameraPosition)
        {
            anchor = position;
            cameraPos = cameraPosition;
        }

        void LateUpdate()
        {
            // Inertia after a swipe, settling into a gentle idle spin.
            spinVelocity = Mathf.Lerp(spinVelocity, 12f, 1f - Mathf.Exp(-2.5f * Time.deltaTime));
            angle += spinVelocity * Time.deltaTime;
            transform.position = anchor;
            Vector3 toCam = cameraPos - anchor;
            toCam.y = 0f;
            float facing = Mathf.Atan2(toCam.x, toCam.z) * Mathf.Rad2Deg;
            if (model != null) model.transform.rotation = Quaternion.Euler(0f, facing + angle, 0f);
        }
    }
}
