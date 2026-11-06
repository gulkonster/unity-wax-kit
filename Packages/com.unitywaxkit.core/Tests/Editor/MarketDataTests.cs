using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityWaxKit.Platform;

namespace UnityWaxKit.Tests
{
    /// <summary>
    /// Prices, oracle, balances and values with recorded answers (no network). The fake platform answers at once, so the
    /// kit's async calls finish inline.
    /// </summary>
    public class MarketDataTests
    {
        const string Rpc = "https://rpc.test";
        static readonly DateTime Clock = new DateTime(2026, 11, 4, 16, 0, 0, DateTimeKind.Utc);

        static readonly WaxToken Crw = new WaxToken("chickentoken", "CRW");
        static readonly WaxToken Tlm = new WaxToken("alien.worlds", "TLM");

        // Recorded Alcor answers (shortened to the fields the kit reads, values as Alcor sends them).
        static string Pool(long id, decimal fee, string aContract, string aSymbol, string aQty, string bContract, string bSymbol, string bQty,
            string priceA, string priceB, bool active = true) =>
            $"{{\"id\":{id},\"active\":{(active ? "true" : "false")},\"fee\":{fee},\"priceA\":{priceA},\"priceB\":{priceB},\"change24\":-1.5,\"changeWeek\":3," +
            $"\"tokenA\":{{\"contract\":\"{aContract}\",\"symbol\":\"{aSymbol}\",\"decimals\":4,\"quantity\":{aQty}}}," +
            $"\"tokenB\":{{\"contract\":\"{bContract}\",\"symbol\":\"{bSymbol}\",\"decimals\":8,\"quantity\":{bQty}}}}}";

        static string TlmPool(long id, int fee, string waxReserve, string price = "0.2475") =>
            Pool(id, fee, "alien.worlds", "TLM", "1000.0000", "eosio.token", "WAX", waxReserve, price, "4.04");

        const string Ticker = "{\"ticker_id\":\"crw-chickentoken_wax-eosio.token\",\"market_id\":1000,\"last_price\":1.3,\"bid\":0.51,\"ask\":3.5,\"change24\":0,\"base_volume\":7332.8116,\"target_volume\":8139.80436486}";
        const string Trades = "[{\"trade_id\":\"a\",\"price\":1.25,\"base_volume\":10,\"target_volume\":12.5,\"trade_timestamp\":1793636221,\"type\":\"sell\",\"trx_id\":\"t1\"}," +
                              "{\"trade_id\":\"b\",\"price\":1.3,\"base_volume\":21.543,\"target_volume\":28.0059,\"trade_timestamp\":1793649707,\"type\":\"sell\",\"trx_id\":\"t2\"}]";
        const string Swaps = "[{\"pool\":1,\"time\":\"2026-11-04T14:56:16.500Z\",\"tokenA\":1,\"tokenB\":-1}]";

        FakePlatform platform;
        IWaxPlatform previous;
        SynchronizationContext previousContext;
        WaxMarketSettings settings;

        [SetUp]
        public void SetUp()
        {
            previous = WaxPlatform.IsSet ? WaxPlatform.Current : null;
            // Without a context, a request completed later (dedupe test) resumes inline instead of waiting for the editor loop.
            previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            platform = new FakePlatform();
            WaxPlatform.Current = platform;
            settings = new WaxMarketSettings();
            WaxMarketData.ClearSessionCaches();
        }

