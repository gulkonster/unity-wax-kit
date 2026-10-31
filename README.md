# Unity WAX Kit

[![Unity 6.0+](https://img.shields.io/badge/Unity-6.0%2B-black?logo=unity)](https://unity.com)
[![WAX](https://img.shields.io/badge/chain-WAX-f89022)](https://wax.io)
[![Platform: WebGL](https://img.shields.io/badge/platform-WebGL-blue)](#current-limits)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

WAX blockchain wallets for Unity games: log in with **Anchor** or **My Cloud Wallet**, send transactions, and read contract tables from C#. The wallet always signs, so the kit never handles private keys. In WebGL builds it uses [WharfKit](https://wharfkit.com) underneath.

## Install

Unity 6.0 or newer (built and tested against Unity 6.4 / 6000.4.5f1).

In **Window > Package Manager**, choose **+ > Install package from git URL…** and enter:

```
https://github.com/gulkonster/unity-wax-kit.git?path=/Packages/com.gulkonster.waxkit
```

The package pulls in `com.unity.nuget.newtonsoft-json` and `com.unity.ugui` automatically. The wallet UI uses TextMeshPro: if your project has never used it, import **Window > TextMeshPro > Import TMP Essential Resources** once.

## Usage

```csharp
using Gulkonster.WaxKit;
using Newtonsoft.Json.Linq;

// Once, at startup
WaxKit.Initialize(WaxNetwork.Testnet, "My Game");

// Log in (or pick up the session saved by an earlier visit)
var session = await WaxKit.RestoreSession() ?? await WaxKit.Login(WalletType.Anchor);
Debug.Log($"Hello {session.Actor}");

// Send a transaction (authorization defaults to the logged-in actor@permission)
var result = await WaxKit.Transact(new WaxAction("eosio.token", "transfer", new
{
    from = session.Actor,
    to = "someaccount1",
    quantity = "0.00010000 WAX", // WAX has 8 decimals
    memo = "hello from Unity",
}));
Debug.Log($"Transaction {result.TransactionId}");

// Read a table
var balances = await WaxKit.GetTableRows<JObject>(new WaxTableQuery
{
    Code = "eosio.token",
    Scope = session.Actor,
    Table = "accounts",
});
```

Every failure is a `WaxKitException` with a `Kind` (`Cancelled`, `Timeout`, `Wallet`, `Network`, `NotLoggedIn`, `WrongChain`, `NotSupported`) and the original message.

Use `new WaxNetwork(name, chainId, rpcEndpoints...)` for a custom chain. Table reads and the wallet's own chain calls try the endpoints in order and move on when one times out or is busy.

## Wallet UI

The kit draws the wallet steps in the game, never as an HTML overlay. `WaxKit.UI` shows them:

- **Anchor**: QR code, **Open Anchor**, countdown, **Cancel**
- **Continue**: title, text, **Continue**, **Cancel** (before My Cloud Wallet opens its window)
- **Waiting**: text, **Cancel**
- **Error**: message, **Close**

By default this is `WaxDefaultUI`. It builds its own overlay canvas (sorting order 32000) in code, stays inside the screen's safe area, and works in portrait and landscape. It never touches your canvases.

To use your own look, implement `IWaxWalletUI` and set it before logging in:

```csharp
public class MyWalletUI : MonoBehaviour, IWaxWalletUI
{
    public void Show(WaxUiRequest request) { /* request.Kind, Title, Text, CreateQrTexture(), ExpiresAt, Cancel() */ }
    public void Close() { /* hide */ }
}

WaxKit.UI = myWalletUI;
```

**Continue** and **Open Anchor** buttons must have a `WaxGestureButton` component, with its action set to `Continue` or `OpenAnchor`. Browsers only let a page open a wallet window or an app link inside a real browser event, and a Unity click is not one. On pointer down, the component arms the action, and the browser's next pointerup / touchend runs it. Don't also call `request.Continue()` / `request.OpenAnchor()` from the button's `onClick`. Cancel and Close are normal buttons.

## Demo

Open this repository in Unity Hub, then run **Tools > Unity WAX Kit > Create Demo Scene**. It creates `Assets/WaxKitDemo/WaxKitDemo.unity`; switch to WebGL and build it to try login, a 0.0001 WAX transfer, and a balance read. The demo uses the Input System package.

## WebGL notes

- The kit adds a `.jspre` that is included in every WebGL build automatically. You don't need a custom WebGL template or npm.
- If the page is fullscreen, the kit exits fullscreen before any wallet dialog.
- The session (`walletType`, `actor`, `permission`, `chainId`) is saved in the browser's `localStorage` under `waxkit.session`.

## Current limits

- **WebGL only for now.** In Editor play mode, Android and Windows, wallet calls throw `NotSupported`; table reads work everywhere. More platforms are planned, and the C# API will stay the same.
- **My Cloud Wallet works on mainnet only.** Logging in with it on another network fails with `WrongChain`.

## Building the JS bundle

You only need this to change the bridge. The built file is already committed.

```
cd js
npm install
npm run build
```

This writes `Packages/com.gulkonster.waxkit/Runtime/Plugins/WebGL/waxkit.bundle.jspre` and `Packages/com.gulkonster.waxkit/THIRD_PARTY_NOTICES.md`.

## Changelog

See [CHANGELOG.md](Packages/com.gulkonster.waxkit/CHANGELOG.md).

## License

MIT.

The WebGL bundle contains third-party open source packages (WharfKit, qrcode-generator and their dependencies). **Games that ship the kit must include [`THIRD_PARTY_NOTICES.md`](Packages/com.gulkonster.waxkit/THIRD_PARTY_NOTICES.md)**, for example on a licenses / credits screen or as a file next to the game.
