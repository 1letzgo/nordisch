using System.Collections.Generic;
using System.IO;
using LeGamez.World;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    public class OsmNode
    {
        public long id;
        public double lat, lon;
        public Dictionary<string, string> tags;
    }

    public class OsmWay
    {
        public long id;
        public List<Vector2> points = new List<Vector2>(); // projected XZ
        public Dictionary<string, string> tags;
        public bool IsClosed => points.Count > 3 && (points[0] - points[points.Count - 1]).sqrMagnitude < 0.01f;
    }

    public class OsmPolygon
    {
        public long id;
        public List<Vector2> outer;
        public List<List<Vector2>> holes = new List<List<Vector2>>();
        public Dictionary<string, string> tags;
    }

    public class OsmAreaMeta
    {
        public string id, name, ground, attribution;
        public double lat, lon;
        public float sizeM;
        /// <summary>Playable rectangle in local metres (from "bounds" [xMin,xMax,zMin,zMax] or a centred size_m square).</summary>
        public AreaRect bounds;
        public List<string> vehicles = new List<string>();
    }

    /// <summary>Overpass JSON (out geom) loaded and projected to local metres.</summary>
    public class OsmData
    {
        public OsmAreaMeta meta;
        public GeoProjection projection;
        public readonly List<OsmNode> nodes = new List<OsmNode>();
        public readonly List<OsmWay> ways = new List<OsmWay>();
        /// <summary>Closed ways and multipolygon relations, as polygons.</summary>
        public readonly List<OsmPolygon> polygons = new List<OsmPolygon>();

        static readonly Dictionary<string, string> EmptyTags = new Dictionary<string, string>();

        public static OsmData Load(string path)
        {
            var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(path));
            var data = new OsmData();

            var m = (Dictionary<string, object>)root["legamez"];
            var center = (List<object>)m["center"];
            data.meta = new OsmAreaMeta
            {
                id = (string)m["id"],
                name = (string)m["name"],
                ground = m.TryGetValue("ground", out var g) ? (string)g : "grass",
                attribution = (string)m["attribution"],
                lat = (double)center[0],
                lon = (double)center[1],
                sizeM = (float)(double)m["size_m"],
            };
            foreach (var v in (List<object>)m["vehicles"]) data.meta.vehicles.Add((string)v);
            if (m.TryGetValue("bounds", out var bObj) && bObj is List<object> bl && bl.Count == 4)
                data.meta.bounds = new AreaRect((float)(double)bl[0], (float)(double)bl[1], (float)(double)bl[2], (float)(double)bl[3]);
            else
                data.meta.bounds = AreaRect.Square(data.meta.sizeM * 0.5f);
            data.projection = new GeoProjection(data.meta.lat, data.meta.lon);

            foreach (Dictionary<string, object> el in (List<object>)root["elements"])
            {
                var type = (string)el["type"];
                var id = (long)(double)el["id"];
                var tags = ReadTags(el);
                switch (type)
                {
                    case "node":
                        data.nodes.Add(new OsmNode { id = id, lat = (double)el["lat"], lon = (double)el["lon"], tags = tags });
                        break;
                    case "way":
                    {
                        if (!el.TryGetValue("geometry", out var geom) || geom == null) break;
                        var way = new OsmWay { id = id, tags = tags, points = data.Project((List<object>)geom) };
                        if (way.points.Count < 2) break;
                        data.ways.Add(way);
                        if (way.IsClosed)
                            data.polygons.Add(new OsmPolygon { id = id, outer = way.points, tags = tags });
                        break;
                    }
                    case "relation":
                        data.ReadMultipolygon(id, el, tags);
                        break;
                }
            }
            return data;
        }

        List<Vector2> Project(List<object> geom)
        {
            var pts = new List<Vector2>(geom.Count);
            foreach (var o in geom)
            {
                if (o == null) continue; // Overpass emits null for nodes outside bbox clipping
                var p = (Dictionary<string, object>)o;
                pts.Add(projection.ToLocal((double)p["lat"], (double)p["lon"]));
            }
            return pts;
        }

        static Dictionary<string, string> ReadTags(Dictionary<string, object> el)
        {
            if (!el.TryGetValue("tags", out var t) || t == null) return EmptyTags;
            var result = new Dictionary<string, string>();
            foreach (var kv in (Dictionary<string, object>)t) result[kv.Key] = kv.Value as string ?? kv.Value?.ToString();
            return result;
        }

        void ReadMultipolygon(long id, Dictionary<string, object> el, Dictionary<string, string> tags)
        {
            if (!tags.TryGetValue("type", out var t) || t != "multipolygon") return;
            if (!el.TryGetValue("members", out var membersObj)) return;

            var outers = new List<List<Vector2>>();
            var inners = new List<List<Vector2>>();
            foreach (Dictionary<string, object> mem in (List<object>)membersObj)
            {
                if ((string)mem["type"] != "way" || !mem.TryGetValue("geometry", out var geom) || geom == null) continue;
                var pts = Project((List<object>)geom);
                if (pts.Count < 2) continue;
                var role = mem.TryGetValue("role", out var r) ? (string)r : "outer";
                (role == "inner" ? inners : outers).Add(pts);
            }

            foreach (var ring in StitchRings(outers))
            {
                var poly = new OsmPolygon { id = id, outer = ring, tags = tags };
                var outerRing = PolygonTriangulator.Clean(ring);
                foreach (var hole in StitchRings(inners))
                    if (hole.Count > 0 && PointInRing(hole[0], outerRing)) poly.holes.Add(hole);
                polygons.Add(poly);
            }
        }

        /// <summary>Joins open way segments into closed rings.</summary>
        static List<List<Vector2>> StitchRings(List<List<Vector2>> segments)
        {
            var rings = new List<List<Vector2>>();
            var open = new List<List<Vector2>>();
            foreach (var s in segments)
            {
                if (s.Count > 3 && (s[0] - s[s.Count - 1]).sqrMagnitude < 0.01f) rings.Add(new List<Vector2>(s));
                else open.Add(new List<Vector2>(s));
            }

            while (open.Count > 0)
            {
                var ring = open[0];
                open.RemoveAt(0);
                bool progress = true;
                while (progress && (ring[0] - ring[ring.Count - 1]).sqrMagnitude >= 0.01f)
                {
                    progress = false;
                    for (int i = 0; i < open.Count; i++)
                    {
                        var s = open[i];
                        Vector2 end = ring[ring.Count - 1];
                        if ((s[0] - end).sqrMagnitude < 0.01f) { ring.AddRange(s.GetRange(1, s.Count - 1)); }
                        else if ((s[s.Count - 1] - end).sqrMagnitude < 0.01f) { s.Reverse(); ring.AddRange(s.GetRange(1, s.Count - 1)); }
                        else continue;
                        open.RemoveAt(i);
                        progress = true;
                        break;
                    }
                }
                if (ring.Count > 3) rings.Add(ring); // close implicitly even if bbox-clipped
            }
            return rings;
        }

        public static bool PointInRing(Vector2 p, List<Vector2> ring)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                if (((ring[i].y > p.y) != (ring[j].y > p.y)) &&
                    p.x < (ring[j].x - ring[i].x) * (p.y - ring[i].y) / (ring[j].y - ring[i].y) + ring[i].x)
                    inside = !inside;
            }
            return inside;
        }
    }

    public static class TagExt
    {
        public static string Get(this Dictionary<string, string> tags, string key) =>
            tags != null && tags.TryGetValue(key, out var v) ? v : null;

        public static bool Has(this Dictionary<string, string> tags, string key) =>
            tags != null && tags.ContainsKey(key);

        public static bool Is(this Dictionary<string, string> tags, string key, string value) =>
            tags != null && tags.TryGetValue(key, out var v) && v == value;
    }
}
