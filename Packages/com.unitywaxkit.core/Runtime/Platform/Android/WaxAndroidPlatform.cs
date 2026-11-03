#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityWaxKit.Platform
{
    /// <summary>
    /// Android: wallet apps through intents, the game's own link scheme to come back (Anchor's return_path),
    /// My Cloud Wallet and WebAuth web in a Custom Tab on top of the game (Plugins/Android/WaxKitAndroid.androidlib).
    /// </summary>
    internal sealed class WaxAndroidPlatform : WaxUnityPlatformBase
    {
        const string PluginClass = "com.unitywaxkit.core.WaxKitAndroid";
        /// <summary>Host part of the return link: {scheme}://waxkit.</summary>
        internal const string ReturnHost = "waxkit";

        readonly AndroidJavaObject activity;
        readonly AndroidJavaClass plugin;
        readonly string scheme;
        // Custom Tabs side of the plugin (WaxKitBrowser.java).
        const string BrowserClass = "com.unitywaxkit.core.WaxKitBrowser";
        readonly int mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        WaxNativePopupFlow browserFlow;
        bool browserTookFocus;

        /// <summary>The return link the game was started or resumed with (cold start after the wallet), or null.</summary>
        internal static string LastReturnLink { get; private set; }

        /// <summary>Connects to the Java plugin and listens for the return link.</summary>
        public WaxAndroidPlatform()
        {
            activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer").GetStatic<AndroidJavaObject>("currentActivity");
            plugin = new AndroidJavaClass(PluginClass);
            // Written into the manifest by the kit's Android build step; the same rule as the Editor's default otherwise.
            scheme = plugin.CallStatic<string>("metaData", activity, "waxkit.scheme") ?? DefaultScheme(Application.identifier);

            Application.deepLinkActivated += OnDeepLink;
            if (!string.IsNullOrEmpty(Application.absoluteURL)) OnDeepLink(Application.absoluteURL);
            Application.focusChanged += OnFocusChanged;
            WaxAppEvents.Ensure();
        }

        /// <summary>The application id, lowercase, with characters a URI scheme cannot hold turned into '-'.</summary>
        internal static string DefaultScheme(string applicationId)
        {
            var chars = (applicationId ?? "game").ToLowerInvariant().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '.' || chars[i] == '-' || chars[i] == '+')) chars[i] = '-';
            var result = new string(chars);
            return char.IsLetter(result[0]) ? result : "app" + result;
        }

        public override bool IsMobile => true;
        /// <summary>
        /// The link Anchor opens after a request: android-app://{application id}/{scheme}/waxkit/ (the android-app form
        /// WharfKit also uses on Android). Android turns it into the game's own link {scheme}://waxkit/ limited to the
        /// game's package, which starts the game even when it was closed (a login approved meanwhile is then finished by
        /// RestoreSession) and brings it back when it is running. The slash at the end is needed: without it Android
        /// drops the "waxkit" part, the link matches nothing in the game and the player lands on the home screen.
        /// </summary>
        public override string ReturnUrl => $"android-app://{Application.identifier}/{scheme}/{ReturnHost}/";

        /// <summary>The same return link for a web page in a normal browser (Chrome-style intent link).</summary>
        string BrowserReturnLink => $"intent://{ReturnHost}#Intent;scheme={scheme};package={Application.identifier};end";
        public override string UserAgent => $"Android {SystemInfo.operatingSystem}; Unity {Application.unityVersion}";

        public override bool? CanOpenScheme(string linkScheme) => plugin.CallStatic<bool>("canOpen", activity, linkScheme + ":");

        public override void OpenLink(string url)
        {
            if (!plugin.CallStatic<bool>("openLink", activity, url))
                Debug.LogWarning($"Unity WAX Kit: no app on this device opens {url.Split(':')[0]}: links.");
        }

        public override void ReturnToGame()
        {
            if (!Application.isFocused) plugin.CallStatic("bringToFront", activity);
        }

        public override IWaxPopupFlow OpenWalletPopup(string url, JObject post, WaxPopupProtocol protocol)
        {
            browserFlow?.Close();
            var flow = new WaxNativePopupFlow(url, post, protocol, _ => BringToFrontFromAnyThread());
            browserFlow = flow;
            browserTookFocus = false;
            // In a Custom Tab the game closes the page and comes back by itself when the request ends. Some browsers (and
            // every browser without Custom Tabs support) keep the page in their own task, where Android does not let the
            // game pull itself back in front; there the page, still on screen, opens the game's return link instead.
            flow.ReturnLink = BrowserReturnLink;
            using (var browser = new AndroidJavaClass(BrowserClass))
                browser.CallStatic<bool>("openBrowser", activity, flow.BridgeUrl, (int)(Screen.height * 0.88f));
            return flow;
        }

        /// <summary>The bridge server thread ends the flow while the game is paused behind the Custom Tab.</summary>
        void BringToFrontFromAnyThread()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == mainThread)
            {
                plugin.CallStatic("bringToFront", activity);
                return;
            }
            AndroidJNI.AttachCurrentThread();
            try
            {
                using var pluginClass = new AndroidJavaClass(PluginClass);
                pluginClass.CallStatic("bringToFront", activity);
            }
            finally
            {
                AndroidJNI.DetachCurrentThread();
            }
        }

        /// <summary>The game is in front again while the browser step is open: the player closed the Custom Tab.</summary>
        async void OnFocusChanged(bool focused)
        {
            var flow = browserFlow;
            if (flow == null || flow.IsFinished) return;
            if (!focused)
            {
                browserTookFocus = true;
                return;
            }
            if (!browserTookFocus) return;

            // The page's last request may still be on its way.
            await Task.Yield();
            await Delay(1500, default);
            if (!flow.IsFinished) flow.Finish(null);
        }

        static void OnDeepLink(string url)
        {
            if (url.IndexOf("://" + ReturnHost, StringComparison.OrdinalIgnoreCase) >= 0) LastReturnLink = url;
        }
    }
}
#endif
