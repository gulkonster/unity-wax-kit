using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace UnityWaxKit.Platform
{
    internal static class WaxUnityPlatform
    {
        /// <summary>The platform for the current build target.</summary>
        public static IWaxPlatform Create()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WaxWebGLPlatform();
#elif UNITY_ANDROID && !UNITY_EDITOR
            return new WaxAndroidPlatform();
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return new WaxWindowsPlatform();
#else
            return new WaxUnityPlatformBase();
#endif
        }
    }

    /// <summary>
    /// Hidden object that reports when the game is paused (the player switched to another app) and resumed, for
    /// <see cref="WaxUnityPlatformBase.Background"/>. The browser reports this itself on WebGL.
    /// </summary>
    internal sealed class WaxAppEvents : MonoBehaviour
    {
        static WaxAppEvents instance;

        /// <summary>Creates the hidden object once.</summary>
        public static void Ensure()
        {
            if (instance != null) return;
            var go = new GameObject(nameof(WaxAppEvents)) { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<WaxAppEvents>();
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        void OnApplicationPause(bool paused) => WaxUnityPlatformBase.Background.Report(paused);
#endif
    }

    /// <summary>What all Unity platforms share: UnityWebRequest, PlayerPrefs, ClientWebSocket and frame-based delays.</summary>
    internal class WaxUnityPlatformBase : IWaxPlatform
    {
        /// <summary>HTTP through UnityWebRequest (works on every platform, WebGL included).</summary>
        public async Task<WaxHttpResponse> Http(string method, string url, byte[] body, string contentType,
            IDictionary<string, string> headers, int timeoutSeconds, CancellationToken cancellation)
        {
            using var request = new UnityWebRequest(url, method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = timeoutSeconds,
            };
            if (body != null) request.uploadHandler = new UploadHandlerRaw(body) { contentType = contentType };
            if (headers != null) foreach (var h in headers) request.SetRequestHeader(h.Key, h.Value);

            using (cancellation.Register(request.Abort))
            {
                await request.SendWebRequest();
            }
            cancellation.ThrowIfCancellationRequested();

            var response = new WaxHttpResponse
            {
                Status = request.result == UnityWebRequest.Result.ConnectionError ? 0 : request.responseCode,
                Text = request.downloadHandler?.text,
                Error = request.error,
            };
            var responseHeaders = request.GetResponseHeaders();
            if (responseHeaders != null) foreach (var h in responseHeaders) response.Headers[h.Key] = h.Value;
            return response;
        }

        /// <summary>A WebSocket (System.Net on native platforms).</summary>
        public virtual IWaxSocket CreateSocket() => new ClientSocket();

        /// <summary>Checks the wall clock every frame, so time spent in the background counts and timeScale is ignored.</summary>
        public async Task Delay(int milliseconds, CancellationToken cancellation)
        {
            var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < end) await Awaitable.NextFrameAsync(cancellation);
        }

        /// <summary>Reads a saved value (PlayerPrefs), or null.</summary>
        public string Load(string key) => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;

        /// <summary>Saves a value (PlayerPrefs) and writes it to disk at once.</summary>
        public void Save(string key, string value)
        {
            PlayerPrefs.SetString(key, value);
            PlayerPrefs.Save();
        }

        /// <summary>Removes a saved value.</summary>
        public void Delete(string key)
        {
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }

        /// <summary>True on phones and tablets.</summary>
        public virtual bool IsMobile => Application.isMobilePlatform;
        /// <summary>True where a wallet window may only open inside a click (browsers).</summary>
        public virtual bool PopupNeedsGesture => false;
        /// <summary>The link a wallet app opens to come back to the game, or null.</summary>
        public virtual string ReturnUrl => null;
        /// <summary>Platform text sent to wallets.</summary>
        public virtual string UserAgent => $"{Application.platform}; Unity {Application.unityVersion}";
        /// <summary>Whether an app handles the link scheme (null = unknown).</summary>
        public virtual bool? CanOpenScheme(string scheme) => null;
        /// <summary>Opens a wallet link.</summary>
        public virtual void OpenLink(string url) => Application.OpenURL(url);
        /// <summary>Puts the game back in front (nothing to do on this platform).</summary>
        public virtual void ReturnToGame() { }
        /// <summary>Nothing to do on this platform.</summary>
        public virtual void LetWalletAppComeToFront(string scheme, bool force) { }
        /// <summary>Nothing to do on this platform.</summary>
        public virtual bool BringWalletAppToFront(string scheme) => false;

        int activeRequests;
        bool gameRunInBackground;

        /// <summary>
        /// Turns Run In Background on while a request waits and puts the game's own value back afterwards. Without it a
        /// game that pauses when it loses focus (the Unity default) would only see the wallet's answer once the player
        /// switched back by hand.
        /// </summary>
        public void BeginWalletRequest()
        {
            if (activeRequests++ > 0) return;
            gameRunInBackground = Application.runInBackground;
            Application.runInBackground = true;
        }

        /// <summary>Puts the game's own Run In Background value back after the last request.</summary>
        public void EndWalletRequest()
        {
            if (activeRequests == 0 || --activeRequests > 0) return;
            Application.runInBackground = gameRunInBackground;
        }

        /// <summary>Pause / resume of the game, fed by <see cref="WaxAppEvents"/> (or the browser on WebGL).</summary>
        internal static readonly WaxBackgroundTracker Background = new WaxBackgroundTracker();

        /// <summary>See <see cref="IWaxPlatform.WaitForPlayerBack"/>.</summary>
        public Task WaitForPlayerBack(int stayMilliseconds, CancellationToken cancellation)
        {
            // Desktops: the player often looks at another window (or a phone) while the wallet works; that is no answer.
            if (!IsMobile) return Never(cancellation);
            WaxAppEvents.Ensure();
            return Background.WaitForPlayerBack(this, stayMilliseconds, cancellation);
        }

        static Task Never(CancellationToken cancellation)
        {
            var never = new TaskCompletionSource<bool>();
            cancellation.Register(() => never.TrySetCanceled());
            return never.Task;
        }

        /// <summary>Wallet windows are not supported on this platform.</summary>
        public virtual IWaxPopupFlow OpenWalletPopup(string url, JObject post, WaxPopupProtocol protocol) =>
            throw new WaxKitException(WaxErrorKind.NotSupported,
                $"{(protocol == WaxPopupProtocol.WebAuth ? "WebAuth web" : "My Cloud Wallet")} is not supported on {Application.platform}.");

        /// <summary>System.Net.WebSockets client (Windows, Android, Editor).</summary>
        sealed class ClientSocket : IWaxSocket
        {
            readonly ClientWebSocket socket = new ClientWebSocket();

            /// <summary>Opens the socket.</summary>
            public Task Connect(string url, CancellationToken cancellation) => socket.ConnectAsync(new Uri(url), cancellation);

            /// <summary>The next whole message, or null when the socket closed.</summary>
            public async Task<byte[]> Receive(CancellationToken cancellation)
            {
                var buffer = new byte[8192];
                using var message = new MemoryStream();
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) return message.ToArray();
                }
            }

            /// <summary>Sends one binary message.</summary>
            public Task Send(byte[] data, CancellationToken cancellation) =>
                socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, cancellation);

            /// <summary>Closes the socket.</summary>
            public void Dispose() => socket.Dispose();
        }
    }
}
