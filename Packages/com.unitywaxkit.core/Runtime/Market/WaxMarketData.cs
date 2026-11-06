using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityWaxKit.Chain;
using UnityWaxKit.Platform;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit
{
    /// <summary>
    /// Market data and balances for one network: Alcor reads (cache, in-flight dedupe, 429 wait and retry), on-chain oracle
    /// reads and token balances. Amounts and prices are decimals parsed from the answer's text, never floats.
    /// </summary>
    internal sealed class WaxMarketData
    {
        internal const int MaxRequests = 3;
        internal const int MaxWaitMilliseconds = 10_000;

        // Token precisions from each token's stat, per chain, for the session.
        static readonly Dictionary<string, int> Precisions = new Dictionary<string, int>();

        readonly WaxNetwork network;
        readonly WaxMarketSettings settings;
        readonly Dictionary<string, Answer> cache = new Dictionary<string, Answer>();
        readonly Dictionary<string, Task<Answer>> inFlight = new Dictionary<string, Task<Answer>>();
        readonly Dictionary<string, WaxEndpointList> alcorLists = new Dictionary<string, WaxEndpointList>();

        /// <summary>The clock (tests replace it).</summary>
        internal Func<DateTime> Now = () => DateTime.UtcNow;

        internal WaxMarketData(WaxNetwork network, WaxMarketSettings settings)
        {
            this.network = network;
            this.settings = settings;
        }

        internal WaxNetwork Network => network;

        /// <summary>Forgets the token and oracle precisions kept for the session (tests).</summary>
        internal static void ClearSessionCaches()
        {
            lock (Precisions) Precisions.Clear();
        }

        /// <summary>The network coin (eosio.token and the network's core symbol), or null when the network does not say.</summary>
        internal static WaxToken CoinOf(WaxNetwork network) =>
            network != null && WaxToken.IsSymbolCode(network.CoreSymbol) && network.CorePrecision >= 0
                ? new WaxToken("eosio.token", network.CoreSymbol)
                : null;

        internal sealed class Answer
        {
            public long Status;
            public string Text;
            public DateTime FetchedAt;
        }

        // ---- Alcor requests ----

        /// <summary>
        /// GET an Alcor path: a cached answer when it is younger than <see cref="WaxMarketSettings.CacheSeconds"/>, the same
        /// request when one is already on its way, else a new one. Returns 2xx and 4xx answers; throws for the rest.
        /// </summary>
        internal async Task<Answer> Alcor(string path, WaxAnswerShape shape, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!settings.AlcorEnabled)
                throw new WaxKitException(WaxErrorKind.NotSupported, "Alcor is off (WaxKit.MarketSettings.AlcorEnabled).");
            var host = settings.GetAlcorHost(network.ChainId) ?? throw new WaxKitException(WaxErrorKind.NotSupported,
                $"No Alcor host is set for {network.Name} (chain id {network.ChainId.Substring(0, 8)}…). Testnets have no prices by default.");

            var key = host + path;
            Task<Answer> task;
            lock (cache)
            {
                if (cache.TryGetValue(key, out var cached) && (Now() - cached.FetchedAt).TotalSeconds < settings.CacheSeconds) return cached;
                if (!inFlight.TryGetValue(key, out task))
                {
                    task = FetchAlcor(host, path, shape);
                    inFlight[key] = task;
                    task.ContinueWith(t =>
                    {
                        lock (cache)
                        {
                            inFlight.Remove(key);
                            if (t.Status == TaskStatus.RanToCompletion) cache[key] = t.Result;
                        }
                    }, TaskContinuationOptions.ExecuteSynchronously);
                }
            }
            return await WithCancellation(task, cancellation);
        }

        /// <summary>One Alcor read; on 429 waits (Retry-After when given) and tries again: at most 3 requests and 10 s waiting.</summary>
        async Task<Answer> FetchAlcor(string host, string path, WaxAnswerShape shape)
        {
            WaxEndpointList list;
            lock (alcorLists)
            {
                if (!alcorLists.TryGetValue(host, out list)) alcorLists[host] = list = new WaxEndpointList(new[] { host }, "Alcor");
            }

            var waited = 0;
            for (var request = 1; ; request++)
            {
                // The shared request is not stopped by one caller's cancellation; each caller stops waiting for it instead.
                var response = await list.Send("GET", path, null, CancellationToken.None, null, shape, returnRateLimit: true);
                if (response.Status != 429) return new Answer { Status = response.Status, Text = response.Text, FetchedAt = Now() };

                if (request >= MaxRequests)
                    throw new WaxKitException(WaxErrorKind.Network, $"Alcor rate limit (HTTP 429) after {MaxRequests} requests: try again later.");
                var wait = RetryAfterMilliseconds(response) ?? 1000 * request;
                if (waited + wait > MaxWaitMilliseconds)
                    throw new WaxKitException(WaxErrorKind.Network,
                        $"Alcor rate limit (HTTP 429): it asks to wait {wait / 1000.0:0.#} s, more than the kit waits ({MaxWaitMilliseconds / 1000} s in all). Try again later.");
                await WaxPlatform.Current.Delay(wait, CancellationToken.None);
                waited += wait;
            }
        }

        /// <summary>Longest Retry-After the kit reads; anything longer is far over <see cref="MaxWaitMilliseconds"/> anyway.</summary>
        const int MaxRetryAfterMilliseconds = int.MaxValue / 2;

        int? RetryAfterMilliseconds(WaxHttpResponse response)
        {
            if (!response.Headers.TryGetValue("Retry-After", out var value) || string.IsNullOrWhiteSpace(value)) return null;
            var text = value.Trim();
            // Seconds are converted in long and capped, so a large value never wraps around to a short or negative wait.
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                return seconds <= 0 ? 0 : seconds >= MaxRetryAfterMilliseconds / 1000 ? MaxRetryAfterMilliseconds : (int)(seconds * 1000);
            if (IsDigits(text)) return MaxRetryAfterMilliseconds; // more seconds than a long holds
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                return (int)Math.Max(0, Math.Min(MaxRetryAfterMilliseconds, (at - Now()).TotalMilliseconds));
            return null;
        }

        static bool IsDigits(string text)
        {
            foreach (var c in text) if (c < '0' || c > '9') return false;
            return text.Length > 0;
        }

        static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken cancellation)
        {
            if (!cancellation.CanBeCanceled || task.IsCompleted) return await task;
            var cancelled = new TaskCompletionSource<bool>();
            using (cancellation.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task) != task)
                {
                    // Observed, so an abandoned failure is not reported as unobserved.
                    _ = task.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    throw new OperationCanceledException(cancellation);
                }
            }
            return await task;
        }

        static string AlcorError(string what, Answer answer) => $"Alcor {what}: HTTP {answer.Status} {Shorten(answer.Text)}";

        static string Shorten(string text) => text == null ? "" : text.Length > 200 ? text.Substring(0, 200) + "…" : text;

        // ---- Alcor data ----

        internal async Task<WaxAlcorPool> GetPool(long poolId, CancellationToken cancellation)
        {
            if (poolId < 0) throw new ArgumentException("A pool id cannot be negative.", nameof(poolId));
            var answer = await Alcor($"/v2/swap/pools/{poolId}", WaxAnswerShape.ObjectOrNull, cancellation);
            if (answer.Status == 404) return null;
            if (answer.Status / 100 != 2) throw new WaxKitException(WaxErrorKind.Network, AlcorError($"pool {poolId}", answer));
            var json = Parse(answer.Text);
            return json is JObject o ? ParsePool(o, answer.FetchedAt) : null;
        }

        /// <summary>The pools of a pair, in either order (Alcor's tokenA / tokenB filter; never the full pool list).</summary>
        internal async Task<List<WaxAlcorPool>> GetPairPools(WaxToken a, WaxToken b, CancellationToken cancellation)
        {
            var answer = await Alcor($"/v2/swap/pools?tokenA={Uri.EscapeDataString(a.AlcorId)}&tokenB={Uri.EscapeDataString(b.AlcorId)}",
                WaxAnswerShape.Array, cancellation);
            if (answer.Status / 100 != 2) throw new WaxKitException(WaxErrorKind.Network, AlcorError($"pools of {a} / {b}", answer));
            return ((JArray)Parse(answer.Text)).OfType<JObject>()
                .Select(o => ParsePool(o, answer.FetchedAt))
                .Where(p => p != null && IsPairOf(p, a, b))
                .ToList();
        }

        /// <summary>Time of the pool's newest swap, or null when it has none.</summary>
        internal async Task<DateTime?> LastSwapTime(long poolId, CancellationToken cancellation)
        {
            var answer = await Alcor($"/v2/swap/pools/{poolId}/swaps?limit=1", WaxAnswerShape.Array, cancellation);
            if (answer.Status / 100 != 2) throw new WaxKitException(WaxErrorKind.Network, AlcorError($"swaps of pool {poolId}", answer));
            var swap = ((JArray)Parse(answer.Text)).OfType<JObject>().FirstOrDefault();
            return swap == null ? (DateTime?)null : IsoTime((string)swap["time"]);
        }

        /// <summary>The spot market of the token against the network coin, or null when Alcor has none.</summary>
        internal async Task<WaxAlcorMarket> GetMarket(WaxToken token, CancellationToken cancellation)
        {
            var coin = RequireCoin();
            var tickerId = token.AlcorId + "_" + coin.AlcorId;
            var answer = await Alcor($"/v2/tickers/{Uri.EscapeDataString(tickerId)}", WaxAnswerShape.Object, cancellation);
            if (answer.Status == 404) return null;
            if (answer.Status / 100 != 2) throw new WaxKitException(WaxErrorKind.Network, AlcorError($"market {tickerId}", answer));
            var ticker = (JObject)Parse(answer.Text);

            var trades = await GetTrades(token, 1, cancellation);
            var last = trades.FirstOrDefault();
            return new WaxAlcorMarket
            {
                TickerId = tickerId,
                MarketId = (string)ticker["market_id"],
                LastPrice = last?.Price,
                LastTradeTime = last?.Time,
                BestBid = Positive(Dec(ticker["bid"])),
                BestAsk = Positive(Dec(ticker["ask"])),
                Change24h = Dec(ticker["change24"]),
                Volume24hToken = Dec(ticker["base_volume"]),
                Volume24hCoin = Dec(ticker["target_volume"]),
                FetchedAt = answer.FetchedAt,
            };
        }

        /// <summary>The newest spot trades of the token against the network coin, newest first.</summary>
        internal async Task<List<WaxAlcorTrade>> GetTrades(WaxToken token, int limit, CancellationToken cancellation)
        {
            if (limit < 1 || limit > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "1 to 1000.");
            var tickerId = token.AlcorId + "_" + RequireCoin().AlcorId;
            var answer = await Alcor($"/v2/tickers/{Uri.EscapeDataString(tickerId)}/historical_trades?limit={limit}", WaxAnswerShape.Array, cancellation);
            if (answer.Status == 404) return new List<WaxAlcorTrade>();
            if (answer.Status / 100 != 2) throw new WaxKitException(WaxErrorKind.Network, AlcorError($"trades of {tickerId}", answer));
            // Alcor answers the newest trades, oldest first.
            return ((JArray)Parse(answer.Text)).OfType<JObject>().Select(t => new WaxAlcorTrade
                {
                    Price = Dec(t["price"]) ?? 0,
                    TokenAmount = Dec(t["base_volume"]) ?? 0,
                    CoinAmount = Dec(t["target_volume"]) ?? 0,
                    Time = UnixSeconds(t["trade_timestamp"]) ?? default,
                    Side = (string)t["type"],
                    TransactionId = (string)t["trx_id"],
                })
                .OrderByDescending(t => t.Time)
                .ToList();
        }

        internal async Task<List<WaxCandle>> GetPoolCandles(long poolId, WaxCandleInterval interval, int count, CancellationToken cancellation)
        {
            var (resolution, span) = Resolution(interval, count);
            var to = Now();
            var answer = await Alcor($"/v2/swap/pools/{poolId}/candles?resolution={resolution}&from={UnixMs(to - span)}&to={UnixMs(to)}&limit={count}",
                WaxAnswerShape.Array, cancellation);
            if (answer.Status / 100 != 2) throw new WaxKitException(WaxErrorKind.Network, AlcorError($"candles of pool {poolId}", answer));
            return ParseCandles(answer.Text, count);
        }

        internal async Task<List<WaxCandle>> GetMarketCandles(WaxToken token, WaxCandleInterval interval, int count, CancellationToken cancellation)
        {
            var (resolution, span) = Resolution(interval, count);
            var tickerId = token.AlcorId + "_" + RequireCoin().AlcorId;
            var to = Now();
            var answer = await Alcor($"/v2/tickers/{Uri.EscapeDataString(tickerId)}/charts?resolution={resolution}&from={UnixMs(to - span)}&to={UnixMs(to)}&limit={count}",
                WaxAnswerShape.Array, cancellation);
            if (answer.Status == 404) return new List<WaxCandle>();
            if (answer.Status / 100 != 2) throw new WaxKitException(WaxErrorKind.Network, AlcorError($"candles of {tickerId}", answer));
            return ParseCandles(answer.Text, count);
        }

        static (string resolution, TimeSpan span) Resolution(WaxCandleInterval interval, int count)
        {
            if (count < 1 || count > 5000) throw new ArgumentOutOfRangeException(nameof(count), "1 to 5000.");
            switch (interval)
            {
                case WaxCandleInterval.Minute1: return ("1", TimeSpan.FromMinutes(count));
                case WaxCandleInterval.Minute5: return ("5", TimeSpan.FromMinutes(5 * count));
                case WaxCandleInterval.Minute15: return ("15", TimeSpan.FromMinutes(15 * count));
                case WaxCandleInterval.Minute30: return ("30", TimeSpan.FromMinutes(30 * count));
                case WaxCandleInterval.Hour1: return ("60", TimeSpan.FromHours(count));
                case WaxCandleInterval.Hour4: return ("240", TimeSpan.FromHours(4 * count));
                case WaxCandleInterval.Day1: return ("1D", TimeSpan.FromDays(count));
                case WaxCandleInterval.Week1: return ("1W", TimeSpan.FromDays(7 * count));
                default: throw new ArgumentOutOfRangeException(nameof(interval));
            }
        }

        /// <summary>Candles oldest first; at most the newest <paramref name="count"/>.</summary>
        static List<WaxCandle> ParseCandles(string text, int count)
        {
            var list = ((JArray)Parse(text)).OfType<JObject>().Select(c => new WaxCandle
                {
                    Time = UnixMilliseconds(c["time"]) ?? default,
                    Open = Dec(c["open"]) ?? 0,
                    High = Dec(c["high"]) ?? 0,
                    Low = Dec(c["low"]) ?? 0,
                    Close = Dec(c["close"]) ?? 0,
                    Volume = Dec(c["volume"]) ?? 0,
                })
                .OrderBy(c => c.Time)
                .ToList();
            return list.Count > count ? list.Skip(list.Count - count).ToList() : list;
        }

        // ---- Prices ----

        /// <summary>
        /// The token's price in the network coin. Never throws for a missing price or a failed request (the result says
        /// why); throws only <see cref="OperationCanceledException"/> when the caller cancels.
        /// </summary>
        internal async Task<WaxPrice> GetPrice(WaxToken token, CancellationToken cancellation)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));
            var coin = CoinOf(network);
            var unit = coin?.Symbol;
            if (coin == null)
                return new WaxPrice(null, WaxPriceMissing.NotSupported, $"{network.Name} has no core symbol and precision, so it has no network coin to price in.", Now());
            if (token.SameToken(coin)) return new WaxPrice(1m, unit, WaxPriceOrigin.NetworkCoin, null, Now());
            if (!settings.AlcorEnabled) return new WaxPrice(unit, WaxPriceMissing.SourceOff, "Alcor is off (WaxKit.MarketSettings.AlcorEnabled).", Now());
            if (settings.GetAlcorHost(network.ChainId) == null)
                return new WaxPrice(unit, WaxPriceMissing.NotSupported, $"No price source is set for {network.Name} (testnets have no prices by default).", Now());

            var poolId = token.PoolId ?? settings.PoolIdFor(token.Contract, token.Symbol);
            try
            {
                if (poolId != null) return await SetPoolPrice(token, coin, poolId.Value, cancellation);
                if (settings.PriceSource == WaxPriceSource.SpotOnly) return await SpotPrice(token, coin, cancellation);

                var pool = await BestPoolPrice(token, coin, cancellation);
                if (pool.HasValue || settings.PriceSource == WaxPriceSource.PoolOnly) return pool;

                var spot = await SpotPrice(token, coin, cancellation);
                if (spot.HasValue) return spot;
                // Neither: the most useful reason.
                if (pool.Missing == WaxPriceMissing.NoPool && spot.Missing == WaxPriceMissing.NoDirectPair)
                    return new WaxPrice(unit, WaxPriceMissing.NoDirectPair,
                        $"No Alcor pool or spot market pairs {token} directly with {unit} (prices through other tokens are not computed).", spot.FetchedAt);
                return spot.Missing == WaxPriceMissing.NoDirectPair ? pool : spot;
            }
            catch (WaxKitException e)
            {
                cancellation.ThrowIfCancellationRequested();
                return new WaxPrice(unit, WaxPriceMissing.RequestFailed, e.Message, Now(), poolId);
            }
        }

        async Task<WaxPrice> SetPoolPrice(WaxToken token, WaxToken coin, long poolId, CancellationToken cancellation)
        {
            var pool = await GetPool(poolId, cancellation);
            if (pool == null) return new WaxPrice(coin.Symbol, WaxPriceMissing.PoolNotFound, $"Alcor has no pool {poolId} (the pool id set for {token}).", Now(), poolId);
            if (!IsPairOf(pool, token, coin))
                return new WaxPrice(coin.Symbol, WaxPriceMissing.WrongPool,
                    $"Pool {poolId} is {pool.TokenA} / {pool.TokenB}, not {token} / {coin}. The kit does not switch to another pool.", pool.FetchedAt, poolId);
            if (IsEmpty(pool))
                return new WaxPrice(coin.Symbol, WaxPriceMissing.EmptyPool, $"Pool {poolId} is {(pool.Active ? "empty" : "inactive")}. The kit does not switch to another pool.", pool.FetchedAt, poolId);
            return await PriceFromPool(pool, token, coin, cancellation);
        }

        async Task<WaxPrice> BestPoolPrice(WaxToken token, WaxToken coin, CancellationToken cancellation)
        {
            var pools = await GetPairPools(token, coin, cancellation);
            var fetchedAt = pools.Count > 0 ? pools[0].FetchedAt : Now();
            if (pools.Count == 0) return new WaxPrice(coin.Symbol, WaxPriceMissing.NoPool, $"No Alcor pool pairs {token} directly with {coin.Symbol}.", fetchedAt);
            // The pool with the largest network-coin reserve; tie: the lower fee, then the lower id.
            var best = pools.Where(p => !IsEmpty(p))
                .OrderByDescending(p => CoinReserve(p, coin))
                .ThenBy(p => p.FeePercent)
                .ThenBy(p => p.Id)
                .FirstOrDefault();
            if (best == null) return new WaxPrice(coin.Symbol, WaxPriceMissing.EmptyPool, $"Every Alcor pool of {token} / {coin.Symbol} is empty or inactive.", fetchedAt);
            return await PriceFromPool(best, token, coin, cancellation);
        }

        async Task<WaxPrice> PriceFromPool(WaxAlcorPool pool, WaxToken token, WaxToken coin, CancellationToken cancellation)
        {
            var price = pool.TokenA.SameToken(token) ? pool.PriceA : pool.PriceB;
            if (price <= 0) return new WaxPrice(coin.Symbol, WaxPriceMissing.EmptyPool, $"Pool {pool.Id} has no price.", pool.FetchedAt, pool.Id);
            var sourceTime = await LastSwapTime(pool.Id, cancellation);
            // Alcor's change is of token A in B.
            var reversed = !pool.TokenA.SameToken(token);
            return new WaxPrice(price, coin.Symbol, WaxPriceOrigin.Pool, sourceTime, pool.FetchedAt, pool.Id,
                change24h: reversed ? null : pool.Change24h, change7d: reversed ? null : pool.Change7d);
        }

        async Task<WaxPrice> SpotPrice(WaxToken token, WaxToken coin, CancellationToken cancellation)
        {
            var market = await GetMarket(token, cancellation);
            if (market == null)
                return new WaxPrice(coin.Symbol, WaxPriceMissing.NoDirectPair, $"No Alcor spot market of {token} against {coin.Symbol}.", Now());
            if (market.LastPrice == null || market.LastPrice <= 0)
                return new WaxPrice(coin.Symbol, WaxPriceMissing.NoTrades, $"The Alcor spot market {market.TickerId} has no trade.", market.FetchedAt, marketId: market.TickerId);
            return new WaxPrice(market.LastPrice.Value, coin.Symbol, WaxPriceOrigin.Spot, market.LastTradeTime, market.FetchedAt,
                marketId: market.TickerId, change24h: market.Change24h);
        }

        static bool IsPairOf(WaxAlcorPool pool, WaxToken a, WaxToken b) =>
            (pool.TokenA.SameToken(a) && pool.TokenB.SameToken(b)) || (pool.TokenA.SameToken(b) && pool.TokenB.SameToken(a));

        static bool IsEmpty(WaxAlcorPool pool) => !pool.Active || pool.ReserveA <= 0 || pool.ReserveB <= 0;

        static decimal CoinReserve(WaxAlcorPool pool, WaxToken coin) => pool.TokenA.SameToken(coin) ? pool.ReserveA : pool.ReserveB;

        // ---- Network coin in USD ----

        internal async Task<WaxPrice> GetCoreUsdPrice(CancellationToken cancellation)
        {
            var source = settings.CoreUsdSource;
            WaxPrice oracle = null;
            if (source != WaxUsdSource.AlcorOnly)
            {
                oracle = await OracleUsdPrice(cancellation);
                if (oracle.HasValue || source == WaxUsdSource.OracleOnly) return oracle;
            }
            var alcor = await AlcorUsdPrice(cancellation);
            if (alcor.HasValue || oracle == null) return alcor;
            var missing = oracle.Missing == WaxPriceMissing.NotSupported && alcor.Missing == WaxPriceMissing.NotSupported
                ? WaxPriceMissing.NotSupported : alcor.Missing == WaxPriceMissing.NotSupported ? oracle.Missing : alcor.Missing;
            return new WaxPrice("USD", missing, $"Oracle: {oracle.Reason} Alcor: {alcor.Reason}", alcor.FetchedAt);
        }

        internal async Task<WaxPrice> OracleUsdPrice(CancellationToken cancellation)
        {
            var oracle = settings.GetOracle(network.ChainId);
            if (oracle == null) return new WaxPrice("USD", WaxPriceMissing.NotSupported, $"No price oracle is known for {network.Name}.", Now());
            var (contract, pair) = oracle.Value;
            try
            {
                var precision = await OraclePrecision(contract, pair, cancellation);
                var (rows, fetchedAt) = await CachedTableRows(new JObject
                {
                    ["json"] = true, ["code"] = contract, ["scope"] = pair, ["table"] = "datapoints", ["limit"] = 100,
                }, cancellation);
                var newest = rows.OfType<JObject>()
                    .Select(r => (row: r, time: IsoTime((string)r["timestamp"])))
                    .Where(r => r.time != null)
                    .OrderByDescending(r => r.time)
                    .FirstOrDefault();
                var median = newest.row == null ? null : Dec(newest.row["median"]);
                if (median == null)
                    return new WaxPrice("USD", WaxPriceMissing.RequestFailed, $"The oracle {contract} has no datapoint for {pair}.", fetchedAt);
                var time = newest.time.Value;
                if (fetchedAt - time > settings.OracleMaxAge)
                    return new WaxPrice("USD", WaxPriceMissing.Stale,
                        $"The oracle's newest {pair} value is from {time:yyyy-MM-dd HH:mm} UTC, older than {settings.OracleMaxAge.TotalMinutes:0} minutes.", fetchedAt);
                return new WaxPrice(median.Value / Pow10(precision), "USD", WaxPriceOrigin.Oracle, time, fetchedAt);
            }
            catch (WaxKitException e)
            {
                cancellation.ThrowIfCancellationRequested();
                return new WaxPrice("USD", WaxPriceMissing.RequestFailed, $"Oracle {contract} {pair}: {e.Message}", Now());
            }
        }

        async Task<int> OraclePrecision(string contract, string pair, CancellationToken cancellation)
        {
            var key = network.ChainId + "|oracle|" + contract + "|" + pair;
            lock (Precisions)
            {
                if (Precisions.TryGetValue(key, out var known)) return known;
            }
            var (rows, _) = await CachedTableRows(new JObject
            {
                ["json"] = true, ["code"] = contract, ["scope"] = contract, ["table"] = "pairs",
                ["lower_bound"] = pair, ["upper_bound"] = pair, ["limit"] = 1,
            }, cancellation);
            var row = rows.OfType<JObject>().FirstOrDefault(r => (string)r["name"] == pair)
                      ?? throw new WaxKitException(WaxErrorKind.Network, $"the oracle has no pair '{pair}'.");
            var precision = (int?)row["quoted_precision"] ?? throw new WaxKitException(WaxErrorKind.Network, $"pair '{pair}' has no quoted_precision.");
            if (precision < 0 || precision > 18) throw new WaxKitException(WaxErrorKind.Network, $"pair '{pair}' has an unexpected precision {precision}.");
            lock (Precisions) Precisions[key] = precision;
            return precision;
        }

        /// <summary>get_table_rows through the RPC endpoints, cached like Alcor answers.</summary>
        async Task<(JArray rows, DateTime fetchedAt)> CachedTableRows(JObject query, CancellationToken cancellation)
        {
            var body = query.ToString(Formatting.None);
            var key = "rpc:" + network.ChainId + body;
            lock (cache)
            {
                if (cache.TryGetValue(key, out var cached) && (Now() - cached.FetchedAt).TotalSeconds < settings.CacheSeconds)
                    return ((JArray)((JObject)Parse(cached.Text))["rows"] ?? new JArray(), cached.FetchedAt);
            }
            var text = await WaxChainApi.PostText(network, "/v1/chain/get_table_rows", body, WaxAnswerShape.Object, cancellation);
            var answer = new Answer { Status = 200, Text = text, FetchedAt = Now() };
            lock (cache) cache[key] = answer;
            return (((JObject)Parse(text))["rows"] as JArray ?? new JArray(), answer.FetchedAt);
        }

        internal async Task<WaxPrice> AlcorUsdPrice(CancellationToken cancellation)
        {
            var coin = CoinOf(network);
            if (coin == null) return new WaxPrice("USD", WaxPriceMissing.NotSupported, $"{network.Name} has no core symbol.", Now());
            if (!settings.AlcorEnabled) return new WaxPrice("USD", WaxPriceMissing.SourceOff, "Alcor is off (WaxKit.MarketSettings.AlcorEnabled).", Now());
            if (settings.GetAlcorHost(network.ChainId) == null)
                return new WaxPrice("USD", WaxPriceMissing.NotSupported, $"No Alcor host is set for {network.Name} (testnets have no prices by default).", Now());
            try
            {
                var answer = await Alcor($"/v2/tokens/{Uri.EscapeDataString(coin.AlcorId)}", WaxAnswerShape.Object, cancellation);
                if (answer.Status / 100 != 2) throw new WaxKitException(WaxErrorKind.Network, AlcorError($"token {coin.AlcorId}", answer));
                var usd = Positive(Dec(((JObject)Parse(answer.Text))["usd_price"]));
                return usd == null
                    ? new WaxPrice("USD", WaxPriceMissing.RequestFailed, $"Alcor gives no USD price for {coin.Symbol}.", answer.FetchedAt)
                    : new WaxPrice(usd.Value, "USD", WaxPriceOrigin.AlcorUsd, null, answer.FetchedAt);
            }
            catch (WaxKitException e)
            {
                cancellation.ThrowIfCancellationRequested();
                return new WaxPrice("USD", WaxPriceMissing.RequestFailed, e.Message, Now());
            }
        }

        // ---- Balances ----

        /// <summary>One token balance; throws <see cref="WaxKitException"/> when it cannot be read.</summary>
        internal async Task<WaxBalance> GetBalance(string account, WaxToken token, CancellationToken cancellation)
        {
            if (!WaxToken.IsAccountName(account)) throw new ArgumentException($"'{account}' is not an account name.", nameof(account));
            if (token == null) throw new ArgumentNullException(nameof(token));
            var precision = await Precision(token, cancellation);
            var text = await WaxChainApi.PostText(network, "/v1/chain/get_currency_balance",
                new JObject { ["code"] = token.Contract, ["account"] = account.Trim(), ["symbol"] = token.Symbol }.ToString(Formatting.None),
                WaxAnswerShape.Array, cancellation);
            foreach (var item in (JArray)Parse(text))
            {
                var (amount, symbol, _) = ParseAsset((string)item);
                if (symbol == token.Symbol) return new WaxBalance(token, amount, precision);
            }
            // Holding none: 0 with the token's precision.
            return new WaxBalance(token, 0m, precision);
        }

        /// <summary>Balances of several tokens; a token that fails gets its own error. Cancelling stops the whole call.</summary>
        internal async Task<List<WaxBalance>> GetBalances(string account, IEnumerable<WaxToken> tokens, CancellationToken cancellation)
        {
            if (!WaxToken.IsAccountName(account)) throw new ArgumentException($"'{account}' is not an account name.", nameof(account));
            var list = (tokens ?? throw new ArgumentNullException(nameof(tokens))).ToList();
            var result = new List<WaxBalance>();
            foreach (var token in list)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    result.Add(await GetBalance(account, token, cancellation));
                }
                catch (Exception e) when (!(e is OperationCanceledException) && !(e is ArgumentNullException))
                {
                    cancellation.ThrowIfCancellationRequested();
                    result.Add(new WaxBalance(token, e.Message));
                }
            }
            return result;
        }

        /// <summary>The token's decimals from its stat (cached); throws when the token does not exist on this chain.</summary>
        async Task<int> Precision(WaxToken token, CancellationToken cancellation)
        {
            var key = network.ChainId + "|" + token.Contract + "|" + token.Symbol;
            lock (Precisions)
            {
                if (Precisions.TryGetValue(key, out var known)) return known;
            }
            var response = await network.Rpc.Send("POST", "/v1/chain/get_currency_stats",
                new JObject { ["code"] = token.Contract, ["symbol"] = token.Symbol }.ToString(Formatting.None),
                cancellation, WaxChainApi.IsNodeosError, WaxAnswerShape.Object);
            var notFound = $"The token {token} does not exist on {network.Name}.";
            if (!response.IsSuccess)
            {
                var (name, message) = WaxChainApi.NodeosError(response);
                // An account without a contract (or without this table) is a token that does not exist here.
                if (name == "account_query_exception" || name == "contract_table_query_exception" || name == "abi_not_found_exception")
                    throw new WaxKitException(WaxErrorKind.NotSupported, notFound);
                throw new WaxKitException(WaxErrorKind.Network, $"/v1/chain/get_currency_stats: {message}");
            }
            var stat = ((JObject)Parse(response.Text))[token.Symbol] as JObject ?? throw new WaxKitException(WaxErrorKind.NotSupported, notFound);
            var (_, symbol, precision) = ParseAsset((string)stat["supply"] ?? (string)stat["max_supply"]);
            if (symbol != token.Symbol) throw new WaxKitException(WaxErrorKind.NotSupported, notFound);
            lock (Precisions) Precisions[key] = precision;
            return precision;
        }

        // ---- Values ----

        internal async Task<WaxBalanceValues> GetBalanceValues(string account, CancellationToken cancellation)
        {
            var coin = CoinOf(network);
            var balances = await GetBalances(account, settings.TokensFor(network), cancellation);
            var coreUsd = await GetCoreUsdPrice(cancellation);
            var items = new List<WaxBalanceValue>();
            decimal coinTotal = 0, usdTotal = 0;
            int priced = 0, unpriced = 0;
            foreach (var balance in balances)
            {
                var item = new WaxBalanceValue { Balance = balance };
                if (!balance.IsOk)
                {
                    item.Reason = balance.Error;
                }
                else
                {
                    item.Price = await GetPrice(balance.Token, cancellation);
                    if (item.Price.HasValue)
                    {
                        item.CoinValue = balance.Amount.Value * item.Price.Value.Value;
                        if (coreUsd.HasValue) item.UsdValue = item.CoinValue * coreUsd.Value.Value;
                    }
                    else item.Reason = item.Price.Reason;
                }
                if (item.CoinValue.HasValue)
                {
                    priced++;
                    coinTotal += item.CoinValue.Value;
                    usdTotal += item.UsdValue ?? 0;
                }
                else unpriced++;
                items.Add(item);
            }
            return new WaxBalanceValues
            {
                Items = items,
                CoinTotal = coinTotal,
                UsdTotal = coreUsd.HasValue ? usdTotal : (decimal?)null,
                CoinSymbol = coin?.Symbol,
                PricedCount = priced,
                UnpricedCount = unpriced,
                CoreUsdPrice = coreUsd,
            };
        }

        // ---- Parsing ----

        WaxToken RequireCoin() => CoinOf(network) ?? throw new WaxKitException(WaxErrorKind.NotSupported,
            $"{network.Name} has no core symbol and precision (WaxNetwork.CoreSymbol), so it has no network coin.");

        /// <summary>JSON with every number as a decimal (never a double) and dates left as text.</summary>
        internal static JToken Parse(string text)
        {
            using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal };
            return JToken.ReadFrom(reader);
        }

        internal static decimal? Dec(JToken token)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return token.Value<decimal>();
                case JTokenType.String:
                    return decimal.TryParse((string)token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (decimal?)null;
                default:
                    return null;
            }
        }

        static decimal? Positive(decimal? value) => value > 0 ? value : null;

        static WaxAlcorPool ParsePool(JObject o, DateTime fetchedAt)
        {
            try
            {
                var a = (JObject)o["tokenA"];
                var b = (JObject)o["tokenB"];
                return new WaxAlcorPool
                {
                    Id = (long)o["id"],
                    Active = (bool?)o["active"] ?? false,
                    FeePercent = (Dec(o["fee"]) ?? 0) / 10000m,
                    TokenA = new WaxToken((string)a["contract"], (string)a["symbol"]),
                    TokenB = new WaxToken((string)b["contract"], (string)b["symbol"]),
                    DecimalsA = (int?)a["decimals"] ?? -1,
                    DecimalsB = (int?)b["decimals"] ?? -1,
                    ReserveA = Dec(a["quantity"]) ?? 0,
                    ReserveB = Dec(b["quantity"]) ?? 0,
                    PriceA = Dec(o["priceA"]) ?? 0,
                    PriceB = Dec(o["priceB"]) ?? 0,
                    Change24h = Dec(o["change24"]),
                    Change7d = Dec(o["changeWeek"]),
                    TvlUsd = Dec(o["tvlUSD"]),
                    VolumeUsd24h = Dec(o["volumeUSD24"]),
                    FetchedAt = fetchedAt,
                };
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidCastException || e is NullReferenceException || e is FormatException)
            {
                // A pool entry the kit cannot read is left out.
                return null;
            }
        }

        /// <summary>"12.3400 TLM" → (12.34, "TLM", 4).</summary>
        internal static (decimal amount, string symbol, int precision) ParseAsset(string asset)
        {
            var parts = (asset ?? "").Trim().Split(' ');
            if (parts.Length != 2 || !decimal.TryParse(parts[0], NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
                throw new WaxKitException(WaxErrorKind.Network, $"'{asset}' is not a token amount.");
            var dot = parts[0].IndexOf('.');
            return (amount, parts[1], dot < 0 ? 0 : parts[0].Length - dot - 1);
        }

        static decimal Pow10(int n)
        {
            var value = 1m;
            for (var i = 0; i < n; i++) value *= 10m;
            return value;
        }

        static DateTime? IsoTime(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
                ? time
                : (DateTime?)null;
        }

        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static DateTime? UnixSeconds(JToken token) => Dec(token) is decimal s ? Epoch.AddSeconds((double)s) : (DateTime?)null;

        static DateTime? UnixMilliseconds(JToken token) => Dec(token) is decimal ms ? Epoch.AddMilliseconds((double)ms) : (DateTime?)null;

        static long UnixMs(DateTime time) => (long)(time - Epoch).TotalMilliseconds;
    }
}
