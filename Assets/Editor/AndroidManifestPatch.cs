using System.IO;
using UnityEditor.Android;
using UnityEngine;

namespace LeGamez.Editor
{
    /// <summary>
    /// Adds REQUEST_INSTALL_PACKAGES to the generated Gradle project: the in-app updater (NordischUpdater.java)
    /// installs GitHub release APKs through PackageInstaller.
    /// </summary>
    class AndroidManifestPatch : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 0;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string manifest = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifest)) { Debug.LogWarning("[LeGamez] Manifest not found: " + manifest); return; }
            string xml = File.ReadAllText(manifest);
            const string permission = "android.permission.REQUEST_INSTALL_PACKAGES";
            if (xml.Contains(permission)) return;
            int at = xml.IndexOf("<application", System.StringComparison.Ordinal);
            if (at < 0) { Debug.LogWarning("[LeGamez] No <application> in " + manifest); return; }
            xml = xml.Insert(at, $"<uses-permission android:name=\"{permission}\" />\n  ");
            File.WriteAllText(manifest, xml);
        }
    }
}
