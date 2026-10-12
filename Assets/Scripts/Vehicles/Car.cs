using System.Collections.Generic;
using LeGamez.Core;
using LeGamez.UI;
using UnityEngine;

namespace LeGamez.Vehicles
{
    /// <summary>Low-poly hatchback (procedural mesh). Driver sits inside, wheels spin, body rolls a little in turns.</summary>
    public class Car : Vehicle
    {
        const float WheelRadius = 0.32f;
        const float Length = 3.9f, Width = 1.75f;

        Transform body;
        BoxCollider solid;
        readonly List<Transform> wheels = new List<Transform>();
        float roll, rollVel, lastYaw;

        public override float CruiseSpeed => 9f;
        public override float SprintSpeed => 16f;
        public override float TurnSmoothTime => 0.32f;
        public override float CameraBonus => 3.2f;
        public override int AvatarMode => 1;
        public override Vector3 SeatOffset => new Vector3(-0.35f, 0.25f, 0.1f);
        public override bool HideRider => true;
        public override float ColliderRadius => 0.95f;
        public override UiKit.IconKind MountIcon => UiKit.IconKind.Car;
        public override float MountReach => 1.8f;

        /// <summary>Horizontal distance to the car body (oriented box), so any side or bumper counts.</summary>
        public override float DistanceTo(Vector3 p)
        {
            var local = transform.InverseTransformPoint(p);
            float dx = Mathf.Max(0f, Mathf.Abs(local.x) - Width * 0.5f);
            float dz = Mathf.Max(0f, Mathf.Abs(local.z) - Length * 0.5f);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
        public override UiKit.IconKind SprintIcon => UiKit.IconKind.CarFast;

        static readonly Color32[] Paints =
        {
            new Color32(26, 58, 78, 255),    // navy
            new Color32(227, 168, 43, 255),  // mustard
            new Color32(170, 60, 46, 255),   // brick red
            new Color32(232, 230, 222, 255), // white
            new Color32(90, 120, 96, 255),   // green
        };

        public static Car Create(Vector3 position, float yaw, int seed)
        {
            var root = new GameObject("Car");
            root.transform.SetPositionAndRotation(position, Quaternion.Euler(0, yaw, 0));
            var car = root.AddComponent<Car>();
            var mat = GameMaterials.Instance != null ? GameMaterials.Instance.vehicleMaterial : null;

            car.body = new GameObject("Body").transform;
            car.body.SetParent(root.transform, false);
            var paint = Paints[Mathf.Abs(seed) % Paints.Length];
            Add(car.body.gameObject, BodyMesh(paint), mat);

            foreach (var (x, z) in new[] { (-1f, 1f), (1f, 1f), (-1f, -1f), (1f, -1f) })
            {
                var w = new GameObject("Wheel").transform;
                w.SetParent(car.body, false);
                w.localPosition = new Vector3(x * (Width * 0.5f - 0.12f), WheelRadius, z * (Length * 0.5f - 0.7f));
                Add(w.gameObject, WheelMesh(), mat);
                car.wheels.Add(w);
            }
            SetLayerRecursive(root);

            // Solid body while parked (Default layer): the player bumps into it, the camera keeps out of it.
            var col = new GameObject("Collision");
            col.transform.SetParent(root.transform, false);
            var box = col.AddComponent<BoxCollider>();
            box.size = new Vector3(Width, 1.45f, Length);
            box.center = new Vector3(0f, 0.28f + 0.72f, 0f);
            car.solid = box;
            return car;
        }

        static void Add(GameObject go, Mesh mesh, Material mat)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        public override void Mount(Transform rider)
        {
            base.Mount(rider);
            lastYaw = rider.eulerAngles.y;
            if (solid != null) solid.enabled = false; // the driver's capsule takes over collisions
        }

        public override void Dismount(Vector3 parkPosition)
        {
            base.Dismount(parkPosition);
            body.localRotation = Quaternion.identity;
            if (solid != null) solid.enabled = true;
        }

        public override void Animate(float speed)
        {
            float spin = speed / WheelRadius * Mathf.Rad2Deg * Time.deltaTime;
            foreach (var w in wheels) w.Rotate(spin, 0, 0, Space.Self);
            float yaw = transform.eulerAngles.y;
            float yawRate = Mathf.DeltaAngle(lastYaw, yaw) / Mathf.Max(Time.deltaTime, 0.0001f);
            lastYaw = yaw;
            float target = Mathf.Clamp(yawRate * speed * 0.0015f, -4f, 4f); // roll outwards a little
            roll = Mathf.SmoothDamp(roll, target, ref rollVel, 0.2f);
            body.localRotation = Quaternion.Euler(0, 0, roll);
        }

        // ---------------------------------------------------------------- meshes

        static readonly Dictionary<int, Mesh> bodyCache = new Dictionary<int, Mesh>();
        static Mesh wheelMesh;

        static readonly Color32 Glass = new Color32(40, 52, 62, 255);
        static readonly Color32 Trim = new Color32(36, 38, 40, 255);
        static readonly Color32 Light = new Color32(250, 240, 210, 255);
        static readonly Color32 Tail = new Color32(200, 40, 36, 255);

        class B
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

            /// <summary>Box between bottom rectangle (y0, half extents hx0/hz0) and top (y1, hx1/hz1, z shift).</summary>
            public void Frustum(float y0, float y1, float hx0, float hz0, float hx1, float hz1, float zShift, Color32 side, Color32 front, Color32 back, Color32 top)
            {
                Vector3 b0 = new Vector3(-hx0, y0, -hz0), b1 = new Vector3(hx0, y0, -hz0), b2 = new Vector3(hx0, y0, hz0), b3 = new Vector3(-hx0, y0, hz0);
                Vector3 t0 = new Vector3(-hx1, y1, -hz1 + zShift), t1 = new Vector3(hx1, y1, -hz1 + zShift), t2 = new Vector3(hx1, y1, hz1 + zShift), t3 = new Vector3(-hx1, y1, hz1 + zShift);
                Quad(b0, t0, t1, b1, back);   // rear (-z)
                Quad(b1, t1, t2, b2, side);   // right (+x)
                Quad(b2, t2, t3, b3, front);  // front (+z)
                Quad(b3, t3, t0, b0, side);   // left (-x)
                Quad(t0, t3, t2, t1, top);    // top
            }

            public Mesh Mesh(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(v); m.SetNormals(n); m.SetColors(c); m.SetTriangles(t, 0);
                m.RecalculateBounds();
                return m;
            }
        }

