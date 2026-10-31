using System;

namespace Gulkonster.WaxKit
{
    public enum WaxErrorKind
    {
        /// <summary>The user closed the wallet or rejected the request.</summary>
        Cancelled,
        Timeout,
        Wallet,
        Network,
        NotLoggedIn,
        WrongChain,
        NotSupported,
    }

    /// <summary>Every kit failure. <see cref="Exception.Message"/> keeps the original wallet or network message.</summary>
    public sealed class WaxKitException : Exception
    {
        public WaxErrorKind Kind { get; }

        public WaxKitException(WaxErrorKind kind, string message) : base(message)
        {
            Kind = kind;
        }

        public override string ToString() => $"{Kind}: {Message}";
    }
}
