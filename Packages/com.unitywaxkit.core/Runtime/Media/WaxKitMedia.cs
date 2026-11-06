namespace UnityWaxKit
{
    public static partial class WaxKit
    {
        static WaxNftMediaClient nftMedia;

        /// <summary>NFT media settings: data fields, IPFS gateways, animated / still, limits and caches.</summary>
        public static WaxMediaSettings MediaSettings { get; } = new WaxMediaSettings();

        /// <summary>
        /// NFT images and videos (made on first use; nothing is downloaded until a call asks). Reading an asset's or template's
        /// references uses the current network's AtomicAssets endpoints; loading a reference does not need a network.
        /// </summary>
        public static WaxNftMediaClient NftMedia => nftMedia ?? (nftMedia = WaxNftMediaClient.Create(MediaSettings));
    }
}
