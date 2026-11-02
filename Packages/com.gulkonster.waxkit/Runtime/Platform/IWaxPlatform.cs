using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit.Platform
{
    internal sealed class WaxHttpResponse
    {
        /// <summary>0 when the request did not reach the server.</summary>
        public long Status;
        /// <summary>Response body.</summary>
        public string Text;
        /// <summary>Error text when the request failed.</summary>
        public string Error;
        /// <summary>The endpoint that answered, set by an endpoint list.</summary>
        public string Endpoint;
        /// <summary>Response headers (case-insensitive names).</summary>
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>True for a 2xx status.</summary>
        public bool IsSuccess => Status >= 200 && Status < 300;
    }

    /// <summary>A WebSocket that delivers whole messages.</summary>
    internal interface IWaxSocket : IDisposable
    {
        Task Connect(string url, CancellationToken cancellation);
        /// <summary>The next message, or null when the socket closed.</summary>
        Task<byte[]> Receive(CancellationToken cancellation);
        Task Send(byte[] data, CancellationToken cancellation);
    }

    /// <summary>A wallet page in a popup / in-app browser (My Cloud Wallet).</summary>
    internal interface IWaxPopupFlow
    {
        /// <summary>The wallet's final message, or an exception (closed = Cancelled).</summary>
        Task<JObject> Result { get; }
        void Close();
    }

    /// <summary>Everything that differs between WebGL, Windows, Android, the Editor and the local test host.</summary>
    internal interface IWaxPlatform
    {
        Task<WaxHttpResponse> Http(string method, string url, byte[] body, string contentType,
            IDictionary<string, string> headers, int timeoutSeconds, CancellationToken cancellation);

        IWaxSocket CreateSocket();

        /// <summary>Wall-clock delay that keeps counting while the app is in the background.</summary>
        Task Delay(int milliseconds, CancellationToken cancellation);

        string Load(string key);
        void Save(string key, string value);
        void Delete(string key);

        /// <summary>Phone or tablet: the wallet app runs on the same device.</summary>
        bool IsMobile { get; }
        /// <summary>A popup may only open inside a real user gesture (browsers).</summary>
        bool PopupNeedsGesture { get; }
        /// <summary>Link the wallet opens after signing to bring the player back (Anchor return_path), or null.</summary>
        string ReturnUrl { get; }
        string UserAgent { get; }

        /// <summary>True when an app handles links with this scheme (null when unknown).</summary>
        bool? CanOpenScheme(string scheme);
        /// <summary>Opens a wallet link (esr:, anchor:) in the app that handles it.</summary>
        void OpenLink(string url);
        /// <summary>Brings the game back in front after a wallet step.</summary>
        void ReturnToGame();

        /// <summary>
        /// A wallet request starts / ends. While one waits, the game keeps running when it is not in front
        /// (so the wallet's answer is handled at once), whatever the game's own Run In Background setting is.
        /// </summary>
        void BeginWalletRequest();
        void EndWalletRequest();

        /// <summary>
        /// Phones and phone browsers: completes once the player left the game (for the wallet app), came back and stayed
        /// in the game for <paramref name="stayMilliseconds"/>. Never completes on desktops.
        /// </summary>
        Task WaitForPlayerBack(int stayMilliseconds, CancellationToken cancellation);

        /// <summary>
        /// A request was just sent to the wallet app handling <paramref name="scheme"/> links on this computer: let it come
        /// in front of the game. <paramref name="force"/>: also put its window in front if it does not do that itself.
        /// </summary>
        void LetWalletAppComeToFront(string scheme, bool force);

        /// <summary>
        /// Puts the open window of the wallet app handling <paramref name="scheme"/> links on this computer in front (the
        /// player left it, for example with Alt+Tab, while a request waits there). False when there is no such window
        /// (app not running, phones, browsers).
        /// </summary>
        bool BringWalletAppToFront(string scheme);

        /// <summary>
        /// Opens <paramref name="url"/> in a popup / in-app browser. When the wallet page sends its first message,
        /// <paramref name="post"/> (if any) is posted back to it and the next message is the result.
        /// Call it synchronously from the user gesture on WebGL.
        /// </summary>
        IWaxPopupFlow OpenWalletPopup(string url, JObject post);
    }

    /// <summary>
    /// Shared helper for <see cref="IWaxPlatform.WaitForPlayerBack"/>: platforms report when the game goes to the
    /// background and comes back, and waiters complete once the player stayed back long enough.
    /// </summary>
    internal sealed class WaxBackgroundTracker
    {
        readonly List<TaskCompletionSource<bool>> waiters = new List<TaskCompletionSource<bool>>();

        /// <summary>True while the game is in the background (paused activity, hidden browser tab).</summary>
        public bool InBackground { get; private set; }
        /// <summary>Counts every return to the foreground.</summary>
        public int Returns { get; private set; }

        /// <summary>Call from the platform's pause / visibility event (main thread).</summary>
        public void Report(bool inBackground)
        {
            if (InBackground == inBackground) return;
            InBackground = inBackground;
            if (inBackground) return;
            Returns++;
            var done = waiters.ToArray();
            waiters.Clear();
            foreach (var waiter in done) waiter.TrySetResult(true);
        }

        /// <summary>See <see cref="IWaxPlatform.WaitForPlayerBack"/>.</summary>
        public async Task WaitForPlayerBack(IWaxPlatform platform, int stayMilliseconds, CancellationToken cancellation)
        {
            var seen = Returns;
            while (true)
            {
                if (Returns == seen || InBackground)
                {
                    var next = new TaskCompletionSource<bool>();
                    waiters.Add(next);
                    using (cancellation.Register(() => next.TrySetCanceled()))
                        await next.Task;
                }
                // Back in the game: the wallet's answer may still be on its way, so wait a little before giving up on it.
                seen = Returns;
                await platform.Delay(stayMilliseconds, cancellation);
                if (!InBackground && Returns == seen) return;
            }
        }
    }

    /// <summary>
    /// A cancellation token that also fires after a delay. Uses the platform delay because WebGL has no timer threads
    /// (CancellationTokenSource.CancelAfter). Dispose it to stop the timer.
    /// </summary>
    internal sealed class WaxDeadline : IDisposable
    {
        readonly CancellationTokenSource source;
        readonly CancellationTokenSource stop = new CancellationTokenSource();

        /// <summary>Fires on cancel or when the delay is over.</summary>
        public CancellationToken Token => source.Token;

        /// <summary>Starts the delay; <paramref name="linked"/> still cancels it early.</summary>
        public WaxDeadline(CancellationToken linked, TimeSpan delay)
        {
            source = CancellationTokenSource.CreateLinkedTokenSource(linked);
            Run(delay);
        }

        async void Run(TimeSpan delay)
        {
            try
            {
                if (delay > TimeSpan.Zero) await WaxPlatform.Current.Delay((int)Math.Min(delay.TotalMilliseconds, int.MaxValue), stop.Token);
                source.Cancel();
            }
            catch (OperationCanceledException)
            {
                // Disposed before the delay ended.
            }
        }

        /// <summary>Stops the timer.</summary>
        public void Dispose()
        {
            stop.Cancel();
            stop.Dispose();
            source.Dispose();
        }
    }

    internal static class WaxPlatform
    {
        static IWaxPlatform current;

        /// <summary>The platform of this build (set by WaxKit.Initialize; tests may replace it).</summary>
        public static IWaxPlatform Current
        {
            get => current ?? throw new InvalidOperationException("Call WaxKit.Initialize(network, appName) first.");
            set => current = value;
        }

        /// <summary>True once a platform is set.</summary>
        public static bool IsSet => current != null;
    }
}
