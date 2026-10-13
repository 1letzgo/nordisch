using UnityEngine;

namespace LeGamez.Core
{
    /// <summary>
    /// Android auto-rotation. Unity's own AutoRotation stays stuck in the launch orientation on Android
    /// (Input.deviceOrientation turns, the window never does), so we follow the device ourselves:
    /// portrait and both landscapes, never upside down, and only while the system rotation lock is off.
    /// </summary>
    static class AppOrientation
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            var go = new GameObject("AppOrientation");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<Follower>();
        }

        class Follower : MonoBehaviour
        {
            const float SettleTime = 0.3f;   // device pose must hold this long before the window turns
            const float LockPollTime = 1f;

            DeviceOrientation candidate = DeviceOrientation.Unknown;
            float candidateSince;
            bool locked;
            float nextLockPoll;

            void Update()
            {
                if (Time.unscaledTime >= nextLockPoll)
                {
                    nextLockPoll = Time.unscaledTime + LockPollTime;
                    locked = RotationLocked();
                }
                if (locked) return;

                var target = ToScreen(Input.deviceOrientation);
                if (target == ScreenOrientation.AutoRotation) return; // face up/down, upside down, unknown
                var d = Input.deviceOrientation;
                if (d != candidate) { candidate = d; candidateSince = Time.unscaledTime; return; }
                if (Time.unscaledTime - candidateSince < SettleTime) return;
                if (Screen.orientation != target) Screen.orientation = target;
            }

            static ScreenOrientation ToScreen(DeviceOrientation d)
            {
                switch (d)
                {
                    case DeviceOrientation.Portrait: return ScreenOrientation.Portrait;
                    case DeviceOrientation.LandscapeLeft: return ScreenOrientation.LandscapeLeft;
                    case DeviceOrientation.LandscapeRight: return ScreenOrientation.LandscapeRight;
                    default: return ScreenOrientation.AutoRotation;
                }
            }

            /// <summary>Settings.System.ACCELEROMETER_ROTATION == 0 means the user locked rotation.</summary>
            static bool RotationLocked()
            {
                try
                {
                    using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    using var resolver = activity.Call<AndroidJavaObject>("getContentResolver");
                    using var settings = new AndroidJavaClass("android.provider.Settings$System");
                    return settings.CallStatic<int>("getInt", resolver, "accelerometer_rotation", 1) == 0;
                }
                catch (System.Exception) { return false; }
            }
        }
    }
}