        [TearDown]
        public void TearDown()
        {
            WaxPlatform.Current = previous;
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        WaxMarketData Data(string chainId = WaxNetwork.MainnetChainId, string symbol = "WAX", int precision = 8) =>
            new WaxMarketData(new WaxNetwork("Test", chainId, new[] { Rpc }, symbol, precision), settings) { Now = () => platform.Now };

        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

        // ---- Pool choice ----

        [Test]
        public void SeveralPools_LargestWaxReserve_TieGoesToLowerFee()
        {
            platform.On("/v2/swap/pools?tokenA=tlm-alien.worlds&tokenB=wax-eosio.token",
                "[" + TlmPool(1, 3000, "100.5", "0.30") + "," + TlmPool(2, 10000, "500.12345678", "0.40") + "," + TlmPool(3, 500, "500.12345678", "0.25") + "]");
            platform.On("/swaps?limit=1", Swaps);
            var price = Run(Data().GetPrice(Tlm, default));
            Assert.AreEqual(WaxPriceOrigin.Pool, price.Origin);
            Assert.AreEqual(3, price.PoolId, "same WAX reserve: the 0.05% pool wins over the 1% pool");
            Assert.AreEqual(0.25m, price.Value);
            Assert.AreEqual("WAX", price.Unit);
            Assert.AreEqual(new DateTime(2026, 11, 4, 14, 56, 16, 500, DateTimeKind.Utc), price.SourceTime);
            Assert.AreEqual(Clock, price.FetchedAt);
        }

        [Test]
        public void TokenAsPoolB_UsesPriceB()
        {
            platform.On("/v2/swap/pools?", "[" + Pool(9, 3000, "eosio.token", "WAX", "800.0", "alien.worlds", "TLM", "200.0", "4.04", "0.2475") + "]");
            platform.On("/swaps?limit=1", "[]");
            var price = Run(Data().GetPrice(Tlm, default));
            Assert.AreEqual(0.2475m, price.Value);
            Assert.IsNull(price.SourceTime, "no swap yet: the pool does not say when its price came to be");
        }

        [Test]
        public void SetPoolId_Wins_EvenWhenSmaller()
        {
            platform.On("/swaps?limit=1", Swaps);
            platform.On("/v2/swap/pools/1", TlmPool(1, 3000, "100.5", "0.30"));
            var price = Run(Data().GetPrice(new WaxToken("alien.worlds", "TLM", 1), default));
            Assert.AreEqual(1, price.PoolId);
            Assert.AreEqual(0.30m, price.Value);
            Assert.IsFalse(platform.Requests.Any(r => r.Contains("tokenA=")), "the pool list is not read");
        }

        [Test]
        public void SetPoolIdFromTrackedTokens_IsUsed()
        {
            settings.TrackedTokens.Add(new WaxToken("alien.worlds", "TLM", 1));
            platform.On("/swaps?limit=1", Swaps);
            platform.On("/v2/swap/pools/1", TlmPool(1, 3000, "100.5", "0.30"));
            var price = Run(Data().GetPrice(Tlm, default));
            Assert.AreEqual(1, price.PoolId);
            Assert.AreEqual(0.30m, price.Value);
        }

        [TestCase("null", nameof(WaxPriceMissing.PoolNotFound))]
        [TestCase("WRONG", nameof(WaxPriceMissing.WrongPool))]
        [TestCase("EMPTY", nameof(WaxPriceMissing.EmptyPool))]
        [TestCase("INACTIVE", nameof(WaxPriceMissing.EmptyPool))]
        public void SetPoolId_Bad_IsNull_NoSwitch(string answer, string missing)
        {
            var text = answer == "WRONG" ? Pool(7, 3000, "chickentoken", "CRW", "10.0", "eosio.token", "WAX", "10.0", "1", "1")
                : answer == "EMPTY" ? TlmPool(7, 3000, "0")
                : answer == "INACTIVE" ? Pool(7, 3000, "alien.worlds", "TLM", "10.0", "eosio.token", "WAX", "10.0", "1", "1", active: false)
                : answer;
            platform.On("/v2/swap/pools/7", text);
            platform.On("/v2/tickers/", Ticker);
            var price = Run(Data().GetPrice(new WaxToken("alien.worlds", "TLM", 7), default));
            Assert.IsFalse(price.HasValue);
            Assert.AreEqual(missing, price.Missing.ToString());
            Assert.AreEqual(7, price.PoolId);
            Assert.AreEqual(1, platform.Requests.Count, "no other pool and no spot market is read: " + string.Join(", ", platform.Requests));
        }

        [Test]
        public void EmptyPool_PoolOnly_IsEmpty_Auto_FallsBackToSpot()
        {
            platform.On("/v2/swap/pools?", "[" + Pool(4, 3000, "chickentoken", "CRW", "11165.9268", "eosio.token", "WAX", "0", "1.00697", "0.99") + "]");
            platform.On("/historical_trades?limit=1", Trades);
            platform.On("/v2/tickers/crw-chickentoken_wax-eosio.token", Ticker);

            settings.PriceSource = WaxPriceSource.PoolOnly;
            Assert.AreEqual(WaxPriceMissing.EmptyPool, Run(Data().GetPrice(Crw, default)).Missing);

            settings.PriceSource = WaxPriceSource.Auto;
            var price = Run(Data().GetPrice(Crw, default));
            Assert.AreEqual(WaxPriceOrigin.Spot, price.Origin);
            Assert.AreEqual(1.3m, price.Value, "the newest trade (Alcor sends the newest trades oldest first)");
            Assert.AreEqual(new DateTime(2026, 11, 2, 20, 1, 47, DateTimeKind.Utc), price.SourceTime);
            Assert.AreEqual("crw-chickentoken_wax-eosio.token", price.MarketId);
        }

        [Test]
        public void Auto_NoPool_UsesSpot()
        {
            platform.On("/v2/swap/pools?", "[]");
            platform.On("/historical_trades?limit=1", Trades);
            platform.On("/v2/tickers/", Ticker);
            Assert.AreEqual(WaxPriceOrigin.Spot, Run(Data().GetPrice(Crw, default)).Origin);
        }

        [Test]
        public void SpotWithoutTrades_IsNoTrades()
        {
            platform.On("/v2/swap/pools?", "[]");
            platform.On("/historical_trades?limit=1", "[]");
            platform.On("/v2/tickers/", Ticker);
            var price = Run(Data().GetPrice(Crw, default));
            Assert.AreEqual(WaxPriceMissing.NoTrades, price.Missing);
            Assert.IsNull(price.Value);
        }

        [Test]
        public void NoPoolNoMarket_IsNoDirectPair()
        {
            platform.On("/v2/swap/pools?", "[]");
            platform.On("/v2/tickers/", 404, "Market with id x not found or closed :(");
            var price = Run(Data().GetPrice(Crw, default));
            Assert.AreEqual(WaxPriceMissing.NoDirectPair, price.Missing);
        }

        [Test]
        public void OnlyPairWithAnotherToken_IsNoDirectPair()
        {
            // Alcor's pair filter answers only pools of exactly this pair; a CRW / USDT pool never counts.
            platform.On("/v2/swap/pools?", "[" + Pool(5, 3000, "chickentoken", "CRW", "10.0", "usdt.alcor", "USDT", "10.0", "1", "1") + "]");
            platform.On("/v2/tickers/", 404, "not found");
            Assert.AreEqual(WaxPriceMissing.NoDirectPair, Run(Data().GetPrice(Crw, default)).Missing);
        }

        [Test]
        public void NetworkCoin_IsOne()
        {
            var price = Run(Data().GetPrice(new WaxToken("eosio.token", "WAX"), default));
            Assert.AreEqual(1m, price.Value);
            Assert.AreEqual(WaxPriceOrigin.NetworkCoin, price.Origin);
            Assert.AreEqual(0, platform.Requests.Count);
        }

        [Test]
        public void Testnet_IsNotSupported()
        {
            var data = Data(WaxNetwork.TestnetChainId);
            Assert.AreEqual(WaxPriceMissing.NotSupported, Run(data.GetPrice(Crw, default)).Missing);
            Assert.AreEqual(WaxPriceMissing.NotSupported, Run(data.GetCoreUsdPrice(default)).Missing);
            Assert.AreEqual(0, platform.Requests.Count, "a testnet never asks the mainnet sources");
        }

        [Test]
        public void AlcorOff_IsSourceOff_AndRawCallsSayAlcorIsOff()
        {
            settings.AlcorEnabled = false;
            Assert.AreEqual(WaxPriceMissing.SourceOff, Run(Data().GetPrice(Crw, default)).Missing);
            var e = Assert.Throws<WaxKitException>(() => Run(Data().GetPool(1, default)));
            StringAssert.Contains("Alcor is off", e.Message);
            Assert.AreEqual(0, platform.Requests.Count);
        }

        // ---- Requests ----

        [Test]
        public void RateLimit_ThreeRequestsInAll_ThenClearError()
        {
            platform.On("/v2/swap/pools?", 429, "Too many requests");
            var price = Run(Data().GetPrice(Crw, default));
            Assert.AreEqual(WaxPriceMissing.RequestFailed, price.Missing);
            StringAssert.Contains("429", price.Reason);
            Assert.AreEqual(3, platform.Requests.Count);
            Assert.AreEqual(new[] { 1000, 2000 }, platform.Delays);
        }

        [Test]
        public void RateLimit_RetryAfterTooLong_StopsAtOnce()
        {
            platform.On("/v2/swap/pools?", 429, "slow down", retryAfter: "20");
            var price = Run(Data().GetPrice(Crw, default));
            StringAssert.Contains("429", price.Reason);
            Assert.AreEqual(1, platform.Requests.Count);
            Assert.AreEqual(0, platform.Delays.Count);
        }

        // 2147484 s * 1000 does not fit in an int (it wrapped to a negative wait and the kit asked again at once);
        // the second value does not fit in a long.
        [TestCase("2147484")]
        [TestCase("99999999999999999999")]
        public void RateLimit_RetryAfterOverflow_StopsAtOnce(string retryAfter)
        {
            platform.On("/v2/swap/pools?", 429, "slow down", retryAfter: retryAfter);
            var price = Run(Data().GetPrice(Crw, default));
            StringAssert.Contains("429", price.Reason);
            Assert.AreEqual(1, platform.Requests.Count);
            Assert.AreEqual(0, platform.Delays.Count);
        }

        [Test]
        public void RateLimit_ThenAnswer_UsesRetryAfter()
        {
            var calls = 0;
            platform.On("/v2/swap/pools?", () => ++calls == 1
                ? new WaxHttpResponse { Status = 429, Text = "slow", Headers = { ["Retry-After"] = "3" } }
                : Ok("[" + TlmPool(1, 3000, "10") + "]"));
            platform.On("/swaps?limit=1", Swaps);
            Assert.IsTrue(Run(Data().GetPrice(Tlm, default)).HasValue);
            Assert.AreEqual(new[] { 3000 }, platform.Delays);
        }

        [Test]
        public void Cache_SecondCallReusesAnswer_UntilCacheSecondsPass()
        {
            platform.On("/v2/swap/pools?", "[" + TlmPool(1, 3000, "10") + "]");
            platform.On("/swaps?limit=1", Swaps);
            var data = Data();
            var first = Run(data.GetPrice(Tlm, default));
            platform.Now = Clock.AddSeconds(30);
            var second = Run(data.GetPrice(Tlm, default));
            Assert.AreEqual(2, platform.Requests.Count, "pool list and swaps read once");
            Assert.AreEqual(first.FetchedAt, second.FetchedAt, "a cached answer keeps the time it was read");
            platform.Now = Clock.AddSeconds(61);
            Run(data.GetPrice(Tlm, default));
            Assert.AreEqual(4, platform.Requests.Count);
        }

        [Test]
        public void Dedupe_TwoCallsAtOnce_OneRequest()
        {
            var pending = new TaskCompletionSource<WaxHttpResponse>();
            platform.Pending = pending;
            var data = Data();
            var a = data.GetPool(1, default);
            var b = data.GetPool(1, default);
            Assert.AreEqual(1, platform.Requests.Count);
            platform.Pending = null;
            pending.SetResult(Ok(TlmPool(1, 3000, "10")));
            Assert.AreEqual(1, Run(a).Id);
            Assert.AreEqual(1, Run(b).Id);
        }

        [TestCase("/v2/swap/pools?", "{}")]
        [TestCase("/v2/swap/pools?", "<html>bad gateway</html>")]
        [TestCase("/v2/swap/pools/1", "[]")]
        public void WrongShape_IsAFailedEndpoint(string path, string answer)
        {
            platform.On(path, answer);
            var token = path.EndsWith("/1") ? new WaxToken("alien.worlds", "TLM", 1) : Tlm;
            var price = Run(Data().GetPrice(token, default));
            Assert.AreEqual(WaxPriceMissing.RequestFailed, price.Missing);
            StringAssert.Contains("not a JSON", price.Reason);
        }

        [Test]
        public void Cancelled_Throws_NotAPriceResult()
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.Catch<OperationCanceledException>(() => Run(Data().GetPrice(Crw, cancel.Token)));
        }

