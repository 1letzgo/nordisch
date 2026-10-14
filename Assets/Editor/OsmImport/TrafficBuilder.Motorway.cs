using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Blue motorway signs at every exit (highway=motorway_junction in traffic.json, on the carriageway where the ramp
    /// leaves): an advance sign 500 m before (junction number, name, destinations of the ramp, "500 m") and the
    /// "Ausfahrt" sign in the gore. Motorways in OSM are one-way along their points unless oneway=-1.
    /// </summary>
    public static partial class TrafficBuilder
    {
        static readonly Color32 MotorwayBlue = new Color32(16, 74, 150, 255);

        static int MotorwaySigns(List<Road> roads, List<(Vector2 p, Dictionary<string, string> t)> nodes)
        {
            var motorways = new List<Road>();
            var ramps = new List<Road>();
            foreach (var r in roads)
            {
                string hw = r.way.tags.Get("highway");
                if (hw == "motorway") motorways.Add(r);
                else if (hw == "motorway_link") ramps.Add(r);
            }
            if (motorways.Count == 0) return 0;
            // Carriageway chains: which way ends where another one starts (walking back to the advance sign).
            var endsAt = new Dictionary<Vector2Int, Road>();
            foreach (var r in motorways) endsAt[Key(r.way.points[r.way.points.Count - 1])] = r;

            int count = 0;
            foreach (var (p, t) in nodes)
            {
                if (!t.Is("highway", "motorway_junction")) continue;
                Road road = null; int vi = -1;
                foreach (var r in motorways)
                {
                    var pts = r.way.points;
                    for (int i = 0; i < pts.Count; i++)
                        if ((pts[i] - p).sqrMagnitude < 0.25f) { road = r; vi = i; break; }
                    if (road != null) break;
                }
                if (road == null || road.oneway == -1) continue;
                // The ramp that leaves here (an entering ramp ends here instead – no exit on this carriageway).
                Road ramp = null;
                foreach (var r in ramps)
                    if ((r.way.points[0] - p).sqrMagnitude < 0.25f) { ramp = r; break; }
                if (ramp == null) continue;

                string name = t.Get("name") ?? "", number = t.Get("ref") ?? "";
                string dest = ramp.way.tags.Get("destination") ?? ramp.way.tags.Get("exit_to") ?? "";
                var parts = dest.Split(';');
                dest = parts.Length > 2 ? parts[0] + " · " + parts[1] : string.Join(" · ", parts);
                if (dest == name) dest = "";

                // Advance sign 500 m before the junction, right of the hard shoulder, facing the traffic.
                if (BackAlong(road, vi, 500f, endsAt, out var q, out var tan))
                {
                    var pos = q + Right(tan) * (road.Hw + 3.2f);
                    if (bounds.Contains(pos, 3f) && !carriageways.IsBlocked(pos, 1.6f))
                    {
                        Board(pos, -tan, 3.8f, 2.1f, s =>
                        {
                            s.Rect(0f, 0f, 3.8f, 2.1f, White);
                            s.Rect(0f, 0f, 3.68f, 1.98f, MotorwayBlue);
                            if (number.Length > 0)
                            {
                                s.Rect(-1.45f, 0.58f, 0.62f, 0.5f, White);
                                s.Text(number, 0.58f, 0.34f, Black, -1.45f);
                            }
                            // Long names get smaller letters (~0.55 × height per character), so they stay on the board.
                            s.Text(name, 0.6f, Fit(name, 0.34f, number.Length > 0 ? 2.6f : 3.4f), White, number.Length > 0 ? 0.35f : 0f);
                            if (dest.Length > 0) s.Text(dest, 0.02f, Fit(dest, 0.26f, 3.4f), White);
                            s.Text("500 m", -0.62f, 0.24f, White, 1.15f);
                        });
                        count++;
                    }
                }
                // "Ausfahrt" in the gore, ~50 m along the ramp on its left.
                var arm = new Arm { road = ramp, vi = 0, dirSign = 1, dir = (ramp.way.points[1] - ramp.way.points[0]).normalized };
                if (Along(arm, 50f, out var g, out var gt))
                {
                    var pos = g - Right(gt) * (ramp.Hw + 2.2f);
                    PlacePole(pos, -gt, s =>
                    {
                        s.Rect(0f, 0f, 1.5f, 0.6f, White);
                        s.Rect(0f, 0f, 1.42f, 0.52f, MotorwayBlue);
                        s.Text(number.Length > 0 ? "Ausfahrt " + number : "Ausfahrt", 0f, 0.2f, White);
                    });
                    count++;
                }
            }
            count += MapEndSigns(motorways, endsAt);
            return count;
        }

        /// <summary>Where the motorway leaves the map (<see cref="MotorwayBuilder.DeadEnds"/>, closed by a barrier): a yellow
        /// "Ende des Kartenbereichs" board above the barrier and, for the traffic, an advance board 400 m before.</summary>
        static int MapEndSigns(List<Road> motorways, Dictionary<Vector2Int, Road> endsAt)
        {
            int count = 0;
            void Panel(Plate s, string last)
            {
                s.Rect(0f, 0f, 3.4f, 1.5f, Black);
                s.Rect(0f, 0f, 3.3f, 1.4f, Yellow);
                s.Text("Ende des", last != null ? 0.38f : 0.25f, 0.3f, Black);
                s.Text("Kartenbereichs", last != null ? 0.02f : -0.2f, 0.3f, Black);
                if (last != null) s.Text(last, -0.42f, 0.26f, Black);
            }
            foreach (var d in MotorwayBuilder.DeadEnds)
            {
                var pos = d.point - d.dir * 1.6f; // just behind the barrier
                if (!bounds.Contains(pos, 2f)) continue;
                Board(pos, -d.dir, 3.4f, 1.5f, s => Panel(s, null));
                count++;
                if (d.entry || !endsAt.TryGetValue(Key(d.point), out var road) || road.oneway == -1) continue;
                if (!BackAlong(road, road.way.points.Count - 1, 400f, endsAt, out var q, out var tan)) continue;
                var at = q + Right(tan) * (road.Hw + 3.2f);
                if (!bounds.Contains(at, 3f) || carriageways.IsBlocked(at, 1.6f)) continue;
                Board(at, -tan, 3.4f, 1.5f, s => Panel(s, "400 m"));
                count++;
            }
            return count;
        }

        static float Fit(string text, float height, float width) => Mathf.Min(height, width / Mathf.Max(1, text.Length) / 0.55f);

        /// <summary>Point <paramref name="dist"/> metres before vertex <paramref name="vi"/> against the driving direction,
        /// following the carriageway into the ways before it; tan = driving direction there.</summary>
        static bool BackAlong(Road road, int vi, float dist, Dictionary<Vector2Int, Road> endsAt, out Vector2 p, out Vector2 tan)
        {
            p = road.way.points[vi]; tan = Vector2.up;
            float left = dist;
            int guard = 0;
            while (guard++ < 200)
            {
                var pts = road.way.points;
                for (int i = vi; i > 0; i--)
                {
                    var seg = pts[i] - pts[i - 1];
                    float len = seg.magnitude;
                    if (len < 1e-4f) continue;
                    tan = seg / len;
                    if (left <= len) { p = pts[i] - tan * left; return true; }
                    left -= len;
                }
                if (!endsAt.TryGetValue(Key(pts[0]), out var prev) || prev == road) return false;
                road = prev;
                vi = prev.way.points.Count - 1;
            }
            return false;
        }

        /// <summary>Large sign board on two posts (plate centre 3 m above the ground), front facing <paramref name="facing"/>.</summary>
        static void Board(Vector2 pos, Vector2 facing, float w, float h, System.Action<Plate> front)
        {
            facing = facing.normalized;
            var right = new Vector2(facing.y, -facing.x);
            var b = sink.Get(WorldStyle.MatProps, pos);
            float centre = 1.4f + h * 0.5f;
            foreach (float s in new[] { -0.35f, 0.35f })
                FurniturePlacer.Cylinder(b, V(pos + right * (s * w)), 0.07f, centre + h * 0.5f, 6, Pole, Pole);
            front(new Plate(b, V(pos, centre), facing, 0.09f));
            poles.Add(pos);
        }
    }
}
