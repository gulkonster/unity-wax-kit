using System;
using UnityWaxKit.Antelope;
using UnityWaxKit.Esr;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using static UnityWaxKit.Tests.TransactionTestData;

namespace UnityWaxKit.Tests
{
    /// <summary>The check on the transaction an ESR wallet (Anchor) answers with.</summary>
    public class EsrWalletTests
    {
        sealed class TestWallet : WaxEsrWallet
        {
            public override string Id => "testwallet";
            public override string Name => "Test Wallet";
        }

        const string ChainId = WaxNetwork.MainnetChainId;

        static WaxSignRequest Request(JObject transaction)
        {
            var packed = AntelopeTransaction.Encode(transaction);
            return new WaxSignRequest(ChainId, packed, AntelopeTransaction.SigningDigest(ChainId, packed), transaction,
                AntelopeTime.Parse((string)transaction["expiration"]));
        }

        /// <summary>The callback a wallet sends after signing <paramref name="signed"/>.</summary>
        static JObject Callback(JObject signed) =>
            SigningRequest.Transaction(ChainId, signed, false, "https://cb.anchor.link/test", true)
                .Resolve(Player, Permission)
                .GetCallbackPayload(new[] { "SIG_K1_test" });

        [Test]
        public void SameTransaction_IsAccepted()
        {
            var transaction = Transaction(GameAction());
            var result = new TestWallet().ToSignResult(Request(transaction), Callback(transaction));

            CollectionAssert.AreEqual(new[] { "SIG_K1_test" }, result.Signatures);
            Assert.IsNull(result.SerializedTransaction, "The kit broadcasts its own bytes.");
        }

        [Test]
        public void DifferentTransaction_IsRejected()
        {
            var payload = Callback(Transaction(GameAction(), Fee()));
            var error = Assert.Throws<WaxKitException>(() => new TestWallet().ToSignResult(Request(Transaction(GameAction())), payload));
            Assert.AreEqual(WaxErrorKind.Wallet, error.Kind);
        }

        [Test]
        public void DifferentTaPoS_IsRejected()
        {
            var signed = Transaction(GameAction());
            signed["ref_block_num"] = 999;
            var payload = Callback(signed);
            var error = Assert.Throws<WaxKitException>(() => new TestWallet().ToSignResult(Request(Transaction(GameAction())), payload));
            Assert.AreEqual(WaxErrorKind.Wallet, error.Kind);
        }

        [Test]
        public void OtherChainId_IsRejected()
        {
            var transaction = Transaction(GameAction());
            var payload = Callback(transaction);
            payload["cid"] = WaxNetwork.TestnetChainId;
            var error = Assert.Throws<WaxKitException>(() => new TestWallet().ToSignResult(Request(transaction), payload));
            Assert.AreEqual(WaxErrorKind.WrongChain, error.Kind);
        }

        // Night shift round 2: every field of the transaction an ESR wallet echoes back must be the kit's own. Any change
        // is refused (the kit sends its own bytes, so a wallet cannot get anything else broadcast).
        static readonly (string name, Action<JObject> change)[] Changes =
        {
            ("expiration", t => t["expiration"] = "2030-01-01T00:00:00"),
            ("ref_block_num", t => t["ref_block_num"] = 999),
            ("ref_block_prefix", t => t["ref_block_prefix"] = 123456789),
            ("max_net_usage_words", t => t["max_net_usage_words"] = 99),
            ("max_cpu_usage_ms", t => t["max_cpu_usage_ms"] = 99),
            ("delay_sec", t => t["delay_sec"] = 60),
            ("action account", t => t["actions"][0]["account"] = "othergame"),
            ("action name", t => t["actions"][0]["name"] = "burn"),
            ("action data", t => t["actions"][0]["data"] = "02"),
            ("authorization actor", t => t["actions"][0]["authorization"][0]["actor"] = "attacker1111"),
            ("authorization permission", t => t["actions"][0]["authorization"][0]["permission"] = "owner"),
            ("added authorization", t => ((JArray)t["actions"][0]["authorization"]).Add(new JObject { ["actor"] = "cosign.wam", ["permission"] = "paybw" })),
            ("added action", t => ((JArray)t["actions"]).Add(Fee())),
            ("context-free action", t => ((JArray)t["context_free_actions"]).Add(Action("mygame", "note", "", "cosign.wam", "paybw"))),
            ("transaction extension", t => t["transaction_extensions"] = new JArray(new JObject { ["type"] = 1, ["data"] = "0011" })),
        };