        // ---- Network coin in USD ----

        static string Pair(string name, int precision) =>
            $"{{\"rows\":[{{\"name\":\"{name}\",\"quoted_precision\":{precision},\"base_symbol\":\"8,WAXP\",\"quote_symbol\":\"2,USD\"}}],\"more\":false}}";

        static string Datapoints(params (int median, string time)[] points) =>
            "{\"rows\":[" + string.Join(",", points.Select((p, i) => $"{{\"id\":{i},\"owner\":\"bp{i}\",\"value\":{p.median},\"median\":{p.median},\"timestamp\":\"{p.time}\"}}")) + "],\"more\":false}";

        [Test]
        public void Oracle_NewestDatapoint_WithPairPrecision()
        {
            platform.OnPost("\"table\":\"pairs\"", Pair("xprusd", 6));
            platform.OnPost("\"table\":\"datapoints\"", Datapoints((1898, "2026-11-04T15:45:11.500"), (1901, "2026-11-04T15:51:02.500"), (1890, "2026-11-04T15:40:00.000")));
            var price = Run(Data(WaxMarketSettings.XprChainId, "XPR", 4).GetCoreUsdPrice(default));
            Assert.AreEqual(WaxPriceOrigin.Oracle, price.Origin);
            Assert.AreEqual(0.001901m, price.Value);
            Assert.AreEqual("USD", price.Unit);
            Assert.AreEqual(new DateTime(2026, 11, 4, 15, 51, 2, 500, DateTimeKind.Utc), price.SourceTime);
            StringAssert.Contains("\"code\":\"delphioracle\",\"scope\":\"xprusd\"", platform.Requests.Last());
        }

