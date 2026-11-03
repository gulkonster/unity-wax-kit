using System;
using UnityEngine;

namespace UnityWaxKit
{
    /// <summary>
    /// Shows the wallet steps (Anchor QR, Continue, Waiting, Error) in the game.
    /// Set your own on <see cref="WaxKit.UI"/>; buttons that continue or open Anchor need a <see cref="WaxGestureButton"/>.
    /// </summary>
    public interface IWaxWalletUI
    {
        /// <summary>Shows a step. It replaces the step on screen, if any.</summary>
        void Show(WaxUiRequest request);
        /// <summary>Hides the wallet UI (the request ended).</summary>
        void Close();
    }

    /// <summary>The kind of wallet step to show; see <see cref="WaxUiRequest.Kind"/>.</summary>
    public enum WaxUiKind
    {
        /// <summary>Scan the QR or open the wallet app (Anchor or another ESR wallet); <see cref="WaxUiRequest.ExpiresAt"/> may hold a deadline.</summary>
        AnchorSign,
        /// <summary>The user must press Continue so the wallet may open its window.</summary>
        Continue,
        /// <summary>A wallet window is open; show the text and a Cancel button.</summary>
        Waiting,
        /// <summary>The request failed; show the message and a Close button.</summary>
        Error,
    }

    /// <summary>
    /// One wallet step for <see cref="IWaxWalletUI.Show"/>: what to show (texts, QR code, countdown) and how to answer it
    /// (<see cref="Continue"/>, <see cref="OpenAnchor"/>, <see cref="Cancel"/>).
    /// </summary>
    public sealed class WaxUiRequest
    {
        const int QuietZone = 4;

        /// <summary>The step on screen; WaxGestureButton answers it.</summary>
        internal static WaxUiRequest Active;

        readonly int qrSize;
        readonly string qrModules;
        readonly Action<string> answer;

        /// <summary>Which step this is; decides the buttons to show.</summary>
        public WaxUiKind Kind { get; }
        /// <summary>Title of the step.</summary>
        public string Title { get; }
        /// <summary>Body text, waiting text or error message.</summary>
        public string Text { get; }
        /// <summary>True when the step has a QR code; draw it with <see cref="CreateQrTexture"/>.</summary>
        public bool HasQr => qrSize > 0;
        /// <summary>True when an Open {wallet} button should be shown (it needs a <see cref="WaxGestureButton"/>).</summary>
        public bool CanOpenAnchor { get; }
        /// <summary>Wallet of an <see cref="WaxUiKind.AnchorSign"/> step ("Anchor", or a registered ESR wallet).</summary>
        public string WalletName { get; }
        /// <summary>UTC deadline of the request, or null.</summary>
        public DateTime? ExpiresAt { get; }

        internal WaxUiRequest(WaxUiKind kind, string title, string text, int qrSize, string qrModules,
            bool canOpenAnchor, DateTime? expiresAt, Action<string> answer, string walletName = "Anchor")
        {
            Kind = kind;
            Title = title;
            Text = text;
            this.qrSize = qrSize;
            this.qrModules = qrModules;
            CanOpenAnchor = canOpenAnchor;
            ExpiresAt = expiresAt;
            this.answer = answer;
            WalletName = walletName;
        }

        /// <summary>Runs Continue now. From a button, use <see cref="WaxGestureButton"/> so the browser allows the wallet window.</summary>
        public void Continue() => answer("continue");

        /// <summary>Opens the wallet app now. From a button, use <see cref="WaxGestureButton"/> so the browser allows the link.</summary>
        public void OpenAnchor() => answer("openAnchor");

        /// <summary>Cancels the request; the wallet call ends with <see cref="WaxErrorKind.Cancelled"/>.</summary>
        public void Cancel() => answer("cancel");

        /// <summary>The QR code with a 4-module quiet zone, one pixel per module, point filtered. The caller destroys it.</summary>
        public Texture2D CreateQrTexture()
        {
            if (!HasQr) return null;

            var size = qrSize + QuietZone * 2;
            var pixels = new Color32[size * size];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);

            for (var row = 0; row < qrSize; row++)
            for (var col = 0; col < qrSize; col++)
            {
                if (qrModules[row * qrSize + col] != '1') continue;
                // Texture rows start at the bottom.
                var y = size - 1 - (row + QuietZone);
                pixels[y * size + col + QuietZone] = new Color32(0, 0, 0, 255);
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }
    }
}
