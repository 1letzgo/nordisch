using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LeGamez.Quests
{
    /// <summary>Persistent record of visited POIs across all areas (JSON in persistentDataPath).</summary>
    public static class StampBook
    {
        [System.Serializable]
        class SaveData
        {
            public List<string> stamps = new List<string>();
        }

        static HashSet<string> stamps;
        static string FilePath => Path.Combine(Application.persistentDataPath, "stampbook.json");

        static HashSet<string> Stamps
        {
            get
            {
                if (stamps != null) return stamps;
                stamps = new HashSet<string>();
                try
                {
                    if (File.Exists(FilePath))
                        stamps.UnionWith(JsonUtility.FromJson<SaveData>(File.ReadAllText(FilePath)).stamps);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[StampBook] Could not read save: {e.Message}");
                }
                return stamps;
            }
        }

        static string Key(string areaId, string poiId) => areaId + ":" + poiId;

        public static bool Has(string areaId, string poiId) => Stamps.Contains(Key(areaId, poiId));

        /// <summary>Returns true if this was a new stamp.</summary>
        public static bool Add(string areaId, string poiId)
        {
            if (!Stamps.Add(Key(areaId, poiId))) return false;
            Save();
            return true;
        }

        public static int CountForArea(string areaId)
        {
            int n = 0;
            string prefix = areaId + ":";
            foreach (var s in Stamps) if (s.StartsWith(prefix)) n++;
            return n;
        }

        static void Save()
        {
            var data = new SaveData { stamps = new List<string>(Stamps) };
            File.WriteAllText(FilePath, JsonUtility.ToJson(data));
        }
    }
}
