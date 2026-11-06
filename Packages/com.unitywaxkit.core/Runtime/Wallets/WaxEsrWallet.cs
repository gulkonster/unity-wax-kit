using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityWaxKit.Antelope;
using UnityWaxKit.Esr;
using UnityWaxKit.Platform;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit
{
    /// <summary>
    /// Base for wallets that speak ESR (EOSIO signing requests) with callbacks over the buoy relay, on any Antelope chain.
    /// Login sends an identity request (QR + wallet link), checks the identity proof against the account, and every
    /// transaction is a new signing request. Override <see cref="Scheme"/> for a wallet with its own link scheme.
    /// </summary>
    public abstract class WaxEsrWallet : IWaxWallet
    {
        /// <summary>Unique wallet id, for example "mywallet".</summary>
        public abstract string Id { get; }
        /// <summary>Name shown to the player, for example "My Wallet".</summary>
        public abstract string Name { get; }

        /// <summary>Link scheme the wallet app handles, without ':' ("esr").</summary>
        protected virtual string Scheme => "esr";
        /// <summary>Link scheme on <paramref name="network"/>; override for a wallet whose scheme depends on the chain.</summary>
        protected virtual string SchemeFor(WaxNetwork network) => Scheme;
        /// <summary>Layout of this wallet's requests (WharfKit's by default).</summary>
        internal virtual EsrLayout Layout => EsrLayout.WharfKit;
        /// <summary>Account sent as "req_account" info with requests shown as QR code / link; null (default): none.</summary>
        internal virtual string RequestAccount => null;
        /// <summary>True: no Open button where no app handles <see cref="SchemeFor"/> links (default false: always shown).</summary>
        internal virtual bool OpenOnlyWhenInstalled => false;

        /// <summary>Whether to show the Open button, given <see cref="WaxWalletContext.CanOpenScheme"/>.</summary>
        internal bool ShowOpen(bool? installed) => !(OpenOnlyWhenInstalled && installed == false);
        /// <summary>Buoy relay for wallet callbacks.</summary>
        protected virtual string BuoyService => Buoy.DefaultService;
        /// <summary>How long a login request stays valid (default <see cref="WaxKit.AnchorLoginTimeout"/>, 5 minutes).</summary>
        protected virtual TimeSpan LoginTimeout => WaxKit.AnchorLoginTimeout;
        /// <summary>
        /// Phones: how long the game still waits for the wallet's answer after the player came back to the game, before
        /// it treats the request as rejected (the wallet may need a moment to send the answer).
        /// </summary>
        protected virtual int PlayerBackGraceMilliseconds => 5000;

        /// <summary>All chains by default; override to limit the wallet to some chain ids.</summary>
        public virtual bool SupportsChain(WaxNetwork network) => true;

        /// <summary>
        /// Sends an identity request: QR code plus an Open button (the wallet app opens by itself on phones), then checks
        /// the signed proof against the account's keys on chain.
        /// </summary>
        public virtual async Task<WaxWalletSession> Login(WaxWalletContext context)
        {
            var requestKey = PrivateKey.Generate();
            var channel = Buoy.NewChannel();
            var info = new List<KeyValuePair<string, byte[]>>(LoginInfo(context, requestKey));
            // @proton/link sends no scope (neither in the request nor as info).
            if (Layout == EsrLayout.WharfKit)
                info.Add(new KeyValuePair<string, byte[]>("scope", System.Text.Encoding.UTF8.GetBytes(context.AppNameAsName)));
            // Raw UTF-8, as @proton/browser-transport's setInfoKey('req_account', …).
            if (!string.IsNullOrEmpty(RequestAccount))
                info.Add(new KeyValuePair<string, byte[]>("req_account", System.Text.Encoding.UTF8.GetBytes(RequestAccount)));
            var request = SigningRequest.Identity(context.Network.ChainId, Buoy.ChannelUrl(BuoyService, channel), true, context.AppNameAsName,
                info, Layout);
            var expiresAt = DateTime.UtcNow + LoginTimeout;

            var scheme = SchemeFor(context.Network);
            var link = SameDevice(context, request).Encode(true, false, scheme + ":");
            var installed = context.CanOpenScheme(scheme);
            var showQr = !context.IsMobile || installed == false;
            var showOpen = ShowOpen(installed);
            context.ShowSign($"Connect with {Name}",
                !showQr ? $"Open {Name} to log in."
                : installed == false && context.IsMobile ? $"{Name} is not installed on this device. Scan the QR code with {Name} on another device."
                : context.IsMobile ? $"Scan the QR code with {Name} on another device, or open {Name} on this device."
                : !showOpen ? $"Scan the QR code with {Name} on your phone."
                : $"Scan the QR code with {Name} on your phone, or press Open {Name} to use {Name} on this computer.",
                Name, showQr ? request.Encode(true, false, scheme + ":") : null, showOpen ? () => OpenWallet(context, link, false) : (Action)null, expiresAt);

            // If the system stops the game while the player is in the wallet, RestoreSession picks the answer up.
            // The scope this request carries (version 3 only): the identity proof must be for exactly this one.
            var scope = request.IdentityScope;
            SavePending(context, channel, requestKey, expiresAt, scope);
            try
            {
                // On a phone the wallet app opens right away. On a computer the player picks the QR (phone) or the Open
                // button (wallet app on this computer): opening the desktop app by itself would also send the request
                // there while the player scans the QR, and a Cancel in the desktop app would end the login.
                if (installed == true && context.IsMobile) context.OpenLink(link);
                return await CompleteLogin(context, channel, requestKey, expiresAt, scope);
            }
            finally
            {
                WaxPlatform.Current.Delete(PendingKey(context));
            }
        }

        /// <param name="scope">The scope the identity request carried (null for version 2 requests, which have none).</param>
        async Task<WaxWalletSession> CompleteLogin(WaxWalletContext context, string channel, PrivateKey requestKey, DateTime expiresAt, string scope)
        {
            var payload = await WaitForCallback(context, channel, expiresAt);
            context.ReturnToGame();

            var chainId = (string)payload["cid"] ?? context.Network.ChainId;
            if (!string.Equals(chainId, context.Network.ChainId, StringComparison.OrdinalIgnoreCase))
                throw new WaxKitException(WaxErrorKind.WrongChain, $"{Name} answered for chain {chainId}, not {context.Network.Name}.");
            if (string.IsNullOrEmpty((string)payload["sig"])) throw new WaxKitException(WaxErrorKind.Wallet, "Invalid response, must have at least one signature.");

            var actor = (string)payload["sa"];
            var permission = (string)payload["sp"];
            await VerifyIdentity(context, payload, actor, permission, scope);
            return new WaxWalletSession(actor, permission, OnLogin(context, payload, requestKey));
        }

        /// <summary>
        /// Finishes a login that was waiting when the game was stopped (Android can end the game while the player is in
        /// the wallet; mobile browsers can reload the page). Buoy keeps the wallet's answer until the channel is read.
        /// </summary>
        internal async Task<WaxWalletSession> ResumeLogin(WaxWalletContext context, TimeSpan maxWait)
        {
            var saved = WaxPlatform.Current.Load(PendingKey(context));
            if (saved == null) return null;
            try
            {
                var pending = WaxJson.Parse(saved);
                if ((string)pending["wallet"] != Id) return null;
                WaxPlatform.Current.Delete(PendingKey(context));
                var expiresAt = AntelopeTime.Parse((string)pending["expires"]);
                if (expiresAt <= DateTime.UtcNow) return null;
                if (expiresAt - DateTime.UtcNow > maxWait) expiresAt = DateTime.UtcNow + maxWait;
                // A pending login saved by 0.3.0 has no "scope": its request carried the app name as scope then too.
                var scope = pending["scope"] != null ? (string)pending["scope"]
                    : Layout == EsrLayout.WharfKit ? context.AppNameAsName : null;
                return await CompleteLogin(context, (string)pending["channel"], PrivateKey.Parse((string)pending["requestKey"]), expiresAt, scope);
            }
            catch (Exception)
            {
                return null;
            }
        }

        void SavePending(WaxWalletContext context, string channel, PrivateKey requestKey, DateTime expiresAt, string scope) =>
            WaxPlatform.Current.Save(PendingKey(context), new JObject
            {
                ["wallet"] = Id,
                ["channel"] = channel,
                ["requestKey"] = requestKey.ToSecretString(),
                ["expires"] = AntelopeTime.FormatSeconds(expiresAt),
                ["scope"] = scope,
            }.ToString(Newtonsoft.Json.Formatting.None));

        static string PendingKey(WaxWalletContext context) => "waxkit.pending." + context.Network.ChainId;

        /// <summary>Keeps every saved session (nothing to check for plain ESR wallets).</summary>
        public virtual Task<bool> Restore(WaxWalletContext context, WaxWalletSession session) => Task.FromResult(true);

        /// <summary>Nothing to clean up for plain ESR wallets.</summary>
        public virtual Task Logout(WaxWalletContext context, WaxWalletSession session) => Task.CompletedTask;

        /// <summary>Sends a signing request (QR + wallet link) and waits for the signature until the transaction expires.</summary>
        public virtual async Task<WaxSignResult> Sign(WaxWalletContext context, WaxWalletSession session, WaxSignRequest request)
        {
            var channel = Buoy.NewChannel();
            var esr = CreateSignRequest(request, Buoy.ChannelUrl(BuoyService, channel));
            if (!string.IsNullOrEmpty(RequestAccount)) esr.SetInfoString("req_account", RequestAccount);
            var scheme = SchemeFor(context.Network);
            var link = SameDevice(context, esr).Encode(true, false, scheme + ":");
            var installed = context.CanOpenScheme(scheme);
            var showQr = !context.IsMobile || installed == false;
            var showOpen = ShowOpen(installed);

            context.ShowSign($"Sign with {Name}",
                !showQr ? $"Open {Name} to review and sign."
                : !showOpen ? $"Scan the QR code with {Name}."
                : $"Scan the QR code with {Name}, or open {Name} on this device.",
                Name, showQr ? esr.Encode(true, false, scheme + ":") : null, showOpen ? () => OpenWallet(context, link, false) : (Action)null, request.ExpiresAt);
            // Same rule as the login: the app opens by itself on phones only.
            if (installed == true && context.IsMobile) context.OpenLink(link);

            var payload = await WaitForCallback(context, channel, request.ExpiresAt);
            context.ReturnToGame();
            return ToSignResult(request, payload);
        }

        /// <summary>
        /// The Open button. When the request already waits in the wallet app on this computer (sent there before in this
        /// call) and the app has an open window, the button brings that window back in front: the player may have left
        /// it, for example with Alt+Tab behind a full-screen game, and sending the request again would show it twice.
        /// Otherwise it opens <paramref name="link"/> in the app.
        /// </summary>
        internal void OpenWallet(WaxWalletContext context, string link, bool requestIsInApp)
        {
            if ((requestIsInApp || context.OpenedLink) && context.BringWalletAppToFront(SchemeFor(context.Network))) return;
            context.OpenLink(link);
        }

        /// <summary>Extra info pairs of the identity request (Anchor adds its link key).</summary>
        internal virtual IEnumerable<KeyValuePair<string, byte[]>> LoginInfo(WaxWalletContext context, PrivateKey requestKey) =>
            Enumerable.Empty<KeyValuePair<string, byte[]>>();

        /// <summary>Wallet data to save with the session from the login callback.</summary>
        internal virtual JObject OnLogin(WaxWalletContext context, JObject payload, PrivateKey requestKey) => new JObject();

        /// <summary>The ESR for a transaction: not broadcast by the wallet, answer in the background to <paramref name="callbackUrl"/>.</summary>
        internal SigningRequest CreateSignRequest(WaxSignRequest request, string callbackUrl) =>
            SigningRequest.Transaction(request.ChainId, request.Transaction, false, callbackUrl, true, layout: Layout);

        /// <summary>The same-device form of a request: tells the wallet to return to the game afterwards.</summary>
        internal static SigningRequest SameDevice(WaxWalletContext context, SigningRequest request)
        {
            var copy = request.Clone();
            copy.SetInfoBool("same_device", true);
            var returnUrl = context.ReturnUrl;
            if (returnUrl != null) copy.SetInfoString("return_path", returnUrl);
            return copy;
        }

        /// <summary>Waits for the wallet's callback on the buoy channel. Timeout at <paramref name="expiresAt"/>.</summary>
        internal async Task<JObject> WaitForCallback(WaxWalletContext context, string channel, DateTime expiresAt)
        {
            var left = expiresAt - DateTime.UtcNow;
            using var deadline = new WaxDeadline(context.Cancellation, left);

            using var stopReceive = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            using var stopWatching = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);

            string message;
            try
            {
                var receive = Buoy.Receive(BuoyService, channel, stopReceive.Token);
                // Some wallet apps (Anchor on phones) just return to the game when the player rejects, without an answer.
                // So on phones, coming back to the game with no answer for a few seconds ends the request as Cancelled.
                var playerBack = context.WaitForPlayerBack(PlayerBackGraceMilliseconds, stopWatching.Token);
                if (await Task.WhenAny(receive, playerBack) == playerBack && playerBack.Status == TaskStatus.RanToCompletion && !receive.IsCompleted)
                {
                    stopReceive.Cancel();
                    context.ReturnToGame();
                    throw new WaxKitException(WaxErrorKind.Cancelled, $"The request was rejected or closed in {Name}.");
                }
                message = await receive;
            }
            catch (OperationCanceledException) when (!context.Cancellation.IsCancellationRequested)
            {
                throw new WaxKitException(WaxErrorKind.Timeout, "The request expired, please try again.");
            }
            finally
            {
                stopWatching.Cancel();
            }

            JObject payload;
            try
            {
                payload = WaxJson.Parse(message);
            }
            catch (Exception)
            {
                throw new WaxKitException(WaxErrorKind.Wallet, $"{Name} sent an unreadable response.");
            }

            if (payload["rejected"] != null)
            {
                context.ReturnToGame();
                throw new WaxKitException(WaxErrorKind.Cancelled, $"The request was rejected in {Name}: {payload["rejected"]}");
            }
            if (payload["sa"] == null || payload["sp"] == null || payload["cid"] == null)
            {
                context.ReturnToGame();
                throw new WaxKitException(WaxErrorKind.Cancelled, $"The request was cancelled from {Name}.");
            }
            return payload;
        }

        internal WaxSignResult ToSignResult(WaxSignRequest request, JObject payload)
        {
            var signatures = ResolvedSigningRequest.Signatures(payload);
            if (signatures.Count == 0) throw new WaxKitException(WaxErrorKind.Wallet, "The request was not completed.");

            var chainId = (string)payload["cid"];
            if (!string.Equals(chainId, request.ChainId, StringComparison.OrdinalIgnoreCase))
                throw new WaxKitException(WaxErrorKind.WrongChain, $"{Name} answered for chain {chainId ?? "(none)"}, not {request.ChainId}.");

            // The request is a full transaction with TaPoS, so the wallet must sign exactly those bytes. A different
            // transaction is never broadcast; the kit sends its own bytes.
            byte[] resolved;
            try
            {
                resolved = ResolvedSigningRequest.FromPayload(payload).SerializedTransaction;
            }
            catch (Exception)
            {
                resolved = null;
            }
            if (resolved == null || !resolved.SequenceEqual(request.SerializedTransaction))
                throw new WaxKitException(WaxErrorKind.Wallet, "The wallet returned a different transaction.");
            return new WaxSignResult(signatures);
        }

        /// <summary>
        /// Checks the identity proof: for the scope this game's request carried (version 3), signed by a key of
        /// actor@permission, not expired.
        /// </summary>
        async Task VerifyIdentity(WaxWalletContext context, JObject payload, string actor, string permission, string scope)
        {
            IdentityProof proof;
            try
            {
                proof = IdentityProof.FromPayload(payload);
            }
            catch (FormatException e)
            {
                throw new WaxKitException(WaxErrorKind.Wallet, "Invalid identity proof: " + e.Message);
            }
            // The proof must be for the version this wallet's login request has (version 2 proofs have no expiration).
            if (proof.Version != (Layout == EsrLayout.ProtonLink ? 2 : 3))
                throw new WaxKitException(WaxErrorKind.Wallet, $"Invalid identity proof: unexpected request version {proof.Version}.");
            // The scope (this game's app name) is part of what the wallet signed: a proof made for another app name is
            // refused. The proof signs nothing per login (chain id, scope, the wallet's expiration, actor@permission), so
            // it does not tell two logins of the same app apart. What ties it to this login: it must arrive on this
            // login's random buoy channel (only in this login's QR code / link) and not be expired (checked below).
            if (proof.Version > 2 && !string.Equals(proof.Scope, scope, StringComparison.Ordinal))
                throw new WaxKitException(WaxErrorKind.Wallet, $"The login proof is for another app ('{proof.Scope}'), not this game ('{scope}'). Please log in again.");

            var account = await context.GetAccount(actor);
            var level = (account["permissions"] as JArray)?.FirstOrDefault(p => (string)p["perm_name"] == permission);
            if (level == null) throw new WaxKitException(WaxErrorKind.Wallet, $"Account {actor} has no permission '{permission}'.");
            if (!proof.Verify(level["required_auth"], DateTime.UtcNow))
                throw new WaxKitException(WaxErrorKind.Wallet, $"The identity proof is not signed by a key of {actor}@{permission}, or it expired.");
        }
    }
}
