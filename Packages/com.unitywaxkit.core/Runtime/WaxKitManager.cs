using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityWaxKit
{
    /// <summary>
    /// Sets the kit up from the Inspector, no code needed: networks, app name, theme and wait times. In <c>Awake</c> it
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
            if (!string.IsNullOrWhiteSpace(webAuthRequestAccount)) WaxKit.WebAuthRequestAccount = webAuthRequestAccount.Trim();
            if (!anchor) WaxKit.UnregisterWallet(WaxKit.AnchorId);
            if (!myCloudWallet) WaxKit.UnregisterWallet(WaxKit.MyCloudWalletId);
            if (!webAuthApp) WaxKit.UnregisterWallet(WaxKit.WebAuthId);
            if (!webAuthWeb) WaxKit.UnregisterWallet(WaxKit.WebAuthWebId);
            WaxKit.Initialize(CreateNetwork(), appName);
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
