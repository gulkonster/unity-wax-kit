using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityWaxKit
{
    /// <summary>
    /// Read-only Alcor Exchange data for the current network (get it from <see cref="WaxKit.Alcor"/>): swap pools, the spot
    /// market, and token prices in the network coin. The Alcor host is chosen by chain id
    /// (<see cref="WaxMarketSettings.GetAlcorHost"/>): WAX, EOS, Telos and XPR Network mainnets; testnets have none by default.
    /// Answers are cached for <see cref="WaxMarketSettings.CacheSeconds"/>; on HTTP 429 the kit waits and tries again (at
    /// most 3 requests and 10 s), then throws. Every call stops with <see cref="System.OperationCanceledException"/> when
    /// its token is cancelled.
    /// </summary>
    public sealed class WaxAlcor
    {
        readonly WaxMarketData data;

        internal WaxAlcor(WaxMarketData data) => this.data = data;

        /// <summary>
        /// The token's price in the network coin (for example in WAX). Never throws for a missing price or a failed
        /// request: <see cref="WaxPrice.Value"/> is then null with <see cref="WaxPrice.Missing"/> and a reason.
        /// <para>
        /// Source (<see cref="WaxMarketSettings.PriceSource"/>): a pool id set for the token (in <see cref="WaxToken.PoolId"/>
        /// or <see cref="WaxMarketSettings.TrackedTokens"/>) always wins; when that pool is missing, is another pair or is
        /// empty, the price is null with that reason, never taken from another pool. Otherwise only pools that pair the token
        /// directly with the network coin count, and the kit takes the pool with the largest network-coin reserve (for
        /// example the largest WAX reserve: the WAX the pool holds, not the liquidity at the current price); a tie goes to
        /// the lower fee. Auto: when no pool has a price, the last trade of the spot market against the network coin.
        /// Prices through another token (for example via USDT) are not computed.
        /// </para>
        /// </summary>
        public Task<WaxPrice> GetPrice(string contract, string symbol, CancellationToken cancellation = default) =>
            data.GetPrice(new WaxToken(contract, symbol), cancellation);

        /// <summary>As <see cref="GetPrice(string, string, CancellationToken)"/>; the token's <see cref="WaxToken.PoolId"/> is used when set.</summary>
        public Task<WaxPrice> GetPrice(WaxToken token, CancellationToken cancellation = default) => data.GetPrice(token, cancellation);

        /// <summary>One swap pool by id, or null when Alcor has none with this id.</summary>
        public Task<WaxAlcorPool> GetPool(long poolId, CancellationToken cancellation = default) => data.GetPool(poolId, cancellation);

        /// <summary>
        /// The swap pools of a token with the network coin (one pair can have several pools with different fees). Only that
        /// pair is read, never the full pool list.
        /// </summary>
        public Task<List<WaxAlcorPool>> GetCoinPools(string contract, string symbol, CancellationToken cancellation = default) =>
            data.GetPairPools(new WaxToken(contract, symbol), WaxMarketData.CoinOf(data.Network)
                ?? throw new WaxKitException(WaxErrorKind.NotSupported, $"{data.Network.Name} has no core symbol."), cancellation);

        /// <summary>The swap pools of any pair of tokens, in either order.</summary>
        public Task<List<WaxAlcorPool>> GetPairPools(WaxToken tokenA, WaxToken tokenB, CancellationToken cancellation = default) =>
            data.GetPairPools(tokenA, tokenB, cancellation);

        /// <summary>
        /// The spot market (order book) of the token against the network coin: last trade price and time, best bid / ask,
        /// 24 h volume. Null when Alcor has no such market.
        /// </summary>
        public Task<WaxAlcorMarket> GetMarket(string contract, string symbol, CancellationToken cancellation = default) =>
            data.GetMarket(new WaxToken(contract, symbol), cancellation);

        /// <summary>The newest spot trades of the token against the network coin, newest first (1 to 1000).</summary>
        public Task<List<WaxAlcorTrade>> GetTrades(string contract, string symbol, int limit = 50, CancellationToken cancellation = default) =>
            data.GetTrades(new WaxToken(contract, symbol), limit, cancellation);

        /// <summary>Price history of a pool (price of its token A in token B), oldest first: the last <paramref name="count"/> candles.</summary>
        public Task<List<WaxCandle>> GetPoolCandles(long poolId, WaxCandleInterval interval, int count, CancellationToken cancellation = default) =>
            data.GetPoolCandles(poolId, interval, count, cancellation);

        /// <summary>Price history of the spot market against the network coin, oldest first: the last <paramref name="count"/> candles.</summary>
        public Task<List<WaxCandle>> GetMarketCandles(string contract, string symbol, WaxCandleInterval interval, int count,
            CancellationToken cancellation = default) =>
            data.GetMarketCandles(new WaxToken(contract, symbol), interval, count, cancellation);
    }
}
