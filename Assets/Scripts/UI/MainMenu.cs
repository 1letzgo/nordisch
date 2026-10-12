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
    /// rendered thumbnails and stamp progress. Separate layouts for portrait and landscape.
    /// </summary>
    public class MainMenu : MonoBehaviour
    {
        const float PreviewFov = 30f;

        AreaCatalog catalog;
        GameObject canvasGo;
        bool? builtPortrait;
        Camera cam;
        CharacterPreview preview;
        Text characterLabel;

        void Start()
        {
            Application.targetFrameRate = 60;
            catalog = AreaCatalog.Load();
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
        }

        void Update()
        {
            bool portrait = Screen.height > Screen.width;
            if (builtPortrait != portrait)
            {
                builtPortrait = portrait;
                if (canvasGo != null) Destroy(canvasGo);
                BuildUi(portrait);
            }
            PlacePreview(portrait);
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
            UiKit.Logo(root, anchor, pos, 120f);
            var sub = UiKit.Label(root, "Entdecke den Nordwesten", 38, Color.white, align, FontStyle.Bold)
                .Place(anchor, pos + new Vector2(0, -146), new Vector2(900, 50));
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
            UiKit.Label(sheet, "Wohin geht's?", 50, UiKit.Ink, TextAnchor.UpperLeft, FontStyle.Bold)
                .Place(new Vector2(0, 1), new Vector2(padding, -padding * 0.8f), new Vector2(size.x - 2 * padding, 64));
            UiKit.Label(sheet, "Kartendaten © OpenStreetMap-Mitwirkende · Figuren: Kenney (CC0)", 20, UiKit.InkSoft, TextAnchor.LowerCenter)
                .Place(new Vector2(0.5f, 0f), new Vector2(0, 74), new Vector2(size.x, 30));

            if (catalog == null || catalog.areas.Count == 0)
            {
                UiKit.Label(sheet, "Noch keine Gebiete importiert.", 36, UiKit.InkSoft, TextAnchor.MiddleCenter)
                    .Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size.x, 60));
                return;
            }

            const float gap = 28f;
            float top = padding * 0.8f + 90f, bottom = 120f;
            int cols = 2, rows = Mathf.CeilToInt(catalog.areas.Count / 2f);
            float cardW = (size.x - 2 * padding - gap * (cols - 1)) / cols;
            float cardH = Mathf.Min((size.y - top - bottom - gap * (rows - 1)) / rows, cardW * 0.95f);
            for (int i = 0; i < catalog.areas.Count; i++)
            {
                int c = i % cols, r = i / cols;
                var pos = new Vector2(padding + c * (cardW + gap), -(top + r * (cardH + gap)));
                AreaCard(sheet, catalog.areas[i], pos, new Vector2(cardW, cardH));
            }
        }

        void AreaCard(RectTransform parent, AreaCatalog.Entry area, Vector2 pos, Vector2 size)
        {
            var card = UiKit.Rect($"Card {area.displayName}", parent, new Vector2(0, 1), pos, size);
            UiKit.DropShadow(card, 16f, -6f, 0.28f);
            var bg = UiKit.Panel(card.gameObject, Color.white);
            var btn = card.gameObject.AddComponent<Button>();
            btn.targetGraphic = bg;
            var colors = btn.colors;
            colors.pressedColor = new Color(0.9f, 0.92f, 0.95f);
            btn.colors = colors;
            string scene = area.sceneName;
            btn.onClick.AddListener(() => SceneManager.LoadSceneAsync(scene));

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
            var sprite = Resources.Load<Sprite>("AreaThumbs/" + area.id);
            if (sprite != null)
            {
                thumbImg.sprite = sprite;
                var fit = thumb.gameObject.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
                fit.aspectRatio = sprite.rect.width / sprite.rect.height;
            }
            else thumbImg.color = UiKit.Sea;

            // Type badge on the thumbnail.
            bool island = area.vehicles.Contains("carriage");
            var badge = UiKit.Rect("Badge", mask, new Vector2(0, 1), new Vector2(16, -16), new Vector2(116, 46));
            UiKit.Panel(badge.gameObject, island ? UiKit.Island : UiKit.City);
            UiKit.Stretch(UiKit.Label(badge, island ? "Insel" : "Stadt", 26, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold).rectTransform);

            float textTop = thumbH + 22;
            UiKit.Label(card, area.displayName, 40, UiKit.Ink, TextAnchor.UpperLeft, FontStyle.Bold)
                .Place(new Vector2(0, 1), new Vector2(26, -textTop), new Vector2(size.x - 52, 50));

            int stamps = StampBook.CountForArea(area.id);
            float progress = area.poiCount > 0 ? (float)stamps / area.poiCount : 0f;
            UiKit.Label(card, $"{stamps} / {area.poiCount} Stempel", 26, UiKit.InkSoft, TextAnchor.UpperLeft)
                .Place(new Vector2(0, 1), new Vector2(26, -textTop - 54), new Vector2(size.x - 52, 34));

            var track = UiKit.Rect("Track", card, new Vector2(0, 0), new Vector2(26, 24), new Vector2(size.x - 52, 14));
            UiKit.Panel(track.gameObject, UiKit.Track);
            var fill = UiKit.Rect("Fill", track, new Vector2(0, 0.5f), Vector2.zero, new Vector2(Mathf.Max(14, (size.x - 52) * progress), 14));
            UiKit.Panel(fill.gameObject, progress >= 1f ? UiKit.Done : UiKit.Accent);
            fill.gameObject.SetActive(progress > 0f);
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
