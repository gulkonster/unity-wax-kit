namespace UnityWaxKit
{
    /// <summary>XPR Network chains, the only ones the WebAuth wallets (phone app and web) work on.</summary>
    internal static class XprChain
    {
        /// <summary>Chain id of XPR Network mainnet.</summary>
        internal const string MainnetChainId = "384da888112027f0321850a169f737c33e53b388aad48b5adace4bab97f437e0";
        /// <summary>Chain id of XPR Network testnet.</summary>
        internal const string TestnetChainId = "71ee83bcf52142d61019d95f9cc5427ba6a0d7ff8accd9e2088ae2abeaf3d3dd";

        /// <summary>True for XPR Network mainnet and testnet.</summary>
        internal static bool IsXpr(WaxNetwork network) => network.ChainId == MainnetChainId || network.ChainId == TestnetChainId;
    }
}
