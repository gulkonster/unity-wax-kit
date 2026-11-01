using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gulkonster.WaxKit.Antelope;
using Gulkonster.WaxKit.Esr;
using Gulkonster.WaxKit.Platform;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit
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
        /// <summary>Buoy relay for wallet callbacks.</summary>
        protected virtual string BuoyService => Buoy.DefaultService;
        /// <summary>How long a login request stays valid (default 5 minutes).</summary>
        protected virtual TimeSpan LoginTimeout => TimeSpan.FromMinutes(5);
        /// <summary>
        /// Phones: how long the game still waits for the wallet's answer after the player came back to the game, before
        /// it treats the request as rejected (the wallet may need a moment to send the answer).
        /// </summary>
        protected virtual int PlayerBackGraceMilliseconds => 6000;

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
            var info = new List<KeyValuePair<string, byte[]>>(LoginInfo(context, requestKey))
            {
                new KeyValuePair<string, byte[]>("scope", System.Text.Encoding.UTF8.GetBytes(context.AppNameAsName)),
            };
            var request = SigningRequest.Identity(context.Network.ChainId, Buoy.ChannelUrl(BuoyService, channel), true, context.AppNameAsName, info);
            var expiresAt = DateTime.UtcNow + LoginTimeout;

            var link = SameDevice(context, request).Encode(true, false, Scheme + ":");
            var installed = context.CanOpenScheme(Scheme);
            var showQr = !context.IsMobile || installed == false;
            context.ShowSign($"Connect with {Name}",
                !showQr ? $"Open {Name} to log in."
                : installed == false && context.IsMobile ? $"{Name} is not installed on this device. Scan the QR code with {Name} on another device."
                : context.IsMobile ? $"Scan the QR code with {Name} on another device, or open {Name} on this device."
                : $"Scan the QR code with {Name} on your phone, or press Open {Name} to use {Name} on this computer.",
                Name, showQr ? request.Encode(true, false, Scheme + ":") : null, () => OpenWallet(context, link, false), expiresAt);

            // If the system stops the game while the player is in the wallet, RestoreSession picks the answer up.
            SavePending(context, channel, requestKey, expiresAt);
            try
            {
                // On a phone the wallet app opens right away. On a computer the player picks the QR (phone) or the Open
                // button (wallet app on this computer): opening the desktop app by itself would also send the request
                // there while the player scans the QR, and a Cancel in the desktop app would end the login.
                if (installed == true && context.IsMobile) context.OpenLink(link);
                return await CompleteLogin(context, channel, requestKey, expiresAt);
            }
            finally
            {
                WaxPlatform.Current.Delete(PendingKey(context));
            }
        }

        async Task<WaxWalletSession> CompleteLogin(WaxWalletContext context, string channel, PrivateKey requestKey, DateTime expiresAt)
        {
            var payload = await WaitForCallback(context, channel, expiresAt);
            context.ReturnToGame();

            var chainId = (string)payload["cid"] ?? context.Network.ChainId;
            if (!string.Equals(chainId, context.Network.ChainId, StringComparison.OrdinalIgnoreCase))
                throw new WaxKitException(WaxErrorKind.WrongChain, $"{Name} answered for chain {chainId}, not {context.Network.Name}.");
            if (string.IsNullOrEmpty((string)payload["sig"])) throw new WaxKitException(WaxErrorKind.Wallet, "Invalid response, must have at least one signature.");

            var actor = (string)payload["sa"];
            var permission = (string)payload["sp"];
            await VerifyIdentity(context, payload, actor, permission);
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
                return await CompleteLogin(context, (string)pending["channel"], PrivateKey.Parse((string)pending["requestKey"]), expiresAt);
            }
            catch (Exception)
            {
                return null;
            }
        }

        void SavePending(WaxWalletContext context, string channel, PrivateKey requestKey, DateTime expiresAt) =>
            WaxPlatform.Current.Save(PendingKey(context), new JObject
            {
                ["wallet"] = Id,
                ["channel"] = channel,
                ["requestKey"] = requestKey.ToSecretString(),
                ["expires"] = AntelopeTime.FormatSeconds(expiresAt),
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
            var link = SameDevice(context, esr).Encode(true, false, Scheme + ":");
            var installed = context.CanOpenScheme(Scheme);
            var showQr = !context.IsMobile || installed == false;

            context.ShowSign($"Sign with {Name}",
                showQr ? $"Scan the QR code with {Name}, or open {Name} on this device." : $"Open {Name} to review and sign.",
                Name, showQr ? esr.Encode(true, false, Scheme + ":") : null, () => OpenWallet(context, link, false), request.ExpiresAt);
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
            if ((requestIsInApp || context.OpenedLink) && context.BringWalletAppToFront(Scheme)) return;
            context.OpenLink(link);
        }

        /// <summary>Extra info pairs of the identity request (Anchor adds its link key).</summary>
        internal virtual IEnumerable<KeyValuePair<string, byte[]>> LoginInfo(WaxWalletContext context, PrivateKey requestKey) =>
            Enumerable.Empty<KeyValuePair<string, byte[]>>();

        /// <summary>Wallet data to save with the session from the login callback.</summary>
        internal virtual JObject OnLogin(WaxWalletContext context, JObject payload, PrivateKey requestKey) => new JObject();

        /// <summary>The ESR for a transaction: not broadcast by the wallet, answer in the background to <paramref name="callbackUrl"/>.</summary>
        internal static SigningRequest CreateSignRequest(WaxSignRequest request, string callbackUrl) =>
            SigningRequest.Transaction(request.ChainId, request.Transaction, false, callbackUrl, true);

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

            var resolved = ResolvedSigningRequest.FromPayload(payload);
            var changed = !resolved.SerializedTransaction.SequenceEqual(request.SerializedTransaction);
            return new WaxSignResult(signatures, changed ? resolved.SerializedTransaction : null);
        }

        /// <summary>Checks the identity proof: the signature must come from a key of actor@permission and not be expired.</summary>
        async Task VerifyIdentity(WaxWalletContext context, JObject payload, string actor, string permission)
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

            var account = await context.GetAccount(actor);
            var level = (account["permissions"] as JArray)?.FirstOrDefault(p => (string)p["perm_name"] == permission);
            if (level == null) throw new WaxKitException(WaxErrorKind.Wallet, $"Account {actor} has no permission '{permission}'.");
            if (!proof.Verify(level["required_auth"], DateTime.UtcNow))
                throw new WaxKitException(WaxErrorKind.Wallet, $"The identity proof is not signed by a key of {actor}@{permission}, or it expired.");
        }
    }
}
