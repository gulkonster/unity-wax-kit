namespace Gulkonster.WaxKit
{
    public enum WalletType
    {
        Anchor,
        MyCloudWallet,
    }

    /// <summary>The logged-in account. The wallet holds the keys; the kit only knows who signed in.</summary>
    public sealed class WaxSession
    {
        public string Actor { get; }
        public string Permission { get; }
        public WalletType Wallet { get; }
        public string ChainId { get; }

        public WaxSession(string actor, string permission, WalletType wallet, string chainId)
        {
            Actor = actor;
            Permission = permission;
            Wallet = wallet;
            ChainId = chainId;
        }

        public override string ToString() => $"{Actor}@{Permission} ({Wallet})";
    }
}
