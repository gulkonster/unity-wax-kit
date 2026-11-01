using System;
using System.Threading;
using System.Threading.Tasks;
using Gulkonster.WaxKit.Antelope;
using Gulkonster.WaxKit.Crypto;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit.Platform
{
    /// <summary>
    /// Wallet popup on native platforms. A browser window (Windows) or in-app browser (Android) loads a small bridge page
    /// served by the game on 127.0.0.1; the page opens the wallet as its popup (the wallet answers its opener with
    /// postMessage), runs the exchange and posts the result back to the game. Everything after the page loads works while
    /// the game is paused behind it.
    /// </summary>
    internal sealed class WaxNativePopupFlow : IWaxPopupFlow
    {
        /// <summary>How long the page may stay silent before the game treats its window as closed (see WatchBrowserWindow).</summary>
        const int SilentPageMilliseconds = 75000;
        /// <summary>The server stays up this long after the end, so the page can still learn that it should close.</summary>
        const int LingerMilliseconds = 8000;

        readonly TaskCompletionSource<JObject> result = new TaskCompletionSource<JObject>();
        readonly WaxLoopbackServer server;
        readonly string token = Hex.Encode(WaxCrypto.RandomBytes(16));
        readonly string walletUrl;
        readonly JObject post;
        readonly Action<WaxNativePopupFlow> onFinished;
        Timer watchdog;
        // Kept in a field: a timer nothing references may be collected before it fires.
        Timer linger;
        long lastSeen;
        int disposed;

        /// <summary>The wallet's answer, or an exception (closed = Cancelled).</summary>
        public Task<JObject> Result => result.Task;
        /// <summary>The bridge page to load in the browser.</summary>
        public string BridgeUrl => $"{server.BaseUrl}/{token}/";
        /// <summary>True once the flow ended.</summary>
        public bool IsFinished => result.Task.IsCompleted;

        /// <summary>The page closes its own browser window when the request ends (Windows app window).</summary>
        public bool CloseBrowserWindowWhenDone { get; set; }

        /// <summary>
        /// Treat the browser window as closed when the page stops checking in (the player closed the window in a way the
        /// page could not report). Only for desktop browser windows: phones pause hidden pages.
        /// </summary>
        public bool WatchBrowserWindow { get; set; }

        /// <summary>
        /// A link that brings the player back to the game, which the page opens when it is still on screen shortly after
        /// the request ended (Android browsers that keep the page in their own task, where the game cannot bring itself
        /// back), or null.
        /// </summary>
        public string ReturnLink { get; set; }

        /// <param name="onFinished">Called on the server thread when the page reports the end, to close the browser.</param>
        public WaxNativePopupFlow(string walletUrl, JObject post, Action<WaxNativePopupFlow> onFinished)
        {
            this.walletUrl = walletUrl;
            this.post = post;
            this.onFinished = onFinished;
            server = new WaxLoopbackServer(Handle);
        }

        WaxLoopbackServer.Response Handle(WaxLoopbackServer.Request request)
        {
            Interlocked.Exchange(ref lastSeen, Environment.TickCount);
            var path = request.Path.Split('?')[0];

            if (path == $"/{token}/" && request.Method == "GET")
            {
                StartWatchdog();
                return new WaxLoopbackServer.Response { ContentType = "text/html; charset=utf-8", Body = Page() };
            }

            // The page asks every second whether the request is still open (and so tells the game it is still there).
            if (path == $"/{token}/state" && request.Method == "GET")
                return new WaxLoopbackServer.Response { Body = IsFinished ? "closed" : "open" };

            if (path == $"/{token}/result" && request.Method == "POST")
            {
                var message = WaxJson.Parse(string.IsNullOrEmpty(request.Body) ? "{}" : request.Body);
                var type = (string)message["type"];
                Finish(type == "result" ? message["data"] as JObject : null,
                    type == "blocked" ? "The wallet window could not be opened." : null);
                return new WaxLoopbackServer.Response { Body = "ok" };
            }
            return null;
        }

        string Page()
        {
            var config = new JObject
            {
                ["url"] = walletUrl,
                ["post"] = post,
                ["closeWindow"] = CloseBrowserWindowWhenDone,
                ["returnLink"] = ReturnLink,
            }.ToString(Formatting.None);
            return BridgePage.Replace("__CONFIG__", config.Replace("</", "<\\/"));
        }

        void StartWatchdog()
        {
            if (!WatchBrowserWindow || watchdog != null) return;
            watchdog = new Timer(_ =>
            {
                if (IsFinished) return;
                if (unchecked(Environment.TickCount - Interlocked.Read(ref lastSeen)) > SilentPageMilliseconds) Finish(null);
            }, null, 5000, 5000);
        }

        /// <summary>Ends the flow: data = the wallet's answer, null = closed by the player.</summary>
        public void Finish(JObject data, string error = null)
        {
            bool first;
            if (error != null) first = result.TrySetException(new WaxKitException(WaxErrorKind.Wallet, error));
            else if (data == null) first = result.TrySetException(new WaxKitException(WaxErrorKind.Cancelled, "The wallet window was closed before the request was completed."));
            else first = result.TrySetResult(data);
            if (!first) return;

            onFinished?.Invoke(this);
            // Keep answering "closed" for a moment, so a page still open (game Cancel, timeout) closes its window.
            linger = new Timer(_ => Dispose(), null, LingerMilliseconds, Timeout.Infinite);
        }

        /// <summary>Ends the flow as closed by the player; the page closes its window.</summary>
        public void Close() => Finish(null);

        void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            watchdog?.Dispose();
            linger?.Dispose();
            server.Dispose();
        }

        /// <summary>Title of the bridge page; the Windows platform finds the browser window by it.</summary>
        internal const string PageTitle = "My Cloud Wallet";

        // Opens the wallet as a popup of this page and relays its answer (the exchange of @wharfkit/wallet-plugin-cloudwallet).
        // lang + notranslate: Chrome would otherwise offer to translate this page (its bubble) when the browser's language
        // is not English.
        const string BridgePage = @"<!doctype html>
<html lang=""en"" translate=""no"">
<head>
<meta charset=""utf-8"">
<meta name=""google"" content=""notranslate"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>" + PageTitle + @"</title>
<style>
  html, body { margin: 0; height: 100%; font-family: system-ui, sans-serif; background: #1c1f24; color: #eef0f4; }
  main { min-height: 100%; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 16px; padding: 24px; box-sizing: border-box; text-align: center; }
  button, a.button { font-size: 18px; padding: 14px 28px; border: 0; border-radius: 10px; background: #3866d9; color: #fff; text-decoration: none; }
</style>
</head>
<body>
<main>
  <p id=""text"">Opening My Cloud Wallet…</p>
  <button id=""go"" hidden>Continue</button>
  <a id=""back"" class=""button"" hidden>Return to the game</a>
</main>
<script>
(function () {
  var config = __CONFIG__;
  var origin = new URL(config.url).origin;
  var text = document.getElementById('text');
  var go = document.getElementById('go');
  var back = document.getElementById('back');
  var popup = null, timer = null, poll = null, posted = false, done = false;

  function plain(value) {
    return JSON.parse(JSON.stringify(value, function (key, v) { return v instanceof Uint8Array ? Array.from(v) : v; }));
  }

  // Closes the wallet popup and, when asked, this window too (the game comes back in front by itself).
  function closeAll() {
    clearInterval(timer);
    clearInterval(poll);
    try { if (popup && !popup.closed) popup.close(); } catch (e) {}
    if (config.closeWindow) window.close();
  }

  // Android: the Return button is shown as soon as the request ends, for browsers that want a tap or that do not open
  // the return link by themselves (the first time, before the player allowed it).
  function showBack() {
    if (!config.returnLink) return;
    back.href = config.returnLink;
    back.hidden = false;
  }

  // Android: the game normally closes this page itself. If the page is still on screen a moment later (a browser that
  // keeps it in its own task), it opens the game's return link.
  function goBack() {
    if (!config.returnLink) return;
    setTimeout(function () {
      if (document.visibilityState === 'hidden') return;
      window.location.href = config.returnLink;
    }, 1500);
  }

  function finish(type, data) {
    if (done) return;
    done = true;
    text.textContent = 'Done. Returning to the game…';
    showBack();
    fetch('result', { method: 'POST', body: JSON.stringify({ type: type, data: data }) })
      .catch(function () {})
      .then(function () { closeAll(); goBack(); });
  }

  // The wallet's answer always carries 'verified' (true, or false for cancel / failure). Other messages are steps on the
  // way: the signing page says READY each time it loads, and it loads again when the player first has to sign in to
  // the wallet in this browser. Each READY gets the transaction (again); nothing else ends the request.
  window.addEventListener('message', function (event) {
    if (event.origin !== origin || event.source !== popup || !event.data || typeof event.data !== 'object') return;
    if ('verified' in event.data) { finish('result', plain(event.data)); return; }
    if (config.post && (!posted || event.data.type === 'READY')) {
      posted = true;
      var message = Object.assign({}, config.post, { startTime: Date.now(), transaction: new Uint8Array(config.post.transaction) });
      popup.postMessage(message, config.url);
    }
  });

  // The player closed this window: tell the game at once.
  window.addEventListener('pagehide', function () {
    if (!done && navigator.sendBeacon) navigator.sendBeacon('result', JSON.stringify({ type: 'closed' }));
  });

  // The game ended the request (Cancel in the game, time out): close everything.
  poll = setInterval(function () {
    fetch('state').then(function (r) { return r.text(); }).then(function (state) {
      if (state === 'closed' && !done) { done = true; text.textContent = 'The request has ended. You can close this window.'; showBack(); closeAll(); }
    }).catch(function () {});
  }, 1000);

  function open() {
    // Same size as this window and centered over it.
    var width = Math.min(window.outerWidth || 560, 600), height = Math.min(window.outerHeight || 860, 860);
    var left = Math.round((window.screenX || 0) + ((window.outerWidth || width) - width) / 2);
    var top = Math.round((window.screenY || 0) + ((window.outerHeight || height) - height) / 2);
    popup = window.open(config.url, 'WalletPluginCloudWalletPopup', 'width=' + width + ',height=' + height + ',left=' + left + ',top=' + top);
    if (!popup) {
      // The browser only opens a window after a click on this page.
      text.textContent = 'Press Continue to open My Cloud Wallet.';
      go.hidden = false;
      return;
    }
    go.hidden = true;
    text.textContent = 'Complete the request in the My Cloud Wallet window.';
    timer = setInterval(function () { if (popup.closed) finish('closed'); }, 500);
  }

  go.onclick = open;
  open();
})();
</script>
</body>
</html>";
    }
}
