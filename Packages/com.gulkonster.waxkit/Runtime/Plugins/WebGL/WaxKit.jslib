// Browser side of the WebGL platform (WaxWebGLPlatform.cs): WebSockets, wallet links, wallet popups
// and running a wallet step inside the next real user gesture. Events go to C# as (kind, id, text).
var WaxKitPlugin = {
  $WaxKit: {
    onEvent: 0,
    nextId: 1,
    sockets: {},
    popups: {},
    armed: null,

    send: function (kind, id, text) {
      var ptr = stringToNewUTF8(text === undefined || text === null ? '' : String(text));
      try {
        {{{ makeDynCall('viii', 'WaxKit.onEvent') }}}(kind, id, ptr);
      } finally {
        _free(ptr);
      }
    },

    exitFullscreen: function () {
      if (document.fullscreenElement && document.exitFullscreen) document.exitFullscreen().catch(function () {});
    },

    toBase64: function (bytes) {
      var binary = '';
      for (var i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
      return btoa(binary);
    },

    ua: function () { return navigator.userAgent; },
    isAppleHandheld: function () { return /iP(ad|od|hone)/i.test(WaxKit.ua()); },
    isAndroid: function () { return /Android/.test(WaxKit.ua()); },
    isBrave: function () { return navigator.brave && typeof navigator.brave.isBrave === 'function'; },

    // Where the Anchor app sends the player back after signing (generateReturnUrl of @wharfkit/protocol-esr).
    returnUrl: function () {
      var ua = WaxKit.ua();
      if (/CriOS/.test(ua)) return 'googlechrome://';
      if (/FxiOS/.test(ua)) return 'firefox:://';
      if (WaxKit.isAppleHandheld() && WaxKit.isBrave()) return 'brave://';
      if (WaxKit.isAppleHandheld()) {
        // A unique fragment makes iOS Safari return to this tab without reloading.
        var alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
        var url = window.location.href.split('#')[0] + '#';
        for (var i = 0; i < 8; i++) url += alphabet.charAt(Math.floor(Math.random() * alphabet.length));
        return url;
      }
      if (WaxKit.isAndroid()) {
        if (/Firefox/i.test(ua)) return 'android-app://org.mozilla.firefox';
        if (/Edg/.test(ua)) return 'android-app://com.microsoft.emmx';
        if (/OPR/.test(ua) || /Opera/.test(ua)) return 'android-app://com.opera.browser';
        if (WaxKit.isBrave()) return 'android-app://com.brave.browser';
        if (/wv/.test(ua)) return 'android-app://webview';
        if (/Chrome\/[.0-9]* Mobile/i.test(ua)) return 'android-app://com.android.chrome';
      }
      return window.location.href;
    },
  },

  WaxKit_Init: function (onEvent) {
    WaxKit.onEvent = onEvent;
    // Tells C# when the tab is hidden (the player went to the wallet app) and visible again (event 30, '1' = hidden).
    document.addEventListener('visibilitychange', function () {
      WaxKit.send(30, 0, document.hidden ? '1' : '0');
    });
  },

  WaxKit_IsMobile: function () {
    var ua = WaxKit.ua();
    return WaxKit.isAppleHandheld() || /CriOS|FxiOS/.test(ua) || /Chrome\/[.0-9]* Mobile/i.test(ua) || WaxKit.isAndroid() ? 1 : 0;
  },

  WaxKit_ReturnUrl: function () {
    return stringToNewUTF8(WaxKit.returnUrl());
  },

  WaxKit_OpenLink: function (url) {
    WaxKit.exitFullscreen();
    window.location.href = UTF8ToString(url);
  },

  // Asks the browser to show this tab again. Desktop browsers only let a page raise its own window in a few cases
  // (for example right after its own popup closed); otherwise the request is ignored and the player switches back.
  WaxKit_Focus: function () {
    try { window.focus(); } catch (e) {}
  },

  // Runs the action inside the browser's next pointerup / touchend: browsers only open windows and app links there.
  WaxKit_Arm: function (action) {
    var first = WaxKit.armed === null;
    WaxKit.armed = UTF8ToString(action);
    if (!first) return;
    var run = function () {
      window.removeEventListener('pointerup', run, true);
      window.removeEventListener('touchend', run, true);
      var armed = WaxKit.armed;
      WaxKit.armed = null;
      if (armed) WaxKit.send(20, 0, armed);
    };
    window.addEventListener('pointerup', run, true);
    window.addEventListener('touchend', run, true);
  },

  WaxKit_SocketOpen: function (url) {
    var id = WaxKit.nextId++;
    var socket = new WebSocket(UTF8ToString(url));
    socket.binaryType = 'arraybuffer';
    WaxKit.sockets[id] = socket;
    socket.onopen = function () { WaxKit.send(1, id, ''); };
    socket.onmessage = function (event) {
      var bytes = typeof event.data === 'string' ? new TextEncoder().encode(event.data) : new Uint8Array(event.data);
      WaxKit.send(2, id, WaxKit.toBase64(bytes));
    };
    socket.onclose = function () {
      delete WaxKit.sockets[id];
      WaxKit.send(3, id, '');
    };
    return id;
  },

  WaxKit_SocketSend: function (id, base64) {
    var socket = WaxKit.sockets[id];
    if (!socket || socket.readyState !== 1) return;
    var binary = atob(UTF8ToString(base64));
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    socket.send(bytes);
  },

  WaxKit_SocketClose: function (id) {
    var socket = WaxKit.sockets[id];
    if (socket) socket.close(1000);
  },

  // Wallet popup (exchange of @wharfkit/wallet-plugin-cloudwallet): when the page sends its first message, `post`
  // is posted back with the transaction as Uint8Array; the message with 'verified' is the result.
  WaxKit_PopupOpen: function (url, postJson) {
    var id = WaxKit.nextId++;
    var target = UTF8ToString(url);
    var postText = UTF8ToString(postJson);
    var post = postText ? JSON.parse(postText) : null;
    var origin = new URL(target).origin;

    WaxKit.exitFullscreen();
    var popup = window.open(target, 'WalletPluginCloudWalletPopup', 'height=800,width=600');
    if (!popup) {
      setTimeout(function () { WaxKit.send(12, id, ''); }, 0);
      return id;
    }

    var posted = false;
    var state = { popup: popup, done: false };
    var finish = function (kind, text) {
      if (state.done) return;
      state.done = true;
      clearInterval(state.timer);
      window.removeEventListener('message', onMessage);
      try { if (!popup.closed) popup.close(); } catch (e) {}
      delete WaxKit.popups[id];
      window.focus();
      WaxKit.send(kind, id, text);
    };
    var plain = function (value) {
      return JSON.stringify(value, function (key, v) { return v instanceof Uint8Array ? Array.from(v) : v; });
    };
    // The wallet's answer always carries 'verified'. Other messages are steps on the way: the signing page says READY
    // each time it loads (again after the player signed in to the wallet), and each READY gets the transaction.
    var onMessage = function (event) {
      if (event.origin !== origin || event.source !== popup || !event.data || typeof event.data !== 'object') return;
      if ('verified' in event.data) { finish(10, plain(event.data)); return; }
      if (post && (!posted || event.data.type === 'READY')) {
        posted = true;
        var message = Object.assign({}, post, { startTime: Date.now(), transaction: new Uint8Array(post.transaction) });
        popup.postMessage(message, target);
      }
    };
    window.addEventListener('message', onMessage);
    state.timer = setInterval(function () { if (popup.closed) finish(11, ''); }, 500);
    state.finish = finish;
    WaxKit.popups[id] = state;
    return id;
  },

  WaxKit_PopupClose: function (id) {
    var state = WaxKit.popups[id];
    if (!state) return;
    state.done = true;
    clearInterval(state.timer);
    try { state.popup.close(); } catch (e) {}
    delete WaxKit.popups[id];
  },
};

autoAddDeps(WaxKitPlugin, '$WaxKit');
mergeInto(LibraryManager.library, WaxKitPlugin);
