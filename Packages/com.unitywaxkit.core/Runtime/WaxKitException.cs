using System;

namespace UnityWaxKit
{
    /// <summary>Why a kit call failed. Check <see cref="WaxKitException.Kind"/> to decide what to show the player.</summary>
    public enum WaxErrorKind
    {
        /// <summary>The player closed the wallet, pressed Cancel, or rejected the request.</summary>
        Cancelled,
        /// <summary>The wallet did not answer before the request expired. The player can simply try again.</summary>
        Timeout,
        /// <summary>The wallet or the chain refused the request; the message says why (for example "overdrawn balance").</summary>
        Wallet,
        /// <summary>No endpoint could be reached (offline, endpoints down).</summary>
        Network,
        /// <summary>A transaction was sent before anyone logged in.</summary>
        NotLoggedIn,
        /// <summary>The wallet does not work on the current network (for example My Cloud Wallet on testnet).</summary>
        WrongChain,
        /// <summary>The wallet does not work on this platform, or no wallet has the given id.</summary>
        NotSupported,
    }

    /// <summary>
    /// Every kit failure. <see cref="Exception.Message"/> keeps the original wallet or network message.
    /// </summary>
    /// <example><code>
    /// try { await WaxKit.Transact(action); }
    /// catch (WaxKitException e) when (e.Kind == WaxErrorKind.Cancelled) { /* the player changed their mind: nothing to do */ }
    /// catch (WaxKitException e) { statusText.text = e.Message; }
    /// </code></example>
    public sealed class WaxKitException : Exception
    {
        /// <summary>The kind of failure.</summary>
        public WaxErrorKind Kind { get; }

        /// <summary>Makes an error (custom wallets throw these too).</summary>
        public WaxKitException(WaxErrorKind kind, string message) : base(message)
        {
            Kind = kind;
        }

        /// <summary>"Kind: message".</summary>
        public override string ToString() => $"{Kind}: {Message}";
    }
}
