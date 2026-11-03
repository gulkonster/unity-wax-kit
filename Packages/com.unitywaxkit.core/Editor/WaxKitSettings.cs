using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityWaxKit.Editor
{
    /// <summary>Kit settings in ProjectSettings/WaxKitSettings.json, edited in Project Settings > Unity WAX Kit.</summary>
    internal sealed class WaxKitSettings
    {
        const string FilePath = "ProjectSettings/WaxKitSettings.json";

        /// <summary>Link scheme the wallet opens to return to the game on Android. Empty = derived from the application id.</summary>
        public string androidScheme = "";

        /// <summary>Android: open My Cloud Wallet in Chrome when it is installed (passkey support), whatever the default browser is.</summary>
        public bool androidMyCloudWalletInChrome = true;

        /// <summary>WebGL: write Netlify's _headers file next to index.html after each compressed build.</summary>
        public bool webGLWriteNetlifyHeaders = true;

        /// <summary>WebGL: write Apache's .htaccess file next to index.html after each compressed build.</summary>
        public bool webGLWriteApacheHtaccess = true;

        /// <summary>Reads the settings file (defaults when it does not exist).</summary>
        public static WaxKitSettings Load()
        {
            var settings = new WaxKitSettings();
            if (File.Exists(FilePath)) JsonUtility.FromJsonOverwrite(File.ReadAllText(FilePath), settings);
            return settings;
        }

        /// <summary>Writes the settings file (ProjectSettings/WaxKitSettings.json; commit it with your project).</summary>
        public void Save() => File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));

        /// <summary>The scheme the build uses: the setting, or the Android application id made into a valid scheme.</summary>
        public string EffectiveAndroidScheme =>
            string.IsNullOrWhiteSpace(androidScheme)
                ? DefaultScheme(PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android))
                : androidScheme.Trim().ToLowerInvariant();

        /// <summary>Same rule as the runtime fallback: lowercase, invalid scheme characters become '-', starts with a letter.</summary>
        public static string DefaultScheme(string applicationId)
        {
            var chars = (string.IsNullOrEmpty(applicationId) ? "game" : applicationId).ToLowerInvariant().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '.' || chars[i] == '-' || chars[i] == '+')) chars[i] = '-';
            var result = new string(chars);
            return char.IsLetter(result[0]) ? result : "app" + result;
        }

        /// <summary>True for a usable link scheme: starts with a letter, only letters, digits, ".", "-", "+", and not a scheme wallets or browsers own.</summary>
        public static bool IsValidScheme(string scheme)
        {
            if (string.IsNullOrEmpty(scheme) || !char.IsLetter(scheme[0])) return false;
            foreach (var c in scheme)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '+')) return false;
            return scheme != "esr" && scheme != "anchor" && scheme != "http" && scheme != "https";
        }

        [SettingsProvider]
        static SettingsProvider CreateProvider() => new SettingsProvider("Project/Unity WAX Kit", SettingsScope.Project)
        {
            keywords = new HashSet<string>
            {
                "WAX", "Anchor", "scheme", "deep link", "Android", "My Cloud Wallet", "Chrome", "WebGL", "headers", "htaccess", "Netlify", "Apache",
            },
            guiHandler = _ =>
            {
                var settings = Load();
                EditorGUILayout.LabelField("WebGL", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                settings.webGLWriteNetlifyHeaders = EditorGUILayout.Toggle(new GUIContent("Write Netlify _headers",
                    "After each WebGL build with Gzip or Brotli compression (and Decompression Fallback off), write _headers next to " +
                    "index.html so Netlify sends the Content-Encoding headers the game needs."), settings.webGLWriteNetlifyHeaders);
                settings.webGLWriteApacheHtaccess = EditorGUILayout.Toggle(new GUIContent("Write Apache .htaccess",
                    "After each WebGL build with Gzip or Brotli compression (and Decompression Fallback off), write .htaccess next to " +
                    "index.html so Apache sends the Content-Encoding headers the game needs."), settings.webGLWriteApacheHtaccess);
                if (EditorGUI.EndChangeCheck()) settings.Save();

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Android", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                settings.androidScheme = EditorGUILayout.TextField(new GUIContent("Return link scheme",
                    "Anchor opens <scheme>://waxkit after signing to bring the player back. Empty uses the application id."), settings.androidScheme);
                if (EditorGUI.EndChangeCheck()) settings.Save();

                var effective = settings.EffectiveAndroidScheme;
                EditorGUILayout.HelpBox($"Return link: {effective}://waxkit", IsValidScheme(effective) ? MessageType.Info : MessageType.Error);

                EditorGUI.BeginChangeCheck();
                settings.androidMyCloudWalletInChrome = EditorGUILayout.Toggle(new GUIContent("Open My Cloud Wallet in Chrome when installed",
                    "My Cloud Wallet signs with a passkey, which some default browsers do not support. Off: the default browser."), settings.androidMyCloudWalletInChrome);
                if (EditorGUI.EndChangeCheck()) settings.Save();
            },
        };
    }
}
