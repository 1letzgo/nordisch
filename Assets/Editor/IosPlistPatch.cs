#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace LeGamez.Editor
{
    /// <summary>
    /// iOS export: the app uses only standard HTTPS – declare it, so App Store Connect / TestFlight does not ask
    /// about export compliance for every build. Version and build number come from BuildScript (Tools/release/version.json).
    /// </summary>
    public static class IosPlistPatch
    {
        [PostProcessBuild(100)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;
            string plistPath = Path.Combine(path, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            plist.root.SetBoolean("ITSAppUsesNonExemptEncryption", false);
            plist.WriteToFile(plistPath);
        }
    }
}
#endif
