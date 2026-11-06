using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UnityWaxKit
{
    /// <summary>
    /// A token: its contract and symbol (a token is never identified by its symbol alone), plus an optional Alcor pool id
    /// that <see cref="WaxAlcor.GetPrice(WaxToken, System.Threading.CancellationToken)"/> always uses for its price.
    /// </summary>
    public sealed class WaxToken
    {
        /// <summary>Token contract, for example "alien.worlds".</summary>
        public string Contract { get; }
        /// <summary>Symbol code, for example "TLM".</summary>
        public string Symbol { get; }
        /// <summary>Alcor swap pool to price this token with, or null to let the kit choose.</summary>
        public long? PoolId { get; }

        /// <summary>A token by contract and symbol; <paramref name="poolId"/> pins its Alcor price to that pool.</summary>
        public WaxToken(string contract, string symbol, long? poolId = null)
        {
            if (!IsAccountName(contract)) throw new ArgumentException($"'{contract}' is not a contract account name.", nameof(contract));
            if (!IsSymbolCode(symbol)) throw new ArgumentException($"'{symbol}' is not a token symbol (1-7 capital letters A-Z).", nameof(symbol));
            if (poolId < 0) throw new ArgumentException("A pool id cannot be negative.", nameof(poolId));
            Contract = contract.Trim();
            Symbol = symbol.Trim();
            PoolId = poolId;
        }

        /// <summary>Alcor's token id: "symbol-contract" in lowercase, for example "tlm-alien.worlds".</summary>
        public string AlcorId => Symbol.ToLowerInvariant() + "-" + Contract;

        /// <summary>True when contract and symbol are the same (the pool id is not compared).</summary>
        public bool SameToken(WaxToken other) => other != null && other.Contract == Contract && other.Symbol == Symbol;

        /// <summary>"TLM@alien.worlds".</summary>
        public override string ToString() => $"{Symbol}@{Contract}";

        /// <summary>An account name: 1-12 characters a-z, 1-5 and dots, not ending in a dot.</summary>
        internal static bool IsAccountName(string name)
        {
            var s = name?.Trim();
            return !string.IsNullOrEmpty(s) && s.Length <= 12 && !s.EndsWith(".") &&
                   s.All(c => (c >= 'a' && c <= 'z') || (c >= '1' && c <= '5') || c == '.');
        }

        internal static bool IsSymbolCode(string symbol)
        {
            var s = symbol?.Trim();
            return !string.IsNullOrEmpty(s) && s.Length <= 7 && s.All(c => c >= 'A' && c <= 'Z');
        }
    }

    /// <summary>Where <see cref="WaxAlcor.GetPrice(string, string, System.Threading.CancellationToken)"/> takes a token's price from.</summary>
    public enum WaxPriceSource
    {
        /// <summary>The Alcor swap pool (see <see cref="WaxAlcor.GetPrice(string, string, System.Threading.CancellationToken)"/>); when there is none, the spot market.</summary>
        Auto,
        /// <summary>Only an Alcor swap pool.</summary>
        PoolOnly,
        /// <summary>Only the Alcor spot market (order book): its last trade.</summary>
        SpotOnly,
    }

    /// <summary>Where <see cref="WaxKit.GetCoreUsdPrice"/> takes the network coin's USD price from.</summary>
    public enum WaxUsdSource
    {
        /// <summary>The chain's own price oracle when the kit knows one for this chain id and it is fresh, else Alcor.</summary>
        Auto,
        /// <summary>Only the on-chain oracle.</summary>
        OracleOnly,
        /// <summary>Only Alcor's USD price.</summary>
        AlcorOnly,
    }

    /// <summary>Where a <see cref="WaxPrice"/> came from.</summary>
    public enum WaxPriceOrigin
    {
        /// <summary>No price (see <see cref="WaxPrice.Missing"/>).</summary>
        None,
        /// <summary>An Alcor swap pool (<see cref="WaxPrice.PoolId"/>).</summary>
        Pool,
        /// <summary>The last trade of an Alcor spot market (<see cref="WaxPrice.MarketId"/>).</summary>
        Spot,
        /// <summary>The chain's on-chain price oracle (for example delphioracle on WAX).</summary>
        Oracle,
        /// <summary>Alcor's own USD price of the network coin.</summary>
        AlcorUsd,
        /// <summary>The token is the network coin itself: 1.</summary>
        NetworkCoin,
    }

    /// <summary>Why a <see cref="WaxPrice"/> has no value. The value is never guessed and a failed request is never 0.</summary>
    public enum WaxPriceMissing
    {
        /// <summary>The price is there.</summary>
        None,
        /// <summary>No Alcor swap pool pairs the token directly with the network coin.</summary>
        NoPool,
        /// <summary>Neither a pool nor a spot market pairs the token directly with the network coin (no multi-step prices).</summary>
        NoDirectPair,
        /// <summary>The pool holds none of one of its tokens, or is inactive.</summary>
        EmptyPool,
        /// <summary>The set pool id does not exist.</summary>
        PoolNotFound,
        /// <summary>The set pool id is not a pool of this token with the network coin.</summary>
        WrongPool,
        /// <summary>The spot market has no trade.</summary>
        NoTrades,
        /// <summary>The oracle's newest value is older than <see cref="WaxMarketSettings.OracleMaxAge"/>.</summary>
        Stale,
        /// <summary>The source is turned off (<see cref="WaxMarketSettings.AlcorEnabled"/>).</summary>
        SourceOff,
        /// <summary>No source is known for this chain id (testnets have no prices by default).</summary>
        NotSupported,
        /// <summary>The request failed (network, rate limit, wrong answer); see <see cref="WaxPrice.Reason"/>.</summary>
        RequestFailed,
    }

    /// <summary>
    /// A price: of a token in the network coin (<see cref="WaxAlcor.GetPrice(string, string, System.Threading.CancellationToken)"/>),
    /// or of the network coin in USD (<see cref="WaxKit.GetCoreUsdPrice"/>). <see cref="Value"/> is null when there is none,
    /// with <see cref="Missing"/> and <see cref="Reason"/>. A shown price is not the price a trade of some size would get.
    /// </summary>
    public sealed class WaxPrice
    {
        /// <summary>The price, or null (see <see cref="Missing"/>).</summary>
        public decimal? Value { get; }
        /// <summary>What it is priced in: the network coin's symbol ("WAX") or "USD".</summary>
        public string Unit { get; }
        /// <summary>Where it came from.</summary>
        public WaxPriceOrigin Origin { get; }
        /// <summary>The Alcor pool id, for <see cref="WaxPriceOrigin.Pool"/>.</summary>
        public long? PoolId { get; }
        /// <summary>The Alcor spot market (ticker id, for example "tlm-alien.worlds_wax-eosio.token"), for <see cref="WaxPriceOrigin.Spot"/>.</summary>
        public string MarketId { get; }
        /// <summary>
        /// When the price came to be (UTC): the pool's last swap, the market's last trade, the oracle's datapoint. Null when
        /// the source does not say. Compare with <see cref="FetchedAt"/>: a price read now can be hours old.
        /// </summary>
        public DateTime? SourceTime { get; }
        /// <summary>When the kit read it (UTC). A cached answer keeps the time it was read.</summary>
        public DateTime FetchedAt { get; }
        /// <summary>Why there is no value, or <see cref="WaxPriceMissing.None"/>.</summary>
        public WaxPriceMissing Missing { get; }
        /// <summary>A sentence for the developer about a missing value, or null.</summary>
        public string Reason { get; }
        /// <summary>24 h change in percent, when the source gives it.</summary>
        public decimal? Change24h { get; }
        /// <summary>7 day change in percent, when the source gives it.</summary>
        public decimal? Change7d { get; }

        /// <summary>True when <see cref="Value"/> is set.</summary>
        public bool HasValue => Value.HasValue;

        internal WaxPrice(decimal value, string unit, WaxPriceOrigin origin, DateTime? sourceTime, DateTime fetchedAt,
            long? poolId = null, string marketId = null, decimal? change24h = null, decimal? change7d = null)
        {
            Value = value;
            Unit = unit;
            Origin = origin;
            SourceTime = sourceTime;
            FetchedAt = fetchedAt;
            PoolId = poolId;
            MarketId = marketId;
            Change24h = change24h;
            Change7d = change7d;
        }

        internal WaxPrice(string unit, WaxPriceMissing missing, string reason, DateTime fetchedAt, long? poolId = null, string marketId = null)
        {
            Unit = unit;
            Missing = missing;
            Reason = reason;
            FetchedAt = fetchedAt;
            PoolId = poolId;
            MarketId = marketId;
        }

        /// <summary>"1.00697 WAX (pool 12948)" or "no price: …".</summary>
        public override string ToString() => HasValue
            ? $"{Value.Value.ToString(CultureInfo.InvariantCulture)} {Unit} ({Origin}{(PoolId != null ? " " + PoolId : MarketId != null ? " " + MarketId : "")})"
            : $"no price ({Missing}): {Reason}";
    }

    /// <summary>An Alcor swap pool. Amounts are the reserves the pool holds; prices are the pool's current price.</summary>
    public sealed class WaxAlcorPool
    {
        /// <summary>Pool id.</summary>
        public long Id { get; internal set; }
        /// <summary>False for a closed pool.</summary>
        public bool Active { get; internal set; }
        /// <summary>Fee in percent (Alcor's 3000 = 0.3).</summary>
        public decimal FeePercent { get; internal set; }
        /// <summary>Token A.</summary>
        public WaxToken TokenA { get; internal set; }
        /// <summary>Token B.</summary>
        public WaxToken TokenB { get; internal set; }
        /// <summary>Decimals of token A.</summary>
        public int DecimalsA { get; internal set; }
        /// <summary>Decimals of token B.</summary>
        public int DecimalsB { get; internal set; }
        /// <summary>Token A held in the pool (its total reserve, not the liquidity at the current price).</summary>
        public decimal ReserveA { get; internal set; }
        /// <summary>Token B held in the pool.</summary>
        public decimal ReserveB { get; internal set; }
        /// <summary>Price of one A in B.</summary>
        public decimal PriceA { get; internal set; }
        /// <summary>Price of one B in A.</summary>
        public decimal PriceB { get; internal set; }
        /// <summary>24 h price change in percent.</summary>
        public decimal? Change24h { get; internal set; }
        /// <summary>7 day price change in percent.</summary>
        public decimal? Change7d { get; internal set; }
        /// <summary>Total value in the pool in USD, as Alcor computes it.</summary>
        public decimal? TvlUsd { get; internal set; }
        /// <summary>24 h volume in USD.</summary>
        public decimal? VolumeUsd24h { get; internal set; }
        /// <summary>When the kit read it (UTC).</summary>
        public DateTime FetchedAt { get; internal set; }
    }

    /// <summary>An Alcor spot market (order book) of a token against the network coin.</summary>
    public sealed class WaxAlcorMarket
    {
        /// <summary>Ticker id, for example "tlm-alien.worlds_wax-eosio.token".</summary>
        public string TickerId { get; internal set; }
        /// <summary>Alcor market id.</summary>
        public string MarketId { get; internal set; }
        /// <summary>Last trade price in the network coin, or null when there was no trade.</summary>
        public decimal? LastPrice { get; internal set; }
        /// <summary>Time of the last trade (UTC), or null.</summary>
        public DateTime? LastTradeTime { get; internal set; }
        /// <summary>Best bid (highest buy order), or null.</summary>
        public decimal? BestBid { get; internal set; }
        /// <summary>Best ask (lowest sell order), or null.</summary>
        public decimal? BestAsk { get; internal set; }
        /// <summary>24 h change in percent.</summary>
        public decimal? Change24h { get; internal set; }
        /// <summary>24 h volume in the token, as Alcor reports it for this pair.</summary>
        public decimal? Volume24hToken { get; internal set; }
        /// <summary>24 h volume in the network coin, as Alcor reports it for this pair.</summary>
        public decimal? Volume24hCoin { get; internal set; }
        /// <summary>When the kit read it (UTC).</summary>
        public DateTime FetchedAt { get; internal set; }
    }

    /// <summary>One spot trade.</summary>
    public sealed class WaxAlcorTrade
    {
        /// <summary>Price in the network coin per token.</summary>
        public decimal Price { get; internal set; }
        /// <summary>Token amount.</summary>
        public decimal TokenAmount { get; internal set; }
        /// <summary>Network coin amount.</summary>
        public decimal CoinAmount { get; internal set; }
        /// <summary>Trade time (UTC).</summary>
        public DateTime Time { get; internal set; }
        /// <summary>"buy" or "sell" (the taker's side).</summary>
        public string Side { get; internal set; }
        /// <summary>Transaction id.</summary>
        public string TransactionId { get; internal set; }
    }

    /// <summary>Candle length for price history.</summary>
    public enum WaxCandleInterval
    {
        /// <summary>1 minute.</summary>
        Minute1,
        /// <summary>5 minutes.</summary>
        Minute5,
        /// <summary>15 minutes.</summary>
        Minute15,
        /// <summary>30 minutes.</summary>
        Minute30,
        /// <summary>1 hour.</summary>
        Hour1,
        /// <summary>4 hours.</summary>
        Hour4,
        /// <summary>1 day.</summary>
        Day1,
        /// <summary>1 week.</summary>
        Week1,
    }

    /// <summary>One candle of price history.</summary>
    public sealed class WaxCandle
    {
        /// <summary>Start of the candle (UTC).</summary>
        public DateTime Time { get; internal set; }
        /// <summary>Open price.</summary>
        public decimal Open { get; internal set; }
        /// <summary>Highest price.</summary>
        public decimal High { get; internal set; }
        /// <summary>Lowest price.</summary>
        public decimal Low { get; internal set; }
        /// <summary>Close price.</summary>
        public decimal Close { get; internal set; }
        /// <summary>Volume, as Alcor reports it (pools: USD; spot: the network coin).</summary>
        public decimal Volume { get; internal set; }
    }

    /// <summary>A token balance of an account. <see cref="Amount"/> is null when the read failed (see <see cref="Error"/>).</summary>
    public sealed class WaxBalance
    {
        /// <summary>The token.</summary>
        public WaxToken Token { get; }
        /// <summary>The amount (0 when the account holds none), or null when the read failed.</summary>
        public decimal? Amount { get; }
        /// <summary>The token's decimals, or -1 when unknown.</summary>
        public int Precision { get; }
        /// <summary>Why the read failed, or null.</summary>
        public string Error { get; }
        /// <summary>True when <see cref="Amount"/> is set.</summary>
        public bool IsOk => Amount.HasValue;

        internal WaxBalance(WaxToken token, decimal amount, int precision)
        {
            Token = token;
            Amount = amount;
            Precision = precision;
        }

        internal WaxBalance(WaxToken token, string error)
        {
            Token = token;
            Precision = -1;
            Error = error;
        }

        /// <summary>"12.3400 TLM", or the error.</summary>
        public override string ToString() => IsOk
            ? Amount.Value.ToString("F" + Math.Max(0, Precision), CultureInfo.InvariantCulture) + " " + Token.Symbol
            : $"{Token}: {Error}";
    }

    /// <summary>One token of <see cref="WaxKit.GetBalanceValues"/>: its balance and, when it has a price, its value.</summary>
    public sealed class WaxBalanceValue
    {
        /// <summary>The balance.</summary>
        public WaxBalance Balance { get; internal set; }
        /// <summary>The token's price in the network coin (null when the balance read failed).</summary>
        public WaxPrice Price { get; internal set; }
        /// <summary>Amount x price, in the network coin; null without a price.</summary>
        public decimal? CoinValue { get; internal set; }
        /// <summary><see cref="CoinValue"/> x the network coin's USD price; null without either.</summary>
        public decimal? UsdValue { get; internal set; }
        /// <summary>Why there is no value, or null.</summary>
        public string Reason { get; internal set; }
    }

    /// <summary>
    /// The tracked tokens of the logged-in account with their values. Totals add up the priced tokens only: say so on screen
    /// when <see cref="UnpricedCount"/> is not 0.
    /// </summary>
    public sealed class WaxBalanceValues
    {
        /// <summary>One entry per tracked token, in the tracked order.</summary>
        public IReadOnlyList<WaxBalanceValue> Items { get; internal set; }
        /// <summary>Sum of <see cref="WaxBalanceValue.CoinValue"/> of the priced tokens, in the network coin.</summary>
        public decimal CoinTotal { get; internal set; }
        /// <summary>Sum of the USD values of the priced tokens, or null when the network coin has no USD price.</summary>
        public decimal? UsdTotal { get; internal set; }
        /// <summary>The network coin's symbol ("WAX").</summary>
        public string CoinSymbol { get; internal set; }
        /// <summary>Tokens with a value.</summary>
        public int PricedCount { get; internal set; }
        /// <summary>Tokens without a value (no price, or the balance read failed). Not in the totals.</summary>
        public int UnpricedCount { get; internal set; }
        /// <summary>The network coin's USD price used.</summary>
        public WaxPrice CoreUsdPrice { get; internal set; }
    }
}
