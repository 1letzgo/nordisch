using System.Globalization;
using UnityEngine;

namespace LeGamez.Core
{
    /// <summary>Remembers the player's last position and heading per area (PlayerPrefs).</summary>
    public class PositionMemory : MonoBehaviour
    {
        const float SaveInterval = 3f;

        public string areaId;
        float timer;

        /// <summary>Set by the vehicle system: 0 = on foot, 1 = bicycle, 2 = car.</summary>
        public static System.Func<int> RidingKind;

        public static int LastRidingKind(string areaId) => PlayerPrefs.GetInt(Key(areaId) + ".ride", 0);

        static string Key(string areaId) => "legamez.pos." + areaId;

        public static bool TryLoad(string areaId, out Vector3 position, out float yaw)
        {
            position = default;
            yaw = 0f;
            var s = PlayerPrefs.GetString(Key(areaId), "");
            var parts = s.Split(';');
            if (parts.Length != 4) return false;
            var ci = CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[0], NumberStyles.Float, ci, out position.x) ||
                !float.TryParse(parts[1], NumberStyles.Float, ci, out position.y) ||
                !float.TryParse(parts[2], NumberStyles.Float, ci, out position.z) ||
                !float.TryParse(parts[3], NumberStyles.Float, ci, out yaw)) return false;
            return true;
        }

        public static void Forget(string areaId) => PlayerPrefs.DeleteKey(Key(areaId));

        /// <summary>Next start in this area is on foot (a fresh start at a default start point).</summary>
        public static void ForgetRide(string areaId) => PlayerPrefs.DeleteKey(Key(areaId) + ".ride");

        void Save()
        {
            if (string.IsNullOrEmpty(areaId)) return;
            var p = transform.position;
            var ci = CultureInfo.InvariantCulture;
            PlayerPrefs.SetString(Key(areaId),
                string.Join(";", p.x.ToString("F2", ci), p.y.ToString("F2", ci), p.z.ToString("F2", ci), transform.eulerAngles.y.ToString("F1", ci)));
            PlayerPrefs.SetInt(Key(areaId) + ".ride", RidingKind != null ? RidingKind() : 0);
            PlayerPrefs.Save();
        }

        void Update()
        {
            timer += Time.deltaTime;
            if (timer < SaveInterval) return;
            timer = 0f;
            Save();
        }

        // Leaving to the menu, backgrounding or closing the app all keep the latest spot.
        void OnDestroy() => Save();
        void OnApplicationPause(bool paused) { if (paused) Save(); }
        void OnApplicationQuit() => Save();
    }
}
