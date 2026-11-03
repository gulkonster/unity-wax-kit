using System;
using System.Linq;
using System.Threading.Tasks;
using UnityWaxKit.Platform;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit
{
    /// <summary>
    /// WebAuth (XPR Network) web wallet, ported from the protonWeb link of @proton/web-sdk 5.1.0: its login and signing
    /// pages run in a popup (WebGL), a small window of the player's own Chrome / Edge (Windows) or a Custom Tab (Android)
    /// and answer with postMessage, like <see cref="MyCloudWallet"/>. The login page only reports the account (no signed
    /// proof, as in the SDK); WebAuth signs and broadcasts transactions itself.
    /// </summary>
    internal sealed class WebAuthWebWallet : IWaxWallet
    {
        /// <summary>Id of the built-in WebAuth web wallet.</summary>
        public const string WalletId = WaxKit.WebAuthWebId;
        const string MainnetUrl = "https://webauth.com";
        const string TestnetUrl = "https://testnet.webauth.com";

        /// <summary>See <see cref="WalletId"/>.</summary>
        public string Id => WalletId;
        /// <summary>Name shown to the player.</summary>
        public string Name => "WebAuth";

        /// <summary>XPR Network mainnet and testnet only.</summary>
        public bool SupportsChain(WaxNetwork network) => XprChain.IsXpr(network);

        static string BaseUrl(WaxNetwork network) => network.ChainId == XprChain.TestnetChainId ? TestnetUrl : MainnetUrl;

        /// <summary>Opens the WebAuth login page and waits for the account.</summary>
        public async Task<WaxWalletSession> Login(WaxWalletContext context)
        {
            var response = await RunPopup(context, $"{BaseUrl(context.Network)}/login", null,
                "Press Continue to open the WebAuth window.", "Log in in the WebAuth window.");
            if ((string)response["type"] != "loginSuccess") throw Unexpected();

            var data = response["data"] as JObject;
            var actor = Text(data?["actor"]);
            if (string.IsNullOrEmpty(actor)) throw new WaxKitException(WaxErrorKind.Wallet, "WebAuth did not return an account.");
            return new WaxWalletSession(actor, Text(data["permission"]) ?? "active");
        }

        /// <summary>Keeps the saved session, as the SDK does (no wallet call).</summary>
        public Task<bool> Restore(WaxWalletContext context, WaxWalletSession session) => Task.FromResult(true);

        /// <summary>Nothing to clean up (the kit forgets the session).</summary>
        public Task Logout(WaxWalletContext context, WaxWalletSession session) => Task.CompletedTask;

        /// <summary>Opens the signing page with the game's actions; WebAuth signs and broadcasts, the kit gets the transaction id.</summary>
        public async Task<WaxSignResult> Sign(WaxWalletContext context, WaxWalletSession session, WaxSignRequest request)
        {
            if (request.Actions == null) throw new WaxKitException(WaxErrorKind.NotSupported, "WebAuth web needs the transaction's actions.");
            var message = new JObject
            {
                ["type"] = "transaction",
                ["data"] = new JObject
                {
                    ["transaction"] = new JObject { ["actions"] = request.Actions },
                    ["params"] = new JObject { ["broadcast"] = true },
                },
            };
            var response = await RunPopup(context, $"{BaseUrl(context.Network)}/auth", message,
                "Press Continue to open WebAuth and sign.", "Please complete the transaction in the WebAuth window.");
            if ((string)response["type"] != "transactionSuccess") throw Unexpected();

            var error = response["error"];
            if (error != null && error.Type != JTokenType.Null && error.Type != JTokenType.Undefined)
                throw new WaxKitException(WaxErrorKind.Wallet, "WebAuth: " + ErrorText(error));

            var data = response["data"] as JObject;
            var id = Text(data?["transaction_id"]) ?? Text((data?["processed"] as JObject)?["id"]);
            if (string.IsNullOrEmpty(id)) throw new WaxKitException(WaxErrorKind.Wallet, "WebAuth did not return a transaction id.");
            return new WaxSignResult(null, null, id);
        }

        /// <summary>Opens the page (inside the player's Continue click where the platform needs it) and waits for its answer.</summary>
        static async Task<JObject> RunPopup(WaxWalletContext context, string url, JObject post, string continueText, string waitText)
        {
            var platform = WaxPlatform.Current;
            IWaxPopupFlow flow = null;
            if (platform.PopupNeedsGesture)
                await context.WaitForContinue("WebAuth", continueText, () => flow = platform.OpenWalletPopup(url, post, WaxPopupProtocol.WebAuth));
            else
                flow = platform.OpenWalletPopup(url, post, WaxPopupProtocol.WebAuth);

            context.ShowWaiting("WebAuth", waitText);

            // The SDK has no time limit; this keeps a forgotten window from holding the game forever.
            using var deadline = new WaxDeadline(context.Cancellation, WaxKit.WebAuthWebTimeout);
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
            // close: the player closed or rejected the request in WebAuth.
            if ((string)response["type"] == "close")
                throw new WaxKitException(WaxErrorKind.Cancelled, "The WebAuth window was closed before the request was completed.");
            return response;
        }

        /// <summary>The wallet's error text: from error.json (a chain error) when there, else the error itself.</summary>
        static string ErrorText(JToken error)
        {
            if (error is JObject errorObject && errorObject["json"] is JObject json)
            {
                var chainError = json["error"] as JObject;
                var detail = (chainError?["details"] as JArray)?.FirstOrDefault() as JObject;
                return Text(detail?["message"]) ?? Text(chainError?["what"]) ?? Text(json["message"]) ?? json.ToString(Formatting.None);
            }
            if (error is JObject other) return Text(other["message"]) ?? other.ToString(Formatting.None);
            return Text(error) ?? error.ToString(Formatting.None);
        }

        static string Text(JToken token) => token?.Type == JTokenType.String ? (string)token : null;

        static WaxKitException Unexpected() => new WaxKitException(WaxErrorKind.Wallet, "WebAuth sent an unexpected answer.");
    }
}