        static Mesh BodyMesh(Color32 paint)
        {
            int key = paint.r << 16 | paint.g << 8 | paint.b;
            if (bodyCache.TryGetValue(key, out var cached)) return cached;
            var b = new B();
            float hx = Width * 0.5f, hz = Length * 0.5f;
            // Lower body with a darker sill, slightly tapered bonnet.
            b.Frustum(0.28f, 0.42f, hx, hz, hx, hz, 0f, Trim, Trim, Trim, paint);
            b.Frustum(0.42f, 0.95f, hx, hz, hx - 0.05f, hz - 0.12f, 0f, paint, paint, paint, paint);
            // Cabin: glass sides, set back from the bonnet.
            b.Frustum(0.95f, 1.5f, hx - 0.1f, 1.05f, hx - 0.22f, 0.72f, -0.25f, Glass, Glass, Glass, paint);
            // Lights.
            foreach (float s in new[] { -1f, 1f })
            {
                // Headlights face +z, tail lights −z (vertex order decides the facing).
                b.Quad(new Vector3(s * 0.55f + 0.2f, 0.7f, hz + 0.001f), new Vector3(s * 0.55f + 0.2f, 0.85f, hz + 0.001f),
                       new Vector3(s * 0.55f - 0.2f, 0.85f, hz + 0.001f), new Vector3(s * 0.55f - 0.2f, 0.7f, hz + 0.001f), Light);
                b.Quad(new Vector3(s * 0.6f - 0.18f, 0.72f, -hz - 0.001f), new Vector3(s * 0.6f - 0.18f, 0.86f, -hz - 0.001f),
                       new Vector3(s * 0.6f + 0.18f, 0.86f, -hz - 0.001f), new Vector3(s * 0.6f + 0.18f, 0.72f, -hz - 0.001f), Tail);
            }
            return bodyCache[key] = b.Mesh("CarBody");
        }

        static Mesh WheelMesh()
        {
            if (wheelMesh != null) return wheelMesh;
            var b = new B();
            const int seg = 12;
            float r = WheelRadius, hw = 0.12f;
            var rim = new Color32(170, 174, 178, 255);
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg;
                Vector3 o0 = new Vector3(0, Mathf.Sin(a0), Mathf.Cos(a0)) * r, o1 = new Vector3(0, Mathf.Sin(a1), Mathf.Cos(a1)) * r;
                Vector3 L = Vector3.left * hw, R = Vector3.right * hw;
                b.Quad(o0 + R, o1 + R, o1 + L, o0 + L, Trim);          // tread (faces outward/radially)
                b.Quad(o1 + R, o0 + R, Vector3.right * hw, Vector3.right * hw, i % 2 == 0 ? rim : Trim); // outer face (+x)
                b.Quad(o0 + L, o1 + L, Vector3.left * hw, Vector3.left * hw, i % 2 == 0 ? rim : Trim);   // inner face (-x)
            }
            return wheelMesh = b.Mesh("CarWheel");
        }
    }
}
