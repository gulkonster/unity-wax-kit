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
