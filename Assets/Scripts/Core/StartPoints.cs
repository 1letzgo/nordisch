using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LeGamez.Core
{
    /// <summary>
    /// The player's own start points ("Hier als Startpunkt merken" in the full map): area + position + view direction,
    /// named after the street. Listed in the menu and in "Andere Orte" next to the areas' default start points.
    /// Stored in persistentDataPath/startpoints.json (positions are local to the area; a re-centred area simply drops them).
    /// </summary>
    public static class StartPoints
    {
        [Serializable]
        public class Point
        {
            public string id;
            public string areaId;
            public string areaKey; // AreaInfo.MemoryKey when saved – a moved/resized area invalidates the point
            public string name;
            public Vector3 position;
            public float yaw;
            public long created;
        }

        [Serializable] class Store { public List<Point> points = new List<Point>(); }

        static List<Point> points;
        public static int Revision { get; private set; }

        /// <summary>Where the next world start puts the player instead of spawn / last position (set by MapStore.Start).</summary>
        public static Point Pending;

        static string Path => System.IO.Path.Combine(Application.persistentDataPath, "startpoints.json");

        public static List<Point> All
        {
            get
            {
                if (points == null)
                {
                    points = new List<Point>();
                    try { if (File.Exists(Path)) points = JsonUtility.FromJson<Store>(File.ReadAllText(Path)).points ?? new List<Point>(); }
                    catch (Exception e) { Debug.LogWarning("[StartPoints] " + e.Message); }
                }
                return points;
            }
        }

        public static Point Add(string areaId, string areaKey, string name, Vector3 position, float yaw)
        {
            var p = new Point
            {
                id = Guid.NewGuid().ToString("N"), areaId = areaId, areaKey = areaKey, name = name,
                position = position, yaw = yaw, created = DateTime.UtcNow.Ticks,
            };
            All.Add(p);
            Save();
            return p;
        }

        public static void Remove(Point p)
        {
            if (All.Remove(p)) Save();
        }

        static void Save()
        {
            Revision++;
            try { File.WriteAllText(Path, JsonUtility.ToJson(new Store { points = All })); }
            catch (Exception e) { Debug.LogWarning("[StartPoints] " + e.Message); }
        }
    }
}