        [Test]
        public void Oracle_Stale_OracleOnlyIsNull_AutoUsesAlcor()
        {
            platform.OnPost("\"table\":\"pairs\"", Pair("waxpusd", 4));
            platform.OnPost("\"table\":\"datapoints\"", Datapoints((43, "2026-11-04T13:59:00.000")));
            platform.On("/v2/tokens/wax-eosio.token", "{\"id\":\"wax-eosio.token\",\"usd_price\":0.00427407,\"system_price\":1}");

            settings.CoreUsdSource = WaxUsdSource.OracleOnly;
            var stale = Run(Data().GetCoreUsdPrice(default));
            Assert.AreEqual(WaxPriceMissing.Stale, stale.Missing);
            Assert.IsNull(stale.Value);

            settings.CoreUsdSource = WaxUsdSource.Auto;
            var auto = Run(Data().GetCoreUsdPrice(default));
            Assert.AreEqual(WaxPriceOrigin.AlcorUsd, auto.Origin);
            Assert.AreEqual(0.00427407m, auto.Value);
            Assert.IsNull(auto.SourceTime, "Alcor does not say when its USD price came to be");
        }

        [Test]
        public void Oracle_Fresh_AutoUsesOracle()
        {
            platform.OnPost("\"table\":\"pairs\"", Pair("waxpusd", 4));
            platform.OnPost("\"table\":\"datapoints\"", Datapoints((43, "2026-11-04T15:50:08.000")));
            var price = Run(Data().GetCoreUsdPrice(default));
            Assert.AreEqual(WaxPriceOrigin.Oracle, price.Origin);
            Assert.AreEqual(0.0043m, price.Value);
            Assert.IsFalse(platform.Requests.Any(r => r.Contains("alcor")));
        }