        static System.Collections.Generic.IEnumerable<TestCaseData> ChangedFields()
        {
            foreach (var (name, change) in Changes) yield return new TestCaseData(change).SetName("ChangedField_IsRejected(" + name + ")");
        }

        [TestCaseSource(nameof(ChangedFields))]
        public void ChangedField_IsRejected(Action<JObject> change)
        {
            var signed = Transaction(GameAction());
            change(signed);
            var payload = Callback(Normalize(signed));
            var error = Assert.Throws<WaxKitException>(() => new TestWallet().ToSignResult(Request(Transaction(GameAction())), payload));
            Assert.AreEqual(WaxErrorKind.Wallet, error.Kind);
        }

        [Test]
        public void TaPoSFieldsInTheCallback_AreIgnoredForAFullTransaction()
        {
            // The kit's request carries its own expiration and reference block, so ex / rbn / rid of the callback cannot
            // change what is broadcast: the kit sends its own bytes.
            var transaction = Transaction(GameAction());
            var payload = Callback(transaction);
            payload["ex"] = "2030-01-01T00:00:00";
            payload["rbn"] = "999";
            payload["rid"] = "123456789";
            payload["sa"] = "attacker1111";
            payload["sp"] = "owner";
            var result = new TestWallet().ToSignResult(Request(transaction), payload);
            Assert.IsNull(result.SerializedTransaction);
        }

        [Test]
        public void UnreadableRequest_IsRejected()
        {
            var transaction = Transaction(GameAction());
            var payload = Callback(transaction);
            payload["req"] = "esr:not-a-request";
            var error = Assert.Throws<WaxKitException>(() => new TestWallet().ToSignResult(Request(transaction), payload));
            Assert.AreEqual(WaxErrorKind.Wallet, error.Kind);
        }
    }

    /// <summary>Chain id and endpoint checks of <see cref="WaxNetwork"/>.</summary>
    public class WaxNetworkTests
    {
        [Test]
        public void ValidNetwork_IsAccepted()
        {
            var network = new WaxNetwork("Local", WaxNetwork.TestnetChainId.ToUpperInvariant(), "http://127.0.0.1:8888", "https://node.example.com/");
            Assert.AreEqual(WaxNetwork.TestnetChainId, network.ChainId);
            Assert.AreEqual(2, network.RpcEndpoints.Count);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("1064487b3cd1a897")]
        [TestCase("z064487b3cd1a897ce03ae5b6a865651747e2e152090f99c1d19d44e01aea5a4")]
        [TestCase(" 064487b3cd1a897ce03ae5b6a865651747e2e152090f99c1d19d44e01aea5a4")]
        public void InvalidChainId_IsRejected(string chainId) =>
            Assert.Throws<ArgumentException>(() => new WaxNetwork("Bad", chainId, "https://node.example.com"));

        [TestCase("node.example.com")]
        [TestCase("/v1/chain")]
        [TestCase("ftp://node.example.com")]
        [TestCase("file:///c:/node")]
        [TestCase("javascript:alert(1)")]
        public void InvalidRpcEndpoint_IsRejected(string endpoint) =>
            Assert.Throws<ArgumentException>(() => new WaxNetwork("Bad", WaxNetwork.MainnetChainId, endpoint));

        [Test]
        public void InvalidAtomicEndpoint_IsRejected() =>
            Assert.Throws<ArgumentException>(() => WaxNetwork.Mainnet.WithAtomicEndpoints("ws://atomic.example.com"));

        [Test]
        public void InvalidEndpointInWithEndpoints_IsRejected() =>
            Assert.Throws<ArgumentException>(() => WaxNetwork.Mainnet.WithEndpoints("https://ok.example.com", "not a url"));
    }
}
