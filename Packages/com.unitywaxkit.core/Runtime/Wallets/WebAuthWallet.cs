using UnityWaxKit.Esr;

namespace UnityWaxKit
{
    /// <summary>
    /// WebAuth (XPR Network) phone app. Its link is a fork of Anchor Link (ESR + buoy link session), so login, signing,
    /// restore and logout are Anchor's; only the link scheme and the request layout (ESR version 2) differ. Scheme per chain as in XPR Network's web-sdk:
    /// "proton" on XPR mainnet, "proton-dev" on XPR testnet. Callbacks use the default buoy (cb.anchor.link), as proton-link.
    /// </summary>
    internal sealed class WebAuthWallet : AnchorWallet
    {
        /// <summary>Id of the built-in WebAuth wallet.</summary>
        public new const string WalletId = WaxKit.WebAuthId;

        public override string Id => WalletId;
        public override string Name => "WebAuth";

        protected override string Scheme => "proton";

        protected override string SchemeFor(WaxNetwork network) => network.ChainId == XprChain.TestnetChainId ? "proton-dev" : Scheme;

        /// <summary>WebAuth reads requests as @proton/signing-request does: version 2 only (a version 3 login fails to decode).</summary>
        internal override EsrLayout Layout => EsrLayout.ProtonLink;

        /// <summary>WebAuth has no desktop app: no Open button where nothing handles its scheme (it must never open Anchor).</summary>
        internal override bool OpenOnlyWhenInstalled => true;

        /// <summary>See <see cref="WaxKit.WebAuthRequestAccount"/>.</summary>
        internal override string RequestAccount => WaxKit.WebAuthRequestAccount;

        /// <summary>XPR Network mainnet and testnet only.</summary>
        public override bool SupportsChain(WaxNetwork network) => XprChain.IsXpr(network);
    }
}
