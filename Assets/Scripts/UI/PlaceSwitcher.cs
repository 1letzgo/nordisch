using System.Collections.Generic;
using LeGamez.Core;
using LeGamez.World;
using UnityEngine;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>
    /// "Andere Orte" (from the full map): the default start points of every area we have, with its state, and the player's
    /// own start points. Tap = beam there – within the current area directly (no reload); an area not on the device yet
    /// first downloads its area file, then the world switches over.
    /// </summary>
    public class PlaceSwitcher : MonoBehaviour
    {
        static PlaceSwitcher instance;

        class Row
        {
            public MapStore.Map map;
            public StartPoints.Point point;
            public Text status;
            public RectTransform fill;
        }

        readonly List<Row> rows = new List<Row>();
        readonly Dictionary<Texture2D, Sprite> sprites = new Dictionary<Texture2D, Sprite>();
        RectTransform list;
        int builtRevision = -1;

        public static void Open(Transform canvasRoot)
        {
            if (instance == null)
            {
                var rt = UiKit.Rect("PlaceSwitcher", canvasRoot);
                UiKit.Stretch(rt);
                instance = rt.gameObject.AddComponent<PlaceSwitcher>();
                instance.BuildFrame(rt);
            }
            instance.gameObject.SetActive(true);
            instance.transform.SetAsLastSibling();
            instance.builtRevision = -1;
        }

        public static bool CloseIfOpen()
        {
            if (instance == null || !instance.gameObject.activeSelf) return false;
            instance.Close();
            return true;
        }

        void Close()
        {
            gameObject.SetActive(false);
        }

        void BuildFrame(RectTransform root)
        {
            var dim = root.gameObject.AddComponent<Image>();
            dim.color = new Color(UiKit.Navy.r, UiKit.Navy.g, UiKit.Navy.b, 0.92f); // blocks taps on the game
            UiKit.Label(root, "Andere Orte", 56, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold)
                .Place(new Vector2(0.5f, 1f), new Vector2(0, -150), new Vector2(900, 80));
            UiKit.Label(root, "Tippe auf einen Ort, um dorthin zu beamen", 30, new Color(1, 1, 1, 0.8f), TextAnchor.MiddleCenter)
                .Place(new Vector2(0.5f, 1f), new Vector2(0, -215), new Vector2(900, 50));

            var close = UiKit.Rect("Close", root, new Vector2(1f, 1f), new Vector2(-60, -60), new Vector2(84, 84));
            var closeImg = UiKit.Panel(close.gameObject, new Color(1, 1, 1, 0.95f), UiKit.Circle);
            var closeBtn = close.gameObject.AddComponent<Button>();
            closeBtn.targetGraphic = closeImg;
            closeBtn.onClick.AddListener(Close);
            UiKit.Stretch(UiKit.Label(close, "×", 54, UiKit.Navy, TextAnchor.MiddleCenter, FontStyle.Bold).rectTransform);

            // Scrollable list (grows with every new area).
            var view = UiKit.Rect("View", root);
            view.anchorMin = new Vector2(0.5f, 0f); view.anchorMax = new Vector2(0.5f, 1f);
            view.sizeDelta = new Vector2(900, -320);
            view.anchoredPosition = new Vector2(0, -130);
            view.gameObject.AddComponent<RectMask2D>();
            var hit = view.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);
            list = UiKit.Rect("List", view);
            list.anchorMin = new Vector2(0, 1); list.anchorMax = new Vector2(1, 1);
            list.pivot = new Vector2(0.5f, 1f);
            list.anchoredPosition = Vector2.zero;
            var scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.content = list;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
        }

        void Update()
        {
            if (builtRevision != MapStore.Revision + StartPoints.Revision * 100000) Rebuild();
            foreach (var r in rows) Refresh(r);
        }

        void Rebuild()
        {
            builtRevision = MapStore.Revision + StartPoints.Revision * 100000;
            foreach (Transform c in list) Destroy(c.gameObject);
            rows.Clear();
            var area = AreaInfo.Current;
            string current = area != null ? area.areaId : null;
            const float h = 170f, gap = 24f;
            float y = 0f;
            foreach (var m in MapStore.Maps)
            {
                if (m.Id == current)
                {
                    // Other start points of the area we are in (e.g. Oldenburg while in Westerstede) – the area's own data
                    // knows them even when the catalog doesn't. The one we are nearest to is "here" and left out.
                    int here = area.NearestStart(NavState.Player != null ? NavState.Player.position : Vector3.zero);
                    for (int k = 0; k < area.starts.Count; k++)
                    {
                        if (k == here) continue;
                        rows.Add(BuildRow(m, StartPoints.Default(m.Id, k, area.starts[k].name), y, h));
                        y -= h + gap;
                    }
                    continue;
                }
                for (int k = 0; k < m.StartCount; k++)
                {
                    rows.Add(BuildRow(m, StartPoints.Default(m.Id, k, m.StartName(k)), y, h));
                    y -= h + gap;
                }
            }
            // Own start points – also those in the current area (beam there directly).
            foreach (var p in StartPoints.All)
            {
                var m = MapStore.Maps.Find(x => x.Id == p.areaId);
                if (m == null) continue;
                rows.Add(BuildRow(m, p, y, h));
                y -= h + gap;
            }
            list.sizeDelta = new Vector2(0, -y);
            if (rows.Count == 0)
                UiKit.Label(list, "Noch keine anderen Orte verfügbar.", 34, Color.white, TextAnchor.MiddleCenter)
                    .Place(new Vector2(0.5f, 1f), new Vector2(0, -60), new Vector2(880, 60));
        }

        Row BuildRow(MapStore.Map m, StartPoints.Point point, float y, float h)
        {
            var row = new Row { map = m, point = point };
            bool own = point != null && !point.IsDefault;
            var tex = own ? m.thumb : m.StartThumb(point != null ? point.StartIndex : 0);
            var card = UiKit.Rect($"Row {point?.name ?? m.entry.name}", list, new Vector2(0.5f, 1f), new Vector2(0, y), new Vector2(880, h));
            var bg = UiKit.Panel(card.gameObject, Color.white);
            var btn = card.gameObject.AddComponent<Button>();
            btn.targetGraphic = bg;
            btn.onClick.AddListener(() => Choose(m, point));

            var thumb = UiKit.Rect("Thumb", card, new Vector2(0f, 0.5f), new Vector2(14, 0), new Vector2(220, h - 28));
            thumb.gameObject.AddComponent<RectMask2D>();
            var img = UiKit.Rect("Img", thumb);
            UiKit.Stretch(img);
            var image = img.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            if (tex != null)
            {
                if (!sprites.TryGetValue(tex, out var sp))
                    sprites[tex] = sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                image.sprite = sp;
                var fit = img.gameObject.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
                fit.aspectRatio = sp.rect.width / sp.rect.height;
            }
            else image.color = UiKit.Sea;

            bool island = m.HasVehicle("carriage");
            UiKit.Label(card, point != null ? point.name : m.entry.name, 44, UiKit.Ink, TextAnchor.UpperLeft, FontStyle.Bold)
                .Place(new Vector2(0f, 1f), new Vector2(262, -24), new Vector2(580, 56));
            string kind = island ? "Insel" : "Stadt";
            string sub = own ? $"{m.entry.name} · eigener Startpunkt"
                : point != null && point.name != m.entry.name ? $"{kind} · Gebiet {m.entry.name}" : kind;
            UiKit.Label(card, sub, 26, UiKit.InkSoft, TextAnchor.UpperLeft)
                .Place(new Vector2(0f, 1f), new Vector2(262, -80), new Vector2(580, 36));
            row.status = UiKit.Label(card, "", 28, UiKit.Navy, TextAnchor.UpperLeft, FontStyle.Bold)
                .Place(new Vector2(0f, 1f), new Vector2(262, -118), new Vector2(580, 40));

            var track = UiKit.Rect("Progress", card, new Vector2(0f, 0f), new Vector2(262, 14), new Vector2(580, 8));
            track.gameObject.AddComponent<Image>().color = UiKit.Track;
            row.fill = UiKit.Rect("Fill", track);
            row.fill.anchorMin = Vector2.zero; row.fill.anchorMax = new Vector2(0, 1);
            row.fill.offsetMin = row.fill.offsetMax = Vector2.zero;
            row.fill.gameObject.AddComponent<Image>().color = UiKit.Mustard;
            return row;
        }

        void Refresh(Row r)
        {
            var m = r.map;
            string text;
            if (InCurrentArea(r.point)) { r.status.text = "Hierhin beamen"; r.fill.parent.gameObject.SetActive(false); return; }
            switch (m.status)
            {
                case MapStore.Status.Installed:
                case MapStore.Status.UpdateAvailable: text = "Bereit – hierhin beamen"; break;
                case MapStore.Status.Available:
                case MapStore.Status.UpdateRequired: text = "Hierhin beamen"; break;
                case MapStore.Status.Queued: text = "Wartet …"; break;
                case MapStore.Status.Downloading: text = $"Lädt … {Mathf.RoundToInt(m.progress * 100f)} %"; break;
                case MapStore.Status.Installing: text = "Wird eingerichtet …"; break;
                case MapStore.Status.Unavailable: text = "Neue App-Version nötig"; break;
                case MapStore.Status.Pending: text = "Wird gerade aktualisiert"; break;
                case MapStore.Status.Failed: text = (m.error ?? "Fehler") + " – nochmal tippen"; break;
                default: text = ""; break;
            }
            r.status.text = text;
            bool loading = m.status == MapStore.Status.Downloading || m.status == MapStore.Status.Installing;
            r.fill.parent.gameObject.SetActive(loading);
            r.fill.anchorMax = new Vector2(Mathf.Clamp01(m.progress), 1f);
        }

        void Choose(MapStore.Map m, StartPoints.Point point)
        {
            if (m.Busy && !InCurrentArea(point)) return;
            Go(m, point);
        }

        /// <summary>A start point in the area being played: reachable by beaming, no area switch.</summary>
        static bool InCurrentArea(StartPoints.Point point)
        {
            var area = AreaInfo.Current;
            if (point == null || area == null || point.areaId != area.areaId) return false;
            return point.IsDefault ? point.StartIndex < area.starts.Count : point.areaKey == area.MemoryKey && area.Contains(point.position, 2f);
        }

        void Go(MapStore.Map m, StartPoints.Point point)
        {
            if (InCurrentArea(point))
            {
                var area = AreaInfo.Current;
                var s = point.IsDefault ? area.starts[point.StartIndex] : null;
                gameObject.SetActive(false);
                FullMap.BeamTo(transform.parent, s != null ? s.position : point.position, s != null ? s.yaw : point.yaw);
                return;
            }
            var flash = BeamFlash.Create(transform.parent);
            flash.StartCoroutine(flash.Fade(0f, 1f, 0.25f)); // world scene reload follows; the loading screen takes over
            gameObject.SetActive(false);
            MapStore.Start(m, point); // small area file if needed, then the world switches; tiles follow on demand
        }
    }
}
