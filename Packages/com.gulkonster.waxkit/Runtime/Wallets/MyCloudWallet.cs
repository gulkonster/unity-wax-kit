using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gulkonster.WaxKit.Antelope;
using Gulkonster.WaxKit.Platform;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit
{
    /// <summary>
    /// My Cloud Wallet, ported from @wharfkit/wallet-plugin-cloudwallet: its login and signing pages run in a popup
    /// (WebGL), a small window of the player's own Chrome / Edge (Windows) or a Custom Tab (Android) and answer with postMessage.
    /// </summary>
    internal sealed class MyCloudWallet : IWaxWallet
    {
        /// <summary>Id of the built-in My Cloud Wallet.</summary>
        public const string WalletId = "mycloudwallet";
        /// <summary>The wallet site (internal so local tests can point it at a stand-in page).</summary>
        internal static string Url = "https://www.mycloudwallet.com";
        // The protocol version the pages expect (the plugin sends its package version).
        const string ProtocolVersion = "1.6.5";
        static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

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
                "Press Continue to open the My Cloud Wallet window.", "Log in in the My Cloud Wallet window.", DateTime.UtcNow + LoginTimeout);

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
                ValidateModifications(request.Transaction, AntelopeTransaction.Decode(serialized), session.Actor);
                modified = serialized;
            }
            return new WaxSignResult(signatures, modified);
        }

        /// <summary>Opens the page (inside the player's Continue click where the platform needs it) and waits for its answer.</summary>
        static async Task<JObject> RunPopup(WaxWalletContext context, string url, JObject post, string continueText, string waitText, DateTime expiresAt)
        {
            var platform = WaxPlatform.Current;
            IWaxPopupFlow flow = null;
            if (platform.PopupNeedsGesture)
                await context.WaitForContinue("My Cloud Wallet", continueText, () => flow = platform.OpenWalletPopup(url, post));
            else
                flow = platform.OpenWalletPopup(url, post);

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

        /// <summary>A Uint8Array from the page arrives as an array or as an index-keyed object.</summary>
        static byte[] ToBytes(JToken token)
        {
            if (token is JArray array) return array.Select(t => (byte)(int)t).ToArray();
            if (token is JObject obj) return obj.Properties().OrderBy(p => int.Parse(p.Name)).Select(p => (byte)(int)p.Value).ToArray();
            if (token?.Type == JTokenType.String) return Hex.Decode((string)token);
            return null;
        }

        /// <summary>
        /// The wallet may add a fee transfer to txfee.wam or a RAM purchase for the player; every original action must stay
        /// and nothing else may use the player's authority (as validateModifications in the plugin).
        /// </summary>
        internal static void ValidateModifications(JObject original, JObject modified, string actor)
        {
            var originalActions = ((JArray)original["actions"]).Cast<JObject>().ToList();
            var modifiedActions = ((JArray)modified["actions"]).Cast<JObject>().ToList();

            if (!originalActions.All(a => modifiedActions.Any(m => JToken.DeepEquals(a, m))))
                throw new WaxKitException(WaxErrorKind.Wallet, "The modified transaction does not contain all the original actions.");

            foreach (var action in modifiedActions.Where(m => !originalActions.Any(a => JToken.DeepEquals(a, m))))
            {
                if (!((JArray)action["authorization"]).Any(auth => (string)auth["actor"] == actor)) continue;

                var account = (string)action["account"];
                var name = (string)action["name"];
                var data = Hex.Decode((string)action["data"]);
                if (account == "eosio.token" && name == "transfer")
                {
                    var transfer = FeeAbi.Decode("transfer", data);
                    if ((string)transfer["to"] == "txfee.wam" && ((string)transfer["memo"]).StartsWith("WAX fee for", StringComparison.Ordinal)) continue;
                }
                if (account == "eosio" && name == "buyrambytes")
                {
                    if ((string)FeeAbi.Decode("buyrambytes", data)["receiver"] == actor) continue;
                }
                throw new WaxKitException(WaxErrorKind.Wallet, "The modified transaction contains one or more actions that are not allowed.");
            }
        }
    }
}
