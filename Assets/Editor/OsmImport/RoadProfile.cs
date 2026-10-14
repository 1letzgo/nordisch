using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Smooth road surfaces. Draped ribbons follow every dip of the laser terrain (ditches crossing the road, noise, old
    /// pits at bridge ends, the 5 m facets) – on a motorway at 130 km/h that reads as potholes. Before the detailed
    /// ground is built, every road gets a longitudinal height profile along its centre line, and that profile is written
    /// into the height field itself: level across the road, eased back onto the original ground over a few metres beside
    /// it (embankment / cutting). Ribbons, ground mesh, colliders and everything lifted afterwards then agree.
    /// <list type="bullet">
    /// <item>Ways are joined into chains (straight on through a node, same rank). Chains are processed by rank
    /// (motorway → … → footway), longest first; each reads the field as the previous ones left it.</item>
    /// <item>Profile: ground every ~2 m → running median (outliers: ditches, pits) → two box filters (window per class:
    /// motorway 100 m … residential 20 m … footway 10 m) → clamped to ± a cap around the ground (paths: 0.5 m, keeps
    /// dune paths natural) → anchors → gradient limit (motorway 5 %).</item>
    /// <item>Anchors keep the network continuous: nodes shared with an already profiled chain (ramps into the
    /// carriageway, side streets into the main road) and free bridge deck ends (<see cref="BridgeBuilder.FreeEnds"/>:
    /// the road meets the deck; on an extended deck the approach's profile stops where the deck now ends). The
    /// difference is eased in over about one window. Deck ends further off than 'reach' are left to the deck's easing.</item>
    /// <item>Writing: samples within half width + shoulder (at least 5 m – the terrain cells) take the profile; a core
    /// is never moved again, except by a nearer centre line of the same rank (carriageways side by side). Beyond the
    /// ribbon big differences fade the write out (a crossing road's ramp, a ditch, a yard stay). The cell past a deck
    /// end becomes the abutment (deck height; only the ribbon of a way right below may still take it); elsewhere under
    /// a deck nothing near deck height is written (the road below stays). Samples owned by <see cref="CoarseTerrain"/>
    /// (already meshed) and sea/harbour samples are left alone.</item>
    /// </list>
    /// Before/after roughness (deviation from the 5 m chord along the centre line) is logged per road class; the
    /// profiles of all carriageways go to Builds/RoadProfile/&lt;id&gt;_roads.csv (Tools/dgm/plot_road_profile.py).
    /// Call after <see cref="BridgeBuilder.Prepare"/> and before <see cref="BridgeBuilder.EaseEnds"/> / GroundBuilder.BuildBase.
    /// No random numbers – object ids are untouched.
    /// </summary>
    public static class RoadProfile
    {
        class Kind
        {
            public int rank;
            public float window;   // m, full width of the smoothing kernel
            public float grade;    // max gradient (0 = none)
            public float shoulder; // m beyond the half width that is set level with the road
            public float blend;    // m over which the ground eases back
            public float cap;      // max |profile − ground| before anchors
            public float reach = 1.5f; // max height difference a deck end may pull the road (else the deck's own easing / step)
            public float keep = 1f;    // blend zone: differences beyond this fade out (2× = untouched) – ditches, other embankments stay
            public string group;   // report group
        }

        static Kind Classify(Dictionary<string, string> tags)
        {
            switch (tags.Get("highway"))
            {
                case "motorway": return new Kind { rank = 0, window = 100f, grade = 0.05f, shoulder = 3.5f, blend = 10f, cap = 6f, reach = 4f, keep = 1.5f, group = "motorway" };
                case "trunk": return new Kind { rank = 0, window = 80f, grade = 0.06f, shoulder = 3.5f, blend = 10f, cap = 4f, reach = 4f, keep = 1.5f, group = "trunk/primary" };
                case "motorway_link": case "trunk_link": return new Kind { rank = 1, window = 40f, grade = 0.08f, shoulder = 2.5f, blend = 6f, cap = 4f, reach = 4f, keep = 1.5f, group = "motorway_link" };
                case "primary": case "primary_link": return new Kind { rank = 2, window = 50f, shoulder = 2.5f, blend = 6f, cap = 3f, group = "trunk/primary" };
                case "secondary": case "secondary_link": return new Kind { rank = 2, window = 30f, shoulder = 2f, blend = 5f, cap = 3f, group = "secondary/tertiary" };
                case "tertiary": case "tertiary_link": return new Kind { rank = 3, window = 30f, shoulder = 2f, blend = 5f, cap = 2.5f, group = "secondary/tertiary" };
                case "residential": case "unclassified": case "living_street": case "road":
                    return new Kind { rank = 4, window = 20f, shoulder = 1.5f, blend = 4f, cap = 2f, group = "residential" };
                case "pedestrian": return new Kind { rank = 4, window = 15f, shoulder = 1.5f, blend = 3f, cap = 1.5f, group = "residential" };
                case "service": return new Kind { rank = 5, window = 15f, shoulder = 1f, blend = 3f, cap = 1.5f, group = "service" };
                case "track": case "cycleway": return new Kind { rank = 6, window = 12f, shoulder = 0.5f, blend = 2f, cap = 0.6f, reach = 1f, keep = 0.5f, group = "track/path" };
                case "footway": case "path": case "bridleway": case "corridor":
                    return new Kind { rank = 7, window = 10f, shoulder = 0.5f, blend = 2f, cap = 0.5f, reach = 1f, keep = 0.5f, group = "track/path" };
                default: return null; // steps stay steep
            }
        }

        const float Spacing = 2f;        // profile samples along the chain
        const float MinCore = 5f;        // carriageways: level at least this far from the centre (5 m terrain cells)
        const float JoinAngle = 0.7f;    // cos of the largest bend where two ways continue as one chain
        const float Chord = 5f;          // roughness: deviation of h(s) from the chord h(s−5)…h(s+5)
        const float Bump = 0.15f;        // m: counted as a bump
        const float PastEnd = 5f;        // m beyond a deck end that still take the road's height (the abutment cell)

        class Chain
        {
            public readonly List<Vector2> pts = new List<Vector2>();
            public readonly List<float> vertexS = new List<float>(); // arc length of every polyline vertex
            public Kind kind;
            public float halfWidth, y;
            public bool startDeck, endDeck;
            public bool a28;
            public long wayId;     // first way (log)
            public float Length => vertexS[vertexS.Count - 1];
        }

        static Vector2Int Key(Vector2 p) => new Vector2Int(Mathf.RoundToInt(p.x * 10f), Mathf.RoundToInt(p.y * 10f));

        public static void Apply(OsmData data, AreaRect area, TerrainField t, string areaId)
        {
            if (t.IsFlat || t.Samples == null) return;
            var chains = BuildChains(data);
            if (chains.Count == 0) return;
            // Before: the full OSM centre lines (approaches still run up to the OSM deck ends).
            var before = new Roughness();
            foreach (var c in chains) Measure(c, t, before);
            // Deck ends: the approach's profile ends where the built deck ends (further out on an extended deck).
            var deckSurface = new Dictionary<Vector2Int, float>();
            var deckBuilt = new Dictionary<Vector2Int, (Vector2 built, float surface)>();
            foreach (var (end, built, surface) in BridgeBuilder.FreeEnds())
            {
                deckSurface[Key(end)] = surface;
                if ((built - end).sqrMagnitude > 0.01f) deckBuilt[Key(end)] = (built, surface);
            }
            for (int i = chains.Count - 1; i >= 0; i--)
            {
                var c = chains[i];
                foreach (bool start in new[] { true, false })
                {
                    if (!deckBuilt.TryGetValue(Key(start ? c.pts[0] : c.pts[c.pts.Count - 1]), out var db)) continue;
                    var end = Cut(c, start, Vector2.Distance(db.built, start ? c.pts[0] : c.pts[c.pts.Count - 1]));
                    if (end.HasValue) deckSurface[Key(end.Value)] = db.surface;
                }
                if (c.pts.Count < 2 || c.Length < 2f) { chains.RemoveAt(i); continue; }
                c.startDeck = deckSurface.ContainsKey(Key(c.pts[0]));
                c.endDeck = deckSurface.ContainsKey(Key(c.pts[c.pts.Count - 1]));
            }
            chains.Sort((a, b) => a.kind.rank != b.kind.rank ? a.kind.rank.CompareTo(b.kind.rank) : b.Length.CompareTo(a.Length));

            var beforeProfiles = new Dictionary<Chain, float[]>();
            foreach (var c in chains)
                if (c.kind.rank <= 5) beforeProfiles[c] = Measure(c, t, null);

            skippedDecks = 0; bigLogs = 0;
            var claim = new byte[t.Samples.Length];           // 0 free … 254 blend weight, 255 = a road's level core
            var cores = new Dictionary<int, (float d, int rank)>(); // who set a core sample: nearest centre of the same rank wins; rank −1 = abutment
            var nodeHeight = new Dictionary<Vector2Int, float>(); // ground height of profiled chains at their vertices
            int written = 0, anchored = 0;
            float maxUp = 0f, maxDown = 0f;
            foreach (var c in chains)
            {
                var profile = Profile(c, t, deckSurface, nodeHeight, ref anchored);
                Write(c, profile, t, claim, cores, ref written, ref maxUp, ref maxDown);
            }

            var after = new Roughness();
            var csv = new StringBuilder("chain,ref,group,way,s,x,z,before,after\n");
            int ci = 0;
            foreach (var c in chains)
            {
                var h = Measure(c, t, after);
                if (!beforeProfiles.TryGetValue(c, out var hb)) continue;
                for (int i = 0; i < h.Length; i++)
                {
                    var p = PointAt(c, i);
                    csv.Append(ci).Append(',').Append(c.a28 ? "A28" : "").Append(',').Append(c.kind.group).Append(',').Append(c.wayId).Append(',')
                       .Append((c.Length * i / (h.Length - 1)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                       .Append(p.x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                       .Append(p.y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                       .Append(hb[i].ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                       .Append(h[i].ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                }
                ci++;
            }
            if (ci > 0)
            {
                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "RoadProfile"));
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, areaId + "_roads.csv"), csv.ToString());
            }

            Debug.Log($"[OSM] road profiles: {chains.Count} chains, {anchored} anchors ({skippedDecks} deck ends too far off, left), {written} height samples set (max +{maxUp:F2} m at ({maxUpAt.x:F0}, {maxUpAt.y:F0}), -{maxDown:F2} m at ({maxDownAt.x:F0}, {maxDownAt.y:F0}))");
            foreach (var g in before.Groups)
            {
                var b = before.Get(g);
                var a = after.Get(g);
                Debug.Log($"[OSM] road roughness {g,-20} {b.km,7:F1} km: RMS {b.Rms * 100f:F1} → {a.Rms * 100f:F1} cm, max {b.max:F2} → {a.max:F2} m, " +
                          $"bumps >{Bump * 100f:0} cm {b.bumps / Mathf.Max(0.01f, b.km):F1} → {a.bumps / Mathf.Max(0.01f, a.km):F1} /km (worst now at ({a.maxAt.x:F0}, {a.maxAt.y:F0}))");
            }
            foreach (var (p, dev, devAfter) in before.Worst(after, t))
                Debug.Log($"[OSM] road roughness worst motorway spot ({p.x:F0}, {p.y:F0}): {dev:+0.00;-0.00} → {devAfter:+0.00;-0.00} m");
        }

        // ------------------------------------------------------------------ chains

        static List<Chain> BuildChains(OsmData data)
        {
            var ways = new List<(OsmWay w, Kind k, float width, float y)>();
            foreach (var w in data.ways)
            {
                if (w.points.Count < 2 || BridgeBuilder.IsDeck(w) || !GroundBuilder.RoadRibbon(w.tags, out float width, out float y)) continue;
                var k = Classify(w.tags);
                if (k == null) continue;
                ways.Add((w, k, width, y));
            }
            // Ends per node; every end picks its straightest same-rank continuation, mutual picks are joined.
            var ends = new Dictionary<Vector2Int, List<(int way, bool start)>>();
            for (int i = 0; i < ways.Count; i++)
            {
                var pts = ways[i].w.points;
                foreach (bool start in new[] { true, false })
                {
                    var k = Key(start ? pts[0] : pts[pts.Count - 1]);
                    if (!ends.TryGetValue(k, out var l)) ends[k] = l = new List<(int, bool)>();
                    l.Add((i, start));
                }
            }
            Vector2 Out(int i, bool start)
            {
                var pts = ways[i].w.points;
                return start ? (pts[0] - pts[1]).normalized : (pts[pts.Count - 1] - pts[pts.Count - 2]).normalized;
            }
            var best = new Dictionary<(int, bool), (int, bool)>();
            foreach (var l in ends.Values)
                foreach (var (i, si) in l)
                {
                    float top = JoinAngle;
                    (int, bool)? pick = null;
                    foreach (var (j, sj) in l)
                    {
                        if (j == i || ways[j].k.rank != ways[i].k.rank) continue;
                        float dot = Vector2.Dot(Out(i, si), -Out(j, sj));
                        if (dot > top) { top = dot; pick = (j, sj); }
                    }
                    if (pick.HasValue) best[(i, si)] = pick.Value;
                }
            var link = new Dictionary<(int, bool), (int, bool)>();
            foreach (var kv in best)
                if (best.TryGetValue(kv.Value, out var back) && back == kv.Key) link[kv.Key] = kv.Value;

            var chains = new List<Chain>();
            var used = new bool[ways.Count];
            for (int i = 0; i < ways.Count; i++)
            {
                if (used[i]) continue;
                // Walk back to the chain's first way (stop on loops).
                int first = i; bool firstStart = true; // the end of 'first' where the chain begins
                var seen = new HashSet<int> { i };
                while (link.TryGetValue((first, firstStart), out var prev) && !seen.Contains(prev.Item1))
                {
                    first = prev.Item1; firstStart = !prev.Item2; seen.Add(first);
                }
                var c = new Chain { kind = ways[first].k, halfWidth = ways[first].width * 0.5f, y = ways[first].y, wayId = ways[first].w.id };
                int cur = first; bool curStart = firstStart;
                while (true)
                {
                    used[cur] = true;
                    var pts = ways[cur].w.points;
                    c.halfWidth = Mathf.Max(c.halfWidth, ways[cur].width * 0.5f);
                    c.a28 |= IsA28(ways[cur].w.tags);
                    for (int k = 0; k < pts.Count; k++)
                    {
                        var p = curStart ? pts[k] : pts[pts.Count - 1 - k];
                        if (c.pts.Count > 0 && (c.pts[c.pts.Count - 1] - p).sqrMagnitude < 1e-4f) continue;
                        c.vertexS.Add(c.pts.Count == 0 ? 0f : c.vertexS[c.vertexS.Count - 1] + Vector2.Distance(c.pts[c.pts.Count - 1], p));
                        c.pts.Add(p);
                    }
                    if (!link.TryGetValue((cur, !curStart), out var next) || used[next.Item1]) break;
                    cur = next.Item1; curStart = next.Item2;
                }
                if (c.pts.Count >= 2 && c.Length > 1f) chains.Add(c);
            }
            return chains;
        }

        /// <summary>Shortens the chain by <paramref name="by"/> metres at its start or end; returns the new end point
        /// (null if nothing is left).</summary>
        static Vector2? Cut(Chain c, bool start, float by)
        {
            float L = c.Length;
            if (by >= L - 1f) { c.pts.Clear(); c.vertexS.Clear(); return null; }
            float s0 = start ? by : 0f, s1 = start ? L : L - by;
            var pts = new List<Vector2> { PointAtS(c, s0) };
            for (int v = 0; v < c.pts.Count; v++)
                if (c.vertexS[v] > s0 + 0.05f && c.vertexS[v] < s1 - 0.05f) pts.Add(c.pts[v]);
            pts.Add(PointAtS(c, s1));
            c.pts.Clear(); c.pts.AddRange(pts);
            c.vertexS.Clear();
            for (int v = 0; v < pts.Count; v++) c.vertexS.Add(v == 0 ? 0f : c.vertexS[v - 1] + Vector2.Distance(pts[v - 1], pts[v]));
            return start ? pts[0] : pts[pts.Count - 1];
        }

        static bool IsA28(Dictionary<string, string> tags)
        {
            var r = tags.Get("ref");
            return r != null && r.Replace(" ", "").Contains("A28");
        }

        // ------------------------------------------------------------------ profile

        static int Samples(Chain c) => Mathf.Max(2, Mathf.CeilToInt(c.Length / Spacing) + 1);

        /// <summary>Point of profile sample i (uniform along the chain).</summary>
        static Vector2 PointAt(Chain c, int i) => PointAtS(c, c.Length * i / (Samples(c) - 1));

        static Vector2 PointAtS(Chain c, float s)
        {
            var vs = c.vertexS;
            int lo = 0, hi = vs.Count - 1;
            while (hi - lo > 1) { int m = (lo + hi) / 2; if (vs[m] <= s) lo = m; else hi = m; }
            float seg = vs[hi] - vs[lo];
            return Vector2.Lerp(c.pts[lo], c.pts[hi], seg > 0f ? Mathf.Clamp01((s - vs[lo]) / seg) : 0f);
        }

        static int skippedDecks;

        static float[] Profile(Chain c, TerrainField t, Dictionary<Vector2Int, float> deckSurface, Dictionary<Vector2Int, float> nodeHeight, ref int anchored)
        {
            int n = Samples(c);
            float ds = c.Length / (n - 1);
            var ground = new float[n];
            for (int i = 0; i < n; i++) { var p = PointAt(c, i); ground[i] = t.Height(p.x, p.y); }
            var k = c.kind;
            int half = Mathf.Max(1, Mathf.RoundToInt(k.window * 0.25f / ds)); // median and each box: window / 2
            var h = Box(Box(Median(ground, half), half), half);
            for (int i = 0; i < n; i++) h[i] = Mathf.Clamp(h[i], ground[i] - k.cap, ground[i] + k.cap);

            // Anchors: deck ends and vertices shared with chains profiled before.
            var anchors = new SortedDictionary<int, float>();
            for (int v = 0; v < c.pts.Count; v++)
            {
                var key = Key(c.pts[v]);
                float val;
                int at = Mathf.Clamp(Mathf.RoundToInt(c.vertexS[v] / ds), 0, n - 1);
                if (deckSurface.TryGetValue(key, out float surface))
                {
                    // A deck whose abutment search found something else (a path bridge next to a motorway embankment):
                    // leave the road on its ground, BridgeBuilder.EaseEnds keeps the deck's own step handling.
                    val = surface - c.y;
                    if (Mathf.Abs(val - h[at]) > k.reach) { skippedDecks++; continue; }
                }
                else if (!nodeHeight.TryGetValue(key, out val)) continue;
                anchors[at] = val;
            }
            anchored += anchors.Count;
            if (anchors.Count > 0) Anchor(h, anchors, Mathf.Max(k.window, 20f) / ds);

            if (k.grade > 0f)
            {
                float lim = k.grade * ds;
                for (int i = 1; i < n; i++) if (!anchors.ContainsKey(i)) h[i] = Mathf.Clamp(h[i], h[i - 1] - lim, h[i - 1] + lim);
                for (int i = n - 2; i >= 0; i--) if (!anchors.ContainsKey(i)) h[i] = Mathf.Clamp(h[i], h[i + 1] - lim, h[i + 1] + lim);
            }

            for (int v = 0; v < c.pts.Count; v++)
            {
                var key = Key(c.pts[v]);
                if (nodeHeight.ContainsKey(key)) continue;
                float f = c.vertexS[v] / ds;
                int i0 = Mathf.Clamp(Mathf.FloorToInt(f), 0, n - 2);
                nodeHeight[key] = Mathf.Lerp(h[i0], h[i0 + 1], Mathf.Clamp01(f - i0));
            }
            return h;
        }

        /// <summary>Running median over ±half samples (cut off at the ends – a pit right at a chain end, typically at a
        /// bridge, must not survive as the end value).</summary>
        static float[] Median(float[] a, int half)
        {
            int n = a.Length;
            var r = new float[n];
            var w = new List<float>(2 * half + 1);
            for (int i = 0; i < n; i++)
            {
                w.Clear();
                for (int j = Mathf.Max(0, i - half); j <= Mathf.Min(n - 1, i + half); j++) w.Add(a[j]);
                w.Sort();
                r[i] = w[w.Count / 2];
            }
            return r;
        }

        /// <summary>Moving average over ±half samples (cut off at the ends; anchors there correct the slope bias).</summary>
        static float[] Box(float[] a, int half)
        {
            int n = a.Length;
            var sum = new double[n + 1];
            for (int i = 0; i < n; i++) sum[i + 1] = sum[i] + a[i];
            var r = new float[n];
            for (int i = 0; i < n; i++)
            {
                int lo = Mathf.Max(0, i - half), hi = Mathf.Min(n - 1, i + half);
                r[i] = (float)((sum[hi + 1] - sum[lo]) / (hi - lo + 1));
            }
            return r;
        }

        /// <summary>Moves the profile through the anchors: the difference at each anchor fades out over
        /// <paramref name="reach"/> samples; between two close anchors it is interpolated.</summary>
        static void Anchor(float[] h, SortedDictionary<int, float> anchors, float reach)
        {
            int n = h.Length;
            var idx = new List<int>(anchors.Keys);
            var diff = new float[idx.Count];
            for (int a = 0; a < idx.Count; a++) diff[a] = anchors[idx[a]] - h[idx[a]];
            var delta = new float[n];
            for (int i = 0; i < n; i++)
            {
                int a = idx.BinarySearch(i);
                if (a >= 0) { delta[i] = diff[a]; continue; }
                a = ~a; // first anchor after i
                if (a == 0) { delta[i] = diff[0] * Fade((idx[0] - i) / reach); continue; }
                if (a == idx.Count) { delta[i] = diff[a - 1] * Fade((i - idx[a - 1]) / reach); continue; }
                int i0 = idx[a - 1], i1 = idx[a];
                if (i1 - i0 <= 2f * reach) delta[i] = Mathf.Lerp(diff[a - 1], diff[a], SmoothStep((float)(i - i0) / (i1 - i0)));
                else delta[i] = diff[a - 1] * Fade((i - i0) / reach) + diff[a] * Fade((i1 - i) / reach);
            }
            for (int i = 0; i < n; i++) h[i] += delta[i];
        }

        static float SmoothStep(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        static float Fade(float x) => 1f - SmoothStep(x);

        // ------------------------------------------------------------------ terrain

        static Vector2 maxUpAt, maxDownAt;
        static int bigLogs;

        static void Write(Chain c, float[] h, TerrainField t, byte[] claim, Dictionary<int, (float d, int rank)> cores, ref int written, ref float maxUp, ref float maxDown)
        {
            var k = c.kind;
            float core = c.halfWidth + k.shoulder;
            if (k.rank <= 5) core = Mathf.Max(core, MinCore);
            float reach = core + k.blend;
            int n = h.Length;
            var pts = new Vector2[n];
            for (int i = 0; i < n; i++) pts[i] = PointAt(c, i);
            var s = t.Samples;
            float step = t.Step;
            var target = new Dictionary<int, (float d, float h, bool past)>();
            for (int i = 0; i < n - 1; i++)
            {
                Vector2 a = pts[i], ab = pts[i + 1] - a;
                float len2 = ab.sqrMagnitude;
                if (len2 < 1e-6f) continue;
                int i0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, a.x + ab.x) - reach - t.XMin) / step));
                int i1 = Mathf.Min(t.Nx - 1, Mathf.CeilToInt((Mathf.Max(a.x, a.x + ab.x) + reach - t.XMin) / step));
                int k0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, a.y + ab.y) - reach - t.ZMin) / step));
                int k1 = Mathf.Min(t.Nz - 1, Mathf.CeilToInt((Mathf.Max(a.y, a.y + ab.y) + reach - t.ZMin) / step));
                for (int kz = k0; kz <= k1; kz++)
                for (int ix = i0; ix <= i1; ix++)
                {
                    var q = new Vector2(t.XMin + ix * step, t.ZMin + kz * step);
                    float u = Vector2.Dot(q - a, ab) / len2;
                    // Past a deck end only the abutment (the cell the ribbon ends in) – the deck takes over, below it runs
                    // another road or water.
                    bool past = i == 0 && c.startDeck && u < 0f || i == n - 2 && c.endDeck && u > 1f;
                    if (past && (u < 0f ? -u : u - 1f) * Mathf.Sqrt(len2) > PastEnd) continue;
                    u = Mathf.Clamp01(u);
                    float d = Vector2.Distance(q, a + ab * u);
                    if (d > (past ? c.halfWidth + 1.5f : reach)) continue;
                    int idx = kz * t.Nx + ix;
                    if (target.TryGetValue(idx, out var old) && old.d <= d) continue;
                    target[idx] = (d, Mathf.Lerp(h[i], h[i + 1], u), past);
                }
            }
            foreach (var kv in target)
            {
                int idx = kv.Key;
                float w = kv.Value.d <= core || kv.Value.past ? 1f : Fade((kv.Value.d - core) / k.blend);
                if (w <= 0f) continue;
                // A core stays – unless this is a road of the same rank whose centre is nearer (two carriageways side
                // by side: each keeps its own profile up to the middle, no ripples from the neighbour's). An abutment
                // (level with the deck end, the cell the approach ribbon ends in) only gives way to the ribbon of a road
                // passing right below.
                if (claim[idx] == 255)
                {
                    if (w < 1f || kv.Value.past || !cores.TryGetValue(idx, out var owner)) continue;
                    if (owner.rank < 0 ? kv.Value.d > c.halfWidth + 0.5f : owner.rank != k.rank || owner.d <= kv.Value.d) continue;
                }
                int ix = idx % t.Nx, kz = idx / t.Nx;
                var q = new Vector2(t.XMin + ix * step, t.ZMin + kz * step);
                if (CoarseTerrain.Owns(q.x, q.y)) continue;
                // Under a bridge deck only the road below may move the ground (an approach's embankment would bury it);
                // the own carriageway beside a parallel deck (ramp bridge next to the motorway bridge) is fine.
                if (!kv.Value.past && kv.Value.d > c.halfWidth + 1f && BridgeBuilder.Above(q, 1f, out float deck) && kv.Value.h > deck - 2.5f) continue;
                float old = s[idx], nh;
                if (CoastBuilder.Active && old < CoastBuilder.SeaLevel) continue; // sea, harbour basin: a pier is no dam
                // Beyond the ribbon itself big differences are another feature (a bridge abutment next to the road, a
                // ditch, a yard) – the shoulder levels up to 2 m, the blend zone up to 'keep'; beyond twice that, leave it.
                bool hard = kv.Value.past || kv.Value.d <= c.halfWidth + 1f;
                if (!hard) w *= Mathf.Clamp01(2f - Mathf.Abs(kv.Value.h - old) / (kv.Value.d <= core ? Mathf.Max(k.keep, 2f) : k.keep));
                if (w <= 0f) continue;
                if (w >= 1f) { nh = kv.Value.h; claim[idx] = 255; cores[idx] = (kv.Value.d, kv.Value.past ? -1 : k.rank); }
                else
                {
                    float prev = claim[idx] / 254f;
                    if (w <= prev) continue;
                    nh = Mathf.Lerp(old, kv.Value.h, (w - prev) / (1f - prev));
                    claim[idx] = (byte)Mathf.RoundToInt(w * 254f);
                }
                if (Mathf.Abs(nh - old) > 3f && bigLogs++ < 6)
                    Debug.Log($"[OSM] road profile: way {c.wayId} ({c.kind.group}) moves the ground {nh - old:+0.00;-0.00} m at ({t.XMin + ix * step:F0}, {t.ZMin + kz * step:F0}), {kv.Value.d:F1} m from its centre");
                s[idx] = nh;
                written++;
                if (nh - old > maxUp) { maxUp = nh - old; maxUpAt = new Vector2(t.XMin + ix * step, t.ZMin + kz * step); }
                if (old - nh > maxDown) { maxDown = old - nh; maxDownAt = new Vector2(t.XMin + ix * step, t.ZMin + kz * step); }
            }
        }

        // ------------------------------------------------------------------ roughness

        class Roughness
        {
            public class Acc { public double sq; public long count; public float max; public Vector2 maxAt; public int bumps; public float km; public float Rms => count > 0 ? (float)System.Math.Sqrt(sq / count) : 0f; }
            readonly Dictionary<string, Acc> groups = new Dictionary<string, Acc>();
            public readonly List<(Vector2 p, float dev)> motorway = new List<(Vector2, float)>();
            public IEnumerable<string> Groups
            {
                get { var l = new List<string>(groups.Keys); l.Sort(); if (groups.ContainsKey("A28")) { l.Remove("A28"); l.Insert(0, "A28"); } return l; }
            }
            public Acc Get(string g) { if (!groups.TryGetValue(g, out var a)) groups[g] = a = new Acc(); return a; }

            /// <summary>The 8 worst motorway spots before (≥ 200 m apart) and their deviation now.</summary>
            public IEnumerable<(Vector2, float, float)> Worst(Roughness now, TerrainField t)
            {
                var l = new List<(Vector2 p, float dev)>(motorway);
                l.Sort((a, b) => Mathf.Abs(b.dev).CompareTo(Mathf.Abs(a.dev)));
                var picked = new List<Vector2>();
                foreach (var (p, dev) in l)
                {
                    if (picked.Count >= 8) break;
                    bool far = true;
                    foreach (var q in picked) far &= (q - p).sqrMagnitude > 200f * 200f;
                    if (!far) continue;
                    picked.Add(p);
                    float best = 0f, bd = float.MaxValue;
                    foreach (var (q, d) in now.motorway) { float dd = (q - p).sqrMagnitude; if (dd < bd) { bd = dd; best = d; } }
                    yield return (p, dev, best);
                }
            }
        }

        /// <summary>Ground along the chain every <see cref="Spacing"/> m; adds its 5 m chord deviations to the stats.</summary>
        static float[] Measure(Chain c, TerrainField t, Roughness r)
        {
            int n = Samples(c);
            var h = new float[n];
            for (int i = 0; i < n; i++) { var p = PointAt(c, i); h[i] = t.Height(p.x, p.y); }
            if (r == null) return h;
            var groups = c.a28 ? new[] { c.kind.group, "A28" } : new[] { c.kind.group };
            float L = c.Length;
            foreach (var g in groups)
            {
                var acc = r.Get(g);
                acc.km += L / 1000f;
                bool inBump = false;
                for (float s = Chord; s <= L - Chord; s += 1f)
                {
                    var p = PointAtS(c, s);
                    var p0 = PointAtS(c, s - Chord);
                    var p1 = PointAtS(c, s + Chord);
                    float dev = t.Height(p.x, p.y) - 0.5f * (t.Height(p0.x, p0.y) + t.Height(p1.x, p1.y));
                    acc.sq += dev * dev; acc.count++;
                    if (Mathf.Abs(dev) > acc.max) { acc.max = Mathf.Abs(dev); acc.maxAt = p; }
                    bool bump = Mathf.Abs(dev) > Bump;
                    if (bump && !inBump) acc.bumps++;
                    inBump = bump;
                    if (g == c.kind.group && c.kind.group == "motorway" && ((int)s % 5 == 0 || Mathf.Abs(dev) > Bump)) r.motorway.Add((p, dev));
                }
            }
            return h;
        }
    }
}
