using LeGamez.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>Builds the in-game overlay in code: joystick, look area, buttons, area title, OSM credit.</summary>
    public class Hud : MonoBehaviour
    {
        public Text titleLabel;
        public Text infoLabel;
        public Text toastLabel;
        public TapButton jumpButton;
        public TapButton interactButton;
        public TapButton runButton;
        Image runBackground;
        Image interactIcon;

        // Vehicle controls (shown instead of joystick/run/jump while riding).
        RectTransform driveRoot;
        HoldButton steerLeft, steerRight, gasPedal, brakePedal;
        Text gearLabel;
        RectTransform gearBadge;
        Vector2 interactRestPos;

        RectTransform safeRoot;
        Rect appliedSafeArea;
        float toastUntil;
        CanvasScaler scaler;
        RectTransform joystickZone;
        VirtualJoystick joystick;
        RectTransform toastRect;
        bool? portrait;

        public static bool IsPortrait => Screen.height > Screen.width;
        const float MiniMapSize = 280f;

        static Font uiFont;
        public static Font UiFont => uiFont != null ? uiFont : uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        public static Hud Create(string areaName, string attribution)
        {
            var canvasGo = new GameObject("HUD", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // Layout is authored for a 1080-unit short side; ApplyOrientation picks the axis to match.
            scaler.referenceResolution = new Vector2(1080, 1080);

            var hud = canvasGo.AddComponent<Hud>();
            hud.scaler = scaler;
            hud.safeRoot = NewRect("SafeArea", canvasGo.transform);
            Stretch(hud.safeRoot);

            // Look area: whole screen, behind everything else – any drag outside the joystick turns the camera.
            var look = NewRect("LookArea", hud.safeRoot);
            Stretch(look);
            AddRaycastTarget(look.gameObject);
            look.gameObject.AddComponent<TouchLookArea>();

            // Joystick zone: lower-left region, sized per orientation in ApplyOrientation.
            var zone = NewRect("JoystickZone", hud.safeRoot);
            AddRaycastTarget(zone.gameObject);
            hud.joystickZone = zone;
            var bg = NewRect("Base", zone);
            bg.anchorMin = bg.anchorMax = Vector2.zero;
            bg.sizeDelta = new Vector2(280, 280);
            bg.anchoredPosition = new Vector2(220, 220);
            AddCircle(bg.gameObject, new Color(0.1f, 0.12f, 0.15f, 0.28f)).raycastTarget = false;
            var rim = NewRect("Rim", bg);
            rim.sizeDelta = new Vector2(280, 280);
            AddRing(rim.gameObject, new Color(1f, 1f, 1f, 0.55f)).raycastTarget = false;
            var handle = NewRect("Handle", bg);
            handle.sizeDelta = new Vector2(120, 120);
            AddCircle(handle.gameObject, new Color(1f, 1f, 1f, 0.8f)).raycastTarget = false;
            var stick = zone.gameObject.AddComponent<VirtualJoystick>();
            stick.background = bg;
            stick.handle = handle;
            hud.joystick = stick;

            // Action row along the bottom edge, right to left: jump, run, bike (bike only when available).
            const float rowY = 120f, btn = 136f, gap = 22f;
            hud.jumpButton = CreateIconButton(hud.safeRoot, "Jump", UiKit.IconKind.Jump, new Vector2(-40 - btn / 2, rowY), btn);
            hud.jumpButton.onPress = GameInput.QueueJump;
            hud.runButton = CreateIconButton(hud.safeRoot, "Run", UiKit.IconKind.Run, new Vector2(-40 - btn * 1.5f - gap, rowY), btn);
            hud.runButton.onPress = GameInput.ToggleRun;
            hud.runBackground = hud.runButton.GetComponent<Image>();
            // Context action (mount/dismount): round icon button like jump/run, above the jump button.
            hud.interactButton = CreateIconButton(hud.safeRoot, "Interact", UiKit.IconKind.Bike, new Vector2(-40 - btn * 2.5f - gap * 2, rowY), btn);
            hud.interactIcon = hud.interactButton.transform.Find("Icon").GetComponent<Image>();
            hud.interactButton.GetComponent<Image>().color = new Color(UiKit.Mustard.r, UiKit.Mustard.g, UiKit.Mustard.b, 0.9f);
            hud.interactButton.onPress = GameInput.QueueInteract;
            hud.interactButton.gameObject.SetActive(false);
            hud.interactRestPos = ((RectTransform)hud.interactButton.transform).anchoredPosition;
            hud.BuildDriveControls();

            var menuButton = CreateButton(hud.safeRoot, "Menu", "×", Vector2.zero, 90);
            var menuRt = (RectTransform)menuButton.transform;
            menuRt.anchorMin = menuRt.anchorMax = Vector2.one;
            menuRt.anchoredPosition = new Vector2(-28 - MiniMapSize - 20 - 45, -28 - 45);
            menuButton.GetComponentInChildren<Text>().fontSize = 40;
            menuButton.onPress = () => UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(0);

            hud.titleLabel = CreateLabel(hud.safeRoot, "Title", areaName, 44, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(40, -30), new Vector2(580, 60));
            hud.infoLabel = CreateLabel(hud.safeRoot, "Info", "", 30, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(40, -90), new Vector2(580, 50));

            MiniMap.Create(hud.safeRoot, MiniMapSize);
            hud.toastLabel = CreateLabel(hud.safeRoot, "Toast", "", 40, TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f), new Vector2(0, -160), new Vector2(1000, 70));
            hud.toastRect = hud.toastLabel.rectTransform;
            var credit = CreateLabel(hud.safeRoot, "Attribution", attribution, 20, TextAnchor.LowerRight,
                new Vector2(1, 0), new Vector2(-20, 10), new Vector2(700, 30));
            credit.color = new Color(1, 1, 1, 0.75f);

            hud.ApplySafeArea();
            hud.ApplyOrientation();
            return hud;
        }

        void Update()
        {
            if (Screen.safeArea != appliedSafeArea) ApplySafeArea();
            // Android back button arrives as Escape: close the big map first, otherwise back to the menu.
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame && !FullMap.CloseIfOpen())
                UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(0);
            if (portrait != IsPortrait) ApplyOrientation();
            if (toastLabel != null && toastLabel.text.Length > 0 && Time.time > toastUntil) toastLabel.text = "";
            if (driveRoot != null && driveRoot.gameObject.activeSelf)
            {
                GameInput.TouchSteer = (steerRight.Held ? 1f : 0f) - (steerLeft.Held ? 1f : 0f);
                GameInput.TouchGas = gasPedal.Held;
                GameInput.TouchBrake = brakePedal.Held;
            }
            if (runBackground != null)
                runBackground.color = GameInput.RunToggled ? new Color(UiKit.Mustard.r, UiKit.Mustard.g, UiKit.Mustard.b, 0.95f) : ButtonIdle;
        }

        // ---------------------------------------------------------------- vehicle controls

        const float SteerSize = 190f, PedalW = 170f, GasH = 270f, BrakeH = 180f;

        void BuildDriveControls()
        {
            driveRoot = NewRect("DriveControls", safeRoot);
            Stretch(driveRoot);
            // Left thumb: ◀ ▶.
            steerLeft = HoldPad(driveRoot, "SteerLeft", new Vector2(0, 0), new Vector2(60 + SteerSize / 2, 50 + SteerSize / 2), new Vector2(SteerSize, SteerSize), UiKit.IconKind.SteerLeft, null);
            steerRight = HoldPad(driveRoot, "SteerRight", new Vector2(0, 0), new Vector2(60 + SteerSize * 1.5f + 28, 50 + SteerSize / 2), new Vector2(SteerSize, SteerSize), UiKit.IconKind.SteerRight, null);
            // Right thumb: tall gas pedal at the edge, brake next to it.
            gasPedal = HoldPad(driveRoot, "Gas", new Vector2(1, 0), new Vector2(-40 - PedalW / 2, 40 + GasH / 2), new Vector2(PedalW, GasH), null, "GAS");
            brakePedal = HoldPad(driveRoot, "Brake", new Vector2(1, 0), new Vector2(-40 - PedalW * 1.5f - 30, 40 + BrakeH / 2), new Vector2(PedalW, BrakeH), null, "BREMSE");
            // Gear badge (cars): D / R above the brake.
            gearBadge = NewRect("Gear", driveRoot);
            gearBadge.anchorMin = gearBadge.anchorMax = new Vector2(1, 0);
            gearBadge.sizeDelta = new Vector2(96, 72);
            gearBadge.anchoredPosition = new Vector2(-40 - PedalW * 1.5f - 30, 40 + BrakeH + 30 + 36);
            UiKit.Panel(gearBadge.gameObject, new Color(0.1f, 0.23f, 0.31f, 0.75f));
            gearLabel = UiKit.Label(gearBadge, "D", 44, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(gearLabel.rectTransform);
            driveRoot.gameObject.SetActive(false);
        }

        static HoldButton HoldPad(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, UiKit.IconKind? icon, string text)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            var bg = UiKit.Panel(rt.gameObject, ButtonIdle);
            bg.raycastTarget = true;
            if (icon.HasValue)
            {
                var glyph = NewRect("Icon", rt);
                glyph.sizeDelta = Vector2.one * Mathf.Min(size.x, size.y) * 0.5f;
                var img = glyph.gameObject.AddComponent<Image>();
                img.sprite = UiKit.Icon(icon.Value);
                img.raycastTarget = false;
            }
            if (text != null)
            {
                var t = UiKit.Label(rt, text, 34, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
                UiKit.Stretch(t.rectTransform);
                t.raycastTarget = false;
            }
            var hb = rt.gameObject.AddComponent<HoldButton>();
            hb.tint = bg;
            hb.idleColor = ButtonIdle;
            hb.heldColor = new Color(UiKit.Mustard.r, UiKit.Mustard.g, UiKit.Mustard.b, 0.9f);
            return hb;
        }

        /// <summary>Riding: ◀ ▶ + gas/brake instead of joystick, sprint and jump. <paramref name="gears"/> shows D/R.</summary>
        public void SetDriveMode(bool on, bool gears)
        {
            driveRoot.gameObject.SetActive(on);
            gearBadge.gameObject.SetActive(on && gears);
            joystickZone.gameObject.SetActive(!on);
            runButton.gameObject.SetActive(!on);
            jumpButton.gameObject.SetActive(!on);
            // Get off / out: above the gas pedal while riding.
            ((RectTransform)interactButton.transform).anchoredPosition = on ? new Vector2(-40 - PedalW / 2, 40 + GasH + 30 + 68) : interactRestPos;
            if (!on) { GameInput.TouchSteer = 0f; GameInput.TouchGas = GameInput.TouchBrake = false; }
        }

        public void SetGear(bool reverse) => gearLabel.text = reverse ? "R" : "D";

        public void ShowToast(string message, float seconds = 3f)
        {
            toastLabel.text = message;
            toastUntil = Time.time + seconds;
        }

        /// <summary>Swaps the sprint button pictogram (running figure on foot, fast bike while riding).</summary>
        public void SetRunIcon(UiKit.IconKind icon) => runButton.transform.Find("Icon").GetComponent<Image>().sprite = UiKit.Icon(icon);

        /// <summary>Shows the context action button with the given pictogram, or hides it (null).</summary>
        public void SetInteract(UiKit.IconKind? icon)
        {
            bool show = icon.HasValue;
            if (interactButton.gameObject.activeSelf != show) interactButton.gameObject.SetActive(show);
            if (show) interactIcon.sprite = UiKit.Icon(icon.Value);
        }

        void ApplyOrientation()
        {
            portrait = IsPortrait;
            bool p = portrait.Value;
            scaler.matchWidthOrHeight = p ? 0f : 1f; // match the short side
            joystickZone.anchorMin = Vector2.zero;
            joystickZone.anchorMax = p ? new Vector2(0.62f, 0.5f) : new Vector2(0.45f, 0.8f);
            joystickZone.offsetMin = joystickZone.offsetMax = Vector2.zero;
            joystick.ResetRest(p ? new Vector2(230, 330) : new Vector2(250, 250));
            toastRect.anchoredPosition = new Vector2(0, p ? -420 : -190);
        }

        void ApplySafeArea()
        {
            appliedSafeArea = Screen.safeArea;
            var min = appliedSafeArea.position;
            var max = appliedSafeArea.position + appliedSafeArea.size;
            min.x /= Screen.width; min.y /= Screen.height;
            max.x /= Screen.width; max.y /= Screen.height;
            safeRoot.anchorMin = min;
            safeRoot.anchorMax = max;
        }

        // ---- tiny UI factory helpers -------------------------------------------------

        static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        static void AddRaycastTarget(GameObject go)
        {
            var img = go.AddComponent<Image>();
            img.color = new Color(0, 0, 0, 0);
            img.raycastTarget = true;
        }

        static Sprite circleSprite;

        static Sprite CircleSprite()
        {
            if (circleSprite != null) return circleSprite;
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                byte a = (byte)(Mathf.Clamp01(r - d) * 255);
                px[y * size + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(px);
            tex.Apply();
            circleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            return circleSprite;
        }

        static Sprite ringSprite;

        static Image AddRing(GameObject go, Color color)
        {
            if (ringSprite == null)
            {
                const int size = 128;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[size * size];
                float r = size * 0.5f;
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                    float a = Mathf.Clamp01(r - d) * Mathf.Clamp01(d - (r - 5f));
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
                tex.SetPixels32(px);
                tex.Apply();
                ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            }
            var img = go.AddComponent<Image>();
            img.sprite = ringSprite;
            img.color = color;
            return img;
        }

        static Image AddCircle(GameObject go, Color color)
        {
            var img = go.AddComponent<Image>();
            img.sprite = CircleSprite();
            img.color = color;
            return img;
        }

        static readonly Color ButtonIdle = new Color(0.1f, 0.23f, 0.31f, 0.55f);

        /// <summary>Round action button with a pictogram, navy glass background.</summary>
        static TapButton CreateIconButton(Transform parent, string name, UiKit.IconKind icon, Vector2 pos, float size)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin = rt.anchorMax = new Vector2(1, 0);
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = pos;
            AddCircle(rt.gameObject, ButtonIdle);
            var ring = NewRect("Ring", rt);
            ring.sizeDelta = new Vector2(size, size);
            AddRing(ring.gameObject, new Color(1f, 1f, 1f, 0.7f)).raycastTarget = false;
            var glyph = NewRect("Icon", rt);
            glyph.sizeDelta = Vector2.one * size * 0.56f;
            var img = glyph.gameObject.AddComponent<Image>();
            img.sprite = UiKit.Icon(icon);
            img.raycastTarget = false;
            return rt.gameObject.AddComponent<TapButton>();
        }

        /// <summary>Mustard text pill for context actions ("Aufsteigen", "Absteigen").</summary>
        static TapButton CreatePillButton(Transform parent, string name, Vector2 pos, Vector2 size)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin = rt.anchorMax = new Vector2(1, 0);
            rt.pivot = new Vector2(1, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            UiKit.DropShadow(rt, 14f, -4f, 0.3f);
            UiKit.Panel(rt.gameObject, UiKit.Mustard);
            var t = UiKit.Label(rt, "", 36, UiKit.Navy, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(t.rectTransform);
            return rt.gameObject.AddComponent<TapButton>();
        }

        static TapButton CreateButton(Transform parent, string name, string glyph, Vector2 pos, float size)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin = rt.anchorMax = new Vector2(1, 0);
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = pos;
            AddCircle(rt.gameObject, new Color(1f, 1f, 1f, 0.35f));
            var label = CreateLabel(rt, "Label", glyph, 56, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
            label.raycastTarget = false;
            return rt.gameObject.AddComponent<TapButton>();
        }

        static Text CreateLabel(Transform parent, string name, string text, int size, TextAnchor align, Vector2 anchor, Vector2 pos, Vector2 box)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.sizeDelta = box;
            rt.anchoredPosition = pos;
            var t = rt.gameObject.AddComponent<Text>();
            t.font = UiFont;
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = Color.white;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var shadow = rt.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.6f);
            shadow.effectDistance = new Vector2(2, -2);
            return t;
        }
    }
}
