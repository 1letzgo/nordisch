using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LeGamez.Editor
{
    /// <summary>
    /// Exports the Xcode project to Builds/iOS.
    /// Batch: Unity -batchmode -quit -projectPath . -buildTarget iOS -executeMethod LeGamez.Editor.BuildScript.BuildIOS
    /// </summary>
    public static class BuildScript
    {
        /// <summary>Same export but for the iOS Simulator SDK, into Builds/iOS-Sim.</summary>
        [MenuItem("LeGamez/Build iOS Simulator (Xcode project)")]
        public static void BuildIOSSimulator()
        {
            var previous = PlayerSettings.iOS.sdkVersion;
            PlayerSettings.iOS.sdkVersion = iOSSdkVersion.SimulatorSDK;
            PlayerSettings.iOS.simulatorSdkArchitecture = AppleMobileArchitectureSimulator.ARM64;
            try { Build("iOS-Sim"); }
            finally { PlayerSettings.iOS.sdkVersion = previous; }
        }

        [MenuItem("LeGamez/Build iOS (Xcode project)")]
        public static void BuildIOS() => Build("iOS");

        /// <summary>
        /// Android APK into Builds/Android/Nordisch.apk (ARM64, IL2CPP; runs on devices and the arm64 emulator on Apple Silicon).
        /// Batch: Unity -batchmode -quit -projectPath . -buildTarget Android -executeMethod LeGamez.Editor.BuildScript.BuildAndroid
        /// </summary>
        [MenuItem("LeGamez/Build Android (APK)")]
        public static void BuildAndroid()
        {
            RemoveStaleBuildInfo();
            if (!BuildAndroidApk() && Application.isBatchMode) EditorApplication.Exit(1);
        }

        static bool BuildAndroidApk()
        {
            ProjectSetup.ConfigureAndroid();
            var scenes = EnabledScenes();
            EditorUserBuildSettings.development = false;
            EditorUserBuildSettings.buildAppBundle = false;
            string outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Android", "Nordisch.apk"));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outPath,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            Debug.Log($"[LeGamez] Android build {report.summary.result}: {outPath} ({report.summary.totalSize / (1024 * 1024)} MB)");
            return report.summary.result == BuildResult.Succeeded;
        }

        /// <summary>A release build that died half-way could leave its expiry file behind – never ship that by accident.</summary>
        static void RemoveStaleBuildInfo()
        {
            if (File.Exists(BuildInfoPath)) AssetDatabase.DeleteAsset(BuildInfoPath);
        }

        const string BuildInfoPath = "Assets/Resources/BuildInfo.txt";
        public const int ReleaseLifetimeDays = 15;

        /// <summary>
        /// Release APK for GitHub (Tools/release/release_android.sh): version from -releaseVersion / -versionCode,
        /// signed with the release keystore from NORDISCH_KEYSTORE / NORDISCH_KEYSTORE_PASS / NORDISCH_KEY_ALIAS,
        /// expiring <see cref="ReleaseLifetimeDays"/> days after the build (Core/BuildExpiry).
        /// </summary>
        public static void BuildAndroidRelease()
        {
            string version = Arg("-releaseVersion") ?? throw new System.Exception("-releaseVersion missing");
            int code = int.Parse(Arg("-versionCode") ?? throw new System.Exception("-versionCode missing"));
            string keystore = System.Environment.GetEnvironmentVariable("NORDISCH_KEYSTORE");
            string pass = System.Environment.GetEnvironmentVariable("NORDISCH_KEYSTORE_PASS");
            string alias = System.Environment.GetEnvironmentVariable("NORDISCH_KEY_ALIAS") ?? "nordisch";
            if (string.IsNullOrEmpty(keystore) || !File.Exists(keystore) || string.IsNullOrEmpty(pass))
                throw new System.Exception("Release keystore not configured (NORDISCH_KEYSTORE, NORDISCH_KEYSTORE_PASS)");

            var expires = System.DateTime.UtcNow.AddDays(ReleaseLifetimeDays);
            File.WriteAllText(BuildInfoPath, $"version={version}\nexpires={expires:yyyy-MM-ddTHH:mm:ssZ}\n");
            AssetDatabase.ImportAsset(BuildInfoPath);
            string prevVersion = PlayerSettings.bundleVersion;
            int prevCode = PlayerSettings.Android.bundleVersionCode;
            string prevKeystore = PlayerSettings.Android.keystoreName, prevAlias = PlayerSettings.Android.keyaliasName;
            bool ok = false;
            try
            {
                PlayerSettings.bundleVersion = version;
                PlayerSettings.Android.bundleVersionCode = code;
                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = keystore;
                PlayerSettings.Android.keystorePass = pass;
                PlayerSettings.Android.keyaliasName = alias;
                PlayerSettings.Android.keyaliasPass = pass;
                ok = BuildAndroidApk();
            }
            finally
            {
                // Nothing of the release setup stays in the project: no expiry in later builds, no key path.
                AssetDatabase.DeleteAsset(BuildInfoPath);
                PlayerSettings.bundleVersion = prevVersion;
                PlayerSettings.Android.bundleVersionCode = prevCode;
                PlayerSettings.Android.useCustomKeystore = false;
                PlayerSettings.Android.keystoreName = prevKeystore;
                PlayerSettings.Android.keystorePass = "";
                PlayerSettings.Android.keyaliasName = prevAlias;
                PlayerSettings.Android.keyaliasPass = "";
                AssetDatabase.SaveAssets();
            }
            if (!ok && Application.isBatchMode) EditorApplication.Exit(1);
        }

        static string Arg(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }

        static string[] EnabledScenes()
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled && File.Exists(s.path)).Select(s => s.path).ToArray();
            if (scenes.Length == 0) throw new System.Exception("No scenes in build settings – run LeGamez/Import All Areas first.");
            return scenes;
        }

        static void Build(string folder)
        {
            RemoveStaleBuildInfo();
            var scenes = EnabledScenes();

            EditorUserBuildSettings.development = false; // no on-screen development console
            string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", folder));
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outDir,
                target = BuildTarget.iOS,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[LeGamez] iOS build {report.summary.result}: {outDir} ({report.summary.totalSize / (1024 * 1024)} MB)");
            if (report.summary.result != BuildResult.Succeeded && Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
