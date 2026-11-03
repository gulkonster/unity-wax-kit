using System;
using System.Collections.Generic;
using System.Linq;
using UnityWaxKit.Chain;

namespace UnityWaxKit
{
    /// <summary>
    /// An Antelope chain: id, RPC endpoints (tried in order), optional core token and optional AtomicAssets API endpoints.
    /// Every chain call of the kit goes to these lists. Networks are immutable; <see cref="WithEndpoints"/> returns a copy.
    /// Pass one to <see cref="WaxKit.Initialize"/>.
    /// </summary>
    /// <example><code>
    /// WaxKit.Initialize(WaxNetwork.Testnet, "mygame");                                   // test first
    /// WaxKit.Initialize(WaxNetwork.Mainnet.WithEndpoints("https://my-node.example.com"), "mygame");
    /// </code></example>
    public sealed class WaxNetwork
    {
        /// <summary>Chain id of WAX mainnet.</summary>
        public const string MainnetChainId = "1064487b3cd1a897ce03ae5b6a865651747e2e152090f99c1d19d44e01aea5a4";
        /// <summary>Chain id of WAX testnet.</summary>
        public const string TestnetChainId = "f16b1833c747c43682f4386fca9cbb327929334a762755ebec17f6f23c9b8a12";

        /// <summary>WAX mainnet (real tokens) with a list of public RPC and AtomicAssets endpoints.</summary>
        public static readonly WaxNetwork Mainnet = new WaxNetwork("WAX Mainnet", MainnetChainId,
            new[]
            {
                "https://api.wax.alohaeos.com",
                "https://api.waxsweden.org",
                "https://wax.eosusa.io",
                "https://wax.eosphere.io",
                "https://wax.api.eosnation.io",
                "https://api.wax.bountyblok.io",
            },
            "WAX", 8,
            new[]
            {
                "https://wax-aa.eu.eosamsterdam.net",
                "https://wax.eosusa.io",
                "https://atomic.wax.eosrio.io",
                "https://wax-atomic-api.eosphere.io",
                "https://wax.api.atomicassets.io",
            });

        /// <summary>WAX testnet (free test tokens; use it while developing) with public endpoints.</summary>
        public static readonly WaxNetwork Testnet = new WaxNetwork("WAX Testnet", TestnetChainId,
            new[]
            {
                "https://testnet.waxsweden.org",
                "https://api.waxtest.alohaeos.com",
                "https://waxtest.api.eosnation.io",
            },
            "WAX", 8,
            new[]
            {
                "https://test.wax.api.atomicassets.io",
                "https://wax-testnet.api.atomicassets.io",
            });

        /// <summary>Display name ("WAX Mainnet", "WAX Testnet" or the name you gave).</summary>
        public string Name { get; }
        /// <summary>The chain id (64 hex characters). Wallets check it, so a session never signs on the wrong chain.</summary>
        public string ChainId { get; }
        /// <summary>RPC endpoints (chain API servers), tried in order; the first that works is remembered.</summary>
        public IReadOnlyList<string> RpcEndpoints { get; }
        /// <summary>AtomicAssets / AtomicMarket API endpoints; empty when the chain has none.</summary>
        public IReadOnlyList<string> AtomicEndpoints { get; }
        /// <summary>Core token symbol code ("WAX"), or null when unknown.</summary>
        public string CoreSymbol { get; }
        /// <summary>Decimals of the core token, or -1 when unknown.</summary>
        public int CorePrecision { get; }
        /// <summary>WAX mainnet (the only chain My Cloud Wallet works on).</summary>
        public bool IsMainnet => ChainId == MainnetChainId;

        internal WaxEndpointList Rpc { get; }
        internal WaxEndpointList Atomic { get; }

        /// <summary>Any Antelope chain with just a name, chain id and RPC endpoints.</summary>
        public WaxNetwork(string name, string chainId, params string[] rpcEndpoints)
            : this(name, chainId, rpcEndpoints, null, -1, null)
        {
        }

        /// <summary>Any Antelope chain. <paramref name="coreSymbol"/> / <paramref name="corePrecision"/> describe its token
        /// (for example "EOS", 4); <paramref name="atomicEndpoints"/> are AtomicAssets API servers, if the chain has them.</summary>
        public WaxNetwork(string name, string chainId, IEnumerable<string> rpcEndpoints,
            string coreSymbol = null, int corePrecision = -1, IEnumerable<string> atomicEndpoints = null)
        {
            if (string.IsNullOrEmpty(chainId) || chainId.Length != 64 || !chainId.All(Uri.IsHexDigit))
                throw new ArgumentException("Chain id must be 64 hex characters.", nameof(chainId));

            Name = string.IsNullOrEmpty(name) ? chainId.Substring(0, 8) : name;
            ChainId = chainId.ToLowerInvariant();
            RpcEndpoints = Copy(rpcEndpoints, nameof(rpcEndpoints));
            if (RpcEndpoints.Count == 0) throw new ArgumentException("At least one RPC endpoint is required.", nameof(rpcEndpoints));
            AtomicEndpoints = Copy(atomicEndpoints, nameof(atomicEndpoints));
            CoreSymbol = coreSymbol;
            CorePrecision = coreSymbol == null ? -1 : corePrecision;

            Rpc = new WaxEndpointList(RpcEndpoints, "RPC");
            Atomic = new WaxEndpointList(AtomicEndpoints, "AtomicAssets");
        }

        /// <summary>The same network with these RPC endpoints instead (tried in this order).</summary>
        public WaxNetwork WithEndpoints(params string[] rpcEndpoints) =>
            new WaxNetwork(Name, ChainId, rpcEndpoints, CoreSymbol, CorePrecision, AtomicEndpoints);

        /// <summary>The same network with these AtomicAssets API endpoints instead (tried in this order).</summary>
        public WaxNetwork WithAtomicEndpoints(params string[] atomicEndpoints) =>
            new WaxNetwork(Name, ChainId, RpcEndpoints, CoreSymbol, CorePrecision, atomicEndpoints);

        /// <summary>The network name.</summary>
        public override string ToString() => Name;

        static IReadOnlyList<string> Copy(IEnumerable<string> urls, string parameter)
        {
            var copy = (urls ?? Enumerable.Empty<string>()).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).ToArray();
            foreach (var url in copy)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new ArgumentException($"Endpoint '{url}' must be an absolute http or https URL.", parameter);
            }
            return Array.AsReadOnly(copy);
        }
    }
}
