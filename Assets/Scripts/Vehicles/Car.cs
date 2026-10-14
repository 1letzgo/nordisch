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
        float wheelSpin;

        Transform body;
        BoxCollider solid;
        readonly List<Transform> wheels = new List<Transform>();
        float roll, rollVel, lastYaw;

        public override float CruiseSpeed => 14f;
        public override float SprintSpeed => 24f;
        public override float TurnSmoothTime => 0.32f;
        public override float CameraBonus => 3.2f;
        public override int AvatarMode => 1;
        public override Vector3 SeatOffset => new Vector3(-0.35f, 0.25f, 0.1f);
        public override bool HideRider => true;
        public override float ColliderRadius => 0.95f;
        public override float AxleBase => Length - 1.4f;          // wheels sit 0.7 m in from each end
        public override float RearAxle => Length * 0.5f - 0.7f;
        public override float MaxSteer => 38f;                    // ~3.2 m turning radius at walking pace
        public override float LateralAccel => 12f;                // arcade grip: tight turns at town speed
        public override float TopSpeed => 25f;                    // ~90 km/h
        /// <summary>Top speed on motorways and their ramps (VehicleManager, from the area's street index): ~130 km/h.</summary>
        public const float MotorwaySpeed = 36.5f;
        public override float AccelTime => 4.5f;
        public override float BrakeDecel => 11f;
        public override float CoastDecel => 1.6f;
        public override float ReverseSpeed => 6f;
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
                w.localPosition = new Vector3(x * (Width * 0.5f - 0.08f), WheelRadius, z * (Length * 0.5f - 0.7f));
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
            // Rolling + steering: front wheels (first two) turn by the steering angle.
            wheelSpin = (wheelSpin + speed / WheelRadius * Mathf.Rad2Deg * Time.deltaTime) % 360f;
            for (int i = 0; i < wheels.Count; i++)
                wheels[i].localRotation = Quaternion.Euler(0f, i < 2 ? SteerAngle : 0f, 0f) * Quaternion.Euler(wheelSpin, 0f, 0f);
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

            /// <summary>Triangle facing <paramref name="outward"/> (winding fixed up automatically).</summary>
            public void Tri(Vector3 a, Vector3 b, Vector3 cc, Vector3 outward, Color32 col)
            {
                var nrm = Vector3.Cross(b - a, cc - a);
                if (Vector3.Dot(nrm, outward) < 0f) { (b, cc) = (cc, b); nrm = -nrm; }
                nrm.Normalize();
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(cc);
                for (int k = 0; k < 3; k++) { n.Add(nrm); c.Add(col); }
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
            }

            /// <summary>Quad as two triangles (may be non-planar), facing <paramref name="outward"/>.</summary>
            public void Face(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Vector3 outward, Color32 col)
            {
                Tri(a, b, cc, outward, col);
                Tri(a, cc, d, outward, col);
            }

            /// <summary>Flat rectangle centred at <paramref name="c0"/> spanned by half axes u, w, facing outward.</summary>
            public void Rect(Vector3 c0, Vector3 u, Vector3 w, Vector3 outward, Color32 col) =>
                Face(c0 - u - w, c0 + u - w, c0 + u + w, c0 - u + w, outward, col);

            /// <summary>Axis-aligned box (all six faces).</summary>
            public void Box(Vector3 c0, Vector3 h, Color32 col)
            {
                Rect(c0 + Vector3.right * h.x, Vector3.forward * h.z, Vector3.up * h.y, Vector3.right, col);
                Rect(c0 - Vector3.right * h.x, Vector3.forward * h.z, Vector3.up * h.y, Vector3.left, col);
                Rect(c0 + Vector3.forward * h.z, Vector3.right * h.x, Vector3.up * h.y, Vector3.forward, col);
                Rect(c0 - Vector3.forward * h.z, Vector3.right * h.x, Vector3.up * h.y, Vector3.back, col);
                Rect(c0 + Vector3.up * h.y, Vector3.right * h.x, Vector3.forward * h.z, Vector3.up, col);
                Rect(c0 - Vector3.up * h.y, Vector3.right * h.x, Vector3.forward * h.z, Vector3.down, col);
            }

            public Mesh Mesh(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(v); m.SetNormals(n); m.SetColors(c); m.SetTriangles(t, 0);
                m.RecalculateBounds();
                return m;
            }
        }

        static readonly Color32 Plate = new Color32(236, 236, 230, 255);
        static readonly Color32 PlateBlue = new Color32(30, 60, 150, 255);
        static readonly Color32 Indicator = new Color32(235, 150, 40, 255);

        /// <summary>Cross-section of the body at <c>z</c>: top height and beltline; the hull is lofted between them.</summary>
        struct Section
        {
            public float z, top, topHalf;
            public Section(float z, float top, float topHalf) { this.z = z; this.top = top; this.topHalf = topHalf; }
        }

        // Hatchback silhouette, rear (-z) to front (+z).
        static readonly Section[] Sections =
        {
            new Section(-1.95f, 1.18f, 0.66f),  // 0 bottom of the hatch window
            new Section(-1.68f, 1.46f, 0.6f),   // 1 roof rear edge
            new Section(-0.55f, 1.49f, 0.6f),   // 2 C-pillar / rear door
            new Section(-0.05f, 1.49f, 0.6f),   // 3 B-pillar
            new Section(0.05f, 1.49f, 0.6f),    // 4 B-pillar
            new Section(0.42f, 1.47f, 0.62f),   // 5 windscreen top
            new Section(1.2f, 0.98f, 0.8f),     // 6 windscreen bottom / bonnet rear
            new Section(1.85f, 0.88f, 0.8f),    // 7 bonnet front
            new Section(1.95f, 0.76f, 0.78f),   // 8 nose
        };

        const float Sill = 0.3f, Kick = 0.46f, Belt = 0.92f;

        /// <summary>Right half of a section, bottom to top: sill, lower edge, beltline, roof edge.</summary>
        static Vector3[] Half(Section s, float hx, float sign)
        {
            float belt = Mathf.Min(Belt, s.top - 0.04f);
            return new[]
            {
                new Vector3(sign * (hx - 0.1f), Sill, s.z),
                new Vector3(sign * hx, Kick, s.z),
                new Vector3(sign * hx, belt, s.z),
                new Vector3(sign * s.topHalf, s.top, s.z),
            };
        }

        static Mesh BodyMesh(Color32 paint)
        {
            int key = paint.r << 16 | paint.g << 8 | paint.b;
            if (bodyCache.TryGetValue(key, out var cached)) return cached;
            var b = new B();
            float hx = Width * 0.5f, hz = Length * 0.5f;
            var dark = new Color32((byte)(paint.r * 0.75f), (byte)(paint.g * 0.75f), (byte)(paint.b * 0.75f), 255);

            // Hull: loft between consecutive sections; strips are sill (trim), flank (paint), window band, roof.
            for (int i = 0; i < Sections.Length - 1; i++)
            {
                Section s0 = Sections[i], s1 = Sections[i + 1];
                bool sideGlass = i == 2 || i == 4 || i == 5;           // rear door, front door, front quarter
                bool roofGlass = i == 0 || i == 5;                     // hatch window, windscreen
                foreach (float sign in new[] { 1f, -1f })
                {
                    var a = Half(s0, hx, sign);
                    var c = Half(s1, hx, sign);
                    for (int k = 0; k < 3; k++)
                    {
                        Color32 col = k == 0 ? Trim : k == 1 ? paint : sideGlass ? Glass : paint;
                        var mid = (a[k] + a[k + 1] + c[k] + c[k + 1]) * 0.25f;
                        b.Face(a[k], a[k + 1], c[k + 1], c[k], new Vector3(mid.x, mid.y - 0.7f, 0f), col);
                    }
                }
                // Roof strip across the top.
                var l0 = new Vector3(-s0.topHalf, s0.top, s0.z); var r0 = new Vector3(s0.topHalf, s0.top, s0.z);
                var l1 = new Vector3(-s1.topHalf, s1.top, s1.z); var r1 = new Vector3(s1.topHalf, s1.top, s1.z);
                var up = Vector3.Cross(r1 - r0, l0 - r0);
                if (up.y < 0f) up = -up;
                b.Face(l0, r0, r1, l1, up, roofGlass ? Glass : paint);
                // Underside.
                b.Face(new Vector3(-hx + 0.1f, Sill, s0.z), new Vector3(hx - 0.1f, Sill, s0.z), new Vector3(hx - 0.1f, Sill, s1.z),
                       new Vector3(-hx + 0.1f, Sill, s1.z), Vector3.down, Trim);
            }
            // End caps: rear hatch and nose, fanned from the section outline.
            Cap(b, Sections[0], hx, Vector3.back, paint);
            Cap(b, Sections[Sections.Length - 1], hx, Vector3.forward, paint);
            float rz = Sections[0].z, fz = Sections[Sections.Length - 1].z;

            // Bumpers (dark, slightly proud of the body).
            b.Box(new Vector3(0f, 0.4f, rz - 0.03f), new Vector3(hx + 0.02f, 0.11f, 0.06f), Trim);
            b.Box(new Vector3(0f, 0.4f, fz + 0.03f), new Vector3(hx + 0.02f, 0.11f, 0.06f), Trim);
            // Front: grille, headlights with indicators, number plate.
            b.Rect(new Vector3(0f, 0.62f, fz + 0.005f), Vector3.right * 0.42f, Vector3.up * 0.07f, Vector3.forward, Trim);
            foreach (float sg in new[] { -1f, 1f })
            {
                b.Rect(new Vector3(sg * 0.6f, 0.66f, fz + 0.006f), Vector3.right * 0.17f, Vector3.up * 0.06f, Vector3.forward, Light);
                b.Rect(new Vector3(sg * 0.82f, 0.66f, fz - 0.02f), Vector3.right * 0.04f, Vector3.up * 0.05f, Vector3.forward, Indicator);
                // Tail lights wrap round the corner a little.
                b.Rect(new Vector3(sg * 0.66f, 0.84f, rz - 0.006f), Vector3.right * 0.13f, Vector3.up * 0.07f, Vector3.back, Tail);
                b.Rect(new Vector3(sg * (hx + 0.004f), 0.84f, rz + 0.08f), Vector3.forward * 0.08f, Vector3.up * 0.07f, Vector3.right * sg, Tail);
            }
            NumberPlate(b, new Vector3(0f, 0.4f, fz + 0.092f), Vector3.forward);
            NumberPlate(b, new Vector3(0f, 0.72f, rz - 0.006f), Vector3.back);
            // Hatch handle strip.
            b.Rect(new Vector3(0f, 0.96f, rz - 0.007f), Vector3.right * 0.25f, Vector3.up * 0.02f, Vector3.back, Trim);

            foreach (float sg in new[] { -1f, 1f })
            {
                var outv = Vector3.right * sg;
                float x = sg * (hx + 0.003f);
                // Door seams and handles.
                foreach (float z in new[] { -0.55f, 0.0f, 1.12f })
                    b.Rect(new Vector3(x, (Kick + Belt) * 0.5f, z), Vector3.forward * 0.012f, Vector3.up * (Belt - Kick) * 0.5f, outv, dark);
                foreach (float z in new[] { -0.42f, 0.14f })
                    b.Rect(new Vector3(x, Belt - 0.1f, z), Vector3.forward * 0.09f, Vector3.up * 0.018f, outv, Trim);
                // Mirror on a short arm at the base of the A-pillar.
                b.Box(new Vector3(sg * (hx + 0.1f), Belt + 0.06f, 1.05f), new Vector3(0.09f, 0.06f, 0.05f), paint);
                b.Rect(new Vector3(sg * (hx + 0.1f), Belt + 0.06f, 0.995f), Vector3.right * 0.07f, Vector3.up * 0.045f, Vector3.back, Glass);
                // Wheel arches: dark flare around each wheel.
                foreach (float wz in new[] { -(Length * 0.5f - 0.7f), Length * 0.5f - 0.7f })
                    Arch(b, new Vector3(sg * (hx + 0.006f), WheelRadius, wz), outv);
            }
            return bodyCache[key] = b.Mesh("CarBody");
        }

        static void Cap(B b, Section s, float hx, Vector3 outward, Color32 paint)
        {
            var r = Half(s, hx, 1f);
            var l = Half(s, hx, -1f);
            var ring = new List<Vector3> { r[0], r[1], r[2], r[3], l[3], l[2], l[1], l[0] };
            var centre = Vector3.zero;
            foreach (var p in ring) centre += p;
            centre /= ring.Count;
            for (int i = 0; i < ring.Count; i++)
            {
                var p0 = ring[i]; var p1 = ring[(i + 1) % ring.Count];
                b.Tri(centre, p0, p1, outward, paint);
            }
        }

        static void NumberPlate(B b, Vector3 c, Vector3 outward)
        {
            b.Rect(c, Vector3.right * 0.26f, Vector3.up * 0.06f, outward, Plate);
            b.Rect(c + Vector3.left * 0.235f + outward * 0.001f, Vector3.right * 0.025f, Vector3.up * 0.06f, outward, PlateBlue);
            // Suggested lettering: three dark blocks.
            for (int k = 0; k < 3; k++)
                b.Rect(c + new Vector3(-0.1f + k * 0.12f, 0f, 0f) + outward * 0.001f, Vector3.right * 0.04f, Vector3.up * 0.035f, outward, Trim);
        }

        /// <summary>Half ring (arch) above a wheel on the body side.</summary>
        static void Arch(B b, Vector3 c, Vector3 outward)
        {
            const int seg = 8;
            float r0 = WheelRadius + 0.04f, r1 = WheelRadius + 0.1f;
            for (int i = 0; i < seg; i++)
            {
                float a0 = Mathf.PI * i / seg, a1 = Mathf.PI * (i + 1) / seg;
                Vector3 d0 = new Vector3(0f, Mathf.Sin(a0), Mathf.Cos(a0)), d1 = new Vector3(0f, Mathf.Sin(a1), Mathf.Cos(a1));
                b.Face(c + d0 * r0, c + d0 * r1, c + d1 * r1, c + d1 * r0, outward, Trim);
            }
        }

        static Mesh WheelMesh()
        {
            if (wheelMesh != null) return wheelMesh;
            var b = new B();
            const int seg = 15;
            float r = WheelRadius, rimR = WheelRadius * 0.62f, hubR = WheelRadius * 0.18f, hw = 0.11f;
            var rim = new Color32(176, 180, 186, 255);
            var rimDark = new Color32(70, 72, 76, 255);
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg;
                Vector3 d0 = new Vector3(0, Mathf.Sin(a0), Mathf.Cos(a0)), d1 = new Vector3(0, Mathf.Sin(a1), Mathf.Cos(a1));
                Vector3 L = Vector3.left * hw, R = Vector3.right * hw;
                b.Face(d0 * r + R, d1 * r + R, d1 * r + L, d0 * r + L, (d0 + d1), Trim);                 // tread
                foreach (float sg in new[] { 1f, -1f })
                {
                    var side = Vector3.right * sg * hw;
                    var inset = Vector3.right * sg * (hw - 0.03f);
                    var outv = Vector3.right * sg;
                    b.Face(d0 * r + side, d1 * r + side, d1 * rimR + side, d0 * rimR + side, outv, Trim);          // sidewall
                    b.Face(d0 * rimR + side, d1 * rimR + side, d1 * rimR + inset, d0 * rimR + inset, -(d0 + d1), rim); // rim lip
                    // Dish: five spokes (every third segment is a dark gap) and a hub cap.
                    bool gap = i % 3 == 1;
                    b.Face(d0 * rimR + inset, d1 * rimR + inset, d1 * hubR + inset, d0 * hubR + inset, outv, gap ? rimDark : rim);
                    b.Tri(d0 * hubR + inset, d1 * hubR + inset, side * 1.02f, outv, rim);
                }
            }
            return wheelMesh = b.Mesh("CarWheel");
        }
    }
}
