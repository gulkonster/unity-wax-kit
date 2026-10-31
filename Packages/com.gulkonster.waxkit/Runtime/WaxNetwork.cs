using System;
using System.Collections.Generic;

namespace Gulkonster.WaxKit
{
    /// <summary>A WAX chain: its id and the RPC endpoints used for table reads (tried in order).</summary>
    public sealed class WaxNetwork
    {
        public const string MainnetChainId = "1064487b3cd1a897ce03ae5b6a865651747e2e152090f99c1d19d44e01aea5a4";
        public const string TestnetChainId = "f16b1833c747c43682f4386fca9cbb327929334a762755ebec17f6f23c9b8a12";

        public static readonly WaxNetwork Mainnet = new WaxNetwork("Mainnet", MainnetChainId,
            "https://api.wax.alohaeos.com",
            "https://api.waxsweden.org",
            "https://wax.eosusa.io",
            "https://wax.eosphere.io",
            "https://wax.api.eosnation.io",
            "https://api.wax.bountyblok.io");

        public static readonly WaxNetwork Testnet = new WaxNetwork("Testnet", TestnetChainId,
            "https://testnet.waxsweden.org",
            "https://api.waxtest.alohaeos.com",
            "https://waxtest.api.eosnation.io");

        public string Name { get; }
        public string ChainId { get; }
        public IReadOnlyList<string> RpcEndpoints { get; }
        public bool IsMainnet => ChainId == MainnetChainId;

        public WaxNetwork(string name, string chainId, params string[] rpcEndpoints)
        {
            if (string.IsNullOrEmpty(chainId)) throw new ArgumentException("Chain id is required.", nameof(chainId));
            if (rpcEndpoints == null || rpcEndpoints.Length == 0) throw new ArgumentException("At least one RPC endpoint is required.", nameof(rpcEndpoints));

            Name = name;
            ChainId = chainId;
            RpcEndpoints = Array.AsReadOnly((string[])rpcEndpoints.Clone());
        }

        public override string ToString() => Name;
    }
}
