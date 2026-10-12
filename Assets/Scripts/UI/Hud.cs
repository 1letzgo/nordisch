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
        public Text interactLabel;

        RectTransform safeRoot;
        Rect appliedSafeArea;
        float toastUntil;
        CanvasScaler scaler;
        RectTransform joystickZone;
        VirtualJoystick joystick;
        RectTransform toastRect;
        bool? portrait;

        public static bool IsPortrait => Screen.height > Screen.width;

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
            bg.sizeDelta = new Vector2(240, 240);
            bg.anchoredPosition = new Vector2(220, 220);
            AddCircle(bg.gameObject, new Color(1f, 1f, 1f, 0.18f)).raycastTarget = false;
            var handle = NewRect("Handle", bg);
            handle.sizeDelta = new Vector2(110, 110);
            AddCircle(handle.gameObject, new Color(1f, 1f, 1f, 0.55f)).raycastTarget = false;
            var stick = zone.gameObject.AddComponent<VirtualJoystick>();
            stick.background = bg;
            stick.handle = handle;
            hud.joystick = stick;

            hud.jumpButton = CreateButton(hud.safeRoot, "Jump", "▲", new Vector2(-150, 170), 150);
            hud.jumpButton.onPress = GameInput.QueueJump;
            hud.interactButton = CreateButton(hud.safeRoot, "Interact", "", new Vector2(-330, 120), 130);
            hud.interactLabel = hud.interactButton.GetComponentInChildren<Text>();
            hud.interactLabel.fontSize = 26;
            hud.interactButton.onPress = GameInput.QueueInteract;
            hud.interactButton.gameObject.SetActive(false);

            var menuButton = CreateButton(hud.safeRoot, "Menu", "×", Vector2.zero, 90);
            var menuRt = (RectTransform)menuButton.transform;
            menuRt.anchorMin = menuRt.anchorMax = Vector2.one;
            menuRt.anchoredPosition = new Vector2(-70, -70);
            menuButton.GetComponentInChildren<Text>().fontSize = 40;
            menuButton.onPress = () => UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(0);

            hud.titleLabel = CreateLabel(hud.safeRoot, "Title", areaName, 44, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(40, -30), new Vector2(860, 60));
            hud.infoLabel = CreateLabel(hud.safeRoot, "Info", "", 30, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(40, -90), new Vector2(860, 50));
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
            if (portrait != IsPortrait) ApplyOrientation();
            if (toastLabel != null && toastLabel.text.Length > 0 && Time.time > toastUntil) toastLabel.text = "";
        }

        public void ShowToast(string message, float seconds = 3f)
        {
            toastLabel.text = message;
            toastUntil = Time.time + seconds;
        }

        public void SetInteract(string label)
        {
            bool show = !string.IsNullOrEmpty(label);
            if (interactButton.gameObject.activeSelf != show) interactButton.gameObject.SetActive(show);
            if (show) interactLabel.text = label;
        }

        void ApplyOrientation()
        {
            portrait = IsPortrait;
            bool p = portrait.Value;
            scaler.matchWidthOrHeight = p ? 0f : 1f; // match the short side
            joystickZone.anchorMin = Vector2.zero;
            joystickZone.anchorMax = p ? new Vector2(0.6f, 0.4f) : new Vector2(0.4f, 0.7f);
            joystickZone.offsetMin = joystickZone.offsetMax = Vector2.zero;
            joystick.ResetRest(p ? new Vector2(200, 260) : new Vector2(220, 220));
            toastRect.anchoredPosition = new Vector2(0, p ? -200 : -160);
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

        static Image AddCircle(GameObject go, Color color)
        {
            var img = go.AddComponent<Image>();
            img.sprite = CircleSprite();
            img.color = color;
            return img;
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
