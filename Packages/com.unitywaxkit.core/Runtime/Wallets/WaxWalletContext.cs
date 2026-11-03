using System;
using System.Threading;
using System.Threading.Tasks;
using UnityWaxKit.Chain;
using UnityWaxKit.Esr;
using UnityWaxKit.Platform;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit
{
    /// <summary>
    /// What a wallet gets for one login / restore / logout / sign call: the network, the in-game wallet UI and platform helpers.
    /// <see cref="Cancellation"/> fires when the player presses Cancel.
    /// </summary>
    public sealed class WaxWalletContext
    {
        readonly IWaxWalletUI ui;
        readonly CancellationTokenSource cancel = new CancellationTokenSource();

        /// <summary>The network of this call (<see cref="WaxKit.Network"/> when it started).</summary>
        public WaxNetwork Network { get; }
        /// <summary>The app name given to <see cref="WaxKit.Initialize"/>.</summary>
        public string AppName { get; }
        /// <summary>Fires when the player presses Cancel. Pass it to every wait, so the call ends at once.</summary>
        public CancellationToken Cancellation => cancel.Token;

        /// <summary>True once this call showed a step, so a failure is shown as an error step.</summary>
        internal bool Shown { get; private set; }

        internal WaxWalletContext(WaxNetwork network, string appName, IWaxWalletUI ui)
        {
            Network = network;
            AppName = appName;
            this.ui = ui;
        }

        /// <summary>The app name as an Antelope name (lowercase a-z 1-5 and dots, at most 12 characters).</summary>
        public string AppNameAsName => Antelope.AntelopeName.FromText(AppName);

        /// <summary>True on phones and tablets (the wallet app is on the same device).</summary>
        public bool IsMobile => WaxPlatform.Current.IsMobile;

        /// <summary>Link a wallet app opens after a request to bring the player back to the game, or null.</summary>
        public string ReturnUrl => WaxPlatform.Current.ReturnUrl;

        /// <summary>True once <see cref="OpenLink"/> ran in this call (the wallet app on this device got the request).</summary>
        public bool OpenedLink { get; private set; }

        /// <summary>Opens a wallet link (esr:, anchor:, …) in the wallet app on this device.</summary>
        public void OpenLink(string url)
        {
            OpenedLink = true;
            WaxPlatform.Current.OpenLink(url);
        }

        /// <summary>
        /// Call right after sending a request to a wallet app on this computer (for example over a link session): lets the
        /// app come in front of the game. <paramref name="force"/> also brings its window forward if it does not do so itself.
        /// Does nothing on phones and in browsers.
        /// </summary>
        public void LetWalletAppComeToFront(string scheme, bool force) => WaxPlatform.Current.LetWalletAppComeToFront(scheme, force);

        /// <summary>
        /// Computers only: puts the open window of the wallet app (the app handling <paramref name="scheme"/> links) in front
        /// again, for an Open button pressed while a request already waits there (the player left the app, for example
        /// with Alt+Tab behind a full-screen game). Returns false when the app has no open window (not running, phones,
        /// browsers); then send the request again instead.
        /// </summary>
        public bool BringWalletAppToFront(string scheme) => !IsMobile && WaxPlatform.Current.BringWalletAppToFront(scheme);

        /// <summary>
        /// Phones and phone browsers: completes when the player left the game for the wallet, came back and stayed for
        /// <paramref name="stayMilliseconds"/> (the wallet was closed without an answer). Never completes on desktops.
        /// </summary>
        public Task WaitForPlayerBack(int stayMilliseconds, CancellationToken cancellation) =>
            WaxPlatform.Current.WaitForPlayerBack(stayMilliseconds, cancellation);

        /// <summary>True when an installed app handles links with this scheme, false when none does, null when unknown (browsers).</summary>
        public bool? CanOpenScheme(string scheme) => WaxPlatform.Current.CanOpenScheme(scheme);

        /// <summary>Puts the game window / activity back in front.</summary>
        public void ReturnToGame() => WaxPlatform.Current.ReturnToGame();

        /// <summary>/v1/chain/get_account through the network's endpoints.</summary>
        public Task<JObject> GetAccount(string account) => WaxChainApi.GetAccount(Network, account, Cancellation);

        /// <summary>
        /// Shows the sign step: optional QR (<paramref name="qrText"/>), a button that runs <paramref name="openWallet"/>
        /// inside the click (null hides it) and an optional countdown.
        /// </summary>
        public void ShowSign(string title, string text, string walletName, string qrText, Action openWallet, DateTime? expiresAt)
        {
            var qr = string.IsNullOrEmpty(qrText) ? null : QrCode.Encode(qrText);
            Show(new WaxUiRequest(WaxUiKind.AnchorSign, title, text, qr?.Size ?? 0, qr?.ToModuleString(), openWallet != null, expiresAt,
                action =>
                {
                    if (action == "openAnchor") openWallet?.Invoke();
                    else if (action == "cancel") Cancel();
                }, walletName));
        }

        /// <summary>Shows Continue and completes once <paramref name="onContinue"/> ran inside the player's click.</summary>
        public Task WaitForContinue(string title, string text, Action onContinue)
        {
            var done = new TaskCompletionSource<bool>();
            Show(new WaxUiRequest(WaxUiKind.Continue, title, text, 0, null, false, null, action =>
            {
                if (action == "cancel")
                {
                    Cancel();
                    done.TrySetCanceled();
                    return;
                }
                if (action != "continue" || done.Task.IsCompleted) return;
                try
                {
                    onContinue?.Invoke();
                    done.TrySetResult(true);
                }
                catch (Exception e)
                {
                    done.TrySetException(e);
                }
            }));
            return done.Task;
        }

        /// <summary>Shows a waiting step (text and Cancel), for example while a wallet window is open.</summary>
        public void ShowWaiting(string title, string text) =>
            Show(new WaxUiRequest(WaxUiKind.Waiting, title, text, 0, null, false, null, action =>
            {
                if (action == "cancel") Cancel();
            }));

        internal void ShowError(string message) =>
            Show(new WaxUiRequest(WaxUiKind.Error, "Wallet error", message, 0, null, false, null, _ => { }));

        internal void Close()
        {
            WaxUiRequest.Active = null;
            if (Shown) ui.Close();
        }

        internal void Cancel()
        {
            if (!cancel.IsCancellationRequested) cancel.Cancel();
        }

        void Show(WaxUiRequest request)
        {
            Shown = true;
            WaxUiRequest.Active = request;
            ui.Show(request);
        }
    }
}
