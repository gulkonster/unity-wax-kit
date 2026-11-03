using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityWaxKit.Antelope;
using UnityWaxKit.Esr;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit
{
    /// <summary>
    /// Anchor (desktop and mobile), ported from @wharfkit/wallet-plugin-anchor + protocol-esr. Login opens a link session:
    /// later transactions are sealed with the request key and sent to Anchor's own buoy channel, so no new QR is needed.
    /// </summary>
    internal class AnchorWallet : WaxEsrWallet
    {
        /// <summary>Id of the built-in Anchor wallet.</summary>
        public const string WalletId = WaxKit.AnchorId;

        public override string Id => WalletId;
        public override string Name => "Anchor";

        internal override IEnumerable<KeyValuePair<string, byte[]>> LoginInfo(WaxWalletContext context, PrivateKey requestKey)
        {
            var link = BuiltinAbi.Instance.Encode("link_create", new JObject
            {
                ["session_name"] = context.AppNameAsName,
                ["request_key"] = requestKey.ToPublic().ToString(),
                ["user_agent"] = WaxKit.UserAgent,
            });
            yield return new KeyValuePair<string, byte[]>("link", link);
        }

        internal override JObject OnLogin(WaxWalletContext context, JObject payload, PrivateKey requestKey)
        {
            var data = new JObject();
            if (payload["link_ch"] == null || payload["link_key"] == null || payload["link_name"] == null) return data;

            data["requestKey"] = requestKey.ToSecretString();
            data["signerKey"] = PublicKey.Parse((string)payload["link_key"]).ToString();
            data["channelUrl"] = (string)payload["link_ch"];
            data["channelName"] = (string)payload["link_name"];
            // The player opened Anchor on this device (Open button or the app opened by itself) rather than scanning the
            // QR with another device: later requests then go to the Anchor app on this device.
            data["thisDevice"] = context.OpenedLink;
            try
            {
                // link_meta is advisory; a malformed value must not fail the login.
                if (payload["link_meta"] != null)
                {
                    var meta = WaxJson.Parse((string)payload["link_meta"]);
                    data["sameDevice"] = meta["sameDevice"];
                    data["launchUrl"] = meta["launchUrl"];
                }
            }
            catch (Exception)
            {
                // Ignored, as WharfKit.
            }
            return data;
        }

        public override async Task<WaxSignResult> Sign(WaxWalletContext context, WaxWalletSession session, WaxSignRequest request)
        {
            var data = session.Data;
            var channelUrl = (string)data["channelUrl"];
            if (string.IsNullOrEmpty(channelUrl)) return await base.Sign(context, session, request);

            var channel = Buoy.NewChannel();
            var esr = CreateSignRequest(request, Buoy.ChannelUrl(BuoyService, channel));
            esr.SetInfoKey("link", BuiltinAbi.Instance.Encode("link_info", new JObject { ["expiration"] = AntelopeTime.FormatSeconds(request.ExpiresAt) }));
            if (!string.IsNullOrEmpty(RequestAccount)) esr.SetInfoString("req_account", RequestAccount);
            var sameDeviceRequest = SameDevice(context, esr);
            var sameDevice = (bool?)data["sameDevice"] == true || context.IsMobile;
            // null for sessions saved by older versions: unknown.
            var thisDevice = (bool?)data["thisDevice"];
            var scheme = SchemeFor(context.Network);
            var installed = context.CanOpenScheme(scheme);
            // On a phone whose Anchor app got the login, the request opens the app directly, as the login did. Anchor on a
            // phone does not come to the front for a link-session request by itself, so a sealed message alone would wait
            // until the player opens Anchor by hand. Browsers that need a tap keep the Open Anchor button.
            var openAppHere = context.IsMobile && thisDevice != false && installed != false;

            // On a computer whose Anchor got the login, the request goes to that Anchor app below; Open Anchor then brings
            // its window back in front instead of sending the request again (see OpenWallet).
            var requestInAppHere = !context.IsMobile && !openAppHere && thisDevice == true;
            // Every request carries this wallet's own scheme: WebAuth reads only "proton:" / "proton-dev:" requests
            // (@proton/signing-request rejects any other scheme), and the Open link must open this wallet's app.
            var sameDeviceLink = sameDeviceRequest.Encode(true, false, scheme + ":");
            context.ShowSign($"Complete using {Name}",
                $"Please open your {Name} Wallet on \"{data["channelName"]}\" to review and approve this transaction.",
                Name, null, ShowOpen(installed) ? () => OpenWallet(context, sameDeviceLink, requestInAppHere) : (Action)null, request.ExpiresAt);

            // Listen before sending, so the answer cannot be missed.
            var callback = WaitForCallback(context, channel, request.ExpiresAt);

            if (openAppHere)
            {
                // The link carries the whole request, so the sealed message is not sent as well (Anchor would show it twice).
                context.OpenLink(sameDeviceLink);
            }
            else
            {
                // Anchor desktop shows itself when the request arrives; a full-screen game would keep it behind, so the
                // game lets it come in front (and brings it forward when the login was made with Anchor on this computer).
                if (!context.IsMobile) context.LetWalletAppComeToFront(scheme, thisDevice == true && installed == true);

                var sealedMessage = SealedMessage.Seal(sameDevice ? sameDeviceLink : esr.Encode(true, false, scheme + ":"),
                    PrivateKey.Parse((string)data["requestKey"]), PublicKey.Parse((string)data["signerKey"]));
                await Buoy.Send(channelUrl, sealedMessage, context.Cancellation);

                if (sameDevice && !string.IsNullOrEmpty((string)data["launchUrl"])) context.OpenLink((string)data["launchUrl"]);
            }

            var payload = await callback;
            context.ReturnToGame();
            return ToSignResult(request, payload);
        }
    }
}