        // ---- Balances ----

        void Stat(string contract, string symbol, string supply) =>
            platform.OnPost($"get_currency_stats {{\"code\":\"{contract}\",\"symbol\":\"{symbol}\"}}",
                supply == null ? "{}" : $"{{\"{symbol}\":{{\"supply\":\"{supply}\",\"max_supply\":\"{supply}\",\"issuer\":\"{contract}\"}}}}");

        [Test]
        public void Balance_HoldsNone_IsZeroWithPrecision()
        {
            Stat("chickentoken", "CRW", "17367.4370 CRW");
            platform.OnPost("get_currency_balance", "[]");
            var balance = Run(Data().GetBalance("player12", Crw, default));
            Assert.AreEqual(0m, balance.Amount);
            Assert.AreEqual(4, balance.Precision);
            Assert.AreEqual("0.0000 CRW", balance.ToString());
        }

        [Test]
        public void Balance_HighPrecision_NoFloatRounding()
        {
            Stat("eosio.token", "WAX", "1000000000.00000000 WAX");
            platform.OnPost("get_currency_balance", "[\"123456789.12345678 WAX\"]");
            var balance = Run(Data().GetBalance("player12", new WaxToken("eosio.token", "WAX"), default));
            Assert.AreEqual(123456789.12345678m, balance.Amount);
            Assert.AreEqual("123456789.12345678 WAX", balance.ToString());
        }

