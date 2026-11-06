using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityWaxKit
{
    public static partial class WaxKit
    {
        static WaxMarketData marketData;

        /// <summary>Settings of prices and balances: Alcor on/off and hosts, sources, oracle age, cache, tracked tokens.</summary>
        public static WaxMarketSettings MarketSettings { get; } = new WaxMarketSettings();

        /// <summary>Alcor Exchange reads (pools, spot market, token prices) on the current network.</summary>
        public static WaxAlcor Alcor => new WaxAlcor(Market);

        /// <summary>The market data of the current network (one cache per network object).</summary>
        static WaxMarketData Market
        {
            get
            {
                EnsureInitialized();
                var data = marketData;
                if (data == null || data.Network != Network) marketData = data = new WaxMarketData(Network, MarketSettings);
                return data;
            }
        }

        /// <summary>
        /// The network coin's price in USD (<see cref="WaxMarketSettings.CoreUsdSource"/>). Auto: the chain's on-chain oracle
        /// when the kit knows one for this chain id (WAX, EOS, Telos, XPR Network mainnets: delphioracle) and its newest value
        /// is younger than <see cref="WaxMarketSettings.OracleMaxAge"/>, else Alcor's USD price. Never throws for a missing
        /// price: the result says why.
        /// </summary>
        public static Task<WaxPrice> GetCoreUsdPrice(CancellationToken cancellation = default) => Market.GetCoreUsdPrice(cancellation);

        /// <summary>
        /// An account's balance of one token (get_currency_balance). 0 with the token's precision when it holds none; throws
        /// <see cref="WaxKitException"/> when the token does not exist on this chain (NotSupported) or the read fails (Network).
        /// </summary>
        public static Task<WaxBalance> GetBalance(string account, string contract, string symbol, CancellationToken cancellation = default) =>
            Market.GetBalance(account, new WaxToken(contract, symbol), cancellation);

        /// <summary>
        /// An account's balances of several tokens, one request per token. A token that fails has its own
        /// <see cref="WaxBalance.Error"/> and does not stop the others; cancelling stops the whole call.
        /// </summary>
        public static Task<List<WaxBalance>> GetBalances(string account, IEnumerable<WaxToken> tokens, CancellationToken cancellation = default) =>
            Market.GetBalances(account, tokens, cancellation);

        /// <summary>
        /// The logged-in account's balances of the tracked tokens (<see cref="WaxMarketSettings.TrackedTokens"/>, the network
        /// coin first): one request per token. Throws NotLoggedIn without a session.
        /// </summary>
        public static Task<List<WaxBalance>> GetTrackedBalances(CancellationToken cancellation = default) =>
            Market.GetBalances(RequireActor(), MarketSettings.TokensFor(Network), cancellation);

        /// <summary>
        /// The tracked tokens of the logged-in account with their value in the network coin and in USD. Tokens without a
        /// price keep their amount with a null value and a reason; the totals add up the priced tokens only, with the count of
        /// priced and unpriced tokens. Throws NotLoggedIn without a session.
        /// </summary>
        public static Task<WaxBalanceValues> GetBalanceValues(CancellationToken cancellation = default) =>
            Market.GetBalanceValues(RequireActor(), cancellation);

        static string RequireActor()
        {
            EnsureInitialized();
            return Current?.Actor ?? throw new WaxKitException(WaxErrorKind.NotLoggedIn, "Log in first: these balances are the logged-in account's.");
        }
    }
}
