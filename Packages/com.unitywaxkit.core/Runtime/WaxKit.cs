using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityWaxKit.Antelope;
using UnityWaxKit.Chain;
using UnityWaxKit.Platform;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit
{
    /// <summary>
    /// The kit's entry point: everything a game calls is here. Call <see cref="Initialize"/> once at startup, then log in,
    /// send transactions and read tables. All calls are async: <c>await</c> them from an <c>async</c> method.
    /// </summary>
    /// <example><code>
    /// async void Start()
    /// {
    ///     WaxKit.Initialize(WaxNetwork.Testnet, "mygame");
    ///     var session = await WaxKit.RestoreSession() ?? await WaxKit.Login(WalletType.Anchor);
    ///     var result = await WaxKit.Transact(new WaxAction("eosio.token", "transfer",
    ///         new { from = session.Actor, to = "someaccount1", quantity = "0.00010000 WAX", memo = "hi" }));
    ///     Debug.Log(result.TransactionId);
    /// }
    /// </code></example>
    public static class WaxKit
    {
        internal const string Version = "0.2.1";
        const string SessionKeyPrefix = "waxkit.session.";

        // Ids of the built-in wallets, known also while one is unregistered.
        internal const string AnchorId = "anchor";
        internal const string MyCloudWalletId = "mycloudwallet";
        internal const string WebAuthId = "webauth";
        internal const string WebAuthWebId = "webauthweb";

        static readonly Dictionary<string, IWaxWallet> wallets = new Dictionary<string, IWaxWallet>();
        static IWaxWalletUI ui;
        static WaxUiTheme theme = WaxUiTheme.Dark;
        static IWaxWallet currentWallet;
        static WaxWalletSession currentWalletSession;
        // Increased by Initialize and Logout: a login or restore that started before keeps its result to itself.
        static int generation;

        static TimeSpan anchorLoginTimeout = TimeSpan.FromMinutes(5);
        static TimeSpan myCloudWalletLoginTimeout = TimeSpan.FromMinutes(5);
        static TimeSpan webAuthWebTimeout = TimeSpan.FromMinutes(5);
        static TimeSpan transactionExpiry = TimeSpan.FromSeconds(120);

        static WaxKit()
        {
            RegisterWallet(new AnchorWallet());
            RegisterWallet(new MyCloudWallet());
            RegisterWallet(new WebAuthWallet());
            RegisterWallet(new WebAuthWebWallet());
        }

        /// <summary>The network chosen with <see cref="Initialize"/>.</summary>
        public static WaxNetwork Network { get; private set; }

        /// <summary>
        /// The app name given to <see cref="Initialize"/>. Wallets show it to the player (Anchor: "Login request from …")
        /// as an account-style name: at most 12 characters, only a-z, 1-5 and dots. Other names are changed to fit
        /// (lowercase, other characters become dots, cut at 12), and <see cref="Initialize"/> logs a warning with the
        /// result. Good: "mygame", "space.miner". Bad: "My Great Game 2" (shown as "my.great.gam").
        /// </summary>
        public static string AppName { get; private set; }

        /// <summary>The logged-in session, or null.</summary>
        public static WaxSession Current { get; private set; }

        /// <summary>True when a player is logged in (<see cref="Current"/> is set).</summary>
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

        /// <summary>
        /// Optional XPR Network account of your app, sent with WebAuth login and signing requests as "req_account" (as
        /// @proton/web-sdk's requestAccount); WebAuth shows it as the requestor. Null (default): not sent. Other wallets
        /// ignore it.
        /// </summary>
        public static string WebAuthRequestAccount { get; set; }

        /// <summary>
        /// How long Anchor and the WebAuth phone app (and other ESR wallets) wait for the player to log in (default 5
        /// minutes). Set it before <see cref="Login(WalletType)"/>. Throws <see cref="ArgumentOutOfRangeException"/> for zero or less.
        /// </summary>
        public static TimeSpan AnchorLoginTimeout
        {
            get => anchorLoginTimeout;
            set => anchorLoginTimeout = Positive(value, nameof(AnchorLoginTimeout));
        }

        /// <summary>
        /// How long My Cloud Wallet waits for the player to log in (default 5 minutes). Signing waits until the
        /// transaction expires (<see cref="TransactionExpiry"/>). Throws <see cref="ArgumentOutOfRangeException"/> for zero or less.
        /// </summary>
        public static TimeSpan MyCloudWalletLoginTimeout
        {
            get => myCloudWalletLoginTimeout;
            set => myCloudWalletLoginTimeout = Positive(value, nameof(MyCloudWalletLoginTimeout));
        }

        /// <summary>
        /// How long the WebAuth web wallet window may stay open, for login and for signing (default 5 minutes).
        /// Throws <see cref="ArgumentOutOfRangeException"/> for zero or less.
        /// </summary>
        public static TimeSpan WebAuthWebTimeout
        {
            get => webAuthWebTimeout;
            set => webAuthWebTimeout = Positive(value, nameof(WebAuthWebTimeout));
        }

        /// <summary>
        /// How long a transaction built by <see cref="Transact"/> stays valid (default 120 seconds). Anchor, the WebAuth
        /// phone app and My Cloud Wallet wait for the player's signature until then. Chains refuse transactions that expire
        /// later than their own limit (usually one hour). Throws <see cref="ArgumentOutOfRangeException"/> for zero or less.
        /// </summary>
        public static TimeSpan TransactionExpiry
        {
            get => transactionExpiry;
            set => transactionExpiry = Positive(value, nameof(TransactionExpiry));
        }

        /// <summary>The registered wallets: the built-in ones not removed with <see cref="UnregisterWallet"/>, and any added with <see cref="RegisterWallet"/>.</summary>
        public static IReadOnlyCollection<IWaxWallet> Wallets => wallets.Values.ToList();

        /// <summary>AtomicAssets / AtomicMarket reads on the current network's AtomicAssets endpoints.</summary>
        public static WaxAtomicAssets AtomicAssets
        {
            get
            {
                EnsureInitialized();
                return new WaxAtomicAssets(Network);
            }
        }

        internal static string UserAgent => $"Unity WAX Kit {Version} ({(WaxPlatform.IsSet ? WaxPlatform.Current.UserAgent : "unknown")})";

        /// <summary>
        /// Selects the network and names the app. Call it once before anything else; calling it again switches networks and
        /// forgets the current session (saved sessions stay saved per network).
        /// </summary>
        /// <param name="network">For example <see cref="WaxNetwork.Testnet"/> while developing, <see cref="WaxNetwork.Mainnet"/> when live.</param>
        /// <param name="appName">
        /// Your game's name as wallets show it: up to 12 characters, a-z, 1-5 and dots (for example "mygame"). See <see cref="AppName"/>.
        /// </param>
        public static void Initialize(WaxNetwork network, string appName)
        {
            if (network == null) throw new ArgumentNullException(nameof(network));
            if (string.IsNullOrEmpty(appName)) throw new ArgumentException("App name is required.", nameof(appName));

            if (!WaxPlatform.IsSet) WaxPlatform.Current = WaxUnityPlatform.Create();
            Network = network;
            AppName = appName;

            // Wallets show the app as an account-style name. Tell the developer when theirs gets changed.
            var shown = AntelopeName.FromText(appName);
            if (shown != appName)
                LogWarning($"AppName \"{appName}\" is not a valid account-style name; wallets will show it as \"{shown}\". " +
                           "Use up to 12 characters: a-z, 1-5 and dots.");
            Current = null;
            currentWallet = null;
            currentWalletSession = null;
            generation++;
        }

        /// <summary>Adds a wallet (or replaces the one with the same id), so <see cref="Login(string)"/> can use it.</summary>
        public static void RegisterWallet(IWaxWallet wallet)
        {
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));
            if (string.IsNullOrEmpty(wallet.Id)) throw new ArgumentException("A wallet needs an id.", nameof(wallet));
            wallets[wallet.Id] = wallet;
        }

        /// <summary>
        /// Removes a wallet, built-in ("anchor", "mycloudwallet", "webauth", "webauthweb") or added with
        /// <see cref="RegisterWallet"/>: it leaves <see cref="Wallets"/> and <see cref="Login(string)"/> throws NotSupported for
        /// it. Pass the same wallet object to <see cref="RegisterWallet"/> to add it back. Returns false when no wallet has this id.
        /// </summary>
        public static bool UnregisterWallet(string walletId) => walletId != null && wallets.Remove(walletId);

        /// <summary>Registers the built-in wallet with this id again when no wallet has the id (WaxKitManager, Domain Reload off).</summary>
        internal static void RestoreBuiltInWallet(string walletId)
        {
            if (wallets.ContainsKey(walletId)) return;
            IWaxWallet wallet = walletId == AnchorId ? new AnchorWallet()
                : walletId == MyCloudWalletId ? new MyCloudWallet()
                : walletId == WebAuthId ? new WebAuthWallet()
                : walletId == WebAuthWebId ? (IWaxWallet)new WebAuthWebWallet()
                : null;
            if (wallet != null) RegisterWallet(wallet);
        }

        /// <summary>
        /// Asks the player to log in with a wallet and returns the session (also saved for <see cref="RestoreSession"/>).
        /// Shows the wallet steps in <see cref="UI"/>. Throws <see cref="WaxKitException"/>: Cancelled, Timeout,
        /// WrongChain (wallet not on this network), Wallet, Network.
        /// </summary>
        public static Task<WaxSession> Login(WalletType wallet)
        {
            if (wallet == WalletType.Custom) throw new ArgumentException("Use Login(walletId) for registered wallets.", nameof(wallet));
            return Login(WalletIdOf(wallet));
        }

        /// <summary>Logs in with a wallet by id ("anchor", "mycloudwallet" or a registered wallet).</summary>
        public static async Task<WaxSession> Login(string walletId)
        {
            EnsureInitialized();
            var wallet = FindWallet(walletId) ?? throw new WaxKitException(WaxErrorKind.NotSupported,
                $"{BuiltinName(walletId) ?? $"The wallet '{walletId}'"} is not registered: it is turned off in the WAX Kit Manager, " +
                "was removed with WaxKit.UnregisterWallet, or was never added with WaxKit.RegisterWallet.");
            if (!wallet.SupportsChain(Network))
                throw new WaxKitException(WaxErrorKind.WrongChain,
                    wallet.Id == MyCloudWalletId
                        ? $"My Cloud Wallet works on WAX mainnet only (current network: {Network.Name})."
                        : $"{wallet.Name} does not support the network {Network.Name}.");

            var network = Network;
            var started = generation;
            var context = new WaxWalletContext(network, AppName, UI);
            var walletSession = await Run(context, () => wallet.Login(context));
            if (generation != started || Network != network)
                throw new WaxKitException(WaxErrorKind.Cancelled, "The login was replaced by a logout or a network change.");
            SetSession(wallet, walletSession);
            Save();
            return Current;
        }

        /// <summary>
        /// The session saved by an earlier run on this network, or null (no window, no wallet step). Also finishes a login
        /// the player approved while the game was closed (Android may close the game while the player is in Anchor).
        /// Call it at startup before offering the login buttons.
        /// </summary>
        public static async Task<WaxSession> RestoreSession()
        {
            EnsureInitialized();
            var network = Network;
            var started = generation;
            var key = SessionKeyPrefix + network.ChainId;
            var saved = WaxPlatform.Current.Load(key);
            if (string.IsNullOrEmpty(saved)) return await ResumePendingLogin(network);

            JObject json;
            try
            {
                json = WaxJson.Parse(saved);
            }
            catch (Exception)
            {
                WaxPlatform.Current.Delete(key);
                return null;
            }

            var wallet = FindWallet((string)json["wallet"]);
            if (wallet == null || (string)json["chainId"] != network.ChainId)
            {
                WaxPlatform.Current.Delete(key);
                return null;
            }

            var walletSession = new WaxWalletSession((string)json["actor"], (string)json["permission"], json["data"] as JObject);
            var context = new WaxWalletContext(network, AppName, UI);
            if (!await wallet.Restore(context, walletSession))
            {
                WaxPlatform.Current.Delete(key);
                return null;
            }

            // The game may have logged out or switched networks while this ran.
            if (generation != started || Network != network) return null;
            SetSession(wallet, walletSession);
            return Current;
        }

        /// <summary>A login that was waiting for the wallet when the game was stopped; waits at most a few seconds.</summary>
        static async Task<WaxSession> ResumePendingLogin(WaxNetwork network)
        {
            var started = generation;
            foreach (var wallet in wallets.Values.OfType<WaxEsrWallet>())
            {
                var walletSession = await wallet.ResumeLogin(new WaxWalletContext(network, AppName, UI), TimeSpan.FromSeconds(10));
                if (generation != started || Network != network) return null;
                if (walletSession == null) continue;
                SetSession(wallet, walletSession);
                Save();
                return Current;
            }
            return null;
        }

        /// <summary>Forgets the session on this device (the saved one too). The wallet itself stays logged in.</summary>
        public static async Task Logout()
        {
            EnsureInitialized();
            var wallet = currentWallet;
            var walletSession = currentWalletSession;
            Current = null;
            currentWallet = null;
            currentWalletSession = null;
            generation++;
            WaxPlatform.Current.Delete(SessionKeyPrefix + Network.ChainId);

            if (wallet != null) await wallet.Logout(new WaxWalletContext(Network, AppName, UI), walletSession);
        }

        /// <summary>
        /// Sends one transaction with one or more actions: the wallet asks the player to approve, then the kit broadcasts it
        /// and returns the transaction id once the chain accepted it. Each action signs as the logged-in account unless it
        /// sets <see cref="WaxAction.Authorization"/>. Throws <see cref="WaxKitException"/> (NotLoggedIn, Cancelled, Timeout,
        /// Wallet with the chain's reason, Network).
        /// </summary>
        public static async Task<WaxTransactResult> Transact(params WaxAction[] actions)
        {
            EnsureInitialized();
            if (actions == null || actions.Length == 0) throw new ArgumentException("At least one action is required.", nameof(actions));
            if (!IsLoggedIn) throw new WaxKitException(WaxErrorKind.NotLoggedIn, "Log in before sending a transaction.");

            var json = new JArray();
            foreach (var action in actions) json.Add(action.ToJson(Current));

            var network = Network;
            var wallet = currentWallet;
            var walletSession = currentWalletSession;
            var context = new WaxWalletContext(network, AppName, UI);

            var id = await Run(context, async () =>
            {
                var transaction = await WaxChainApi.BuildTransaction(network, json, context.Cancellation);
                var packed = AntelopeTransaction.Encode(transaction);
                var request = new WaxSignRequest(network.ChainId, packed, AntelopeTransaction.SigningDigest(network.ChainId, packed),
                    transaction, AntelopeTime.Parse((string)transaction["expiration"]), json);

                var result = await wallet.Sign(context, walletSession, request);
                // The player is done with the wallet: close its panel now instead of keeping it up while the transaction
                // goes to the chain (a moment on a phone network). Transact still returns only once the chain accepted
                // it, and a chain error still shows in the wallet panel.
                context.Close();
                if (!string.IsNullOrEmpty(result.TransactionId)) return result.TransactionId;
                if (result.Signatures.Count == 0) throw new WaxKitException(WaxErrorKind.Wallet, $"{wallet.Name} returned no signature.");
                return await WaxChainApi.SendTransaction(network, result.SerializedTransaction ?? packed, result.Signatures);
            });
            return new WaxTransactResult(id);
        }

        /// <summary>Reads contract table rows and returns the raw JSON answer (no login needed).</summary>
        public static Task<string> GetTableRowsJson(WaxTableQuery query)
        {
            EnsureInitialized();
            if (query == null) throw new ArgumentNullException(nameof(query));
            return WaxChainApi.GetTableRows(Network, JsonConvert.SerializeObject(query));
        }

        /// <summary>Reads contract table rows as <typeparamref name="T"/> (your row class, or JObject). No login needed.</summary>
        public static async Task<WaxTableResult<T>> GetTableRows<T>(WaxTableQuery query) =>
            JsonConvert.DeserializeObject<WaxTableResult<T>>(await GetTableRowsJson(query));

        internal static string WalletIdOf(WalletType wallet) =>
            wallet == WalletType.Anchor ? AnchorId
            : wallet == WalletType.MyCloudWallet ? MyCloudWalletId
            : wallet == WalletType.WebAuth ? WebAuthId
            : wallet == WalletType.WebAuthWeb ? WebAuthWebId
            : null;

        /// <summary>The name of a built-in wallet's id, or null.</summary>
        static string BuiltinName(string id) =>
            id == AnchorId ? "Anchor"
            : id == MyCloudWalletId ? "My Cloud Wallet"
            : id == WebAuthId ? "WebAuth (phone app)"
            : id == WebAuthWebId ? "WebAuth (web)"
            : null;

        static IWaxWallet FindWallet(string id) => id != null && wallets.TryGetValue(id, out var wallet) ? wallet : null;

        static TimeSpan Positive(TimeSpan value, string name) =>
            value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(name, value, $"WaxKit.{name} must be more than zero.");

        static void SetSession(IWaxWallet wallet, WaxWalletSession walletSession)
        {
            currentWallet = wallet;
            currentWalletSession = walletSession;
            var type = wallet.Id == AnchorId ? WalletType.Anchor
                : wallet.Id == MyCloudWalletId ? WalletType.MyCloudWallet
                : wallet.Id == WebAuthId ? WalletType.WebAuth
                : wallet.Id == WebAuthWebId ? WalletType.WebAuthWeb
                : WalletType.Custom;
            Current = new WaxSession(walletSession.Actor, walletSession.Permission, type, wallet.Id, Network.ChainId);
        }

        // One session per chain id. Holds the wallet's link data (for Anchor the temporary request key), never account keys.
        static void Save() =>
            WaxPlatform.Current.Save(SessionKeyPrefix + Network.ChainId, new JObject
            {
                ["wallet"] = currentWallet.Id,
                ["actor"] = currentWalletSession.Actor,
                ["permission"] = currentWalletSession.Permission,
                ["chainId"] = Network.ChainId,
                ["data"] = currentWalletSession.Data,
            }.ToString(Formatting.None));

        /// <summary>Runs one wallet call: maps the player's Cancel to Cancelled and shows failures as an error step.</summary>
        static async Task<T> Run<T>(WaxWalletContext context, Func<Task<T>> call)
        {
            // Keeps the game running while the player is in the wallet, so the answer is handled (and the game comes
            // back to the front) without the player switching back by hand.
            var platform = WaxPlatform.Current;
            platform.BeginWalletRequest();
            try
            {
                var result = await call();
                context.Close();
                return result;
            }
            catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
            {
                context.Close();
                throw new WaxKitException(WaxErrorKind.Cancelled, "Cancelled by the user.");
            }
            catch (WaxKitException e)
            {
                if (e.Kind == WaxErrorKind.Cancelled || !context.Shown) context.Close();
                else context.ShowError(e.Message);
                throw;
            }
            catch (Exception e)
            {
                var error = new WaxKitException(WaxErrorKind.Wallet, e.Message);
                if (context.Shown) context.ShowError(error.Message);
                throw error;
            }
            finally
            {
                platform.EndWalletRequest();
            }
        }

        static void LogWarning(string message)
        {
#if UNITY_5_3_OR_NEWER
            UnityEngine.Debug.LogWarning("Unity WAX Kit: " + message);
#else
            Console.WriteLine("Unity WAX Kit: " + message);
#endif
        }

        static void EnsureInitialized()
        {
            if (Network == null) throw new InvalidOperationException("Call WaxKit.Initialize(network, appName) first.");
        }
    }
}
