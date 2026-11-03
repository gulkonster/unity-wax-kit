namespace UnityWaxKit
{
    /// <summary>Which wallet a player logged in with. Pass it to <see cref="WaxKit.Login(WalletType)"/>.</summary>
    public enum WalletType
    {
        /// <summary>Anchor by Greymass: the desktop app on Windows, the phone app on Android / iOS (QR code or app link).</summary>
        Anchor,
        /// <summary>My Cloud Wallet (mycloudwallet.com): a web wallet that opens in a browser window. WAX mainnet only.</summary>
        MyCloudWallet,
        /// <summary>A wallet registered with <see cref="WaxKit.RegisterWallet"/>; see <see cref="WaxSession.WalletId"/>.</summary>
        Custom,
        /// <summary>WebAuth (XPR Network): the phone app (QR code or app link). XPR mainnet and testnet only.</summary>
        WebAuth,
        /// <summary>WebAuth (XPR Network) web wallet (webauth.com) in a browser window, like My Cloud Wallet. XPR mainnet and testnet only.</summary>
        WebAuthWeb,
    }

    /// <summary>
    /// The logged-in account, returned by <see cref="WaxKit.Login(WalletType)"/> and <see cref="WaxKit.RestoreSession"/>
    /// and kept in <see cref="WaxKit.Current"/>. The wallet holds the keys; the kit only knows who signed in.
    /// </summary>
    /// <example><code>
    /// var session = await WaxKit.Login(WalletType.Anchor);
    /// Debug.Log($"Hello {session.Actor}");   // for example "Hello mygameplayer"
    /// </code></example>
    public sealed class WaxSession
    {
        /// <summary>The account name, for example "mygameplayer". Use it as "from" in transfers.</summary>
        public string Actor { get; }
        /// <summary>The permission the wallet signs with, usually "active".</summary>
        public string Permission { get; }
        /// <summary>The wallet used to log in.</summary>
        public WalletType Wallet { get; }
        /// <summary>Id of the wallet ("anchor", "mycloudwallet" or a registered wallet's id).</summary>
        public string WalletId { get; }
        /// <summary>Id of the chain the session belongs to (sessions are saved per chain).</summary>
        public string ChainId { get; }

        /// <summary>Makes a session object by hand (for example in tests). Games get sessions from <see cref="WaxKit"/>.</summary>
        public WaxSession(string actor, string permission, WalletType wallet, string chainId)
            : this(actor, permission, wallet, WaxKit.WalletIdOf(wallet), chainId)
        {
        }

        internal WaxSession(string actor, string permission, WalletType wallet, string walletId, string chainId)
        {
            Actor = actor;
            Permission = permission;
            Wallet = wallet;
            WalletId = walletId;
            ChainId = chainId;
        }

        /// <summary>"actor@permission (wallet)", for logs and debug text.</summary>
        public override string ToString() => $"{Actor}@{Permission} ({(Wallet == WalletType.Custom ? WalletId : Wallet.ToString())})";
    }
}
