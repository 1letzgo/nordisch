using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace LeGamez.Core
{
    /// <summary>
    /// In-app update for Android release builds (those with a <see cref="BuildExpiry.Version"/>): asks GitHub for the
    /// latest release of 1letzgo/nordisch, downloads its APK and hands it to Android's installer
    /// (Plugins/Android/NordischUpdater.java). Device/dev builds never check.
    /// </summary>
    public static class AppUpdater
    {
        const string LatestApi = "https://api.github.com/repos/1letzgo/nordisch/releases/latest";

        public enum State { Idle, Checking, UpToDate, Available, Downloading, NeedsPermission, Installing, Failed }

        public static State Current { get; private set; } = State.Idle;
        public static string LatestVersion { get; private set; }
        public static float Progress { get; private set; }
        public static long SizeBytes { get; private set; }
        public static string Error { get; private set; }
        public static bool Supported => Application.platform == RuntimePlatform.Android && BuildExpiry.Version != null;

        static string apkUrl;
        static Runner runner;

        [Serializable] class Asset { public string name; public string browser_download_url; public long size; }
        [Serializable] class Release { public string tag_name; public Asset[] assets; }

        /// <summary>Starts the check once per app run (main menu).</summary>
        public static void CheckOnce()
        {
            if (!Supported || Current != State.Idle) return;
            Run(Check());
        }

        /// <summary>Download + install the available update (button in the menu banner / expiry screen).</summary>
        public static void Install()
        {
            if (Current == State.Available || Current == State.Failed || Current == State.NeedsPermission)
                Run(DownloadAndInstall());
        }

        static void Run(IEnumerator routine)
        {
            if (runner == null)
            {
                runner = new GameObject("AppUpdater").AddComponent<Runner>();
                UnityEngine.Object.DontDestroyOnLoad(runner.gameObject);
            }
            runner.StartCoroutine(routine);
        }

        class Runner : MonoBehaviour
        {
            // Back from the "install unknown apps" settings page: continue right away.
            void OnApplicationFocus(bool focus)
            {
                if (focus && Current == State.NeedsPermission && CanInstall()) Install();
            }
        }

        static IEnumerator Check()
        {
            Current = State.Checking;
            using var req = UnityWebRequest.Get(LatestApi);
            req.SetRequestHeader("Accept", "application/vnd.github+json");
            req.timeout = 15;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) { Current = State.Idle; yield break; } // offline: try next start
            var rel = JsonUtility.FromJson<Release>(req.downloadHandler.text);
            string latest = rel?.tag_name?.TrimStart('v');
            Asset apk = null;
            if (rel?.assets != null) foreach (var a in rel.assets) if (a.name.EndsWith(".apk")) { apk = a; break; }
            if (latest == null || apk == null || Compare(latest, BuildExpiry.Version) <= 0) { Current = State.UpToDate; yield break; }
            LatestVersion = latest;
            apkUrl = apk.browser_download_url;
            SizeBytes = apk.size;
            Current = State.Available;
        }

        static IEnumerator DownloadAndInstall()
        {
            if (!CanInstall())
            {
                Current = State.NeedsPermission;
                using var updater = new AndroidJavaClass("de.letzgo.legamez.NordischUpdater");
                updater.CallStatic("openInstallPermission", Activity());
                yield break;
            }
            string path = Path.Combine(Application.temporaryCachePath, "update.apk");
            Current = State.Downloading;
            Progress = 0f;
            using (var req = new UnityWebRequest(apkUrl, UnityWebRequest.kHttpVerbGET))
            {
                req.downloadHandler = new DownloadHandlerFile(path) { removeFileOnAbort = true };
                var op = req.SendWebRequest();
                while (!op.isDone) { Progress = req.downloadProgress; yield return null; }
                if (req.result != UnityWebRequest.Result.Success) { Fail("Download: " + req.error); yield break; }
            }
            Progress = 1f;
            Current = State.Installing;
            using (var updater = new AndroidJavaClass("de.letzgo.legamez.NordischUpdater"))
            {
                updater.CallStatic("install", Activity(), path);
                // The installer dialog takes over; on success Android restarts the app, so only errors come back here.
                while (true)
                {
                    yield return new WaitForSecondsRealtime(0.5f);
                    string s = updater.CallStatic<string>("getStatus");
                    if (s.StartsWith("error")) { Fail(s); yield break; }
                    if (s == "ok") yield break;
                }
            }
        }

        static void Fail(string message)
        {
            Error = message;
            Current = State.Failed;
            Debug.LogWarning("[LeGamez] Update failed: " + message);
        }

        static bool CanInstall()
        {
            using var updater = new AndroidJavaClass("de.letzgo.legamez.NordischUpdater");
            return updater.CallStatic<bool>("canInstall", Activity());
        }

        static AndroidJavaObject Activity()
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            return player.GetStatic<AndroidJavaObject>("currentActivity");
        }

        /// <summary>Dotted version compare ("2026.10.13.2" &gt; "2026.10.13").</summary>
        public static int Compare(string a, string b)
        {
            var pa = a.Split('.');
            var pb = b.Split('.');
            for (int i = 0; i < Mathf.Max(pa.Length, pb.Length); i++)
            {
                int x = i < pa.Length && int.TryParse(pa[i], out var xi) ? xi : 0;
                int y = i < pb.Length && int.TryParse(pb[i], out var yi) ? yi : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        /// <summary>Button/banner text for the current state.</summary>
        public static string Label()
        {
            switch (Current)
            {
                case State.Available: return $"Update {LatestVersion} installieren ({SizeBytes / (1024 * 1024)} MB)";
                case State.Downloading: return $"Lade Update … {Mathf.RoundToInt(Progress * 100f)} %";
                case State.NeedsPermission: return "Installation erlauben, dann erneut tippen";
                case State.Installing: return "Installiere …";
                case State.Failed: return "Update fehlgeschlagen – erneut versuchen";
                default: return "";
            }
        }
    }
}
