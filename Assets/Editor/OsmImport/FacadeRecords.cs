using System.Collections.Generic;
using System.IO;
using LeGamez.World;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Collects close-up facade detail (windows, doors, gutters) during the import as compact records for the
    /// runtime <see cref="FacadeDetails"/> builder. Positions are world space (terrain offset applied).
    /// </summary>
    public static class FacadeRecords
    {
        static readonly List<float> rec = new List<float>();
        public static int Windows, Doors, Gutters, Verges, Ridges;

        public static void Begin() { rec.Clear(); Windows = Doors = Gutters = Verges = Ridges = 0; }

        /// <param name="along">Horizontal wall direction; the outward normal must be (-along.z, along.x).</param>
        static void Add(int kind, Vector3 o, Vector3 along, float a, float b, float c, Color32 colour)
        {
            along.y = 0f;
            along.Normalize();
            rec.Add(kind); rec.Add(o.x); rec.Add(o.y); rec.Add(o.z); rec.Add(along.x); rec.Add(along.z);
            rec.Add(a); rec.Add(b); rec.Add(c); rec.Add(FacadeDetails.Pack(colour));
        }

        /// <summary>Along vector whose outward normal (-z, x) equals <paramref name="normal"/>.</summary>
        public static Vector3 AlongFor(Vector3 normal) => new Vector3(normal.z, 0f, -normal.x).normalized;

        public static void Window(Vector3 bottomCentre, Vector3 normal, float halfWidth, float height, bool shop, bool brick, Color32 wall)
        {
            int flags = (brick ? FacadeDetails.FlagBrick : 0) | (shop ? 0 : FacadeDetails.FlagMullion);
            Add(shop ? FacadeDetails.KindShop : FacadeDetails.KindWindow, bottomCentre, AlongFor(normal), halfWidth, height, flags, wall);
            Windows++;
        }

        public static void Door(Vector3 bottomCentre, Vector3 normal, float halfWidth, float height)
        {
            Add(FacadeDetails.KindDoor, bottomCentre, AlongFor(normal), halfWidth, height, 0f, new Color32(0, 0, 0, 255));
            Doors++;
        }

        /// <summary>Gutter from a to b (same height), hanging on the side of <paramref name="outward"/>; downpipe to drop metres below.</summary>
        public static void Gutter(Vector3 a, Vector3 b, Vector3 outward, float drop)
        {
            var along = AlongFor(outward);
            if (Vector3.Dot(b - a, along) < 0f) (a, b) = (b, a);
            float len = new Vector2(b.x - a.x, b.z - a.z).magnitude;
            int end = (Mathf.RoundToInt(a.x * 3f) + Mathf.RoundToInt(a.z * 7f)) & 1;
            Add(FacadeDetails.KindGutter, a, along, len, drop, end, new Color32(0, 0, 0, 255));
            Gutters++;
        }

        /// <summary>Sloped gable edge a–b; board on the <paramref name="outward"/> side.</summary>
        public static void Verge(Vector3 a, Vector3 b, Vector3 outward)
        {
            var along = AlongFor(outward);
            if (Vector3.Dot(b - a, along) < 0f) (a, b) = (b, a);
            float len = new Vector2(b.x - a.x, b.z - a.z).magnitude;
            if (len < 0.5f) return;
            Add(FacadeDetails.KindVerge, a, along, len, b.y - a.y, 0f, new Color32(0, 0, 0, 255));
            Verges++;
        }

        public static void Ridge(Vector3 a, Vector3 b, Color32 roof)
        {
            var d = new Vector3(b.x - a.x, 0f, b.z - a.z);
            float len = d.magnitude;
            if (len < 0.8f) return;
            Add(FacadeDetails.KindRidge, a, d / len, len, 0f, 0f, roof);
            Ridges++;
        }

        /// <summary>Plinth band along a wall's bottom edge a–b.</summary>
        public static void Plinth(Vector3 a, Vector3 b, Vector3 outward, float height, Color32 colour)
        {
            var along = AlongFor(outward);
            if (Vector3.Dot(b - a, along) < 0f) (a, b) = (b, a);
            float len = new Vector2(b.x - a.x, b.z - a.z).magnitude;
            if (len < 0.4f) return;
            Add(FacadeDetails.KindPlinth, a, along, len, height, 0f, colour);
        }

        public static void Save(string path)
        {
            var bytes = new byte[rec.Count * 4];
            System.Buffer.BlockCopy(rec.ToArray(), 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(path, bytes);
        }
    }
}
