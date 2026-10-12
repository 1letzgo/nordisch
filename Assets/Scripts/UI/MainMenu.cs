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
    /// <summary>Area picker: stylised map of north-west Lower Saxony with one pin per area.</summary>
    public class MainMenu : MonoBehaviour
    {
        // Map frame (lat/lon) that contains all current areas with some margin.
        const double MinLat = 53.05, MaxLat = 53.85, MinLon = 7.30, MaxLon = 8.35;

        static readonly Color Sea = new Color(0.33f, 0.58f, 0.78f);
        static readonly Color Land = new Color(0.62f, 0.76f, 0.50f);
        static readonly Color PinColor = new Color(0.95f, 0.36f, 0.25f);

        void Start()
        {
            Application.targetFrameRate = 60;
            var cam = Camera.main;
            if (cam != null) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Sea; }
            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
                es.AddComponent<InputSystemUIInputModule>();
#else
                es.AddComponent<StandaloneInputModule>();
#endif
            }
            catalog = AreaCatalog.Load();
        }

        AreaCatalog catalog;
        GameObject canvasGo;
        bool? builtPortrait;

        void Update()
        {
            // Rebuild on rotation so the map always fits the screen.
            bool portrait = Screen.height > Screen.width;
            if (builtPortrait == portrait) return;
            builtPortrait = portrait;
            if (canvasGo != null) Destroy(canvasGo);
            BuildUi(catalog, portrait);
        }

        void BuildUi(AreaCatalog catalog, bool portrait)
        {
            canvasGo = new GameObject("Menu", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1080);
            scaler.matchWidthOrHeight = portrait ? 0f : 1f; // short side = 1080 units

            // Map panel: mainland lower half, islands along the top edge. Scaled down to fit portrait.
            var map = Rect("Map", canvasGo.transform, new Vector2(0.5f, 0.5f), new Vector2(0, portrait ? -40 : -20), new Vector2(1300, 860));
            if (portrait) map.localScale = Vector3.one * (1020f / 1300f);
            Image(map.gameObject, Sea);
            var mainland = Rect("Mainland", map, new Vector2(0.5f, 0f), Vector2.zero, new Vector2(1300, 560));
            mainland.pivot = new Vector2(0.5f, 0f);
            mainland.anchoredPosition = Vector2.zero;
            Image(mainland.gameObject, Land);

            var title = Label(canvasGo.transform, portrait ? "LeGamez\n<size=44>Nordwest entdecken</size>" : "LeGamez – Nordwest entdecken", 64,
                new Vector2(0.5f, 1f), new Vector2(0, portrait ? -120 : -40), new Vector2(1000, portrait ? 160 : 90));
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            Label(canvasGo.transform, "Kartendaten © OpenStreetMap-Mitwirkende (ODbL)", 22, new Vector2(1f, 0f), new Vector2(-30, portrait ? 60 : 20), new Vector2(800, 30))
                .alignment = TextAnchor.LowerRight;

            if (catalog == null || catalog.areas.Count == 0)
            {
                Label(map, "Keine Gebiete importiert.", 40, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(900, 60));
                return;
            }

            foreach (var area in catalog.areas)
            {
                float x = (float)((area.lon - MinLon) / (MaxLon - MinLon));
                float y = (float)((area.lat - MinLat) / (MaxLat - MinLat));
                var pin = Rect($"Pin {area.displayName}", map, new Vector2(x, y), Vector2.zero, new Vector2(230, 110));
                var bg = Image(pin.gameObject, PinColor);
                var btn = pin.gameObject.AddComponent<Button>();
                btn.targetGraphic = bg;
                string sceneName = area.sceneName;
                btn.onClick.AddListener(() => SceneManager.LoadSceneAsync(sceneName));
                int stamps = StampBook.CountForArea(area.id);
                Label(pin, $"{area.displayName}\n<size=22>Stempel {stamps}/{area.poiCount}</size>", 30,
                    new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(230, 110)).raycastTarget = false;
            }
        }

        static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            return rt;
        }

        static Image Image(GameObject go, Color c)
        {
            var img = go.AddComponent<Image>();
            img.color = c;
            return img;
        }

        static Text Label(Transform parent, string text, int size, Vector2 anchor, Vector2 pos, Vector2 box)
        {
            var rt = Rect("Label", parent, anchor, pos, box);
            rt.pivot = anchor;
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Hud.UiFont;
            t.text = text;
            t.fontSize = size;
            t.supportRichText = true;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Color.white;
            t.raycastTarget = false;
            var shadow = rt.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.5f);
            return t;
        }
    }
}
