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
        [MenuItem("LeGamez/Build iOS (Xcode project)")]
        public static void BuildIOS()
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled && File.Exists(s.path)).Select(s => s.path).ToArray();
            if (scenes.Length == 0) throw new System.Exception("No scenes in build settings – run LeGamez/Import All Areas first.");

            string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "iOS"));
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outDir,
                target = BuildTarget.iOS,
                options = EditorUserBuildSettings.development ? BuildOptions.Development : BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[LeGamez] iOS build {report.summary.result}: {outDir} ({report.summary.totalSize / (1024 * 1024)} MB)");
            if (report.summary.result != BuildResult.Succeeded && Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
