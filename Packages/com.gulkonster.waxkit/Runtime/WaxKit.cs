using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit
{
    /// <summary>Game-facing entry point. Call <see cref="Initialize"/> once, then log in, transact and read tables.</summary>
    public static class WaxKit
    {
        static IWalletProvider provider;
        static IWaxWalletUI ui;
        static WaxUiTheme theme = WaxUiTheme.Dark;

        public static WaxNetwork Network { get; private set; }
        public static string AppName { get; private set; }
        public static WaxSession Current { get; private set; }
        public static bool IsLoggedIn => Current != null;

        /// <summary>Shows the wallet steps. Defaults to <see cref="WaxDefaultUI"/>; set your own to replace it.</summary>
        public static IWaxWalletUI UI
        {
            get => ui ??= WaxDefaultUI.Create();
            set => ui = value ?? throw new ArgumentNullException(nameof(value), "WaxKit.UI cannot be null.");
        }

        /// <summary>Colors and font of <see cref="WaxDefaultUI"/> (Dark by default). A custom <see cref="UI"/> ignores it.</summary>
        public static WaxUiTheme Theme
        {
            get => theme;
            set
            {
                theme = value ?? throw new ArgumentNullException(nameof(value), "WaxKit.Theme cannot be null.");
                if (ui is WaxDefaultUI defaultUI) defaultUI.ApplyTheme(theme);
            }
        }

        /// <summary>Selects the network. Calling it again switches networks and forgets the current session.</summary>
        public static void Initialize(WaxNetwork network, string appName)
        {
            if (network == null) throw new ArgumentNullException(nameof(network));
            if (string.IsNullOrEmpty(appName)) throw new ArgumentException("App name is required.", nameof(appName));

            Network = network;
            AppName = appName;
            Current = null;
            provider ??= CreateProvider();
            provider.Initialize(network, appName);
        }

        public static async Task<WaxSession> Login(WalletType wallet)
        {
            EnsureInitialized();
            if (wallet == WalletType.MyCloudWallet && !Network.IsMainnet)
                throw new WaxKitException(WaxErrorKind.WrongChain, $"My Cloud Wallet works on WAX mainnet only (current network: {Network.Name}).");

            Current = await provider.Login(wallet);
            return Current;
        }

        /// <summary>The saved session for this network, or null.</summary>
        public static async Task<WaxSession> RestoreSession()
        {
            EnsureInitialized();
            Current = await provider.Restore();
            return Current;
        }

        public static async Task Logout()
        {
            EnsureInitialized();
            await provider.Logout();
            Current = null;
        }

        public static async Task<WaxTransactResult> Transact(params WaxAction[] actions)
        {
            EnsureInitialized();
            if (actions == null || actions.Length == 0) throw new ArgumentException("At least one action is required.", nameof(actions));
            if (!IsLoggedIn) throw new WaxKitException(WaxErrorKind.NotLoggedIn, "Log in before sending a transaction.");

            var json = new JArray();
            foreach (var action in actions) json.Add(action.ToJson(Current));

            return new WaxTransactResult(await provider.Transact(json));
        }

        public static Task<string> GetTableRowsJson(WaxTableQuery query)
        {
            EnsureInitialized();
            if (query == null) throw new ArgumentNullException(nameof(query));
            return WaxChainApi.GetTableRows(Network, JsonConvert.SerializeObject(query));
        }

        public static async Task<WaxTableResult<T>> GetTableRows<T>(WaxTableQuery query) =>
            JsonConvert.DeserializeObject<WaxTableResult<T>>(await GetTableRowsJson(query));

        static void EnsureInitialized()
        {
            if (Network == null) throw new InvalidOperationException("Call WaxKit.Initialize(network, appName) first.");
        }

        static IWalletProvider CreateProvider()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebGLWalletProvider();
#else
            return new UnsupportedWalletProvider();
#endif
        }
    }
}
