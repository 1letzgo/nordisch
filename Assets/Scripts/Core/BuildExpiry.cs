using System;
using System.Globalization;
using UnityEngine;

namespace LeGamez.Core
{
    /// <summary>
    /// Release builds expire: BuildScript.BuildAndroidRelease embeds Resources/BuildInfo.txt
    /// ("version=…", "expires=yyyy-MM-ddTHH:mm:ssZ"), only for the duration of that build. Without the file
    /// (editor, development and device builds) nothing expires.
    /// </summary>
    public static class BuildExpiry
    {
        public const string ReleasesUrl = "https://github.com/1letzgo/nordisch/releases/latest";

        static bool loaded;
        static DateTime? expires;
        static string version;

        public static DateTime? Expires { get { Load(); return expires; } }
        public static string Version { get { Load(); return version; } }
        public static bool IsExpired => Expires.HasValue && DateTime.UtcNow > Expires.Value;

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            var info = Resources.Load<TextAsset>("BuildInfo");
            if (info == null) return;
            foreach (var line in info.text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (key == "version") version = value;
                else if (key == "expires" && DateTime.TryParse(value, CultureInfo.InvariantCulture,
                             DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                    expires = d;
            }
        }
    }
}
