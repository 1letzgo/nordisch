using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>Low-poly trees from natural=tree nodes, tree rows and scattered inside parks / woods.</summary>
    public static class PropPlacer
    {
        public static int PlaceTrees(OsmData data, MeshSink sink, float half, System.Random rng)
        {
            var positions = new List<Vector2>();

            foreach (var n in data.nodes)
                if (n.tags.Is("natural", "tree"))
                    positions.Add(data.projection.ToLocal(n.lat, n.lon));

            foreach (var w in data.ways)
            {
                if (!w.tags.Is("natural", "tree_row")) continue;
                for (int i = 0; i < w.points.Count - 1; i++)
                {
                    Vector2 a = w.points[i], b = w.points[i + 1];
                    float len = Vector2.Distance(a, b);
                    for (float d = 0; d < len; d += 9f) positions.Add(Vector2.Lerp(a, b, d / len));
                }
            }

            // Footprints that must stay free of scattered trees.
            var blockers = new List<List<Vector2>>();
            foreach (var p in data.polygons)
                if (p.tags.Has("building") || p.tags.Is("natural", "water") || p.tags.Has("water"))
                    blockers.Add(PolygonTriangulator.Clean(p.outer));

            foreach (var poly in data.polygons)
            {
                float spacing;
                if (poly.tags.Is("landuse", "forest") || poly.tags.Is("natural", "wood")) spacing = 9f;
                else if (poly.tags.Is("leisure", "park") || poly.tags.Is("landuse", "cemetery")) spacing = 18f;
                else if (poly.tags.Is("natural", "scrub") || poly.tags.Is("natural", "heath")) spacing = 22f;
                else continue;
                var ring = GeoUtil.ClipToSquare(PolygonTriangulator.Clean(poly.outer), half);
                if (ring.Count < 3) continue;
                var bounds = GeoUtil.Bounds(ring);
                for (float x = bounds.xMin; x < bounds.xMax; x += spacing)
                for (float y = bounds.yMin; y < bounds.yMax; y += spacing)
                {
                    var p = new Vector2(x + (float)rng.NextDouble() * spacing * 0.8f, y + (float)rng.NextDouble() * spacing * 0.8f);
                    if (!OsmData.PointInRing(p, ring)) continue;
                    bool inHole = false;
                    foreach (var h in poly.holes) if (OsmData.PointInRing(p, h)) { inHole = true; break; }
                    if (inHole || IsBlocked(p, blockers)) continue;
                    positions.Add(p);
                }
            }

            int count = 0;
            foreach (var p in positions)
            {
                if (Mathf.Abs(p.x) > half || Mathf.Abs(p.y) > half) continue;
                Tree(sink.Get(WorldStyle.MatTrees, p), p, rng);
                count++;
            }
            return count;
        }

        static bool IsBlocked(Vector2 p, List<List<Vector2>> blockers)
        {
            foreach (var b in blockers)
            {
                if (b.Count < 3) continue;
                if (OsmData.PointInRing(p, b)) return true;
            }
            return false;
        }

        static void Tree(MeshSink.Batch b, Vector2 p, System.Random rng)
        {
            float scale = 0.8f + (float)rng.NextDouble() * 0.6f;
            float trunkH = 2.2f * scale, trunkR = 0.22f * scale;
            float crownR = 2.2f * scale, crownH = 4.2f * scale;
            float rot = (float)rng.NextDouble() * Mathf.PI * 2f;
            var leaves = WorldStyle.Tint(WorldStyle.TreeLeaves, rng, 0.12f);
            var basePos = new Vector3(p.x, 0f, p.y);

            Prism(b, basePos, trunkR, 0f, trunkH + 0.4f, 5, rot, WorldStyle.TreeTrunk);
            // Crown: low-poly double cone ("diamond") – reads well from a 3rd-person camera.
            bool conifer = rng.NextDouble() < 0.25;
            int sides = 6;
            float midY = trunkH + crownH * (conifer ? 0.15f : 0.4f);
            float topY = trunkH + crownH * (conifer ? 1.25f : 1f);
            var top = basePos + Vector3.up * topY;
            var bottom = basePos + Vector3.up * trunkH;
            for (int i = 0; i < sides; i++)
            {
                float a0 = rot + i * Mathf.PI * 2f / sides, a1 = rot + (i + 1) * Mathf.PI * 2f / sides;
                var r0 = basePos + new Vector3(Mathf.Cos(a0) * crownR, midY, Mathf.Sin(a0) * crownR);
                var r1 = basePos + new Vector3(Mathf.Cos(a1) * crownR, midY, Mathf.Sin(a1) * crownR);
                MeshSink.Tri(b, r0, top, r1, leaves);
                MeshSink.Tri(b, r0, bottom, r1, WorldStyle.Tint(leaves, rng, 0.05f));
            }
        }

        static void Prism(MeshSink.Batch b, Vector3 basePos, float r, float y0, float y1, int sides, float rot, Color32 c)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = rot + i * Mathf.PI * 2f / sides, a1 = rot + (i + 1) * Mathf.PI * 2f / sides;
                var p0 = basePos + new Vector3(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r);
                var p1 = basePos + new Vector3(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r);
                // Increasing angle = CCW from above, so p0->p1 matches building outer-ring wall winding.
                MeshSink.Quad(b, p0 + Vector3.up * y0, p0 + Vector3.up * y1, p1 + Vector3.up * y1, p1 + Vector3.up * y0, c);
            }
        }
    }
}
