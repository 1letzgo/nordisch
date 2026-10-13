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
            var (version, build) = ReadVersion();
            if (!BuildAndroidApk($"{version}.{build}", VersionCode(build)) && Application.isBatchMode) EditorApplication.Exit(1);
        }

        [System.Serializable] class VersionFile { public string version = "1.0"; public int build; }

        /// <summary>Marketing version ("1.0") and build number from Tools/release/version.json (bumped by bump_build.py).</summary>
        public static (string version, int build) ReadVersion()
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "release", "version.json"));
            var v = File.Exists(path) ? JsonUtility.FromJson<VersionFile>(File.ReadAllText(path)) : new VersionFile();
            return (v.version, v.build);
        }

        /// <summary>Android versionCode: above the old date-based codes (~93 600), so new builds install over old ones.</summary>
        public static int VersionCode(int build) => 1000000 + build;

        /// <summary>Applies version/build to the player settings for one build and puts the old values back
        /// (ProjectSettings stays unchanged in git).</summary>
        static bool WithVersion(string name, string iosBuild, int code, System.Func<bool> build)
        {
            string prevVersion = PlayerSettings.bundleVersion, prevIos = PlayerSettings.iOS.buildNumber;
            int prevCode = PlayerSettings.Android.bundleVersionCode;
            try
            {
                PlayerSettings.bundleVersion = name;
                PlayerSettings.iOS.buildNumber = iosBuild;
                PlayerSettings.Android.bundleVersionCode = code;
                return build();
            }
            finally
            {
                PlayerSettings.bundleVersion = prevVersion;
                PlayerSettings.iOS.buildNumber = prevIos;
                PlayerSettings.Android.bundleVersionCode = prevCode;
                AssetDatabase.SaveAssets();
            }
        }

        static bool BuildAndroidApk(string versionName, int versionCode) =>
            WithVersion(versionName, PlayerSettings.iOS.buildNumber, versionCode, BuildAndroidApkInner);

        static bool BuildAndroidApkInner()
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
                assetBundleManifestPath = BundleManifest(BuildTarget.Android),
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
            string prevKeystore = PlayerSettings.Android.keystoreName, prevAlias = PlayerSettings.Android.keyaliasName;
            bool ok = false;
            try
            {
                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = keystore;
                PlayerSettings.Android.keystorePass = pass;
                PlayerSettings.Android.keyaliasName = alias;
                PlayerSettings.Android.keyaliasPass = pass;
                ok = BuildAndroidApk(version, code);
            }
            finally
            {
                // Nothing of the release setup stays in the project: no expiry in later builds, no key path.
                AssetDatabase.DeleteAsset(BuildInfoPath);
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

        /// <summary>The app ships only the menu – areas are downloaded map bundles (Editor/MapBundles, Core/MapStore).</summary>
        static string[] EnabledScenes()
        {
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled && File.Exists(s.path) && !s.path.StartsWith("Assets/Scenes/Areas/"))
                .Select(s => s.path).ToArray();
            if (scenes.Length == 0) throw new System.Exception("No scenes in build settings – run LeGamez/Create Main Menu Scene first.");
            return scenes;
        }

        /// <summary>Last map bundle build of this platform: engine code stripping then also keeps what the maps use
        /// (besides Assets/link.xml).</summary>
        static string BundleManifest(BuildTarget target)
        {
            string path = MapBundles.ManifestPath(target);
            if (File.Exists(path)) return path;
            Debug.LogWarning($"[LeGamez] No map bundle manifest at {path} – stripping relies on link.xml only.");
            return null;
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
                assetBundleManifestPath = BundleManifest(BuildTarget.iOS),
            };
            // iOS: CFBundleShortVersionString = "1.0", CFBundleVersion = build number.
            var (version, build) = ReadVersion();
            bool ok = WithVersion(version, build.ToString(), PlayerSettings.Android.bundleVersionCode, () =>
            {
                var report = BuildPipeline.BuildPlayer(options);
                Debug.Log($"[LeGamez] iOS build {report.summary.result} {version} ({build}): {outDir} ({report.summary.totalSize / (1024 * 1024)} MB)");
                return report.summary.result == BuildResult.Succeeded;
            });
            if (!ok && Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
