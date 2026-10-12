using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Per-house tweaks from Data/&lt;area&gt;.buildings.json for OSM-built houses (<see cref="BuildingBuilder"/>):
    /// <code>{ "buildings": [ { "address": "Alte Weide 4", "wall": "#9E4834", "brick": true,
    ///                         "gableWindows": 2, "gableWindowFloor": 1 } ] }</code>
    /// Match by <c>address</c> ("street number", as in addr:street/addr:housenumber) or <c>osmId</c> (way id).
    /// <c>gableWindows</c> puts that many windows side by side, centred on both gable ends, on floor
    /// <c>gableWindowFloor</c> (0 = ground floor, 1 = 1. OG …).
    /// </summary>
    public static class BuildingOverrides
    {
        public class Entry
        {
            public string address;
            public long osmId;
            public Color32? wall;
            public bool brick;
            public int gableWindows;
            public int gableWindowFloor = 1;
            public bool used;
        }

        static readonly List<Entry> entries = new List<Entry>();

        public static void Load(string path)
        {
            entries.Clear();
            if (!File.Exists(path)) return;
            var root = MiniJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
            if (root == null || !(root.TryGetValue("buildings", out var list) && list is List<object> items)) return;
            foreach (Dictionary<string, object> o in items)
            {
                var e = new Entry();
                if (o.TryGetValue("address", out var a)) e.address = Normalize((string)a);
                if (o.TryGetValue("osmId", out var id) && id is double d) e.osmId = (long)d;
                if (o.TryGetValue("wall", out var w) && ColorUtility.TryParseHtmlString((string)w, out var c)) e.wall = c;
                if (o.TryGetValue("brick", out var b) && b is bool bb) e.brick = bb;
                if (o.TryGetValue("gableWindows", out var gw) && gw is double g) e.gableWindows = (int)g;
                if (o.TryGetValue("gableWindowFloor", out var gf) && gf is double f) e.gableWindowFloor = (int)f;
                entries.Add(e);
            }
        }

        public static Entry Find(OsmPolygon poly)
        {
            if (entries.Count == 0) return null;
            string street = poly.tags.Get("addr:street"), number = poly.tags.Get("addr:housenumber");
            string address = street != null && number != null ? Normalize(street + " " + number) : null;
            foreach (var e in entries)
                if ((e.osmId != 0 && e.osmId == poly.id) || (address != null && e.address == address))
                {
                    e.used = true;
                    return e;
                }
            return null;
        }

        /// <summary>Warns about entries that matched no house (typo, or the house comes from LoD2).</summary>
        public static void ReportUnused()
        {
            foreach (var e in entries)
                if (!e.used) Debug.LogWarning($"[OSM] buildings.json: no OSM-built house for '{e.address ?? e.osmId.ToString()}'");
        }

        static string Normalize(string s) => string.Join(" ", s.ToLowerInvariant().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries));
    }
}
