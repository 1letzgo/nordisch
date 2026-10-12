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

        static void Build(string folder)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled && File.Exists(s.path)).Select(s => s.path).ToArray();
            if (scenes.Length == 0) throw new System.Exception("No scenes in build settings – run LeGamez/Import All Areas first.");

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
