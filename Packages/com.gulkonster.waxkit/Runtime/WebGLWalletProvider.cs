#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit
{
    /// <summary>WharfKit in the browser, through Plugins/WebGL/WaxKit.jslib and waxkit.bundle.jspre.</summary>
    internal sealed class WebGLWalletProvider : IWalletProvider
    {
        [DllImport("__Internal")] static extern void WaxKit_Init(string configJson);
        [DllImport("__Internal")] static extern void WaxKit_Call(int requestId, string method, string argsJson);
        [DllImport("__Internal")] static extern void WaxKit_Answer(int promptId, string action);
        [DllImport("__Internal")] static extern void WaxKit_Arm(string action);

        /// <summary>Runs the action inside the next browser pointerup / touchend (see <see cref="WaxGestureButton"/>).</summary>
        internal static void Arm(string action) => WaxKit_Arm(action);

        public void Initialize(WaxNetwork network, string appName)
        {
            WaxKitBridge.Instance.UiEvent = OnUiEvent;
            WaxKit_Init(new JObject
            {
                ["appName"] = appName,
                ["chainId"] = network.ChainId,
                ["rpcEndpoints"] = new JArray(network.RpcEndpoints),
            }.ToString());
        }

        public async Task<WaxSession> Login(WalletType wallet) =>
            ToSession(await Call("login", new JObject { ["walletType"] = wallet.ToString() }));

        public async Task<WaxSession> Restore() => ToSession(await Call("restore", new JObject()));

        public Task Logout() => Call("logout", new JObject());

        public async Task<string> Transact(JArray actions)
        {
            var data = await Call("transact", new JObject { ["actions"] = actions });
            return (string)data["transactionId"];
        }

        static Task<JToken> Call(string method, JObject args)
        {
            var id = WaxKitBridge.Instance.Begin(out var result);
            WaxKit_Call(id, method, args.ToString());
            return result;
        }

        static void OnUiEvent(JObject e)
        {
            if ((string)e["type"] != "show")
            {
                WaxKit.UI.Close();
                return;
            }

            var promptId = (int)e["id"];
            var qr = e["qr"] as JObject;
            var expiresAt = (long?)e["expiresAt"];
            WaxKit.UI.Show(new WaxUiRequest(
                (WaxUiKind)Enum.Parse(typeof(WaxUiKind), (string)e["kind"]),
                (string)e["title"],
                (string)e["text"],
                qr == null ? 0 : (int)qr["size"],
                (string)qr?["modules"],
                (bool?)e["canOpenAnchor"] ?? false,
                expiresAt == null ? (DateTime?)null : DateTimeOffset.FromUnixTimeMilliseconds(expiresAt.Value).UtcDateTime,
                action => WaxKit_Answer(promptId, action)));
        }

        static WaxSession ToSession(JToken data)
        {
            if (data == null || data.Type == JTokenType.Null) return null;
            return new WaxSession(
                (string)data["actor"],
                (string)data["permission"],
                (WalletType)Enum.Parse(typeof(WalletType), (string)data["walletType"]),
                (string)data["chainId"]);
        }
    }
}
#endif
