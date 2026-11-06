using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityWaxKit
{
    /// <summary>
    /// Sets the kit up from the Inspector, no code needed: networks, app name, wallets, sessions, theme and wait times. In <c>Awake</c> it
    /// applies them and calls <see cref="WaxKit.Initialize"/> with the active network. Add it with GameObject > Unity WAX
    /// Kit > WAX Kit Manager, one per game: it stays across scenes, and a second one removes itself with a warning. Code
    /// can still change any value afterwards (these are the starting values), and the kit works without this component too.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1000)]
    [AddComponentMenu("Unity WAX Kit/WAX Kit Manager")]
    public sealed class WaxKitManager : MonoBehaviour
    {
        /// <summary>One network of the Inspector's Networks list (the fields of a <see cref="WaxNetwork"/>).</summary>
        [Serializable]
        internal sealed class NetworkEntry
        {
            [Tooltip("Name of the network; the Active network dropdown lists networks by it.")]
            public string name = "";
            [Tooltip("Chain id: 64 hex characters (get_info's chain_id).")]
            public string chainId = "";
            [Tooltip("RPC endpoints (https://…), tried in this order.")]
            public string[] rpcEndpoints = new string[0];
            [Tooltip("AtomicAssets / AtomicMarket API endpoints (https://…), tried in this order. Empty when the chain has none.")]
            public string[] atomicEndpoints = new string[0];
            [Tooltip("Core token symbol (for example WAX). Empty when unknown.")]
            public string coreSymbol = "";
            [Tooltip("Decimals of the core token (8 for WAX). Used only with a core symbol.")]
            [Min(0)] public int precision = 8;

            /// <summary>An entry filled from <paramref name="network"/>.</summary>
            internal static NetworkEntry From(WaxNetwork network) => new NetworkEntry
            {
                name = network.Name,
                chainId = network.ChainId,
                rpcEndpoints = network.RpcEndpoints.ToArray(),
                atomicEndpoints = network.AtomicEndpoints.ToArray(),
                coreSymbol = network.CoreSymbol ?? "",
                precision = Math.Max(0, network.CorePrecision),
            };

            /// <summary>The <see cref="WaxNetwork"/> this entry describes (throws ArgumentException for a wrong chain id or endpoint).</summary>
            internal WaxNetwork ToNetwork()
            {
                var symbol = string.IsNullOrWhiteSpace(coreSymbol) ? null : coreSymbol.Trim();
                return new WaxNetwork(name, chainId?.Trim(), rpcEndpoints ?? Enumerable.Empty<string>(), symbol, precision,
                    atomicEndpoints ?? Enumerable.Empty<string>());
            }
        }

        /// <summary>One token of the Tracked Tokens list (<see cref="WaxMarketSettings.TrackedTokens"/>).</summary>
        [Serializable]
        internal sealed class TrackedTokenEntry
        {
            [Tooltip("Token contract, for example alien.worlds.")]
            public string contract = "";
            [Tooltip("Symbol code in capitals, for example TLM.")]
            public string symbol = "";
            [Tooltip("Optional Alcor pool id that always gives this token's price. Empty: the kit chooses (largest network-coin reserve).")]
            public string poolId = "";

            /// <summary>The token, or null when an entry is not valid (the Inspector shows why).</summary>
            internal WaxToken ToToken()
            {
                long? pool = null;
                if (!string.IsNullOrWhiteSpace(poolId))
                {
                    if (!long.TryParse(poolId.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)) return null;
                    pool = id;
                }
                return WaxToken.IsAccountName(contract) && WaxToken.IsSymbolCode(symbol) ? new WaxToken(contract, symbol, pool) : null;
            }
        }

        /// <summary>One Alcor host for a chain id (<see cref="WaxMarketSettings.SetAlcorHost"/>).</summary>
        [Serializable]
        internal sealed class AlcorHostEntry
        {
            [Tooltip("Chain id: 64 hex characters.")]
            public string chainId = "";
            [Tooltip("Alcor API URL (https://…/api). Empty: no Alcor on this chain.")]
            public string apiUrl = "";
        }

        /// <summary>Media field names for one collection or schema (<see cref="WaxMediaSettings.SetFields"/>).</summary>
        [Serializable]
        internal sealed class MediaFieldsEntry
        {
            [Tooltip("Collection name.")]
            public string collection = "";
            [Tooltip("Schema name. Empty: the whole collection.")]
            public string schema = "";
            [Tooltip("Image field. Empty: the global one.")]
            public string imageField = "";
            [Tooltip("Video field. Empty: the global one.")]
            public string videoField = "";
            [Tooltip("Cover image field for videos. Empty: the global one.")]
            public string coverField = "";
        }

        /// <summary>Which <see cref="WaxKit.Theme"/> the wallet panel uses.</summary>
        public enum ThemeChoice
        {
            /// <summary><see cref="WaxUiTheme.Dark"/>.</summary>
            Dark,
            /// <summary><see cref="WaxUiTheme.Light"/>.</summary>
            Light,
            /// <summary>The colors and font of the custom theme field.</summary>
            Custom,
        }

        static WaxKitManager instance;

        [Tooltip("The networks the game can use, each with its chain id and endpoints. Add, remove and reorder entries and endpoints freely.")]
        [SerializeField] List<NetworkEntry> networks = new List<NetworkEntry> { NetworkEntry.From(WaxNetwork.Mainnet), NetworkEntry.From(WaxNetwork.Testnet) };
        [Tooltip("Name of the network WaxKit.Initialize gets: WAX Testnet while developing (free test tokens), WAX Mainnet when live.")]
        [SerializeField] string activeNetwork = WaxNetwork.Mainnet.Name;

        [Tooltip("Your game's name as wallets show it: up to 12 characters, a-z, 1-5 and dots (for example \"mygame\").")]
        [SerializeField] string appName = "mygame";

        [Tooltip("Anchor by Greymass (desktop and phone app, QR code). Off: removed with WaxKit.UnregisterWallet at start.")]
        [SerializeField] bool anchor = true;
        [Tooltip("My Cloud Wallet, WAX's web wallet in a browser window (WAX mainnet only). Off: removed with WaxKit.UnregisterWallet at start.")]
        [SerializeField] bool myCloudWallet = true;
        [Tooltip("XPR Network's WebAuth phone app (XPR Network only). Off: removed with WaxKit.UnregisterWallet at start.")]
        [SerializeField] bool webAuthApp = true;
        [Tooltip("XPR Network's WebAuth web wallet in a browser window (XPR Network only). Off: removed with WaxKit.UnregisterWallet at start.")]
        [SerializeField] bool webAuthWeb = true;

        [Tooltip("Off: one saved account per network; a new login replaces it. On: the kit keeps every account the player logged in with, so they can switch between them (WaxKit.MultipleSessions, WaxKit.Sessions, WaxKit.SwitchSession).")]
        [SerializeField] bool multipleSessions;
        [Tooltip("On: when the game starts (and after a network change), the last account comes back by itself, with no login screen (WaxKit.AutoRestore). Listen to WaxKit.SessionChanged to see it.")]
        [SerializeField] bool autoRestore = true;

        [Tooltip("Colors of the kit's wallet panel: Dark, Light, or Custom (your own colors and font).")]
        [SerializeField] ThemeChoice theme = ThemeChoice.Dark;
        [Tooltip("Your own colors and font for the wallet panel. The QR code always stays black on white.")]
        [SerializeField] WaxUiTheme customTheme = WaxUiTheme.Dark;

        [Tooltip("Seconds Anchor and the WebAuth phone app wait for the player to log in (WaxKit.AnchorLoginTimeout). Default 300.")]
        [SerializeField, Min(1)] int anchorLoginTimeoutSeconds = 300;
        [Tooltip("Seconds My Cloud Wallet waits for the player to log in (WaxKit.MyCloudWalletLoginTimeout). Default 300.")]
        [SerializeField, Min(1)] int myCloudWalletLoginTimeoutSeconds = 300;
        [Tooltip("Seconds the WebAuth web window may stay open, for login and signing (WaxKit.WebAuthWebTimeout). Default 300.")]
        [SerializeField, Min(1)] int webAuthWebTimeoutSeconds = 300;
        [Tooltip("Seconds a transaction stays valid; Anchor, the WebAuth phone app and My Cloud Wallet wait for the signature until then (WaxKit.TransactionExpiry). Default 120. Chains refuse more than their limit (usually 3600).")]
        [SerializeField, Min(1)] int transactionExpirySeconds = 120;

        [Tooltip("Optional XPR Network account of your app; WebAuth shows it as the requestor (WaxKit.WebAuthRequestAccount). Empty: not sent.")]
        [SerializeField] string webAuthRequestAccount = "";

        [Tooltip("Off: the kit never calls Alcor; Alcor prices are missing with \"Alcor is off\" (WaxKit.MarketSettings.AlcorEnabled).")]
        [SerializeField] bool alcorEnabled = true;
        [Tooltip("Alcor API host per chain id, in addition to (or instead of) the built-in WAX, EOS, Telos and XPR Network mainnet hosts. Testnets have none by default.")]
        [SerializeField] List<AlcorHostEntry> alcorHosts = new List<AlcorHostEntry>();
        [Tooltip("Where token prices come from: Auto (pool, else spot market), Pool Only, Spot Only (WaxKit.MarketSettings.PriceSource).")]
        [SerializeField] WaxPriceSource priceSource = WaxPriceSource.Auto;
        [Tooltip("Where the network coin's USD price comes from: Auto (the chain's oracle when fresh, else Alcor), Oracle Only, Alcor Only.")]
        [SerializeField] WaxUsdSource coreUsdSource = WaxUsdSource.Auto;
        [Tooltip("Minutes after which an oracle value is too old to use (WaxKit.MarketSettings.OracleMaxAge). Default 60.")]
        [SerializeField, Min(1)] int oracleMaxAgeMinutes = 60;
        [Tooltip("Seconds Alcor and oracle answers are reused (WaxKit.MarketSettings.CacheSeconds). 0: not cached. Default 60.")]
        [SerializeField, Min(0)] int marketCacheSeconds = 60;
        [Tooltip("Also track the network coin (first in the list).")]
        [SerializeField] bool trackNetworkCoin = true;
        [Tooltip("Tokens WaxKit.GetTrackedBalances and GetBalanceValues read (one request per token), each with an optional Alcor pool id.")]
        [SerializeField] List<TrackedTokenEntry> trackedTokens = new List<TrackedTokenEntry>();

        [Tooltip("Data field of an NFT's image (WaxKit.MediaSettings.ImageField). Default img.")]
        [SerializeField] string imageField = "img";
        [Tooltip("Data field of an NFT's video. Default video.")]
        [SerializeField] string videoField = "video";
        [Tooltip("Data field of the still image shown for a video in Static mode. Default img.")]
        [SerializeField] string coverField = "img";
        [Tooltip("Other field names for some collections or schemas (a schema entry wins over its collection's).")]
        [SerializeField] List<MediaFieldsEntry> mediaFieldOverrides = new List<MediaFieldsEntry>();
        [Tooltip("Your own IPFS gateways (https://…), tried first. You can start with the defaults (public gateways are rate-limited and change); choose your own when your traffic or uptime needs it. Never put a secret key in a URL: it ships with the game.")]
        [SerializeField] List<string> ipfsGateways = new List<string>();
        [Tooltip("Also try the kit's public gateways (gateway.pinata.cloud, ipfs.filebase.io) after yours.")]
        [SerializeField] bool useDefaultGateways = true;
        [Tooltip("Seconds one gateway may take before the next is tried. Default 15.")]
        [SerializeField, Min(1)] int gatewayTimeoutSeconds = 15;
        [Tooltip("Seconds a video may take to start (prepared and its first frame shown); later, the NFT media component reports it as failed. Default 30.")]
        [SerializeField, Min(1)] int videoStartTimeoutSeconds = 30;
        [Tooltip("Animated (GIFs animate, videos play) or Static (one still image, nothing plays) for components that do not choose.")]
        [SerializeField] WaxMediaMode mediaMode = WaxMediaMode.Animated;
        [Tooltip("Videos play their sound. Default off.")]
        [SerializeField] bool videoAudio;
        [Tooltip("Static mode: a video without a cover image shows its first frame (downloads the start of the video). Default off: the placeholder.")]
        [SerializeField] bool firstFrameForVideo;
        [Tooltip("Largest file downloaded, in MB. Default 32.")]
        [SerializeField, Min(1)] int maxDownloadMegabytes = 32;
        [Tooltip("Largest image width in pixels. Default 4096.")]
        [SerializeField, Min(1)] int maxImageWidth = 4096;
        [Tooltip("Largest image height in pixels. Default 4096.")]
        [SerializeField, Min(1)] int maxImageHeight = 4096;
        [Tooltip("Most frames of an animation. Default 500.")]
        [SerializeField, Min(1)] int maxFrames = 500;
        [Tooltip("Most memory the decoded frames of one image may use, in MB. Default 128.")]
        [SerializeField, Min(1)] int maxDecodedMegabytes = 128;
        [Tooltip("Downloaded files kept in memory, in MB. Default 64; 0 = none.")]
        [SerializeField, Min(0)] int memoryCacheMegabytes = 64;
        [Tooltip("Downloaded files kept on disk on Windows and Android, in MB. Default 256; 0 = none. WebGL keeps none.")]
        [SerializeField, Min(0)] int diskCacheMegabytes = 256;

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Debug.LogWarning($"Unity WAX Kit: there is already a WAX Kit Manager ({instance.name}); removing the one on {name}.", this);
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
            Apply();
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        /// <summary>Puts the Inspector values into <see cref="WaxKit"/> and initializes it.</summary>
        void Apply()
        {
            WaxKit.AnchorLoginTimeout = TimeSpan.FromSeconds(anchorLoginTimeoutSeconds);
            WaxKit.MyCloudWalletLoginTimeout = TimeSpan.FromSeconds(myCloudWalletLoginTimeoutSeconds);
            WaxKit.WebAuthWebTimeout = TimeSpan.FromSeconds(webAuthWebTimeoutSeconds);
            WaxKit.TransactionExpiry = TimeSpan.FromSeconds(transactionExpirySeconds);
            WaxKit.Theme = theme == ThemeChoice.Light ? WaxUiTheme.Light
                : theme == ThemeChoice.Custom && customTheme != null ? customTheme
                : WaxUiTheme.Dark;
            WaxKit.WebAuthRequestAccount = string.IsNullOrWhiteSpace(webAuthRequestAccount) ? null : webAuthRequestAccount.Trim();
            WaxKit.MultipleSessions = multipleSessions;
            WaxKit.AutoRestore = autoRestore;
            // With Domain Reload off, a wallet removed in an earlier Play is still gone: put a checked one back.
            SetWallet(WaxKit.AnchorId, anchor);
            SetWallet(WaxKit.MyCloudWalletId, myCloudWallet);
            SetWallet(WaxKit.WebAuthId, webAuthApp);
            SetWallet(WaxKit.WebAuthWebId, webAuthWeb);
            ApplyMarket(WaxKit.MarketSettings);
            ApplyMedia(WaxKit.MediaSettings);
            WaxKit.Initialize(CreateNetwork(), appName);
        }

        /// <summary>Puts the NFT media values into <paramref name="settings"/>; entries that are not valid are skipped with a warning.</summary>
        internal void ApplyMedia(WaxMediaSettings settings)
        {
            try
            {
                settings.ImageField = imageField;
                settings.VideoField = videoField;
                settings.CoverField = coverField;
            }
            catch (ArgumentException e)
            {
                Debug.LogWarning("Unity WAX Kit: media field skipped: " + e.Message, this);
            }
            foreach (var entry in mediaFieldOverrides ?? new List<MediaFieldsEntry>())
            {
                try
                {
                    settings.SetFields(entry.collection?.Trim(), string.IsNullOrWhiteSpace(entry.schema) ? null : entry.schema.Trim(),
                        entry.imageField, entry.videoField, entry.coverField);
                }
                catch (ArgumentException e)
                {
                    Debug.LogWarning("Unity WAX Kit: media field override skipped: " + e.Message, this);
                }
            }
            settings.Gateways.Clear();
            foreach (var gateway in ipfsGateways ?? new List<string>())
            {
                if (WaxMediaSettings.CheckGateway(gateway) != null) settings.Gateways.Add(gateway.Trim());
                else if (!string.IsNullOrWhiteSpace(gateway)) Debug.LogWarning($"Unity WAX Kit: IPFS gateway \"{gateway}\" skipped: it must be an https URL.", this);
            }
            settings.UseDefaultGateways = useDefaultGateways;
            settings.GatewayTimeoutSeconds = Math.Max(1, gatewayTimeoutSeconds);
            settings.VideoStartTimeoutSeconds = Math.Max(1, videoStartTimeoutSeconds);
            settings.Mode = mediaMode;
            settings.VideoAudio = videoAudio;
            settings.FirstFrameForVideo = firstFrameForVideo;
            settings.MaxDownloadBytes = (long)Math.Max(1, maxDownloadMegabytes) << 20;
            settings.MaxWidth = Math.Max(1, maxImageWidth);
            settings.MaxHeight = Math.Max(1, maxImageHeight);
            settings.MaxFrames = Math.Max(1, maxFrames);
            settings.MaxDecodedBytes = (long)Math.Max(1, maxDecodedMegabytes) << 20;
            settings.MemoryCacheBytes = (long)Math.Max(0, memoryCacheMegabytes) << 20;
            settings.DiskCacheBytes = (long)Math.Max(0, diskCacheMegabytes) << 20;
        }

        /// <summary>Puts the market data values into <paramref name="settings"/>; entries that are not valid are skipped with a warning.</summary>
        internal void ApplyMarket(WaxMarketSettings settings)
        {
            settings.AlcorEnabled = alcorEnabled;
            settings.PriceSource = priceSource;
            settings.CoreUsdSource = coreUsdSource;
            settings.OracleMaxAge = TimeSpan.FromMinutes(Math.Max(1, oracleMaxAgeMinutes));
            settings.CacheSeconds = Math.Max(0, marketCacheSeconds);
            settings.TrackNetworkCoin = trackNetworkCoin;
            foreach (var host in alcorHosts ?? new List<AlcorHostEntry>())
            {
                try
                {
                    settings.SetAlcorHost(host.chainId?.Trim(), host.apiUrl);
                }
                catch (ArgumentException e)
                {
                    Debug.LogWarning("Unity WAX Kit: Alcor host skipped: " + e.Message, this);
                }
            }
            settings.TrackedTokens.Clear();
            foreach (var entry in trackedTokens ?? new List<TrackedTokenEntry>())
            {
                var token = entry?.ToToken();
                if (token != null) settings.TrackedTokens.Add(token);
                else if (entry != null) Debug.LogWarning($"Unity WAX Kit: tracked token \"{entry.symbol}@{entry.contract}\" skipped (see the WAX Kit Manager).", this);
            }
        }

        static void SetWallet(string walletId, bool on)
        {
            if (on) WaxKit.RestoreBuiltInWallet(walletId);
            else WaxKit.UnregisterWallet(walletId);
        }

        /// <summary>The Networks list (the Inspector edits it).</summary>
        internal List<NetworkEntry> Networks => networks;

        /// <summary>
        /// The networks of the Inspector's Networks list, in list order (new objects on each call), for example to fill a
        /// network menu; pass one to <see cref="WaxKit.Initialize"/> to switch to it.
        /// </summary>
        public IReadOnlyList<WaxNetwork> CreateNetworks() => networks.Where(n => n != null).Select(n => n.ToNetwork()).ToList();

        /// <summary>The list entry named <see cref="activeNetwork"/>, or null.</summary>
        internal NetworkEntry ActiveEntry => networks?.FirstOrDefault(n => n != null && n.name == activeNetwork);

        /// <summary>The active network of the Inspector's list.</summary>
        internal WaxNetwork CreateNetwork() =>
            (ActiveEntry ?? throw new InvalidOperationException(
                $"Unity WAX Kit: the active network \"{activeNetwork}\" is not in the WAX Kit Manager's Networks list.")).ToNetwork();
    }
}
