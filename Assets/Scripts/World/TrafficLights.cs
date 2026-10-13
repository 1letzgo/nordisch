using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.World
{
    /// <summary>
    /// Animated traffic light lamps of an area. The importer bakes poles and housings into the static props
    /// mesh and stores only the lamp positions here; at runtime all lamps become one small mesh whose vertex
    /// colours switch with the signal phases (one draw call, no lights).
    /// </summary>
    public class TrafficLights : MonoBehaviour
    {
        [System.Serializable]
        public struct Head
        {
            public Vector3 position;   // centre of the middle (yellow) lamp, world space
            public float yaw;          // lamps face local +Z rotated by yaw
            public int signal;         // index into signals
            public int group;          // 0 / 1: the two crossing directions of a junction
        }

        [System.Serializable]
        public struct Signal
        {
            public float offset;       // seconds into the cycle at t = 0 (junctions don't switch in sync)
            public bool pedestrian;    // mid-block pedestrian light: long car green, single group
        }

        public List<Head> heads = new List<Head>();
        public List<Signal> signals = new List<Signal>();
        public Material material;

        // States: 0 red, 1 red+yellow, 2 green, 3 yellow.
        const float Green0 = 16f, Green1 = 11f, Yellow = 3f, RedYellow = 1.2f, AllRed = 2f;
        const float PedGreen = 22f, PedRed = 12f;
        const float LampRadius = 0.1f, LampSpacing = 0.29f;
        const int Sides = 8;

        static readonly Color32[] On = { new Color32(255, 46, 32, 255), new Color32(255, 186, 30, 255), new Color32(60, 235, 110, 255) };
        static readonly Color32[] Off = { new Color32(58, 22, 20, 255), new Color32(58, 48, 22, 255), new Color32(20, 46, 30, 255) };

        Mesh mesh;
        Color32[] colors;
        int[] state;           // per head, -1 = not yet drawn
        float nextUpdate;

        void Start()
        {
            if (heads.Count == 0 || material == null) { enabled = false; return; }
            var verts = new List<Vector3>();
            var tris = new List<int>();
            foreach (var h in heads)
            {
                var rot = Quaternion.Euler(0f, h.yaw, 0f);
                for (int lamp = 0; lamp < 3; lamp++)
                {
                    var c = h.position + rot * new Vector3(0f, (1 - lamp) * LampSpacing, 0f);
                    int i0 = verts.Count;
                    verts.Add(c);
                    for (int s = 0; s < Sides; s++)
                    {
                        float a = s * Mathf.PI * 2f / Sides;
                        verts.Add(c + rot * new Vector3(Mathf.Cos(a) * LampRadius, Mathf.Sin(a) * LampRadius, 0f));
                    }
                    for (int s = 0; s < Sides; s++)
                    {
                        // Clockwise seen from the front (+Z of the head).
                        tris.Add(i0); tris.Add(i0 + 1 + s); tris.Add(i0 + 1 + (s + 1) % Sides);
                    }
                }
            }
            mesh = new Mesh { name = "TrafficLamps" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            // Normals straight up: lamps read as lit from every side, whatever the sun does.
            var normals = new Vector3[verts.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            mesh.normals = normals;
            colors = new Color32[verts.Count];
            mesh.colors32 = colors;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            mesh.MarkDynamic();

            var go = new GameObject("TrafficLamps");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            state = new int[heads.Count];
            for (int i = 0; i < state.Length; i++) state[i] = -1;
            Refresh();
        }

        void Update()
        {
            if (Time.time < nextUpdate) return;
            nextUpdate = Time.time + 0.2f;
            Refresh();
        }

        void Refresh()
        {
            bool changed = false;
            float t = Time.time;
            for (int i = 0; i < heads.Count; i++)
            {
                var h = heads[i];
                var sig = signals[h.signal];
                int s = sig.pedestrian ? PedestrianState(t + sig.offset) : JunctionState(t + sig.offset, h.group);
                if (s == state[i]) continue;
                state[i] = s;
                changed = true;
                bool red = s == 0 || s == 1, yellow = s == 1 || s == 3, green = s == 2;
                Paint(i, 0, red ? On[0] : Off[0]);
                Paint(i, 1, yellow ? On[1] : Off[1]);
                Paint(i, 2, green ? On[2] : Off[2]);
            }
            if (changed) mesh.colors32 = colors;
        }

        void Paint(int head, int lamp, Color32 c)
        {
            int start = (head * 3 + lamp) * (Sides + 1);
            for (int k = 0; k <= Sides; k++) colors[start + k] = c;
        }

        /// <summary>Two-phase cycle: group 0 gets green first, then (after all-red) group 1.</summary>
        static int JunctionState(float t, int group)
        {
            const float half0 = RedYellow + Green0 + Yellow + AllRed;
            const float half1 = RedYellow + Green1 + Yellow + AllRed;
            float c = Mathf.Repeat(t, half0 + half1);
            if (group == 1) c = Mathf.Repeat(c - half0, half0 + half1);
            float green = group == 0 ? Green0 : Green1;
            if (c < RedYellow) return 1;
            if (c < RedYellow + green) return 2;
            if (c < RedYellow + green + Yellow) return 3;
            return 0;
        }

        static int PedestrianState(float t)
        {
            float c = Mathf.Repeat(t, PedGreen + Yellow + PedRed + RedYellow);
            if (c < PedGreen) return 2;
            if (c < PedGreen + Yellow) return 3;
            if (c < PedGreen + Yellow + PedRed) return 0;
            return 1;
        }
    }
}
