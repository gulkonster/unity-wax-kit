using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityWaxKit
{
    /// <summary>
    /// Settings of the market data and balance calls (<see cref="WaxKit.MarketSettings"/>; the WAX Kit Manager sets them
    /// too). Sources are looked up by chain id, never by network name: the built-in Alcor hosts and oracles are for the
    /// mainnets only, so a testnet has no prices unless you set a source for its chain id.
    /// </summary>
    public sealed class WaxMarketSettings
    {
        /// <summary>WAX mainnet, EOS, Telos and XPR Network chain ids.</summary>
        internal const string EosChainId = "aca376f206b8fc25a6ed44dbdc66547c36c6c33e3a119ffbeaef943642f0e906";
        internal const string TelosChainId = "4667b205c6838ef70ff7988f6e8257e8be0e1284a2f59699054a018f743b1d11";
        internal const string XprChainId = "384da888112027f0321850a169f737c33e53b388aad48b5adace4bab97f437e0";

        static readonly Dictionary<string, string> DefaultAlcorHosts = new Dictionary<string, string>
        {
            [WaxNetwork.MainnetChainId] = "https://wax.alcor.exchange/api",
            [EosChainId] = "https://eos.alcor.exchange/api",
            [TelosChainId] = "https://telos.alcor.exchange/api",
            [XprChainId] = "https://proton.alcor.exchange/api",
        };

        // delphioracle pairs checked on each chain (pairs table: quote USD, base the core token).
        static readonly Dictionary<string, (string contract, string pair)> DefaultOracles = new Dictionary<string, (string, string)>
        {
            [WaxNetwork.MainnetChainId] = ("delphioracle", "waxpusd"),
            [EosChainId] = ("delphioracle", "eosusd"),
            [TelosChainId] = ("delphioracle", "tlosusd"),
            [XprChainId] = ("delphioracle", "xprusd"),
        };

        readonly Dictionary<string, string> alcorHosts = new Dictionary<string, string>(DefaultAlcorHosts);
        readonly Dictionary<string, (string contract, string pair)> oracles = new Dictionary<string, (string, string)>(DefaultOracles);
        TimeSpan oracleMaxAge = TimeSpan.FromHours(1);
        int cacheSeconds = 60;

        /// <summary>Off: the kit never calls Alcor; prices from Alcor are missing with "Alcor is off". Default on.</summary>
        public bool AlcorEnabled { get; set; } = true;

        /// <summary>Where token prices come from. Default <see cref="WaxPriceSource.Auto"/>.</summary>
        public WaxPriceSource PriceSource { get; set; } = WaxPriceSource.Auto;

        /// <summary>Where the network coin's USD price comes from. Default <see cref="WaxUsdSource.Auto"/>.</summary>
        public WaxUsdSource CoreUsdSource { get; set; } = WaxUsdSource.Auto;

        /// <summary>An oracle value older than this is not used ("stale"). Default 1 hour.</summary>
        public TimeSpan OracleMaxAge
        {
            get => oracleMaxAge;
            set => oracleMaxAge = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(OracleMaxAge), "Must be positive.");
        }

        /// <summary>How long Alcor and oracle answers are reused (0 = not cached). Default 60.</summary>
        public int CacheSeconds
        {
            get => cacheSeconds;
            set => cacheSeconds = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(CacheSeconds), "Cannot be negative.");
        }

        /// <summary>
        /// Tokens <see cref="WaxKit.GetTrackedBalances"/> and <see cref="WaxKit.GetBalanceValues"/> read (one request per
        /// token), each with an optional Alcor pool id for its price.
        /// </summary>
        public List<WaxToken> TrackedTokens { get; } = new List<WaxToken>();

        /// <summary>Also track the network coin (eosio.token and the network's <see cref="WaxNetwork.CoreSymbol"/>), first. Default on.</summary>
        public bool TrackNetworkCoin { get; set; } = true;

        /// <summary>The Alcor API host for a chain id (for example "https://wax.alcor.exchange/api"), or null when none.</summary>
        public string GetAlcorHost(string chainId) => chainId != null && alcorHosts.TryGetValue(chainId.ToLowerInvariant(), out var host) ? host : null;

        /// <summary>Sets the Alcor API host (https, ending in "/api") for a chain id; null or empty: no Alcor on that chain.</summary>
        public void SetAlcorHost(string chainId, string apiUrl)
        {
            var id = CheckChainId(chainId);
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                alcorHosts.Remove(id);
                return;
            }
            var url = apiUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException($"'{apiUrl}' must be an absolute https URL.", nameof(apiUrl));
            alcorHosts[id] = url;
        }

        /// <summary>The delphioracle-style oracle (contract, pair) for a chain id, or null.</summary>
        public (string contract, string pair)? GetOracle(string chainId) =>
            chainId != null && oracles.TryGetValue(chainId.ToLowerInvariant(), out var oracle) ? oracle : ((string, string)?)null;

        /// <summary>
        /// Sets the oracle for a chain id: a contract with delphioracle's tables (pairs, and datapoints scoped by pair) and
        /// the pair name (for example "delphioracle", "waxpusd"). Null contract: no oracle on that chain.
        /// </summary>
        public void SetOracle(string chainId, string contract, string pair)
        {
            var id = CheckChainId(chainId);
            if (string.IsNullOrWhiteSpace(contract))
            {
                oracles.Remove(id);
                return;
            }
            if (!WaxToken.IsAccountName(contract)) throw new ArgumentException($"'{contract}' is not an account name.", nameof(contract));
            if (!WaxToken.IsAccountName(pair)) throw new ArgumentException($"'{pair}' is not a pair name.", nameof(pair));
            oracles[id] = (contract.Trim(), pair.Trim());
        }

        /// <summary>The tokens to read: the network coin first (when <see cref="TrackNetworkCoin"/>), then <see cref="TrackedTokens"/>.</summary>
        internal List<WaxToken> TokensFor(WaxNetwork network)
        {
            var tokens = new List<WaxToken>();
            var coin = WaxMarketData.CoinOf(network);
            if (TrackNetworkCoin && coin != null) tokens.Add(coin);
            foreach (var token in TrackedTokens.Where(t => t != null))
                if (!tokens.Any(t => t.SameToken(token))) tokens.Add(token);
                else if (token.PoolId != null) tokens[tokens.FindIndex(t => t.SameToken(token))] = token;
            return tokens;
        }

        /// <summary>The pool id set for this token in <see cref="TrackedTokens"/>, or null.</summary>
        internal long? PoolIdFor(string contract, string symbol) =>
            TrackedTokens.FirstOrDefault(t => t != null && t.Contract == contract && t.Symbol == symbol && t.PoolId != null)?.PoolId;

        static string CheckChainId(string chainId)
        {
            if (string.IsNullOrEmpty(chainId) || chainId.Length != 64 || !chainId.All(Uri.IsHexDigit))
                throw new ArgumentException("Chain id must be 64 hex characters.", nameof(chainId));
            return chainId.ToLowerInvariant();
        }
    }
}
