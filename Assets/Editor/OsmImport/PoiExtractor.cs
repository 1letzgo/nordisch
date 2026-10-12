using System.Collections.Generic;
using LeGamez.Quests;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>Picks visit-worthy named places (no hotels, shops, restaurants) as stamp-book POIs.</summary>
    public static class PoiExtractor
    {
        const int MaxPerArea = 30;
        const float MergeDistance = 40f;

        struct Candidate
        {
            public PoiEntry entry;
            public int score;
        }

        public static List<PoiEntry> Extract(OsmData data, float half)
        {
            var candidates = new List<Candidate>();

            foreach (var n in data.nodes)
                TryAdd(candidates, n.tags, data.projection.ToLocal(n.lat, n.lon), "node/" + n.id, data);
            foreach (var w in data.ways)
                TryAdd(candidates, w.tags, w.IsClosed ? GeoUtil.Centroid(PolygonTriangulator.Clean(w.points)) : w.points[w.points.Count / 2], "way/" + w.id, data);

            candidates.Sort((a, b) => b.score.CompareTo(a.score));
            var result = new List<PoiEntry>();
            foreach (var c in candidates)
            {
                var p = c.entry.position;
                if (Mathf.Abs(p.x) > half - 10f || Mathf.Abs(p.z) > half - 10f) continue;
                bool duplicate = false;
                foreach (var r in result)
                {
                    if (r.name == c.entry.name || Vector3.Distance(r.position, p) < MergeDistance && r.category == c.entry.category)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate) continue;
                result.Add(c.entry);
                if (result.Count >= MaxPerArea) break;
            }
            return result;
        }

        static void TryAdd(List<Candidate> list, Dictionary<string, string> tags, Vector2 local, string id, OsmData data)
        {
            var name = tags.Get("name");
            if (string.IsNullOrEmpty(name) || !HasLetter(name)) return;
            if (!Classify(tags, out var category, out int score)) return;
            if (tags.Has("wikipedia") || tags.Has("wikidata")) score += 3;
            if (tags.Has("heritage")) score += 2;

            data.projection.ToGeo(new Vector3(local.x, 0, local.y), out var lat, out var lon);
            list.Add(new Candidate
            {
                score = score,
                entry = new PoiEntry
                {
                    id = id,
                    name = name,
                    category = category,
                    position = new Vector3(local.x, 0f, local.y),
                    lat = lat,
                    lon = lon,
                },
            });
        }

        static bool HasLetter(string s)
        {
            foreach (char c in s) if (char.IsLetter(c)) return true;
            return false;
        }

        static bool Classify(Dictionary<string, string> t, out PoiCategory cat, out int score)
        {
            cat = PoiCategory.Landmark;
            score = 0;
            switch (t.Get("man_made"))
            {
                case "lighthouse": cat = PoiCategory.Lighthouse; score = 10; return true;
                case "water_tower": case "windmill": case "tower": cat = PoiCategory.Landmark; score = 7; return true;
            }
            switch (t.Get("tourism"))
            {
                case "museum": case "gallery": cat = PoiCategory.Museum; score = 8; return true;
                case "viewpoint": cat = PoiCategory.Viewpoint; score = 7; return true;
                case "artwork": cat = PoiCategory.Artwork; score = 4; return true;
                case "attraction": case "zoo": case "theme_park": cat = PoiCategory.Landmark; score = 7; return true;
            }
            var historic = t.Get("historic");
            if (historic != null && historic != "no" && historic != "yes")
            {
                switch (historic)
                {
                    case "castle": case "palace": case "manor": cat = PoiCategory.Landmark; score = 10; return true;
                    case "church": case "chapel": cat = PoiCategory.Church; score = 8; return true;
                    case "monument": case "memorial": case "wayside_cross": case "milestone": cat = PoiCategory.Monument; score = 4; return true;
                    case "boundary_stone": case "district_boundary": return false;
                    default: cat = PoiCategory.Landmark; score = 5; return true;
                }
            }
            switch (t.Get("amenity"))
            {
                case "place_of_worship": cat = PoiCategory.Church; score = 7; return true;
                case "theatre": case "arts_centre": case "library": case "townhall": cat = PoiCategory.Culture; score = 6; return true;
                case "fountain": cat = PoiCategory.Artwork; score = 3; return true;
            }
            return false;
        }
    }
}
