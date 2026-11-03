using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityWaxKit.Antelope;
using UnityWaxKit.Chain;
using UnityWaxKit.Platform;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit
{
    /// <summary>
    /// My Cloud Wallet, ported from @wharfkit/wallet-plugin-cloudwallet: its login and signing pages run in a popup
    /// (WebGL), a small window of the player's own Chrome / Edge (Windows) or a Custom Tab (Android) and answer with postMessage.
    /// </summary>
    internal sealed class MyCloudWallet : IWaxWallet
    {
        /// <summary>Id of the built-in My Cloud Wallet.</summary>
        public const string WalletId = WaxKit.MyCloudWalletId;
        /// <summary>The wallet site (internal so local tests can point it at a stand-in page).</summary>
        internal static string Url = "https://www.mycloudwallet.com";
        // The protocol version the pages expect (the plugin sends its package version).
        const string ProtocolVersion = "1.6.5";

        static readonly Abi FeeAbi = Abi.FromJson(WaxJson.Parse(@"{ ""structs"": [
            { ""name"": ""transfer"", ""base"": """", ""fields"": [ { ""name"": ""from"", ""type"": ""name"" }, { ""name"": ""to"", ""type"": ""name"" },
              { ""name"": ""quantity"", ""type"": ""asset"" }, { ""name"": ""memo"", ""type"": ""string"" } ] },
            { ""name"": ""buyrambytes"", ""base"": """", ""fields"": [ { ""name"": ""payer"", ""type"": ""name"" }, { ""name"": ""receiver"", ""type"": ""name"" },
              { ""name"": ""bytes"", ""type"": ""uint32"" } ] } ] }"));

        /// <summary>See <see cref="WalletId"/>.</summary>
        public string Id => WalletId;
        /// <summary>Name shown to the player.</summary>
        public string Name => "My Cloud Wallet";

        /// <summary>WAX mainnet only (the wallet has no testnet).</summary>
        public bool SupportsChain(WaxNetwork network) => network.IsMainnet;

        /// <summary>Opens the My Cloud Wallet login page and waits for the account.</summary>
        public async Task<WaxWalletSession> Login(WaxWalletContext context)
        {
            var response = await RunPopup(context, $"{Url}/cloud-wallet/login?v={ProtocolVersion}", null,
                "Press Continue to open the My Cloud Wallet window.", "Log in in the My Cloud Wallet window.", DateTime.UtcNow + WaxKit.MyCloudWalletLoginTimeout);

            var actor = (string)response["userAccount"];
            if (string.IsNullOrEmpty(actor)) throw new WaxKitException(WaxErrorKind.Wallet, "Cloud Wallet failed to respond.");
            return new WaxWalletSession(actor, (string)response["permission"] ?? "active");
        }

        /// <summary>Keeps the saved session (the wallet site keeps its own login).</summary>
        public Task<bool> Restore(WaxWalletContext context, WaxWalletSession session) => Task.FromResult(true);

        /// <summary>Nothing to clean up.</summary>
        public Task Logout(WaxWalletContext context, WaxWalletSession session) => Task.CompletedTask;

        /// <summary>Opens the signing page with the transaction and checks any change the wallet made (fees, RAM).</summary>
        public async Task<WaxSignResult> Sign(WaxWalletContext context, WaxWalletSession session, WaxSignRequest request)
        {
            var message = new JObject
            {
                ["feeFallback"] = true,
                ["freeBandwidth"] = true,
                ["startTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                // The page expects a Uint8Array; the popup bridge converts this byte array.
                ["transaction"] = new JArray(request.SerializedTransaction.Select(b => (int)b)),
                ["type"] = "TRANSACTION",
                ["version"] = ProtocolVersion,
            };
            var response = await RunPopup(context, $"{Url}/cloud-wallet/signing/", message,
                "Press Continue to open My Cloud Wallet and sign.", "Please complete the transaction using the Cloud Wallet window.", request.ExpiresAt);

            var signatures = (response["signatures"] as JArray)?.Select(s => (string)s).ToList() ?? new List<string>();
            if (signatures.Count == 0) throw new WaxKitException(WaxErrorKind.Wallet, "The Cloud Wallet failed to respond.");

            byte[] modified = null;
            var serialized = ToBytes(response["serializedTransaction"]);
            if (serialized != null && !serialized.SequenceEqual(request.SerializedTransaction))
            {
                // Both sides decoded from bytes, so they compare in the same JSON form.
                ValidateModifications(AntelopeTransaction.Decode(request.SerializedTransaction), AntelopeTransaction.Decode(serialized),
                    session.Actor, session.Permission);
                modified = serialized;
            }
            await EnsureSignedBySession(context, session, modified ?? request.SerializedTransaction, signatures);
            return new WaxSignResult(signatures, modified);
        }

        /// <summary>
        /// The wallet site signs as whatever account it is logged in as, which can be another account than the session's
        /// (the player switched accounts there). The chain must accept the signatures for the transaction's authorizations;
        /// otherwise nothing is broadcast and the player is told to log in again.
        /// </summary>
        static async Task EnsureSignedBySession(WaxWalletContext context, WaxWalletSession session, byte[] packed, List<string> signatures)
        {
            var digest = AntelopeTransaction.SigningDigest(context.Network.ChainId, packed);
            var keys = new List<string>();
            foreach (var text in signatures)
            {
                try
                {
                    keys.Add(Signature.Parse(text).RecoverDigest(digest).ToString());
                }
                catch (Exception)
                {
                    // Not a K1 signature (for example WebAuthn): the kit cannot tell its key, so it leaves the check to the
                    // chain; Transact turns the chain's missing-authority error into the same message.
                    return;
                }
            }
            if (await WaxChainApi.KeysSatisfy(context.Network, AntelopeTransaction.Decode(packed), keys, context.Cancellation)) return;

            // Name the other account when the keys point to exactly one (the wallet's own co-signing key may add another).
            var others = (await WaxChainApi.AccountsOfKeys(context.Network, keys, context.Cancellation)).Where(a => a != session.Actor).ToList();
            throw WaxKit.WrongAccount("My Cloud Wallet", others.Count == 1 ? others[0] : null, session.Actor);
        }

        /// <summary>Opens the page (inside the player's Continue click where the platform needs it) and waits for its answer.</summary>
        static async Task<JObject> RunPopup(WaxWalletContext context, string url, JObject post, string continueText, string waitText, DateTime expiresAt)
        {
            var platform = WaxPlatform.Current;
            IWaxPopupFlow flow = null;
            if (platform.PopupNeedsGesture)
                await context.WaitForContinue("My Cloud Wallet", continueText, () => flow = platform.OpenWalletPopup(url, post, WaxPopupProtocol.CloudWallet));
            else
                flow = platform.OpenWalletPopup(url, post, WaxPopupProtocol.CloudWallet);

            context.ShowWaiting("My Cloud Wallet", waitText);

            var left = expiresAt - DateTime.UtcNow;
            using var deadline = new WaxDeadline(context.Cancellation, left);
            var stop = new TaskCompletionSource<bool>();
            using (deadline.Token.Register(() => stop.TrySetResult(true)))
            {
                var finished = await Task.WhenAny(flow.Result, stop.Task);
                if (finished != flow.Result)
                {
                    flow.Close();
                    context.ReturnToGame();
                    if (context.Cancellation.IsCancellationRequested) throw new OperationCanceledException(context.Cancellation);
                    throw new WaxKitException(WaxErrorKind.Timeout, "The request has timed out. Please try again.");
                }
            }

            context.ReturnToGame();
            var response = await flow.Result;
            // verified = false: the player cancelled in the wallet, or signing failed there (the wallet says why).
            if ((bool?)response["verified"] != true)
            {
                var reason = Text(response["message"]) ?? Text(response["error"]);
                throw new WaxKitException(WaxErrorKind.Cancelled, string.IsNullOrEmpty(reason)
                    ? "The Cloud Wallet was closed before the request was completed."
                    : "My Cloud Wallet: " + reason);
            }
            return response;
        }

        static string Text(JToken token) => token?.Type == JTokenType.String ? (string)token : null;

        /// <summary>
        /// A Uint8Array from the page arrives as an array, as an index-keyed object ("0", "1", …) or as hex. Anything that
        /// is not exactly a list of bytes is rejected.
        /// </summary>
        internal static byte[] ToBytes(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return null;
            if (token is JArray array) return array.Select(ToByte).ToArray();
            if (token is JObject obj)
            {
                var result = new byte[obj.Count];
                var seen = new bool[obj.Count];
                foreach (var property in obj.Properties())
                {
                    // Only canonical indexes 0..n-1, each once ("01" would be a second key for index 1).
                    if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                        || index >= result.Length || seen[index] || index.ToString(CultureInfo.InvariantCulture) != property.Name)
                        throw Unreadable();
                    seen[index] = true;
                    result[index] = ToByte(property.Value);
                }
                return result;
            }
            if (token.Type == JTokenType.String)
            {
                var hex = (string)token;
                if (hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit)) throw Unreadable();
                return Hex.Decode(hex);
            }
            throw Unreadable();
        }

        static byte ToByte(JToken token)
        {
            if (token?.Type == JTokenType.Integer && ((JValue)token).Value is IConvertible value)
            {
                try
                {
                    var number = value.ToInt64(CultureInfo.InvariantCulture);
                    if (number >= 0 && number <= 255) return (byte)number;
                }
                catch (OverflowException)
                {
                }
            }
            throw Unreadable();
        }

        static WaxKitException Unreadable() => new WaxKitException(WaxErrorKind.Wallet, "My Cloud Wallet returned an unreadable transaction.");

        /// <summary>
        /// Checks the transaction the wallet signed against the one the game asked for. Every original action must stay,
        /// unchanged and in order (each matched once). The wallet may add actions around them: without the player's
        /// authority as it likes (its free-bandwidth cosign), with it only one WAX fee transfer to txfee.wam and one RAM
        /// purchase for the player, signed by the session's permission. delay_sec and context-free actions must not change.
        /// </summary>
        internal static void ValidateModifications(JObject original, JObject modified, string actor, string permission)
        {
            if (!JToken.DeepEquals(original["delay_sec"], modified["delay_sec"])
                || !JToken.DeepEquals(original["context_free_actions"], modified["context_free_actions"]))
                throw new WaxKitException(WaxErrorKind.Wallet, "The modified transaction changed the delay or the context-free actions.");

            var originalActions = ((JArray)original["actions"]).Cast<JObject>().ToList();
            var modifiedActions = ((JArray)modified["actions"]).Cast<JObject>().ToList();

            // Match the originals in order, each with its own modified action; whatever is left over was added.
            var matched = new bool[modifiedActions.Count];
            var next = 0;
            foreach (var action in originalActions)
            {
                while (next < modifiedActions.Count && !JToken.DeepEquals(action, modifiedActions[next])) next++;
                if (next == modifiedActions.Count)
                    throw new WaxKitException(WaxErrorKind.Wallet, "The modified transaction does not contain all the original actions.");
                matched[next++] = true;
            }

            var fees = 0;
            var ramPurchases = 0;
            for (var i = 0; i < modifiedActions.Count; i++)
            {
                if (matched[i]) continue;
                var action = modifiedActions[i];
                if (originalActions.Any(a => JToken.DeepEquals(a, action)))
                    throw new WaxKitException(WaxErrorKind.Wallet, "The modified transaction repeats an original action.");

                var authorization = ((JArray)action["authorization"]).ToList();
                if (!authorization.Any(auth => (string)auth["actor"] == actor)) continue;

                if (authorization.All(auth => (string)auth["actor"] == actor && (string)auth["permission"] == permission))
                {
                    if (IsFee(action, actor) && ++fees == 1) continue;
                    if (IsRamPurchase(action, actor) && ++ramPurchases == 1) continue;
                }
                throw new WaxKitException(WaxErrorKind.Wallet, "The modified transaction contains one or more actions that are not allowed.");
            }
        }

        static readonly ulong WaxSymbol = AntelopeSymbol.Encode(8, "WAX");

        /// <summary>eosio.token::transfer of a positive 8,WAX amount from the player to txfee.wam, memo "WAX fee for …".</summary>
        static bool IsFee(JObject action, string actor)
        {
            if ((string)action["account"] != "eosio.token" || (string)action["name"] != "transfer") return false;
            var transfer = DecodeData(action, "transfer");
            if (transfer == null || (string)transfer["from"] != actor || (string)transfer["to"] != "txfee.wam"
                || !((string)transfer["memo"]).StartsWith("WAX fee for", StringComparison.Ordinal)) return false;
            try
            {
                AntelopeAsset.Parse((string)transfer["quantity"], out var amount, out var symbol);
                return symbol == WaxSymbol && amount > 0;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>eosio::buyrambytes of a positive number of bytes, paid by and for the player.</summary>
        static bool IsRamPurchase(JObject action, string actor)
        {
            if ((string)action["account"] != "eosio" || (string)action["name"] != "buyrambytes") return false;
            var purchase = DecodeData(action, "buyrambytes");
            return purchase != null && (string)purchase["payer"] == actor && (string)purchase["receiver"] == actor
                   && (long)purchase["bytes"] > 0;
        }

        static JObject DecodeData(JObject action, string type)
        {
            try
            {
                return (JObject)FeeAbi.Decode(type, Hex.Decode((string)action["data"]));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
