#if UNITY_ANDROID
using System.IO;
using System.Linq;
using System.Xml;
using UnityEditor.Android;
using UnityEditor.Build;

namespace Gulkonster.WaxKit.Editor
{
    /// <summary>
    /// Android build step: the game's activity handles {scheme}://waxkit (the link wallets open to return to the game),
    /// the scheme and the My Cloud Wallet browser choice go into the manifest meta-data for the runtime, and the kit's Android library gets androidx.browser.
    /// </summary>
    internal sealed class WaxKitAndroidBuild : IPostGenerateGradleAndroidProject
    {
        const string AndroidNs = "http://schemas.android.com/apk/res/android";
        const string LibraryFolder = "WaxKitAndroid.androidlib";
        const string BrowserDependency = "implementation 'androidx.browser:browser:1.8.0'";

        /// <summary>Order among Gradle post-processing steps (any order works).</summary>
        public int callbackOrder => 0;

        /// <summary>Edits the generated Gradle project: return link intent filter, scheme meta-data, browser library.</summary>
        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            var settings = WaxKitSettings.Load();
            var scheme = settings.EffectiveAndroidScheme;
            if (!WaxKitSettings.IsValidScheme(scheme))
                throw new BuildFailedException($"Unity WAX Kit: '{scheme}' is not a valid link scheme. Set one in Project Settings > Unity WAX Kit.");

            AddReturnLink(Path.Combine(unityLibraryPath, "src", "main", "AndroidManifest.xml"), scheme, settings.androidMyCloudWalletInChrome);
            AddBrowserDependency(unityLibraryPath);
        }

        static void AddReturnLink(string manifestPath, string scheme, bool myCloudWalletInChrome)
        {
            var doc = new XmlDocument();
            doc.Load(manifestPath);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("android", AndroidNs);

            var application = doc.SelectSingleNode("/manifest/application");
            var activity = doc.SelectSingleNode("/manifest/application/activity[intent-filter/category/@android:name='android.intent.category.LAUNCHER']", ns)
                           ?? throw new BuildFailedException("Unity WAX Kit: the Unity activity was not found in the manifest.");

            foreach (var old in activity.SelectNodes("intent-filter[data/@android:host='waxkit']", ns).Cast<XmlNode>().ToList())
                activity.RemoveChild(old);

            var filter = doc.CreateElement("intent-filter");
            filter.AppendChild(Element(doc, "action", "name", "android.intent.action.VIEW"));
            filter.AppendChild(Element(doc, "category", "name", "android.intent.category.DEFAULT"));
            filter.AppendChild(Element(doc, "category", "name", "android.intent.category.BROWSABLE"));
            var data = Element(doc, "data", "scheme", scheme);
            data.SetAttribute("host", AndroidNs, "waxkit");
            filter.AppendChild(data);
            activity.AppendChild(filter);

            SetMetaData(doc, application, ns, "waxkit.scheme", scheme);
            // Read by the Android plugin when it opens My Cloud Wallet.
            SetMetaData(doc, application, ns, "waxkit.mycloudwallet.chrome", myCloudWalletInChrome ? "true" : "false");

            doc.Save(manifestPath);
        }

        static void SetMetaData(XmlDocument doc, XmlNode application, XmlNamespaceManager ns, string name, string value)
        {
            var meta = application.SelectSingleNode($"meta-data[@android:name='{name}']", ns) as XmlElement;
            if (meta == null)
            {
                meta = Element(doc, "meta-data", "name", name);
                application.AppendChild(meta);
            }
            meta.SetAttribute("value", AndroidNs, value);
        }

        static void AddBrowserDependency(string unityLibraryPath)
        {
            var root = Directory.GetParent(unityLibraryPath).FullName;
            foreach (var gradle in Directory.GetDirectories(root, LibraryFolder, SearchOption.AllDirectories)
                         .Select(dir => Path.Combine(dir, "build.gradle")).Where(File.Exists))
            {
                var text = File.ReadAllText(gradle);
                if (text.Contains("androidx.browser:browser")) continue;
                var index = text.IndexOf("dependencies {", System.StringComparison.Ordinal);
                text = index < 0
                    ? text + $"\ndependencies {{\n    {BrowserDependency}\n}}\n"
                    : text.Insert(index + "dependencies {".Length, $"\n    {BrowserDependency}");
                File.WriteAllText(gradle, text);
            }
        }

        static XmlElement Element(XmlDocument doc, string name, string attribute, string value)
        {
            var element = doc.CreateElement(name);
            element.SetAttribute(attribute, AndroidNs, value);
            return element;
        }
    }
}
#endif