        [Test]
        public void Balance_UnknownToken_IsAnError_NotZero()
        {
            Stat("chickentoken", "NOPE", null);
            var e = Assert.Throws<WaxKitException>(() => Run(Data().GetBalance("player12", new WaxToken("chickentoken", "NOPE"), default)));
            StringAssert.Contains("does not exist", e.Message);

            platform.OnPost("get_currency_stats {\"code\":\"nosuchcontr1\"", 400,
                "{\"code\":400,\"error\":{\"code\":3060002,\"name\":\"account_query_exception\",\"what\":\"Account Query Exception\",\"details\":[{\"message\":\"Fail to retrieve account for nosuchcontr1\"}]}}");
            e = Assert.Throws<WaxKitException>(() => Run(Data().GetBalance("player12", new WaxToken("nosuchcontr1", "CRW"), default)));
            StringAssert.Contains("does not exist", e.Message);
        }

        [Test]
        public void Balances_OneFailing_OthersOk()
        {
            Stat("eosio.token", "WAX", "1.00000000 WAX");
            Stat("chickentoken", "CRW", "1.0000 CRW");
            Stat("alien.worlds", "TLM", "1.0000 TLM");
            platform.OnPost("get_currency_balance {\"code\":\"chickentoken\"", 500, "{\"code\":500,\"error\":{\"code\":1,\"name\":\"x\",\"what\":\"boom\"}}");
            platform.OnPost("get_currency_balance {\"code\":\"eosio.token\"", "[\"5.00000000 WAX\"]");
            platform.OnPost("get_currency_balance {\"code\":\"alien.worlds\"", "[\"2.5000 TLM\"]");
            var balances = Run(Data().GetBalances("player12", new[] { new WaxToken("eosio.token", "WAX"), Crw, Tlm }, default));
            Assert.AreEqual(3, balances.Count);
            Assert.AreEqual(5m, balances[0].Amount);
            Assert.IsFalse(balances[1].IsOk);
            Assert.IsNotNull(balances[1].Error);
            Assert.AreEqual(2.5m, balances[2].Amount);
        }

