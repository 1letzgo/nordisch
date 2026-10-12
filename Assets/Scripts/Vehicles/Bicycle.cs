using System.Collections.Generic;
using LeGamez.Core;
using LeGamez.UI;
using UnityEngine;

namespace LeGamez.Vehicles
{
    /// <summary>
    /// Low-poly bicycle (procedural mesh, brand colours). Parked it just stands there; while ridden it is
    /// parented to the player, spins its wheels with speed and leans into turns.
    /// </summary>
    public class Bicycle : Vehicle
    {
        public override float CruiseSpeed => 7.5f;
        public override float SprintSpeed => 11f;
        public override float TurnSmoothTime => 0.2f;
        public override float CameraBonus => 1.5f;
        public override int AvatarMode => 2;
        public override Vector3 SeatOffset => new Vector3(0, 0.52f, -0.12f);
        public override UiKit.IconKind MountIcon => UiKit.IconKind.Bike;
        public override UiKit.IconKind SprintIcon => UiKit.IconKind.BikeFast;

        const float WheelRadius = 0.34f;
        const float WheelBase = 1.05f;

        Transform frontWheel, rearWheel, body;
        float lean, leanVel;
        float lastYaw;

        static Mesh frameMesh, wheelMesh;

        public static Bicycle Create(Vector3 position, float yaw)
        {
            var root = new GameObject("Bicycle");
            root.transform.SetPositionAndRotation(position, Quaternion.Euler(0, yaw, 0));
            var bike = root.AddComponent<Bicycle>();
            var mat = GameMaterials.Instance != null ? GameMaterials.Instance.vehicleMaterial : null;

            bike.body = new GameObject("Body").transform;
            bike.body.SetParent(root.transform, false);
            AddMesh(bike.body.gameObject, FrameMesh(), mat);

            bike.frontWheel = Wheel(bike.body, new Vector3(0, WheelRadius, WheelBase * 0.5f), mat);
            bike.rearWheel = Wheel(bike.body, new Vector3(0, WheelRadius, -WheelBase * 0.5f), mat);
            SetLayerRecursive(root);
            return bike;
        }

        static Transform Wheel(Transform parent, Vector3 pos, Material mat)
        {
            var w = new GameObject("Wheel").transform;
            w.SetParent(parent, false);
            w.localPosition = pos;
            AddMesh(w.gameObject, WheelMesh(), mat);
            return w;
        }

        static void AddMesh(GameObject go, Mesh mesh, Material mat)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
        }

        public override void Mount(Transform rider)
        {
            base.Mount(rider);
            lastYaw = rider.eulerAngles.y;
        }

        public override void Dismount(Vector3 parkPosition)
        {
            base.Dismount(parkPosition);
            body.localRotation = Quaternion.Euler(0, 0, 8f); // resting on its kickstand
        }

        /// <summary>Called by the rider each frame with the current ground speed.</summary>
        public override void Animate(float speed)
        {
            float spin = speed / WheelRadius * Mathf.Rad2Deg * Time.deltaTime;
            frontWheel.Rotate(spin, 0, 0, Space.Self);
            rearWheel.Rotate(spin, 0, 0, Space.Self);

            // Lean into turns proportional to yaw rate × speed.
            float yaw = transform.eulerAngles.y;
            float yawRate = Mathf.DeltaAngle(lastYaw, yaw) / Mathf.Max(Time.deltaTime, 0.0001f);
            lastYaw = yaw;
            float target = Mathf.Clamp(-yawRate * speed * 0.004f, -22f, 22f);
            lean = Mathf.SmoothDamp(lean, target, ref leanVel, 0.15f);
            body.localRotation = Quaternion.Euler(0, 0, lean);
        }

        public float Lean => lean;

        // ---------------------------------------------------------------- procedural meshes

        static readonly Color32 Mustard = new Color32(227, 168, 43, 255);
        static readonly Color32 Navy = new Color32(26, 58, 78, 255);
        static readonly Color32 Black = new Color32(34, 36, 38, 255);
        static readonly Color32 Chrome = new Color32(190, 194, 198, 255);
        static readonly Color32 Saddle = new Color32(92, 60, 40, 255);

        class Builder
        {
            public readonly List<Vector3> v = new List<Vector3>();
            public readonly List<Vector3> n = new List<Vector3>();
            public readonly List<Color32> c = new List<Color32>();
            public readonly List<int> t = new List<int>();

            public void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Color32 col)
            {
                var nrm = Vector3.Cross(b - a, cc - a).normalized;
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
                for (int k = 0; k < 4; k++) { n.Add(nrm); c.Add(col); }
                t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }

