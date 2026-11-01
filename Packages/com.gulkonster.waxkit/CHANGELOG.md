# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses [Semantic Versioning](https://semver.org/).

## [0.2.0] - 2026-11-01

### Added
- Windows (player and Editor) and Android support for Anchor and My Cloud Wallet, next to WebGL.
- Own C# wallet core: Antelope serialization through contract ABIs, transaction building and broadcast, secp256k1 keys and signatures, ESR (EOSIO signing requests), the Anchor link protocol with sealed messages over the buoy relay, and the My Cloud Wallet protocol.
- Anchor link session: after login, transactions go straight to the Anchor app (no new QR, no browser).
- Return to the game after every wallet step: on Android the kit gives Anchor the game's own return link (Anchor on Android does not open it today: it closes itself and Android shows the app it was opened from; a game closed during login is not reopened, and `RestoreSession()` finishes the login when the player opens it), the game window comes back to the front on Windows, the wallet popup closes and the tab asks for focus on WebGL.
- While a wallet request waits, the game keeps running in the background (Run In Background is turned on and put back afterwards), so the answer is handled at once.
- Windows: Anchor desktop comes in front of the game (also full screen) when a transaction is sent to it.
- Phones: every transaction opens the Anchor app by itself, like the login; a return to the game without an answer (Reject in Anchor) ends the request as Cancelled.
- My Cloud Wallet on Windows in a small app window of the player's own Chrome (or Edge), so saved sign-ins and passkeys work; on Android in a Custom Tab (bottom sheet where supported), with a fallback to the normal browser that returns to the game through its link.
- Android: My Cloud Wallet opens in Chrome whenever Chrome is installed, whatever the default browser is (it signs with a passkey, which some default browsers do not support). Switch in **Project Settings > Unity WAX Kit**: **Open My Cloud Wallet in Chrome when installed**, on by default; off uses the default browser.
- Android: the My Cloud Wallet bridge page shows a **Return to the game** button as soon as the request ends.
- A Console warning when the app name is not a valid account-style name, with the name wallets will show.
- Android return link scheme setting (**Project Settings > Unity WAX Kit**) and a build step that adds the intent filter.
- Pluggable wallets: `IWaxWallet`, `WaxKit.RegisterWallet`, `WaxKit.Login(string walletId)`, `WaxKit.Wallets`, the `WaxEsrWallet` base for any ESR wallet, `WalletType.Custom` and `WaxSession.WalletId`.
- Any Antelope chain: `new WaxNetwork(name, chainId, rpcEndpoints, coreSymbol, corePrecision, atomicEndpoints)`, `WaxNetwork.WithEndpoints`, `WaxNetwork.WithAtomicEndpoints`; sessions are stored per chain id.
- AtomicAssets / AtomicMarket reads (`WaxKit.AtomicAssets`): assets, templates, schemas, collections, account summary, transfers, sales, listings, template prices and raw JSON, with endpoint failover.
- `WaxUiRequest.WalletName` for the wallet shown in a sign step.
- The demo scene reads the player's NFTs.

### Changed
- WharfKit and the JavaScript bundle are removed; WebGL runs the same C# core as the other platforms.
- Sessions are saved in PlayerPrefs per chain id (WebGL sessions from 0.1.0 are not restored; log in again once).
- Every chain call (reads, ABI, broadcast, AtomicAssets) uses the network's own endpoint lists with failover.
- `WaxGestureButton` runs its action on click on non-WebGL platforms.
- On computers the kit no longer opens Anchor desktop by itself at login: the player scans the QR code or presses Open Anchor.
- XML documentation for the whole API and a step-by-step README (install, login, transactions, reads, networks, UI, platform notes, publishing WebGL builds, what was tested on real devices, known issues). The Windows notes say Anchor desktop 1.4.0 or newer is required.
- The demo uses a valid app name, larger network list items, restores the session at start and disables wallets the network does not support. On phones its To field keeps the typed text when the keyboard closes in any way, and the player can place the cursor and select text in it. Its Android build uses OpenGLES3 first, Vulkan second.
- The wallet panel closes as soon as the wallet answered, not after the chain accepted the transaction.
- Windows: Open Anchor brings a waiting Anchor desktop window back in front (for example after Alt+Tab behind a full-screen game) instead of sending the request again.

### Fixed
- Windows: My Cloud Wallet showed a large extra window behind the wallet when the browser was already running; the kit now puts its window and the wallet popup at the wallet size over the game, so the player sees one small window (when Chrome is already running, it can show at Chrome's last size for a moment first; see Known issues in the README).
- Chrome no longer offers to translate the kit's My Cloud Wallet page.
- Android: the return link is now one Android can resolve (`android-app://<id>/<scheme>/waxkit/`), limited to the game.
- My Cloud Wallet signing failed as Cancelled when the player first had to sign in to the wallet (the wallet page loads again and asks for the transaction again); the kit now answers every new request for the transaction and only ends on the wallet's real answer. Also on WebGL.
- Android: in browsers that keep the My Cloud Wallet page in their own task, the page opens the game's link at the end.

## [0.1.0] - 2026-10-31

### Added
- Login with Anchor and My Cloud Wallet (WebGL).
- Session restore from the browser's `localStorage`.
- `WaxKit.Transact` for signed transactions through the wallet.
- `WaxKit.GetTableRows` for contract table reads on every platform, with RPC endpoint failover.
- In-game wallet UI (`WaxDefaultUI`) and the `IWaxWalletUI` interface for custom UIs.
- `WaxKitException` with typed failure kinds.
- Demo scene generator: **Tools > Unity WAX Kit > Create Demo Scene**.
