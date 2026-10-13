using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Per-house tweaks from Data/&lt;area&gt;.buildings.json (written by the map editor or by hand), for LoD2 and
    /// OSM-built houses alike:
    /// <code>{ "buildings": [ { "address": "Alte Weide 4", "wall": "#9E4834", "brick": true, "roof": "#3E3E42",
    ///                         "gableWindows": 2, "gableWindowFloor": 1, "windows": false, "hidden": false,
    ///                         "door": "street"|"none"|"N"|"E"|"S"|"W", "doorColor": "#264E3C",
    ///                         "roofShape": "gabled"|"hipped"|"flat" (OSM houses), "chimney": true, "floors": 2 } ] }</code>
    /// <c>floors</c>: full storeys up to the eaves (3 m each); LoD2 walls are stretched, the roof moves along.
    /// Match by <c>id</c> (LoD2 gml id, or "osm:&lt;way id&gt;") or <c>address</c> ("Street 12", official ALKIS
    /// address from LoD2, addr:* from OSM) – an address hits every part of that building.
    /// <c>gableWindows</c>: that many windows side by side, centred on each gable end, on floor
    /// <c>gableWindowFloor</c> (0 = ground floor, 1 = 1. OG …).
    /// Also collects every built house (id, addresses, footprint) for the map editor, see <see cref="SaveCatalog"/>.
    /// </summary>
    public static class BuildingOverrides
    {
        public class Entry
        {
            public string id;
            public string address;
            public Color32? wall;
            public Color32? roof;
            public bool brick;
            public int gableWindows;
            public int gableWindowFloor = 1;
            public bool? windows;      // false = no facade windows
            public bool hidden;        // house not built (demolished / duplicate)
            public string door;        // null/"street" = automatic, "none", or "N"/"E"/"S"/"W"
            public Color32? doorColor;
            public string roofShape;   // OSM houses: "gabled" / "hipped" / "flat"
            public bool? chimney;
            public int floors;         // full storeys up to the eaves (0 = as measured / estimated)
            public bool used;
        }

        struct CatalogItem { public string id, src; public List<string> addr; public List<Vector2> ring; public int floors; }

        static readonly List<Entry> entries = new List<Entry>();
        static readonly List<CatalogItem> catalog = new List<CatalogItem>();

        public static void Load(string path)
        {
            entries.Clear();
            catalog.Clear();
            if (!File.Exists(path)) return;
            var root = MiniJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
            if (root == null || !(root.TryGetValue("buildings", out var list) && list is List<object> items)) return;
            foreach (Dictionary<string, object> o in items)
            {
                var e = new Entry();
                if (o.TryGetValue("id", out var id) && id is string ids) e.id = ids;
                if (o.TryGetValue("osmId", out var oid) && oid is double d) e.id = "osm:" + (long)d;
                if (o.TryGetValue("address", out var a) && a is string adr) e.address = Normalize(adr);
                if (o.TryGetValue("wall", out var w) && w is string ws && ColorUtility.TryParseHtmlString(ws, out var c)) e.wall = c;
                if (o.TryGetValue("roof", out var r) && r is string rs && ColorUtility.TryParseHtmlString(rs, out var rc)) e.roof = rc;
                if (o.TryGetValue("brick", out var b) && b is bool bb) e.brick = bb;
                if (o.TryGetValue("gableWindows", out var gw) && gw is double g) e.gableWindows = (int)g;
                if (o.TryGetValue("gableWindowFloor", out var gf) && gf is double f) e.gableWindowFloor = (int)f;
                if (o.TryGetValue("windows", out var wi) && wi is bool wb) e.windows = wb;
                if (o.TryGetValue("hidden", out var hi) && hi is bool hb) e.hidden = hb;
                if (o.TryGetValue("door", out var dr) && dr is string ds) e.door = ds;
                if (o.TryGetValue("doorColor", out var dc) && dc is string dcs && ColorUtility.TryParseHtmlString(dcs, out var dcc)) e.doorColor = dcc;
                if (o.TryGetValue("roofShape", out var rs2) && rs2 is string rss) e.roofShape = rss;
                if (o.TryGetValue("chimney", out var ch) && ch is bool chb) e.chimney = chb;
                if (o.TryGetValue("floors", out var fl) && fl is double fld) e.floors = Mathf.Clamp((int)fld, 0, 12);
                entries.Add(e);
            }
        }

        /// <summary>Override for a house with this id / these addresses; an id match wins over an address match.</summary>
        public static Entry Find(string id, IList<string> addresses)
        {
            Entry byAddress = null;
            foreach (var e in entries)
            {
                if (e.id != null && e.id == id) { e.used = true; return e; }
                if (byAddress == null && e.address != null && addresses != null)
                    foreach (var a in addresses)
                        if (Normalize(a) == e.address) { byAddress = e; break; }
            }
            if (byAddress != null) byAddress.used = true;
            return byAddress;
        }

        /// <summary>Addresses from OSM tags ("12a,12b" / "12;14" become several).</summary>
        public static List<string> OsmAddresses(Dictionary<string, string> tags)
        {
            var list = new List<string>();
            string street = tags.Get("addr:street"), number = tags.Get("addr:housenumber");
            if (street == null) return list;
            if (number == null) { list.Add(street); return list; }
            foreach (var n in number.Split(',', ';'))
                if (n.Trim().Length > 0) list.Add(street + " " + n.Trim());
            return list;
        }

        /// <summary>Footprints of every house built in this import (addressed = main house, not a shed).</summary>
        public static IEnumerable<(List<Vector2> ring, bool addressed, bool osm)> Footprints()
        {
            foreach (var c in catalog) yield return (c.ring, c.addr != null && c.addr.Count > 0, c.src == "osm");
        }

        public static void Register(string id, string src, List<string> addresses, List<Vector2> ring, int floors = 0)
        {
            if (ring == null || ring.Count < 3) return;
            catalog.Add(new CatalogItem { id = id, src = src, addr = addresses, ring = ring, floors = floors });
        }

        /// <summary>Warns about entries that matched no house (typo, or the house is outside the area).</summary>
        public static void ReportUnused()
        {
            foreach (var e in entries)
                if (!e.used) Debug.LogWarning($"[OSM] buildings.json: no house for '{e.address ?? e.id}'");
        }

        /// <summary>Every built house with id, source, addresses and footprint – the map editor's building layer.</summary>
        public static void SaveCatalog(string path)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("{\"buildings\":[\n");
            for (int i = 0; i < catalog.Count; i++)
            {
                var c = catalog[i];
                sb.Append("{\"id\":\"").Append(c.id).Append("\",\"src\":\"").Append(c.src).Append("\",\"addr\":[");
                for (int k = 0; k < (c.addr?.Count ?? 0); k++)
                    sb.Append(k > 0 ? "," : "").Append('"').Append(c.addr[k].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                sb.Append("],\"floors\":").Append(c.floors).Append(",\"ring\":[");
                for (int k = 0; k < c.ring.Count; k++)
                    sb.Append(k > 0 ? "," : "").Append(string.Format(ci, "[{0:0.#},{1:0.#}]", c.ring[k].x, c.ring[k].y));
                sb.Append(i < catalog.Count - 1 ? "]},\n" : "]}\n");
            }
            sb.Append("]}\n");
            File.WriteAllText(path, sb.ToString());
        }

        static string Normalize(string s) => string.Join(" ", s.ToLowerInvariant().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries));
    }
}
