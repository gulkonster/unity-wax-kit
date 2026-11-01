#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Gulkonster.WaxKit.Antelope;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit.Platform
{
    /// <summary>Browser: WebSocket and wallet popups through Plugins/WebGL/WaxKit.jslib.</summary>
    internal sealed class WaxWebGLPlatform : WaxUnityPlatformBase
    {
        [DllImport("__Internal")] static extern void WaxKit_Init(Action<int, int, string> onEvent);
        [DllImport("__Internal")] static extern int WaxKit_IsMobile();
        [DllImport("__Internal")] static extern string WaxKit_ReturnUrl();
        [DllImport("__Internal")] static extern void WaxKit_OpenLink(string url);
        [DllImport("__Internal")] static extern void WaxKit_Focus();
        [DllImport("__Internal")] static extern void WaxKit_Arm(string action);
        [DllImport("__Internal")] static extern int WaxKit_SocketOpen(string url);
        [DllImport("__Internal")] static extern void WaxKit_SocketSend(int id, string base64);
        [DllImport("__Internal")] static extern void WaxKit_SocketClose(int id);
        [DllImport("__Internal")] static extern int WaxKit_PopupOpen(string url, string postJson);
        [DllImport("__Internal")] static extern void WaxKit_PopupClose(int id);

        // Event kinds sent by the jslib.
        const int SocketOpen = 1, SocketMessage = 2, SocketClosed = 3, PopupResult = 10, PopupClosed = 11, PopupBlocked = 12, Gesture = 20, Visibility = 30;

        static readonly Dictionary<int, WebSocketChannel> Sockets = new Dictionary<int, WebSocketChannel>();
        static readonly Dictionary<int, Popup> Popups = new Dictionary<int, Popup>();

        /// <summary>Registers the event callback with the jslib.</summary>
        public WaxWebGLPlatform() => WaxKit_Init(OnEvent);

        public override bool IsMobile => WaxKit_IsMobile() != 0;
        public override bool PopupNeedsGesture => true;
        public override string ReturnUrl => WaxKit_ReturnUrl();
        public override string UserAgent => "WebGL";
        public override void OpenLink(string url) => WaxKit_OpenLink(url);
        public override void ReturnToGame() => WaxKit_Focus();
        public override IWaxSocket CreateSocket() => new WebSocketChannel();

        /// <summary>Called by <see cref="WaxGestureButton"/>: the action runs inside the browser's next pointerup / touchend.</summary>
        internal static void Arm(string action) => WaxKit_Arm(action);

        public override IWaxPopupFlow OpenWalletPopup(string url, JObject post)
        {
            var popup = new Popup();
            popup.Id = WaxKit_PopupOpen(url, post == null ? "" : post.ToString(Formatting.None));
            Popups[popup.Id] = popup;
            return popup;
        }

        [AOT.MonoPInvokeCallback(typeof(Action<int, int, string>))]
        static void OnEvent(int kind, int id, string data)
        {
            switch (kind)
            {
                case Visibility:
                    Background.Report(data == "1");
                    break;
                case Gesture:
                    // Runs inside the browser event, so the wallet may open its window or link.
                    var request = WaxUiRequest.Active;
                    if (data == "continue") request?.Continue();
                    else if (data == "openAnchor") request?.OpenAnchor();
                    break;
                case SocketOpen:
                case SocketMessage:
                case SocketClosed:
                    if (Sockets.TryGetValue(id, out var socket)) socket.OnEvent(kind, data);
                    break;
                case PopupResult:
                case PopupClosed:
                case PopupBlocked:
                    if (!Popups.TryGetValue(id, out var popup)) break;
                    Popups.Remove(id);
                    if (kind == PopupResult) popup.Done.TrySetResult(WaxJson.Parse(data));
                    else if (kind == PopupBlocked) popup.Done.TrySetException(new WaxKitException(WaxErrorKind.Wallet, "Unable to open the popup window. Check your browser settings and try again."));
                    else popup.Done.TrySetException(new WaxKitException(WaxErrorKind.Cancelled, "The wallet window was closed before the request was completed."));
                    break;
            }
        }

        sealed class Popup : IWaxPopupFlow
        {
            /// <summary>Popup id in the jslib.</summary>
            public int Id;
            /// <summary>Completes with the wallet answer.</summary>
            public readonly TaskCompletionSource<JObject> Done = new TaskCompletionSource<JObject>();
            /// <summary>The wallet's answer.</summary>
            public Task<JObject> Result => Done.Task;

            /// <summary>Closes the popup; the call ends as Cancelled.</summary>
            public void Close()
            {
                WaxKit_PopupClose(Id);
                Popups.Remove(Id);
                Done.TrySetException(new WaxKitException(WaxErrorKind.Cancelled, "The wallet window was closed."));
            }
        }

        /// <summary>Browser WebSocket; messages arrive base64-encoded from the jslib.</summary>
        sealed class WebSocketChannel : IWaxSocket
        {
            readonly Queue<byte[]> messages = new Queue<byte[]>();
            TaskCompletionSource<bool> opened = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> arrived = new TaskCompletionSource<bool>();
            bool closed;
            int id;

            /// <summary>Opens the browser WebSocket.</summary>
            public async Task Connect(string url, CancellationToken cancellation)
            {
                id = WaxKit_SocketOpen(url);
                Sockets[id] = this;
                using (cancellation.Register(() => opened.TrySetCanceled()))
                    await opened.Task;
            }

            /// <summary>The next message, or null when closed.</summary>
            public async Task<byte[]> Receive(CancellationToken cancellation)
            {
                while (true)
                {
                    if (messages.Count > 0) return messages.Dequeue();
                    if (closed) return null;
                    using (cancellation.Register(() => arrived.TrySetCanceled()))
                        await arrived.Task;
                    arrived = new TaskCompletionSource<bool>();
                }
            }

            /// <summary>Sends one binary message.</summary>
            public Task Send(byte[] data, CancellationToken cancellation)
            {
                WaxKit_SocketSend(id, Convert.ToBase64String(data));
                return Task.CompletedTask;
            }

            /// <summary>Socket event from the jslib.</summary>
            public void OnEvent(int kind, string data)
            {
                if (kind == SocketOpen)
                {
                    opened.TrySetResult(true);
                    return;
                }
                if (kind == SocketMessage) messages.Enqueue(Convert.FromBase64String(data));
                else
                {
                    closed = true;
                    opened.TrySetException(new WaxKitException(WaxErrorKind.Network, "WebSocket closed."));
                }
                arrived.TrySetResult(true);
            }

            /// <summary>Closes the socket.</summary>
            public void Dispose()
            {
                closed = true;
                Sockets.Remove(id);
                WaxKit_SocketClose(id);
            }
        }
    }
}
#endif