        [Test]
        public void Balances_Cancelled_StopsTheWholeCall()
        {
            using var cancel = new CancellationTokenSource();
            Stat("eosio.token", "WAX", "1.00000000 WAX");
            Stat("chickentoken", "CRW", "1.0000 CRW");
            platform.OnPost("get_currency_balance {\"code\":\"eosio.token\"", () =>
            {
                cancel.Cancel();
                return Ok("[\"5.00000000 WAX\"]");
            });
            platform.OnPost("get_currency_balance", "[\"1.0000 CRW\"]");
            Assert.Catch<OperationCanceledException>(() =>
                Run(Data().GetBalances("player12", new[] { new WaxToken("eosio.token", "WAX"), Crw, Tlm }, cancel.Token)));
            Assert.IsFalse(platform.Requests.Any(r => r.Contains("alien.worlds")), "nothing after the cancel");
        }

        // ---- Values ----

        [Test]
        public void Values_TotalsOfPricedTokensOnly()
        {
            settings.TrackedTokens.Add(Crw);
            settings.TrackedTokens.Add(new WaxToken("nopair.token", "XYZ"));
            Stat("eosio.token", "WAX", "1.00000000 WAX");
            Stat("chickentoken", "CRW", "1.0000 CRW");
            Stat("nopair.token", "XYZ", "1.000 XYZ");
            platform.OnPost("get_currency_balance {\"code\":\"eosio.token\"", "[\"100.12345678 WAX\"]");
            platform.OnPost("get_currency_balance {\"code\":\"chickentoken\"", "[\"10.0000 CRW\"]");
            platform.OnPost("get_currency_balance {\"code\":\"nopair.token\"", "[\"7.000 XYZ\"]");
            platform.OnPost("\"table\":\"pairs\"", Pair("waxpusd", 4));
            platform.OnPost("\"table\":\"datapoints\"", Datapoints((43, "2026-11-04T15:50:08.000")));
            platform.On("tokenA=crw-chickentoken", "[" + Pool(4, 10000, "chickentoken", "CRW", "11165.9268", "eosio.token", "WAX", "50002.67151275", "1.00697", "0.993075") + "]");
            platform.On("/swaps?limit=1", Swaps);
            platform.On("tokenA=xyz-nopair.token", "[]");
            platform.On("/v2/tickers/", 404, "not found");

            var values = Run(Data().GetBalanceValues("player12", default));
            Assert.AreEqual(3, values.Items.Count);
            Assert.AreEqual(2, values.PricedCount);
            Assert.AreEqual(1, values.UnpricedCount);
            Assert.AreEqual(100.12345678m + 10m * 1.00697m, values.CoinTotal);
            Assert.AreEqual((100.12345678m + 10.0697m) * 0.0043m, values.UsdTotal);
            Assert.AreEqual(7m, values.Items[2].Balance.Amount, "an unpriced token keeps its amount");
            Assert.IsNull(values.Items[2].CoinValue);
            Assert.IsNotNull(values.Items[2].Reason);
            Assert.AreEqual("WAX", values.CoinSymbol);
        }

        // ---- Settings ----

