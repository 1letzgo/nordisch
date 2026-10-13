using System.Collections.Generic;
using LeGamez.Core;
using LeGamez.World;
using UnityEngine;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>
    /// "Andere Orte" (from the full map): every area we have, with its state. Tap = beam there – an area not on the device
    /// yet first downloads its start (area data + regions around the start point), then the world switches over.
    /// </summary>
    public class PlaceSwitcher : MonoBehaviour
    {
        static PlaceSwitcher instance;

        class Row
        {
            public MapStore.Map map;
            public Text status;
            public RectTransform fill;
        }

        readonly List<Row> rows = new List<Row>();
        readonly Dictionary<Texture2D, Sprite> sprites = new Dictionary<Texture2D, Sprite>();
        MapStore.Map pending; // download running, beam when done
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
            pending = null;
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
            if (builtRevision != MapStore.Revision) Rebuild();
            foreach (var r in rows) Refresh(r);
            // Download of the chosen place finished → go.
            if (pending != null && !pending.Busy)
            {
                var m = pending;
                pending = null;
                if (m.Playable) Go(m);
            }
        }

        void Rebuild()
        {
            builtRevision = MapStore.Revision;
            foreach (Transform c in list) Destroy(c.gameObject);
            rows.Clear();
            string current = AreaInfo.Current != null ? AreaInfo.Current.areaId : null;
            const float h = 170f, gap = 24f;
            float y = 0f;
            foreach (var m in MapStore.Maps)
            {
                if (m.Id == current) continue;
                rows.Add(BuildRow(m, y, h));
                y -= h + gap;
            }
            list.sizeDelta = new Vector2(0, -y);
            if (rows.Count == 0)
                UiKit.Label(list, "Noch keine anderen Orte verfügbar.", 34, Color.white, TextAnchor.MiddleCenter)
                    .Place(new Vector2(0.5f, 1f), new Vector2(0, -60), new Vector2(880, 60));
        }

        Row BuildRow(MapStore.Map m, float y, float h)
        {
            var row = new Row { map = m };
            var card = UiKit.Rect($"Row {m.entry.name}", list, new Vector2(0.5f, 1f), new Vector2(0, y), new Vector2(880, h));
            var bg = UiKit.Panel(card.gameObject, Color.white);
            var btn = card.gameObject.AddComponent<Button>();
            btn.targetGraphic = bg;
            btn.onClick.AddListener(() => Choose(m));

            var thumb = UiKit.Rect("Thumb", card, new Vector2(0f, 0.5f), new Vector2(14, 0), new Vector2(220, h - 28));
            thumb.gameObject.AddComponent<RectMask2D>();
            var img = UiKit.Rect("Img", thumb);
            UiKit.Stretch(img);
            var image = img.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            if (m.thumb != null)
            {
                if (!sprites.TryGetValue(m.thumb, out var sp))
                    sprites[m.thumb] = sp = Sprite.Create(m.thumb, new Rect(0, 0, m.thumb.width, m.thumb.height), new Vector2(0.5f, 0.5f));
                image.sprite = sp;
                var fit = img.gameObject.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
                fit.aspectRatio = sp.rect.width / sp.rect.height;
            }
            else image.color = UiKit.Sea;

            bool island = m.HasVehicle("carriage");
            UiKit.Label(card, m.entry.name, 44, UiKit.Ink, TextAnchor.UpperLeft, FontStyle.Bold)
                .Place(new Vector2(0f, 1f), new Vector2(262, -24), new Vector2(580, 56));
            UiKit.Label(card, island ? "Insel" : "Stadt", 26, UiKit.InkSoft, TextAnchor.UpperLeft)
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
            switch (m.status)
            {
                case MapStore.Status.Installed:
                case MapStore.Status.UpdateAvailable: text = "Bereit – hierhin beamen"; break;
                case MapStore.Status.Available: text = $"Laden und beamen · {MapStore.Megabytes(m.DownloadSize)}"; break;
                case MapStore.Status.UpdateRequired: text = $"Update und beamen · {MapStore.Megabytes(m.DownloadSize)}"; break;
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

        void Choose(MapStore.Map m)
        {
            if (m.Playable && m.status != MapStore.Status.UpdateRequired) { Go(m); return; }
            if (m.latest == null || m.Busy) return;
            pending = m;
            MapStore.Download(m);
        }

        void Go(MapStore.Map m)
        {
            var flash = BeamFlash.Create(transform.parent);
            flash.StartCoroutine(flash.Fade(0f, 1f, 0.25f)); // world scene reload follows; the loading screen takes over
            gameObject.SetActive(false);
            MapStore.Play(m);
        }
    }
}
