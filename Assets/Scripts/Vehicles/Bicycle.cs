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
        public override float CruiseSpeed => 11f;
        public override float SprintSpeed => 16f;
        public override float TurnSmoothTime => 0.2f;
        public override float CameraBonus => 1.5f;
        public override int AvatarMode => 2;
        public override Vector3 SeatOffset => new Vector3(0, 0.52f, -0.12f);
        public override UiKit.IconKind MountIcon => UiKit.IconKind.Bike;
        public override UiKit.IconKind SprintIcon => UiKit.IconKind.BikeFast;

        const float WheelRadius = 0.34f;
        const float WheelBase = 1.05f;
        public override float AxleBase => WheelBase;
        public override float RearAxle => WheelBase * 0.5f;
        public override float MaxSteer => 38f;
        public override float LateralAccel => 5f;
        public override float TopSpeed => 13f;                    // ~47 km/h, game-fast
        public override float AccelTime => 2.5f;
        public override float BrakeDecel => 8f;
        public override float CoastDecel => 0.8f;
        public override float ReverseSpeed => 1.6f;               // pushing back with the feet, walking pace

        Transform frontWheel, rearWheel, body;
        float lean, leanVel, wheelSpin;
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
            wheelSpin = (wheelSpin + speed / WheelRadius * Mathf.Rad2Deg * Time.deltaTime) % 360f;
            frontWheel.localRotation = Quaternion.Euler(0f, SteerAngle, 0f) * Quaternion.Euler(wheelSpin, 0f, 0f);
            rearWheel.localRotation = Quaternion.Euler(wheelSpin, 0f, 0f);

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

            /// <summary>Axis-aligned box (half extents h), all faces double-sided like Tube.</summary>
            public void Box(Vector3 c, Vector3 h, Color32 col)
            {
                Vector3 x = Vector3.right * h.x, y = Vector3.up * h.y, z = Vector3.forward * h.z;
                void Side(Vector3 o, Vector3 u, Vector3 v)
                {
                    Quad(o - u - v, o - u + v, o + u + v, o + u - v, col);
                    Quad(o - u - v, o + u - v, o + u + v, o - u + v, col);
                }
                Side(c + x, z, y); Side(c - x, z, y);
                Side(c + y, x, z); Side(c - y, x, z);
                Side(c + z, x, y); Side(c - z, x, y);
            }

            /// <summary>Mudguard: flat strip bent round a wheel hub from angle a0 to a1 (radians, 0 = forward, π/2 = up).</summary>
            public void Arc(Vector3 hub, float radius, float a0, float a1, float width, Color32 col)
            {
                const int seg = 7;
                Vector3 hw = Vector3.right * (width * 0.5f);
                for (int i = 0; i < seg; i++)
                {
                    float t0 = Mathf.Lerp(a0, a1, (float)i / seg), t1 = Mathf.Lerp(a0, a1, (float)(i + 1) / seg);
                    Vector3 p0 = hub + new Vector3(0, Mathf.Sin(t0), Mathf.Cos(t0)) * radius;
                    Vector3 p1 = hub + new Vector3(0, Mathf.Sin(t1), Mathf.Cos(t1)) * radius;
                    Quad(p0 - hw, p0 + hw, p1 + hw, p1 - hw, col);
                    Quad(p0 - hw, p1 - hw, p1 + hw, p0 + hw, col);
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
            // Saddle: wide, flat rear and a narrow nose.
            b.Box(seatTop + new Vector3(0, 0.1f, -0.05f), new Vector3(0.085f, 0.03f, 0.07f), Saddle);
            b.Box(seatTop + new Vector3(0, 0.095f, 0.08f), new Vector3(0.035f, 0.025f, 0.07f), Saddle);
            // Rear rack: two rails and cross bars on struts down to the hub.
            foreach (float x in new[] { -0.07f, 0.07f })
            {
                b.Tube(new Vector3(x, 0.66f, -0.22f), new Vector3(x, 0.66f, -0.66f), 0.025f, Navy);
                b.Tube(new Vector3(x, 0.66f, -0.6f), rearHub + new Vector3(x, 0f, 0f), 0.02f, Navy);
            }
            for (int k = 0; k < 3; k++)
                b.Tube(new Vector3(-0.09f, 0.66f, -0.3f - k * 0.15f), new Vector3(0.09f, 0.66f, -0.3f - k * 0.15f), 0.02f, Navy);
            // Mudguards hugging the tyres.
            b.Arc(rearHub, r + 0.05f, 0.15f, Mathf.PI * 1.05f, 0.07f, Navy);
            b.Arc(frontHub, r + 0.05f, -0.1f, Mathf.PI * 0.85f, 0.07f, Navy);
            // Chain guard (Dutch city bike) and crank with pedals.
            b.Tube(bottom + new Vector3(0.06f, 0.02f, 0.06f), rearHub + new Vector3(0.06f, 0.02f, 0.04f), 0.09f, Navy);
            var crankEnd = bottom + new Vector3(0.1f, -0.12f, 0.08f);
            b.Tube(bottom + new Vector3(0.08f, 0f, 0f), crankEnd, 0.025f, Chrome);
            b.Tube(crankEnd, crankEnd + new Vector3(0.1f, 0f, 0f), 0.05f, Black);
            var crankEnd2 = bottom + new Vector3(-0.1f, 0.12f, -0.08f);
            b.Tube(bottom + new Vector3(-0.08f, 0f, 0f), crankEnd2, 0.025f, Chrome);
            b.Tube(crankEnd2, crankEnd2 + new Vector3(-0.1f, 0f, 0f), 0.05f, Black);
            // Grips.
            b.Tube(headTop + new Vector3(-0.3f, 0.1f, -0.16f), headTop + new Vector3(-0.2f, 0.1f, -0.16f), 0.04f, Black);
            b.Tube(headTop + new Vector3(0.2f, 0.1f, -0.16f), headTop + new Vector3(0.3f, 0.1f, -0.16f), 0.04f, Black);
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