        [Test]
        public void Settings_CheckInput()
        {
            Assert.Throws<ArgumentException>(() => new WaxToken("", "TLM"));
            Assert.Throws<ArgumentException>(() => new WaxToken("Alien.Worlds", "TLM"));
            Assert.Throws<ArgumentException>(() => new WaxToken("alien.worlds", "tlm"));
            Assert.Throws<ArgumentException>(() => new WaxToken("alien.worlds", "TLM", -1));
            Assert.Throws<ArgumentException>(() => settings.SetAlcorHost(WaxNetwork.TestnetChainId, "http://plain.example/api"));
            settings.SetAlcorHost(WaxNetwork.TestnetChainId, "https://my-alcor.example/api/");
            Assert.AreEqual("https://my-alcor.example/api", settings.GetAlcorHost(WaxNetwork.TestnetChainId));
            Assert.AreEqual("https://wax.alcor.exchange/api", settings.GetAlcorHost(WaxNetwork.MainnetChainId));
            Assert.IsNull(settings.GetOracle(WaxNetwork.TestnetChainId));
            Assert.AreEqual(("delphioracle", "waxpusd"), settings.GetOracle(WaxNetwork.MainnetChainId));
        }

        static WaxHttpResponse Ok(string text) => new WaxHttpResponse { Status = 200, Text = text };

        /// <summary>Answers recorded texts by URL (GET) or by path + body (POST); the first matching rule wins.</summary>
        sealed class FakePlatform : IWaxPlatform
        {
            readonly List<(string match, bool post, Func<WaxHttpResponse> answer)> rules = new List<(string, bool, Func<WaxHttpResponse>)>();
            public readonly List<string> Requests = new List<string>();
            public readonly List<int> Delays = new List<int>();
            public TaskCompletionSource<WaxHttpResponse> Pending;
            public DateTime Now = Clock;

            public void On(string match, string text) => rules.Add((match, false, () => Ok(text)));
            public void On(string match, int status, string text, string retryAfter = null) => rules.Add((match, false, () =>
            {
                var response = new WaxHttpResponse { Status = status, Text = text };
                if (retryAfter != null) response.Headers["Retry-After"] = retryAfter;
                return response;
            }));
            public void On(string match, Func<WaxHttpResponse> answer) => rules.Add((match, false, answer));
            public void OnPost(string match, string text) => rules.Add((match, true, () => Ok(text)));
            public void OnPost(string match, int status, string text) => rules.Add((match, true, () => new WaxHttpResponse { Status = status, Text = text }));
            public void OnPost(string match, Func<WaxHttpResponse> answer) => rules.Add((match, true, answer));

            public Task<WaxHttpResponse> Http(string method, string url, byte[] body, string contentType, IDictionary<string, string> headers,
                int timeoutSeconds, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                var post = method == "POST";
                var key = post ? url.Substring(url.LastIndexOf('/') + 1) + " " + Encoding.UTF8.GetString(body ?? new byte[0]) : url;
                Requests.Add(key);
                if (Pending != null) return Pending.Task;
                foreach (var rule in rules)
                    if (rule.post == post && key.Contains(rule.match)) return Task.FromResult(rule.answer());
                return Task.FromResult(new WaxHttpResponse { Status = 0, Error = "no recorded answer for " + key });
            }

            public Task Delay(int milliseconds, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                Delays.Add(milliseconds);
                return Task.CompletedTask;
            }

            public IWaxSocket CreateSocket() => throw new NotSupportedException();
            public string Load(string key) => null;
            public void Save(string key, string value) { }
            public void Delete(string key) { }
            public bool IsMobile => false;
            public bool PopupNeedsGesture => false;
            public string ReturnUrl => null;
            public string UserAgent => "test";
            public bool? CanOpenScheme(string scheme) => null;
            public void OpenLink(string url) { }
            public void ReturnToGame() { }
            public void BeginWalletRequest() { }
            public void EndWalletRequest() { }
            public Task WaitForPlayerBack(int stayMilliseconds, CancellationToken cancellation) => Task.CompletedTask;
            public void LetWalletAppComeToFront(string scheme, bool force) { }
            public bool BringWalletAppToFront(string scheme) => false;
            public IWaxPopupFlow OpenWalletPopup(string url, JObject post, WaxPopupProtocol protocol) => throw new NotSupportedException();
        }
    }
}
