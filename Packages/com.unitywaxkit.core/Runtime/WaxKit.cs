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
    public static partial class WaxKit
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
        // The Login or Transact still waiting for its wallet, if any. One at a time: a newer one, Logout and Initialize
        // replace it (Cancelled, panel closed, its late answer ignored).
        static WaxWalletContext activeRequest;
        const string ReplacedByNewer = "The request was replaced by a newer wallet request.";
        const string ReplacedByLogout = "The request was replaced by a logout or a network change.";
        // Increased by Initialize, Logout and a successful Login: a restore that started before keeps its result to itself.
        static int generation;
        // The wallets' own logout calls (Logout, RemoveSession): a Login waits for them, so they cannot undo the new login.
        static Task walletLogout = Task.CompletedTask;
        // The RestoreSession of this generation, so AutoRestore and a RestoreSession by hand share one answer.
        static Task<WaxSession> restoring;
        static int restoringGeneration;
        static WaxSession current;

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

        /// <summary>The logged-in session, or null. <see cref="SessionChanged"/> tells when it changes.</summary>
        public static WaxSession Current
        {
            get => current;
            private set
            {
                if (current == value) return;
                current = value;
                try
                {
                    SessionChanged?.Invoke(value);
                }
                catch (Exception e)
                {
                    LogWarning("A SessionChanged handler failed: " + e);
                }
            }
        }

        /// <summary>
        /// Raised whenever <see cref="Current"/> changes: login, restore (automatic or by hand), <see cref="SwitchSession"/>,
        /// <see cref="Logout"/>, <see cref="RemoveSession"/> and a network change with <see cref="Initialize"/>. The argument is
        /// the new session, or null when nobody is logged in. Runs on the main thread, like the kit's other callbacks: update
        /// your UI here instead of waiting for <see cref="RestoreSession"/>.
        /// </summary>
        /// <example><code>
        /// WaxKit.SessionChanged += session => nameText.text = session == null ? "Not logged in" : session.Actor;
        /// </code></example>
        public static event Action<WaxSession> SessionChanged;

        /// <summary>
        /// Off (default): one saved account per network; a new login replaces it. On: the kit keeps every account the player
        /// logged in with on this network (see <see cref="Sessions"/>, <see cref="SwitchSession"/>, <see cref="RemoveSession"/>),
        /// so players can switch between wallets and accounts without logging in again. Set it before <see cref="Initialize"/>
        /// (WAX Kit Manager: Multiple Sessions).
        /// </summary>
        public static bool MultipleSessions { get; set; }

        /// <summary>
        /// On (default): <see cref="Initialize"/> runs <see cref="RestoreSession"/> by itself, so the last account is back
        /// without a login screen; listen to <see cref="SessionChanged"/> to see it arrive. Calling RestoreSession by hand
        /// still works and gives the same answer. Set it before <see cref="Initialize"/> (WAX Kit Manager: Auto Restore).
        /// </summary>
        public static bool AutoRestore { get; set; } = true;

        /// <summary>
        /// The saved accounts of the current network, in the order they were added (with <see cref="MultipleSessions"/> off:
        /// only <see cref="Current"/>, if any). Pass one to <see cref="SwitchSession"/> or <see cref="RemoveSession"/>.
        /// </summary>
        public static IReadOnlyList<WaxSession> Sessions
        {
            get
            {
                EnsureInitialized();
                if (!MultipleSessions) return Current == null ? new WaxSession[0] : new[] { Current };
                return LoadSessions(Network, out _)
                    .Where(e => (string)e["chainId"] == Network.ChainId)
                    .Select(e => new WaxSession((string)e["actor"], (string)e["permission"], WalletTypeOf((string)e["wallet"]),
                        (string)e["wallet"], Network.ChainId))
                    .ToList();
            }
        }

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
        /// forgets the current session (saved sessions stay saved per network). With <see cref="AutoRestore"/> on, it then
        /// restores this network's saved session by itself.
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
            ReplaceActiveRequest(ReplacedByLogout);
            Network = network;
            AppName = appName;

            // Wallets show the app as an account-style name. Tell the developer when theirs gets changed.
            var shown = AntelopeName.FromText(appName);
            if (shown != appName)
                LogWarning($"AppName \"{appName}\" is not a valid account-style name; wallets will show it as \"{shown}\". " +
                           "Use up to 12 characters: a-z, 1-5 and dots.");
            currentWallet = null;
            currentWalletSession = null;
            generation++;
            Current = null;
            if (AutoRestore) RunAutoRestore();
        }

        static async void RunAutoRestore()
        {
            try
            {
                await RestoreSession();
            }
            catch (Exception e)
            {
                LogWarning("Restoring the saved session failed: " + e.Message);
            }
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

            var context = StartRequest();
            WaxWalletSession walletSession;
            try
            {
                walletSession = await Run(context, async () =>
                {
                    // Waits for a wallet logout still running (its failure is not this login's), or until this login is replaced.
                    var replaced = new TaskCompletionSource<bool>();
                    using (context.Cancellation.Register(() => replaced.TrySetResult(true)))
                        await Task.WhenAny(walletLogout, replaced.Task);
                    if (context.Replaced) throw new WaxKitException(WaxErrorKind.Cancelled, context.ReplacedReason);
                    context.Cancellation.ThrowIfCancellationRequested();
                    return await wallet.Login(context);
                });
            }
            finally
            {
                EndRequest(context);
            }
            if (context.Replaced) throw new WaxKitException(WaxErrorKind.Cancelled, context.ReplacedReason);
            // A RestoreSession still running must not overwrite this login's session. A failed or cancelled login
            // changes nothing: the session it would have replaced stays.
            generation++;
            SetSession(wallet, walletSession, true);
            return Current;
        }

        /// <summary>
        /// The session saved by an earlier run on this network, or null (no window, no wallet step). Also finishes a login
        /// the player approved while the game was closed (Android may close the game while the player is in Anchor).
        /// Call it at startup before offering the login buttons, or leave it to <see cref="AutoRestore"/> (on by default):
        /// then this returns the answer of the restore already running or done, without a second wallet call.
        /// </summary>
        public static Task<WaxSession> RestoreSession()
        {
            EnsureInitialized();
            if (restoring != null && restoringGeneration == generation && !restoring.IsFaulted && !restoring.IsCanceled) return restoring;
            restoringGeneration = generation;
            return restoring = Restore();
        }

        static async Task<WaxSession> Restore()
        {
            var network = Network;
            var started = generation;
            var entries = LoadSessions(network, out var index);
            if (index < 0) return await ResumePendingLogin(network);

            var json = entries[index];
            var wallet = FindWallet((string)json["wallet"]);
            if (wallet == null || (string)json["chainId"] != network.ChainId)
            {
                RemoveSaved(network, json);
                return null;
            }

            var walletSession = new WaxWalletSession((string)json["actor"], (string)json["permission"], json["data"] as JObject);
            var context = new WaxWalletContext(network, AppName, UI);
            if (!await wallet.Restore(context, walletSession))
            {
                RemoveSaved(network, json);
                return null;
            }

            // The game may have logged out or switched networks while this ran.
            if (generation != started || Network != network) return null;
            SetSession(wallet, walletSession, false);
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
                SetSession(wallet, walletSession, true);
                return Current;
            }
            return null;
        }

        /// <summary>
        /// Forgets the current session on this device (the saved one too). The wallet itself stays logged in. With
        /// <see cref="MultipleSessions"/> on, the other saved accounts stay, and <see cref="Current"/> becomes null (no
        /// automatic switch to another account).
        /// </summary>
        public static async Task Logout()
        {
            EnsureInitialized();
            var wallet = currentWallet;
            var walletSession = currentWalletSession;
            ReplaceActiveRequest(ReplacedByLogout);
            currentWallet = null;
            currentWalletSession = null;
            generation++;
            var network = Network;
            if (!MultipleSessions) WaxPlatform.Current.Delete(SessionKeyPrefix + network.ChainId);
            else
            {
                // The current account, or the saved current one when it was not restored yet.
                var entries = LoadSessions(network, out var index);
                if (wallet != null) index = entries.FindIndex(e => IsEntryOf(e, wallet.Id, walletSession.Actor, walletSession.Permission));
                if (index >= 0) entries.RemoveAt(index);
                StoreSessions(network, entries, -1);
            }
            Current = null;

            if (wallet != null) await CallWalletLogout(wallet, walletSession);
        }

        /// <summary>
        /// Makes a saved account (from <see cref="Sessions"/>) the current one, with no wallet step. Needs
        /// <see cref="MultipleSessions"/> on. Like a login, it replaces a wallet request still waiting. Throws
        /// <see cref="WaxKitException"/>: NotLoggedIn when the wallet no longer accepts the saved session (it is then removed
        /// from <see cref="Sessions"/>; log in again), Cancelled when something newer replaced it.
        /// </summary>
        public static async Task<WaxSession> SwitchSession(WaxSession session)
        {
            EnsureInitialized();
            EnsureMultipleSessions(nameof(SwitchSession));
            if (session == null) throw new ArgumentNullException(nameof(session));
            var network = Network;
            var entry = FindSaved(network, session) ?? throw new ArgumentException(
                $"{session} is not a saved account of {network.Name}; pick one from WaxKit.Sessions.", nameof(session));

            var wallet = FindWallet(session.WalletId);
            var walletSession = new WaxWalletSession((string)entry["actor"], (string)entry["permission"], entry["data"] as JObject);
            var context = StartRequest();
            bool restored;
            try
            {
                restored = wallet != null && await Run(context, () => wallet.Restore(context, walletSession));
            }
            finally
            {
                EndRequest(context);
            }
            if (context.Replaced) throw new WaxKitException(WaxErrorKind.Cancelled, context.ReplacedReason);
            if (!restored)
            {
                RemoveSaved(network, entry);
                throw new WaxKitException(WaxErrorKind.NotLoggedIn,
                    $"The saved login of {session.Actor} has ended and was removed. Log in again with {session.Actor}.");
            }

            // A RestoreSession still running must not overwrite the account the player picked.
            generation++;
            SetSession(wallet, walletSession, true);
            return Current;
        }

        /// <summary>
        /// Removes one saved account (from <see cref="Sessions"/>) and logs its wallet out, as <see cref="Logout"/> does.
        /// Removing the current account is <see cref="Logout"/>. Needs <see cref="MultipleSessions"/> on. Like a login, it
        /// replaces a wallet request still waiting (for example a <see cref="SwitchSession"/>). Does nothing for an account
        /// that is not saved.
        /// </summary>
        public static async Task RemoveSession(WaxSession session)
        {
            EnsureInitialized();
            EnsureMultipleSessions(nameof(RemoveSession));
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (Current != null && IsSame(Current, session))
            {
                await Logout();
                return;
            }

            // A waiting SwitchSession must not save the removed account again.
            ReplaceActiveRequest(ReplacedByNewer);
            var network = Network;
            var entry = FindSaved(network, session);
            if (entry == null) return;
            // The saved current account that a running RestoreSession is about to set.
            if (RemoveSaved(network, entry)) generation++;

            var wallet = FindWallet(session.WalletId);
            if (wallet != null)
                await CallWalletLogout(wallet, new WaxWalletSession((string)entry["actor"], (string)entry["permission"], entry["data"] as JObject));
        }

        /// <summary>Runs a wallet's own logout; a Login started meanwhile waits for it.</summary>
        static Task CallWalletLogout(IWaxWallet wallet, WaxWalletSession walletSession)
        {
            var call = wallet.Logout(new WaxWalletContext(Network, AppName, UI), walletSession);
            walletLogout = Task.WhenAll(walletLogout, call);
            return call;
        }

        /// <summary>True when the chain's error says the session account's authority has no signature (another account signed).</summary>
        internal static bool IsUnsignedAuthority(string chainError, string actor) =>
            chainError.Contains("does not have signatures for it") && chainError.Contains($"\"{actor}\"");

        /// <summary>The error for a web wallet logged in as another account (<paramref name="other"/>, or null when unknown).</summary>
        internal static WaxKitException WrongAccount(string walletName, string other, string actor) =>
            new WaxKitException(WaxErrorKind.Wallet, $"{walletName} is logged in as {other ?? "another account"}, not {actor}. Log in again with {actor}.");

        static void EnsureMultipleSessions(string method)
        {
            if (!MultipleSessions)
                throw new InvalidOperationException($"WaxKit.{method} needs WaxKit.MultipleSessions on (WAX Kit Manager: Multiple Sessions). " +
                                                    "With it off the kit keeps one account per network.");
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
            var context = StartRequest();

            // A newer request, Logout or Initialize made meanwhile ends the transaction before the wallet signs or the kit broadcasts.
            void EnsureNotReplaced()
            {
                if (context.Replaced) throw new WaxKitException(WaxErrorKind.Cancelled, context.ReplacedReason);
            }

            string id;
            try
            {
                id = await Run(context, async () =>
                {
                    var transaction = await WaxChainApi.BuildTransaction(network, json, context.Cancellation);
                    EnsureNotReplaced();
                    var packed = AntelopeTransaction.Encode(transaction);
                    var request = new WaxSignRequest(network.ChainId, packed, AntelopeTransaction.SigningDigest(network.ChainId, packed),
                        transaction, AntelopeTime.Parse((string)transaction["expiration"]), json);

                    var result = await wallet.Sign(context, walletSession, request);
                    // The player is done with the wallet: close its panel now instead of keeping it up while the transaction
                    // goes to the chain (a moment on a phone network). Transact still returns only once the chain accepted
                    // it, and a chain error still shows in the wallet panel.
                    context.Close();
                    // The wallet broadcast it itself: it is on chain, so its id is the answer even if the request was replaced.
                    if (!string.IsNullOrEmpty(result.TransactionId)) return result.TransactionId;
                    EnsureNotReplaced();
                    if (result.Signatures.Count == 0) throw new WaxKitException(WaxErrorKind.Wallet, $"{wallet.Name} returned no signature.");
                    // From here a replace only closes the panel: the chain's answer (id or error) is the result.
                    context.Sending = true;
                    try
                    {
                        return await WaxChainApi.SendTransaction(network, result.SerializedTransaction ?? packed, result.Signatures);
                    }
                    catch (WaxKitException e) when (wallet.Id == MyCloudWalletId && IsUnsignedAuthority(e.Message, walletSession.Actor))
                    {
                        // My Cloud Wallet's signatures could not be checked before (not K1): its site signed as another account.
                        throw WrongAccount(wallet.Name, null, walletSession.Actor);
                    }
                });
            }
            finally
            {
                EndRequest(context);
            }
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
            ReadTableRows<T>(await GetTableRowsJson(query));

        // Row text stays as the chain sent it: a time_point such as "2026-11-04T05:00:00.000" in a JObject row is not
        // turned into a date (read back as text, it would come in the device's date format). DateTime fields still read it.
        static readonly JsonSerializerSettings TableRowSettings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };

        internal static WaxTableResult<T> ReadTableRows<T>(string json) => JsonConvert.DeserializeObject<WaxTableResult<T>>(json, TableRowSettings);

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

        /// <summary>Makes the session current; with <paramref name="save"/> saves it first, so SessionChanged sees the saved list.</summary>
        static void SetSession(IWaxWallet wallet, WaxWalletSession walletSession, bool save)
        {
            currentWallet = wallet;
            currentWalletSession = walletSession;
            if (save) SaveCurrent();
            Current = new WaxSession(walletSession.Actor, walletSession.Permission, WalletTypeOf(wallet.Id), wallet.Id, Network.ChainId);
        }

        static WalletType WalletTypeOf(string walletId) =>
            walletId == AnchorId ? WalletType.Anchor
            : walletId == MyCloudWalletId ? WalletType.MyCloudWallet
            : walletId == WebAuthId ? WalletType.WebAuth
            : walletId == WebAuthWebId ? WalletType.WebAuthWeb
            : WalletType.Custom;

        // One saved entry per chain id. Each session holds the wallet's link data (for Anchor the temporary request key),
        // never account keys. MultipleSessions off: the session itself (the 0.2.x format). On: {"sessions": [...], "current": i}.

        /// <summary>The saved sessions of a network; <paramref name="current"/> is the index of the current one, or -1.</summary>
        static List<JObject> LoadSessions(WaxNetwork network, out int current)
        {
            current = -1;
            var key = SessionKeyPrefix + network.ChainId;
            var saved = WaxPlatform.Current.Load(key);
            if (string.IsNullOrEmpty(saved)) return new List<JObject>();
            try
            {
                var json = WaxJson.Parse(saved);
                if (!(json["sessions"] is JArray list))
                {
                    current = 0;
                    return new List<JObject> { json };
                }
                var entries = list.OfType<JObject>().ToList();
                var index = (int?)json["current"] ?? -1;
                current = index >= 0 && index < entries.Count ? index : -1;
                return entries;
            }
            catch (Exception)
            {
                WaxPlatform.Current.Delete(key);
                return new List<JObject>();
            }
        }

        static void StoreSessions(WaxNetwork network, List<JObject> entries, int current)
        {
            var key = SessionKeyPrefix + network.ChainId;
            if (!MultipleSessions)
            {
                if (current >= 0) WaxPlatform.Current.Save(key, entries[current].ToString(Formatting.None));
                else WaxPlatform.Current.Delete(key);
            }
            else if (entries.Count == 0) WaxPlatform.Current.Delete(key);
            else WaxPlatform.Current.Save(key, new JObject { ["sessions"] = new JArray(entries), ["current"] = current }.ToString(Formatting.None));
        }

        /// <summary>Saves the current session: adds it to the saved ones (or updates the same account) and marks it current.</summary>
        static void SaveCurrent()
        {
            var entries = MultipleSessions ? LoadSessions(Network, out _) : new List<JObject>();
            var entry = new JObject
            {
                ["wallet"] = currentWallet.Id,
                ["actor"] = currentWalletSession.Actor,
                ["permission"] = currentWalletSession.Permission,
                ["chainId"] = Network.ChainId,
                ["data"] = currentWalletSession.Data,
            };
            var index = entries.FindIndex(e => IsEntryOf(e, currentWallet.Id, currentWalletSession.Actor, currentWalletSession.Permission));
            if (index >= 0) entries[index] = entry;
            else
            {
                entries.Add(entry);
                index = entries.Count - 1;
            }
            StoreSessions(Network, entries, index);
        }

        /// <summary>Removes a saved session; true when it was the saved current one.</summary>
        static bool RemoveSaved(WaxNetwork network, JObject entry)
        {
            var entries = LoadSessions(network, out var current);
            var index = entries.FindIndex(e => IsEntryOf(e, (string)entry["wallet"], (string)entry["actor"], (string)entry["permission"]));
            if (index < 0) return false;
            entries.RemoveAt(index);
            StoreSessions(network, entries, current == index ? -1 : current > index ? current - 1 : current);
            return current == index;
        }

        static JObject FindSaved(WaxNetwork network, WaxSession session) =>
            session.ChainId != network.ChainId ? null
            : LoadSessions(network, out _).FirstOrDefault(e => IsEntryOf(e, session.WalletId, session.Actor, session.Permission));

        // A saved account is the same when account, permission and wallet are.
        static bool IsEntryOf(JObject entry, string walletId, string actor, string permission) =>
            (string)entry["wallet"] == walletId && (string)entry["actor"] == actor && (string)entry["permission"] == permission;

        static bool IsSame(WaxSession a, WaxSession b) =>
            a.WalletId == b.WalletId && a.Actor == b.Actor && a.Permission == b.Permission && a.ChainId == b.ChainId;

        /// <summary>Starts a Login or Transact as the active request, replacing the one still waiting.</summary>
        static WaxWalletContext StartRequest()
        {
            ReplaceActiveRequest(ReplacedByNewer);
            return activeRequest = new WaxWalletContext(Network, AppName, UI);
        }

        static void ReplaceActiveRequest(string reason)
        {
            activeRequest?.Replace(reason);
            activeRequest = null;
        }

        static void EndRequest(WaxWalletContext context)
        {
            if (activeRequest == context) activeRequest = null;
        }

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
            catch (Exception) when (context.Replaced && !context.Sending)
            {
                // Something newer took over; whatever panel it shows stays as it is.
                throw new WaxKitException(WaxErrorKind.Cancelled, context.ReplacedReason);
            }
            catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested && !context.Sending)
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

        /// <summary>A warning in the Unity console (or the console outside Unity), with the kit's prefix.</summary>
        internal static void LogWarning(string message)
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
