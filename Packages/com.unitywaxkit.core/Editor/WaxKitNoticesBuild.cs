using System.IO;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityWaxKit.Editor
{
    /// <summary>
    /// Puts the kit's third-party notices into every supported build output, so they reach players: next to index.html
    /// (WebGL), next to the .exe (Windows) and into the APK's assets (Android). The game still has to show or ship them,
    /// with the notices of its other plugins.
    /// </summary>
    internal sealed class WaxKitNoticesBuild : IPostprocessBuildWithReport, IPostGenerateGradleAndroidProject
    {
        internal const string FileName = "UnityWaxKit-THIRD_PARTY_NOTICES.txt";
        const string Source = "Packages/com.unitywaxkit.core/Runtime/Resources/UnityWaxKit/ThirdPartyNotices.txt";

        public int callbackOrder => 0;

        static string Notices()
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(Source);
            if (asset == null) Debug.LogWarning($"Unity WAX Kit: {Source} was not found; the third-party notices were not added to the build.");
            return asset?.text;
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            var summary = report.summary;
            if (summary.result == BuildResult.Failed || summary.result == BuildResult.Cancelled) return;
            string folder;
            switch (summary.platform)
            {
                case BuildTarget.WebGL:
                    folder = summary.outputPath;
                    break;
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    folder = Path.GetDirectoryName(summary.outputPath);
                    break;
                default:
                    return; // Android: OnPostGenerateGradleAndroidProject
            }
            Write(folder);
        }

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath) =>
            Write(Path.Combine(unityLibraryPath, "src", "main", "assets"));

        static void Write(string folder)
        {
            var text = Notices();
            if (text == null || string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, FileName), text);
            Debug.Log($"Unity WAX Kit: wrote {FileName} to {folder}. Show or ship it with your game's other notices.");
        }
    }
}