            /// <summary>Square tube from a to b (thickness w) – used for frame, fork, bars.</summary>
            public void Tube(Vector3 a, Vector3 b, float w, Color32 col)
            {
                Vector3 dir = (b - a).normalized;
                Vector3 side = Vector3.Cross(dir, Mathf.Abs(dir.y) > 0.9f ? Vector3.forward : Vector3.up).normalized * (w * 0.5f);
                Vector3 up = Vector3.Cross(side.normalized, dir) * (w * 0.5f);
                Vector3[] o = { side + up, -side + up, -side - up, side - up };
                for (int k = 0; k < 4; k++)
                {
                    Vector3 p0 = o[k], p1 = o[(k + 1) % 4];
                    Quad(a + p0, a + p1, b + p1, b + p0, col);
                    Quad(a + p0, b + p0, b + p1, a + p1, col); // double-sided: orientation of 'side' varies
                }
            }

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(v); m.SetNormals(n); m.SetColors(c); m.SetTriangles(t, 0);
                m.RecalculateBounds();
                return m;
            }
        }

        static Mesh FrameMesh()
        {
            if (frameMesh != null) return frameMesh;
            var b = new Builder();
            float r = WheelRadius, hb = WheelBase * 0.5f;
            Vector3 rearHub = new Vector3(0, r, -hb), frontHub = new Vector3(0, r, hb);
            Vector3 bottom = new Vector3(0, 0.3f, -0.05f);
            Vector3 seatTop = new Vector3(0, 0.82f, -0.2f);
            Vector3 headTop = new Vector3(0, 0.88f, 0.38f), headLow = new Vector3(0, 0.7f, 0.42f);
            float w = 0.045f;
            // Dutch step-through frame.
            b.Tube(bottom, headLow, w, Mustard);
            b.Tube(bottom, seatTop, w, Mustard);
            b.Tube(bottom, rearHub, w * 0.8f, Mustard);
            b.Tube(seatTop + Vector3.down * 0.1f, rearHub, w * 0.8f, Mustard);
            b.Tube(headLow, headTop, w, Mustard);
            b.Tube(headLow, frontHub, w * 0.8f, Chrome);                        // fork
            b.Tube(headTop, headTop + new Vector3(0, 0.08f, -0.12f), w * 0.7f, Chrome);
            b.Tube(headTop + new Vector3(-0.28f, 0.1f, -0.16f), headTop + new Vector3(0.28f, 0.1f, -0.16f), w * 0.7f, Navy); // handlebar
            b.Tube(seatTop, seatTop + Vector3.up * 0.08f, w * 0.7f, Chrome);
            b.Tube(seatTop + new Vector3(0, 0.1f, -0.1f), seatTop + new Vector3(0, 0.1f, 0.12f), 0.14f, Saddle); // saddle
            // Rear rack + front basket (very Nordwest).
            b.Tube(new Vector3(0, 0.62f, -0.2f), new Vector3(0, 0.62f, -0.62f), 0.2f, Navy);
            b.Tube(headTop + new Vector3(0, -0.05f, 0.14f), headTop + new Vector3(0, -0.05f, 0.4f), 0.3f, Navy);
            // Mudguards.
            b.Tube(new Vector3(0, r + 0.36f, -hb + 0.25f), new Vector3(0, r + 0.2f, -hb - 0.3f), 0.08f, Navy);
            b.Tube(new Vector3(0, r + 0.36f, hb - 0.25f), new Vector3(0, r + 0.2f, hb + 0.3f), 0.08f, Navy);
            return frameMesh = b.ToMesh("BicycleFrame");
        }

        static Mesh WheelMesh()
        {
            if (wheelMesh != null) return wheelMesh;
            var b = new Builder();
            const int seg = 18;
            float ro = WheelRadius, ri = WheelRadius - 0.05f, hw = 0.025f;
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg;
                Vector3 o0 = new Vector3(0, Mathf.Sin(a0), Mathf.Cos(a0)), o1 = new Vector3(0, Mathf.Sin(a1), Mathf.Cos(a1));
                Vector3 L = Vector3.left * hw, R = Vector3.right * hw;
                // Tyre: outer band + both side faces (double-sided).
                b.Quad(o0 * ro + L, o0 * ro + R, o1 * ro + R, o1 * ro + L, Black);
                b.Quad(o0 * ro + L, o1 * ro + L, o1 * ro + R, o0 * ro + R, Black);
                b.Quad(o0 * ri + L, o0 * ro + L, o1 * ro + L, o1 * ri + L, Black);
                b.Quad(o0 * ri + L, o1 * ri + L, o1 * ro + L, o0 * ro + L, Black);
                b.Quad(o0 * ri + R, o1 * ri + R, o1 * ro + R, o0 * ro + R, Black);
                b.Quad(o0 * ri + R, o0 * ro + R, o1 * ro + R, o1 * ri + R, Black);
            }
            for (int s = 0; s < 6; s++) // spokes, visible when spinning
            {
                float a = s * Mathf.PI / 3f;
                b.Tube(Vector3.zero, new Vector3(0, Mathf.Sin(a), Mathf.Cos(a)) * ri, 0.012f, Chrome);
            }
            return wheelMesh = b.ToMesh("BicycleWheel");
        }
    }
}
