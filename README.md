# Unity WAX Kit

[![Unity 6.0+](https://img.shields.io/badge/Unity-6.0%2B-black?logo=unity)](https://unity.com)
[![WAX](https://img.shields.io/badge/chain-WAX-f89022)](https://wax.io)
[![Platforms](https://img.shields.io/badge/platforms-WebGL%20%7C%20Windows%20%7C%20Android-blue)](#platform-notes)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

WAX (and other Antelope chain) wallets for Unity games: log in with **Anchor** or **My Cloud Wallet**, send transactions, read contract tables and AtomicAssets (NFT) data from C#. The wallet always signs, so the kit never handles the player's keys. The same C# code runs on **WebGL, Windows (player and Editor) and Android**.

> **Use at your own risk. Not audited. Test on testnet first.**

**Contents:** [Install](#install) · [Quick start](#quick-start) · [App name](#app-name) · [Log in](#log-in) · [Send a transaction](#send-a-transaction) · [Errors](#errors) · [Restore and log out](#restore-and-log-out) · [Read balance, tables and NFTs](#read-balance-tables-and-nfts) · [Networks and endpoints](#networks-and-endpoints) · [Wallet UI and theme](#wallet-ui-and-theme) · [Adding a wallet](#adding-a-wallet) · [Platform notes](#platform-notes) ([WebGL](#webgl) · [Windows](#windows) · [Android](#android)) · [Demo](#demo) · [Current limits](#current-limits) · [Known issues](#known-issues)

## Words used here

- **Account**: a player's name on the chain, for example `mygameplayer` (up to 12 characters).
- **Wallet**: the app that holds the player's keys and approves requests: Anchor (desktop and phone app) or My Cloud Wallet (a website).
- **Transaction**: one or more **actions** (calls to a contract, for example `eosio.token` / `transfer`) that the wallet signs and the chain runs.
- **Testnet / mainnet**: the test chain (free test tokens) and the real chain.
- **Endpoint**: a server that answers chain requests (RPC) or NFT requests (AtomicAssets API).

## Install

1. Unity 6.0 or newer (built and tested with Unity 6000.4.5f1).
2. **Window > Package Manager > + > Install package from git URL…**, enter:

   ```
   https://github.com/gulkonster/unity-wax-kit.git?path=/Packages/com.gulkonster.waxkit
   ```

3. The package brings `com.unity.nuget.newtonsoft-json`, `com.unity.ugui` and the Android JNI module with it.
4. The wallet UI uses TextMeshPro. If your project has never used it, run **Window > TextMeshPro > Import TMP Essential Resources** once (otherwise the wallet panel has no text and the Console says so).
5. Android only: check **Project Settings > Unity WAX Kit** (see [Android](#android)).

## Quick start

Put this on any GameObject in your first scene:

```csharp
using Gulkonster.WaxKit;
using UnityEngine;

public class WalletExample : MonoBehaviour
{
    async void Start()
    {
        // 1. Once, at startup: pick the network and name your app (see "App name").
        WaxKit.Initialize(WaxNetwork.Testnet, "mygame");

        // 2. Pick up the session of an earlier run, or ask the player to log in.
        var session = await WaxKit.RestoreSession() ?? await WaxKit.Login(WalletType.Anchor);
        Debug.Log($"Logged in as {session.Actor}");
    }
}
```

Every kit call is `async`: call it with `await` inside an `async` method (in a MonoBehaviour, `async void Start()` or `async void OnButtonClicked()` is fine). The wallet steps (QR code, "Open Anchor", waiting, errors) appear in the game on their own; you do not need to build any UI.

## App name

The second argument of `WaxKit.Initialize` is shown by the wallet ("Login request from …"). Wallets show it as an **account-style name: at most 12 characters, only `a-z`, `1-5` and dots**. Other names are changed to fit, and the kit logs a warning in the Console with the name players will see:

```
Unity WAX Kit: AppName "Unity WAX Kit Demo" is not a valid account-style name; wallets will show it as "unity.wax.ki". Use up to 12 characters: a-z, 1-5 and dots.
```

| Good | Bad (what players see) |
|---|---|
| `"mygame"`, `"space.miner"`, `"cardgame123"` | `"My Great Game 2"` → `my.great.gam`, `"Game_6789"` → `game` |

## Log in

```csharp
var session = await WaxKit.Login(WalletType.Anchor);          // or WalletType.MyCloudWallet
Debug.Log(session.Actor);        // "mygameplayer"
Debug.Log(session.Permission);   // "active"
```

What the player sees:

| | Anchor | My Cloud Wallet (WAX mainnet only) |
|---|---|---|
| **Windows** (player, Editor) | A QR code (scan it with Anchor on the phone) and an **Open Anchor** button (Anchor desktop on this computer). The player picks one; the kit never opens Anchor desktop by itself, so a phone login is not disturbed by a second request on the computer. | A small window of the player's own **Chrome** (or **Edge**) opens over the game, so their saved Google login, passwords and passkeys work. The player presses **Continue** once in that window. The window closes by itself when done. |
| **Android** | The Anchor app opens by itself. If Anchor is not installed, a QR code is shown for Anchor on another device. | A browser sheet (Chrome Custom Tab) opens over the game. |
| **WebGL** (browser) | Desktop: QR code or **Open Anchor**. Phone browser: **Open Anchor** opens the Anchor app. | Press **Continue** in the game; My Cloud Wallet opens in a popup window. |

After the wallet answers, the game comes back to the front by itself (see [Platform notes](#platform-notes) for what each platform allows).

Only offer wallets that work on the current network. Every wallet can tell you:

```csharp
foreach (var wallet in WaxKit.Wallets)
    Debug.Log($"{wallet.Name}: {(wallet.SupportsChain(WaxKit.Network) ? "available" : "not on this network")}");

// For example, disable the My Cloud Wallet button on testnet:
var cloud = WaxKit.Wallets.First(w => w.Id == "mycloudwallet");
myCloudWalletButton.interactable = cloud.SupportsChain(WaxKit.Network);
```

## Send a transaction

```csharp
var result = await WaxKit.Transact(new WaxAction("eosio.token", "transfer", new
{
    from = WaxKit.Current.Actor,
    to = "someaccount1",
    quantity = "0.00010000 WAX",   // WAX has 8 decimals: always write all 8
    memo = "hello from Unity",
}));
Debug.Log($"Done: https://testnet.waxblock.io/transaction/{result.TransactionId}");
```

- The data object can be an anonymous object (as above), your own class, a dictionary or a `JObject`. Field names must match the contract's ABI (PascalCase properties are matched too). The kit reads the contract's ABI once per network and turns your data into the binary form the chain expects.
- Several actions in one transaction: `WaxKit.Transact(action1, action2)`. They succeed or fail together.
- Each action signs as the logged-in `actor@permission`. To sign with something else set `action.Authorization = new[] { new WaxPermissionLevel("mygameplayer", "active") }`.
- After an Anchor login, transactions go straight to the Anchor app that logged in (no new QR code). On phones the Anchor app opens by itself with the request; on Windows, Anchor desktop comes in front of the game by itself.
- The wallet panel closes as soon as the wallet answered. `Transact` itself returns a moment later, once the chain accepted the transaction (that wait is needed for the transaction id); if the chain refuses it, the panel opens again with the chain's reason.

## Errors

Every failure is a `WaxKitException` with a `Kind` and the original message (for a refused transaction, the contract's own message, for example `overdrawn balance`):

```csharp
try
{
    await WaxKit.Transact(action);
}
catch (WaxKitException e) when (e.Kind == WaxErrorKind.Cancelled)
{
    // The player pressed Cancel, closed the wallet or rejected the request: usually nothing to show.
}
catch (WaxKitException e)
{
    statusText.text = e.Message;   // also shown in the wallet panel as an error step
}
```

| Kind | Meaning |
|---|---|
| `Cancelled` | Cancel in the game, the wallet window closed, or Reject in the wallet (on phones also: the player came back from the wallet without approving). |
| `Timeout` | The wallet did not answer before the request expired (about 2 minutes for transactions, 5 for logins). |
| `Wallet` | The wallet or the chain refused it; the message says why. |
| `Network` | No endpoint answered (offline or all endpoints down). |
| `NotLoggedIn` | `Transact` before a login. |
| `WrongChain` | The wallet does not work on this network (My Cloud Wallet on testnet). |
| `NotSupported` | Not available on this platform (for example iOS), or an unknown wallet id. |

## Restore and log out

Sessions are saved per network in `PlayerPrefs` (account, wallet and the temporary Anchor link key; never the player's account keys).

```csharp
// At startup: null when nobody is logged in on this network.
var session = await WaxKit.RestoreSession();
if (session == null) ShowLoginButtons();

// Log out on this device (the wallet itself stays logged in).
await WaxKit.Logout();
```

`RestoreSession()` also finishes a login the player approved while the game was closed (Android may close a game in the background). Call it when the game starts.

`WaxKit.Initialize` again (for example to switch to mainnet) forgets the current session in memory; the saved session of each network stays and comes back with `RestoreSession()`.

## Read balance, tables and NFTs

No login is needed for reads.

```csharp
using Newtonsoft.Json.Linq;

// WAX balance: table "accounts" of "eosio.token", scope = the account
var rows = await WaxKit.GetTableRows<JObject>(new WaxTableQuery
{
    Code = "eosio.token",
    Scope = "mygameplayer",
    Table = "accounts",
});
var balance = rows.Rows.Count > 0 ? (string)rows.Rows[0]["balance"] : "0.00000000 WAX";

// Your own contract table, into your own class
public class Player { public string account; public int level; }
var players = await WaxKit.GetTableRows<Player>(new WaxTableQuery { Code = "mygamecontr", Scope = "mygamecontr", Table = "players", Limit = 100 });
// players.More == true: read on with LowerBound = players.NextKey
```

NFTs (AtomicAssets / AtomicMarket, the data AtomicHub shows):

```csharp
var atomic = WaxKit.AtomicAssets;
var mine = await atomic.GetAssets(new WaxAtomicQuery { Owner = "mygameplayer", CollectionName = "mygame", Limit = 50, Page = 1 });
foreach (var nft in mine) Debug.Log($"{nft.AssetId} {nft.Name} {nft.Data?["img"]}");

var template = await atomic.GetTemplate("mygame", "123456");
var listings = await atomic.GetListings(new WaxAtomicQuery { CollectionName = "mygame", Sort = "price", Order = "asc" });
var prices = await atomic.GetTemplatePrices(new WaxAtomicQuery { CollectionName = "mygame" });

// Any other filter the API documents
var burnable = await atomic.GetAssets(new WaxAtomicQuery { Owner = "mygameplayer" }.Set("is_burnable", "true"));
// Anything not modelled: the raw JSON ({ success, data, query_time })
var json = await atomic.GetJson("/atomicmarket/v1/auctions", new WaxAtomicQuery { Limit = 10 });
```

Fields without a property stay in `Extra`. NFT actions (transfer, burn, buy) are normal transactions through `WaxKit.Transact`.

## Networks and endpoints

`WaxNetwork.Mainnet` and `WaxNetwork.Testnet` come with lists of public endpoints. Every chain call tries the endpoints in order; a timeout, network error, 429 or 5xx moves on to the next one, and the one that worked is remembered.

```csharp
// Your own RPC servers first (a new network object; the built-in one is unchanged)
var wax = WaxNetwork.Mainnet.WithEndpoints("https://my-node.example.com", "https://wax.eosphere.io");
// Your own AtomicAssets API servers
wax = wax.WithAtomicEndpoints("https://my-atomic.example.com");
WaxKit.Initialize(wax, "mygame");

// Any Antelope chain: name, chain id, RPC endpoints, core token, optional AtomicAssets endpoints
var eos = new WaxNetwork("EOS", "aca376f206b8fc25a6ed44dbdc66547c36c6c33e3a119ffbeaef943642f0e906",
    new[] { "https://eos.greymass.com" }, coreSymbol: "EOS", corePrecision: 4);
WaxKit.Initialize(eos, "mygame");
```

Anchor works on every chain Anchor supports. My Cloud Wallet works on WAX mainnet only (`WrongChain` elsewhere).

## Wallet UI and theme

By default `WaxDefaultUI` shows the wallet steps on its own overlay canvas (sorting order 32000) over your game. It stays inside the screen's safe area, works in portrait and landscape, and never touches your canvases. The steps:

- **Anchor** (`WaxUiKind.AnchorSign`): QR code (when the wallet can be on another device), **Open {wallet}**, countdown, **Cancel**
- **Continue**: title, text, **Continue**, **Cancel** (WebGL, before My Cloud Wallet opens its window)
- **Waiting**: text, **Cancel**
- **Error**: message, **Close**

Colors and font:

```csharp
var theme = WaxUiTheme.Light;              // or WaxUiTheme.Dark (default); each call returns a new copy
theme.Primary = new Color(0.9f, 0.5f, 0.1f);
theme.Font = myTmpFontAsset;               // optional TextMeshPro font
WaxKit.Theme = theme;                      // applies at once, even while the panel is open
```

Your own look: implement `IWaxWalletUI` and set it before logging in.

```csharp
public class MyWalletUI : MonoBehaviour, IWaxWalletUI
{
    public GameObject panel; public TMP_Text title, body; public RawImage qr; public Button openButton, cancelButton;
    WaxUiRequest current;

    public void Show(WaxUiRequest request)
    {
        current = request;
        panel.SetActive(true);
        title.text = request.Title;
        body.text = request.Text;
        if (qr.texture != null) Destroy(qr.texture);
        qr.texture = request.CreateQrTexture();          // null when there is no QR
        qr.gameObject.SetActive(request.HasQr);
        openButton.gameObject.SetActive(request.CanOpenAnchor || request.Kind == WaxUiKind.Continue);
        // openButton has a WaxGestureButton component (action = OpenAnchor, or Continue for Continue steps).
    }

    public void Close() => panel.SetActive(false);

    void Awake() => cancelButton.onClick.AddListener(() => current?.Cancel());
}

WaxKit.UI = myWalletUI;
```

**Continue** and **Open {wallet}** buttons must have a `WaxGestureButton` component (action `Continue` or `OpenAnchor`). Browsers only let a page open a window or an app link inside a real browser click, and a Unity click is not one: on WebGL the component arms the action on pointer down and the browser's pointer up runs it. On other platforms the click runs it. Do not also call `request.Continue()` / `request.OpenAnchor()` from `onClick`. Cancel and Close are normal buttons.

## Adding a wallet

Wallets are plugins. Register more without changing the kit:

```csharp
WaxKit.RegisterWallet(new MyWallet());
var session = await WaxKit.Login("mywallet");   // session.Wallet == WalletType.Custom, session.WalletId == "mywallet"
```

Any wallet that speaks ESR (EOSIO signing requests, the protocol Anchor uses) needs only a few lines:

```csharp
public sealed class MyEsrWallet : WaxEsrWallet
{
    public override string Id => "mywallet";
    public override string Name => "My Wallet";                 // shown as "Open My Wallet"
    protected override string Scheme => "mywallet";             // the link scheme the wallet app handles; default "esr"
    public override bool SupportsChain(WaxNetwork network) => network.ChainId == WaxNetwork.MainnetChainId;
}
```

For anything else implement `IWaxWallet` (`Login`, `Restore`, `Logout`, `Sign`, `SupportsChain`). `WaxWalletContext` gives the network, the wallet UI (`ShowSign`, `WaitForContinue`, `ShowWaiting`), `OpenLink`, `ReturnUrl`, `ReturnToGame` and the player's Cancel as a `CancellationToken`. `Sign` returns signatures (the kit broadcasts), optionally a changed transaction, or a transaction id if the wallet broadcast it itself.

## Platform notes

### WebGL

**Building and publishing.** Build into a folder of its own (not mixed with a Windows build). Serve the game over **HTTPS**: wallets and popups need it (only `localhost` works without it while testing).

**Compression.** Unity compresses the build files to make them smaller: with Gzip they end in `.gz`, with Brotli in `.br` (**Player Settings > Publishing Settings > Compression Format**). The browser can only use them when the server says how they are compressed, with the HTTP header `Content-Encoding: gzip` (or `br`). Without it the game does not load and Unity shows an error about the server configuration. This is not caused by the kit, but you will meet it the first time you upload a build. Two ways to fix it:

1. **Easiest: Decompression Fallback.** Turn on **Player Settings > Publishing Settings > Decompression Fallback** and build again. Unity then unpacks the files itself in the browser: works on any server, the game loads a bit slower. On phones the loading bar can wait a long time near 90%; use way 2 when your server allows it.
2. **Faster: set the headers on your server.** Examples (file names follow your build name; check the `Build` folder):

   Netlify, file `_headers` next to `index.html`, for **Gzip**:

   ```
   /Build/*.gz
     Content-Encoding: gzip
   /Build/*.wasm.gz
     Content-Type: application/wasm
   /Build/*.js.gz
     Content-Type: application/javascript
   /Build/*.data.gz
     Content-Type: application/octet-stream
   ```

   Netlify `_headers` for **Brotli**:

   ```
   /Build/*.br
     Content-Encoding: br
   /Build/*.wasm.br
     Content-Type: application/wasm
   /Build/*.js.br
     Content-Type: application/javascript
   /Build/*.data.br
     Content-Type: application/octet-stream
   ```

   Apache, file `.htaccess` in the build folder:

   ```apache
   <IfModule mod_mime.c>
     RemoveType .gz .br
     AddEncoding gzip .gz
     AddEncoding br .br
     AddType application/wasm .wasm.gz .wasm.br
     AddType application/javascript .js.gz .js.br
     AddType application/octet-stream .data.gz .data.br .symbols.json.gz .symbols.json.br
   </IfModule>
   ```

   Nginx, inside your `server { }` block:

   ```nginx
   location ~ .+\.(data|symbols\.json)\.gz$ { gzip off; add_header Content-Encoding gzip; default_type application/octet-stream; }
   location ~ .+\.js\.gz$                   { gzip off; add_header Content-Encoding gzip; default_type application/javascript; }
   location ~ .+\.wasm\.gz$                 { gzip off; add_header Content-Encoding gzip; default_type application/wasm; }
   location ~ .+\.(data|symbols\.json)\.br$ { gzip off; add_header Content-Encoding br;   default_type application/octet-stream; }
   location ~ .+\.js\.br$                   { gzip off; add_header Content-Encoding br;   default_type application/javascript; }
   location ~ .+\.wasm\.br$                 { gzip off; add_header Content-Encoding br;   default_type application/wasm; }
   ```

   More servers: Unity's manual, [WebGL server configuration code samples](https://docs.unity3d.com/Manual/webgl-server-configuration-code-samples.html).

**Wallet windows and links.** Buttons that open a wallet window or link need a `WaxGestureButton` (the default UI has it). If the page is fullscreen, the kit leaves fullscreen before opening a wallet.

**Phones (Chrome Android, Safari iOS).** Anchor returns to the same browser tab after signing. For a transaction the kit tries to open Anchor by itself right after the player's Send click; browsers only allow this shortly after a tap, so if the transaction takes longer to prepare, the browser blocks it and the **Open Anchor** button stays on screen: one tap opens Anchor. Anchor on phones does not report Reject to the game: when the player comes back to the tab without approving, the request ends as `Cancelled` after a few seconds.

**Back to the game on a desktop browser.** A web page cannot bring its browser window in front of another app. After Anchor desktop (or Anchor on the phone, while the player looked at another window) the player clicks back into the browser. The kit asks the browser to focus the tab; most browsers ignore that unless the page just closed its own popup (My Cloud Wallet).

**My Cloud Wallet "Secure Vault" window.** After signing, My Cloud Wallet may open its own window "Secure Vault – Keep this window open". It is the wallet's persistent signing session: while it stays open, later signatures need no passkey confirmation. The wallet opens it, not the kit, and the kit leaves it alone (closing it would make the player confirm every later transaction with their passkey). Players can close it at any time, or turn it off in My Cloud Wallet under **Settings > Account Security**.

### Windows

- **Anchor desktop 1.4.0 or newer** is required. Older versions (for example 1.3.12) can open the signing prompt hidden after a Cancel.
- **Anchor desktop** is opened through the `esr:` link handler it installs. For transactions after login, Anchor desktop comes in front of the game by itself, also when the game is full screen; if it does not, the kit brings its window forward after about 1.5 seconds. Then the game comes back in front.
- **Back to Anchor.** If the player leaves Anchor desktop while a request waits there (for example with Alt+Tab, which can leave Anchor hidden behind a full-screen game), **Open Anchor** in the game brings the waiting Anchor window back in front instead of sending the request again. If Anchor has no open window (it was closed), the button sends the request to Anchor again.
- **Run In Background.** While a wallet request waits, the kit turns `Application.runInBackground` on, so the game handles the answer and comes back to the front without the player switching back. Afterwards it puts your own value back (a game that already had it on keeps it on).
- **My Cloud Wallet** opens in a small app-style window (no tabs, no address bar) of the player's own browser, centered over the game: Chrome when installed, otherwise Edge (part of every Windows 10 / 11). Because it is their normal browser, their Google login, saved passwords and passkeys work. The player presses **Continue** in that window once (browsers only open the wallet popup after a click). My Cloud Wallet then opens exactly over it, at the same place and size, so the player sees one small window. (A browser that is already running ignores the window size the kit asks for, and the size a page asks for its popup; so the kit moves both windows to the wallet size itself, about 560 x 860, smaller on small screens.) Further windows My Cloud Wallet opens by itself, such as its **Secure Vault**, open where the wallet puts them. The window closes by itself when the request ends (done, Cancel in the game, timeout); closing it by hand ends the request as `Cancelled`. The page talks to the game over `127.0.0.1` only. It is marked as English and "do not translate", so Chrome does not offer to translate it; Chrome may still offer that on the My Cloud Wallet pages (they belong to the wallet), and the player can close the offer or choose "Never translate English" once. Without Chrome and Edge, the default browser opens instead and the player switches back by hand.

### Android

- **Return link.** After the player approves, the kit asks Anchor to come back through the game's own link `<scheme>://waxkit/`. A build step adds it to your app, so there is nothing to edit by hand. Set the scheme in **Project Settings > Unity WAX Kit**; empty uses your application id (for example `com.mycompany.mygame://waxkit/`). Use something unique to your game. The kit sends it in the `android-app://<application id>/<scheme>/waxkit/` form, limited to your app. Anchor on Android does not open this link today: after approving, it just closes itself and Android shows the app the player came from. If Anchor was opened from the game, that is the game. If the player switched to Anchor from another app (for example through recent apps), it is that app or the home screen, and the player opens the game by hand. While the game is still running, the request finishes by itself when the player is back in it.
- **Game closed during login.** If the player (or Android) closes the game while Anchor shows the login, approving in Anchor does not open the game again. When the player opens the game, `RestoreSession()` at startup finishes that login.
- **Transactions** open the Anchor app by itself, like the login.
- **Reject in Anchor**: Anchor on phones returns to the game without telling it. When the player is back in the game without an answer for a few seconds, the request ends as `Cancelled`.
- **My Cloud Wallet** runs in a Chrome Custom Tab over the game (a bottom sheet where the browser supports it), and the game comes back by itself when the request ends. When Chrome is installed (and enabled), the kit always opens My Cloud Wallet in Chrome, even when another browser is the phone's default: My Cloud Wallet signs with a passkey, and some default browsers (for example the own browser on many Xiaomi phones) do not support passkeys. To use the phone's default browser instead, turn off **Open My Cloud Wallet in Chrome when installed** in **Project Settings > Unity WAX Kit**. On phones without Chrome (or with that setting off), the default browser runs it (as a Custom Tab, or as a normal browser window outside the game). Such browsers may ask the player once to allow new windows (**Allow**: My Cloud Wallet opens in one) and, at the end, whether the page may open your game (**OK**); the page also shows a **Return to the game** button for that case. If the player is not yet signed in to My Cloud Wallet in that browser, the wallet page loads again after the sign-in; the kit sends the transaction again then, so the request still finishes.

## Demo

Open this repository in Unity Hub and run **Tools > Unity WAX Kit > Create Demo Scene**. It creates `Assets/WaxKitDemo/WaxKitDemo.unity` (testnet by default) with login, a 0.0001 WAX transfer, a balance read and an NFT read. It runs in the Editor and in WebGL, Windows and Android builds, and uses the Input System package.

## Current limits

- **My Cloud Wallet works on WAX mainnet only** (`WrongChain` elsewhere).
- **iOS, macOS and Linux** are not supported yet: wallet calls throw `NotSupported` there; table and AtomicAssets reads work.
- **Android Custom Tab bottom sheet** needs a browser with partial Custom Tabs (Chrome 107+); other browsers show the page full height.
- The buoy relay (`https://cb.anchor.link`, run by Greymass) carries the Anchor messages; it keeps one pending message per channel, which is all the wallet protocols need.
- **Tested on real devices:** WAX mainnet and testnet; Unity Editor and Windows player (Mono) with Anchor desktop 1.4.0 and My Cloud Wallet in Chrome; Android phone (Xiaomi, Android 16) with the Anchor app and My Cloud Wallet in Chrome; WebGL in Chrome on Windows and on Android.
- **Not tested yet:** other Antelope chains (supported in code), Safari on iPhone / iPad, Windows IL2CPP builds, other browsers (Edge, Firefox, Samsung Internet...).

These lists will be updated as more is tested.

## Known issues

These come from the wallets or the browser, not from the kit. Each item says what happens, why the kit cannot solve it, and what to do.

- **Windows, My Cloud Wallet: the window is large for a moment.** When Chrome is already running, the wallet window can open at the size of Chrome's last window for a moment, before the kit moves it to its small size. Why: a running Chrome ignores the window size the kit asks for. What to do: nothing; the window shrinks by itself (about 560 x 860).
- **Android, Anchor: a game closed during login is not opened again.** If the game is closed while Anchor shows the login, approving in Anchor does not reopen the game. Why: Anchor on Android does not open the kit's return link; it only closes itself. What to do: call `RestoreSession()` when your game starts. When the player opens the game, it finishes the login.
- **Android, My Cloud Wallet without Chrome: signing needs passkey support.** My Cloud Wallet signs with a passkey (WebAuthn). When Chrome is installed, the kit uses it, so this only affects phones without Chrome (or games that turned that setting off). In browsers without passkey support (some phones' own browsers) login works, but signing ends as `Cancelled` with the reason "WebAuthn is not supported in this browser"; nothing reaches the chain. Why: the browser cannot use passkeys, and the wallet needs them. What to do: install or enable Chrome. Support for other browsers with passkeys (or other solutions) can be added by pull request.

Found a fix for one of these? Anyone is welcome to open a pull request.

## Changelog

See [CHANGELOG.md](Packages/com.gulkonster.waxkit/CHANGELOG.md).

## License

MIT. The kit ships BouncyCastle, adds AndroidX Browser to Android builds, and contains C# ports of parts of WharfKit, Greymass buoy, elliptic, hmac-drbg and qrcode-generator. **Games that ship the kit must include [`THIRD_PARTY_NOTICES.md`](Packages/com.gulkonster.waxkit/THIRD_PARTY_NOTICES.md)**, for example on a licenses / credits screen or as a file next to the game.

Questions and bug reports: open an issue.

Pull requests are welcome.

Like the kit? [Sponsor it on GitHub](https://github.com/sponsors/gulkonster).

Built with the help of AI coding tools. Design, decisions and real-device testing by [gulkonster](https://github.com/gulkonster).
