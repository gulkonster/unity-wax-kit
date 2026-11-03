using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using static UnityWaxKit.Tests.TransactionTestData;

namespace UnityWaxKit.Tests
{
    /// <summary>The checks on the transaction My Cloud Wallet returns.</summary>
    public class MyCloudWalletTests
    {
        static void Validate(JObject original, JObject modified) =>
            MyCloudWallet.ValidateModifications(original, modified, Player, Permission);

        static void AssertRejected(JObject original, JObject modified)
        {
            var error = Assert.Throws<WaxKitException>(() => Validate(original, modified));
            Assert.AreEqual(WaxErrorKind.Wallet, error.Kind);
        }

        // ---- Accepted ----

        [Test]
        public void Unchanged_IsAccepted() =>
            Validate(Transaction(GameAction(), GameAction("02")), Transaction(GameAction(), GameAction("02")));

        [Test]
        public void OneValidFee_IsAccepted() => Validate(Transaction(GameAction()), Transaction(Fee(), GameAction()));

        [Test]
        public void OneValidRamPurchase_IsAccepted() => Validate(Transaction(GameAction()), Transaction(GameAction(), BuyRam()));

        [Test]
        public void FeeAndRamPurchase_AreAccepted() => Validate(Transaction(GameAction()), Transaction(Fee(), GameAction(), BuyRam()));

        [Test]
        public void AddedActionWithoutPlayerAuthority_IsAccepted() =>
            Validate(Transaction(GameAction()), Transaction(Action("boost.wax", "noop", "", "cosign.wam", "paybw"), GameAction()));

        [Test]
        public void ActionsInsertedBetweenOriginals_AreAccepted() =>
            Validate(Transaction(GameAction(), GameAction("02")), Transaction(GameAction(), Fee(), GameAction("02")));

        // ---- Original actions ----

        [Test]
        public void DroppedDuplicate_IsRejected() =>
            AssertRejected(Transaction(GameAction(), GameAction()), Transaction(GameAction(), Fee()));

        [Test]
        public void ExtraCopy_IsRejected() => AssertRejected(Transaction(GameAction()), Transaction(GameAction(), GameAction()));

        [Test]
        public void ReorderedOriginals_AreRejected() =>
            AssertRejected(Transaction(GameAction(), GameAction("02")), Transaction(GameAction("02"), GameAction()));

        [Test]
        public void ChangedAction_IsRejected() => AssertRejected(Transaction(GameAction()), Transaction(GameAction("ff")));

        [Test]
        public void ChangedAuthorization_IsRejected() =>
            AssertRejected(Transaction(GameAction()), Transaction(Action("mygame", "play", "01", Player, "owner")));

        // ---- Fee ----

        [Test]
        public void FeeFromAnotherAccount_IsRejected() =>
            AssertRejected(Transaction(GameAction()), Transaction(Transfer("other1111", "txfee.wam", "0.00010000 WAX", "WAX fee for x"), GameAction()));

        [Test]
        public void FeeToAnotherAccount_IsRejected() =>
            AssertRejected(Transaction(GameAction()), Transaction(Transfer(Player, "other1111", "0.00010000 WAX", "WAX fee for x"), GameAction()));

        [Test]
        public void FeeWithOtherMemo_IsRejected() =>
            AssertRejected(Transaction(GameAction()), Transaction(Transfer(Player, "txfee.wam", "0.00010000 WAX", "tip"), GameAction()));

        [Test]
        public void FeeWithWrongSymbol_IsRejected() => AssertRejected(Transaction(GameAction()), Transaction(Fee("0.00010000 WAXP"), GameAction()));

        [Test]
        public void FeeWithWrongPrecision_IsRejected() => AssertRejected(Transaction(GameAction()), Transaction(Fee("0.0001 WAX"), GameAction()));

        [Test]
        public void FeeOfZero_IsRejected() => AssertRejected(Transaction(GameAction()), Transaction(Fee("0.00000000 WAX"), GameAction()));

        [Test]
        public void TwoFees_AreRejected() => AssertRejected(Transaction(GameAction()), Transaction(Fee(), Fee("0.00000001 WAX"), GameAction()));

        [Test]
        public void FeeWithOtherPermission_IsRejected() =>
            AssertRejected(Transaction(GameAction()),
                Transaction(Transfer(Player, "txfee.wam", "0.00010000 WAX", "WAX fee for x", Player, "owner"), GameAction()));

        // ---- RAM ----

        [Test]
        public void RamWithAnotherPayer_IsRejected() => AssertRejected(Transaction(GameAction()), Transaction(GameAction(), BuyRam(payer: "other1111")));

        [Test]
        public void RamForAnotherReceiver_IsRejected() => AssertRejected(Transaction(GameAction()), Transaction(GameAction(), BuyRam(receiver: "other1111")));

        [Test]
        public void RamOfZeroBytes_IsRejected() => AssertRejected(Transaction(GameAction()), Transaction(GameAction(), BuyRam(bytes: 0)));

        [Test]
        public void TwoRamPurchases_AreRejected() => AssertRejected(Transaction(GameAction()), Transaction(GameAction(), BuyRam(), BuyRam()));

        // ---- Other changes ----

        [Test]
        public void OtherActionWithPlayerAuthority_IsRejected() =>
            AssertRejected(Transaction(GameAction()), Transaction(GameAction(), Action("eosio", "delegatebw", "00")));

        [Test]
        public void ChangedDelay_IsRejected()
        {
            var modified = Transaction(GameAction());
            modified["delay_sec"] = 60;
            AssertRejected(Transaction(GameAction()), Normalize(modified));
        }

        [Test]
        public void ChangedContextFreeActions_AreRejected()
        {
            var modified = Transaction(GameAction());
            modified["context_free_actions"] = new JArray(Action("mygame", "note", "", "cosign.wam", "paybw"));
            AssertRejected(Transaction(GameAction()), Normalize(modified));
        }

        // ---- ToBytes ----

        [Test]
        public void ToBytes_ReadsArray() =>
            CollectionAssert.AreEqual(new byte[] { 0, 1, 255 }, MyCloudWallet.ToBytes(JArray.Parse("[0, 1, 255]")));

        [Test]
        public void ToBytes_ReadsIndexObject() =>
            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, MyCloudWallet.ToBytes(JObject.Parse(@"{ ""2"": 9, ""0"": 7, ""1"": 8 }")));

        [Test]
        public void ToBytes_ReadsHex() => CollectionAssert.AreEqual(new byte[] { 0x0a, 0xff }, MyCloudWallet.ToBytes("0aFF"));

        [Test]
        public void ToBytes_Missing_IsNull() => Assert.IsNull(MyCloudWallet.ToBytes(null));

        [TestCase("[256]")]
        [TestCase("[-1]")]
        [TestCase("[1.5]")]
        [TestCase(@"[""1""]")]
        [TestCase("[null]")]
        [TestCase(@"{ ""0"": 1, ""2"": 3 }")]
        [TestCase(@"{ ""0"": 1, ""01"": 3 }")]
        [TestCase(@"{ ""a"": 1 }")]
        [TestCase(@"""abc""")]
        [TestCase(@"""zz""")]
        [TestCase(@""" a""")]
        [TestCase("true")]
        public void ToBytes_RejectsUnreadable(string json)
        {
            var error = Assert.Throws<WaxKitException>(() => MyCloudWallet.ToBytes(JToken.Parse(json)));
            Assert.AreEqual(WaxErrorKind.Wallet, error.Kind);
            StringAssert.Contains("unreadable transaction", error.Message);
        }
    }
}
