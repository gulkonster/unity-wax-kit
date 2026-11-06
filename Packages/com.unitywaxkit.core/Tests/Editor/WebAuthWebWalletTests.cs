using System;
using System.Collections.Generic;
using UnityWaxKit.Antelope;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using static UnityWaxKit.Tests.TransactionTestData;

namespace UnityWaxKit.Tests
{
    /// <summary>The check on the transaction WebAuth Web signs (it signs without broadcasting; the kit broadcasts it).</summary>
    public class WebAuthWebWalletTests
    {
        static byte[] Requested => AntelopeTransaction.Encode(Transaction(GameAction()));

        static string Signed(Action<JObject> change)
        {
            var signed = Transaction(GameAction());
            change?.Invoke(signed);
            return Hex.Encode(AntelopeTransaction.Encode(Normalize(signed)));
        }

        [Test]
        public void Unchanged_IsAccepted() =>
            CollectionAssert.AreEqual(Requested, WebAuthWebWallet.CheckSignedTransaction(Requested, Signed(null)));

        // Allowed on purpose: WebAuth sets its own expiration and reference block (the chain enforces both).
        [Test]
        public void OwnTaPoS_IsAccepted()
        {
            var signed = Signed(t =>
            {
                t["expiration"] = "2030-01-01T00:00:00";
                t["ref_block_num"] = 999;
                t["ref_block_prefix"] = 123456789;
            });
            Assert.IsNotNull(WebAuthWebWallet.CheckSignedTransaction(Requested, signed));
        }

        static readonly (string name, Action<JObject> change)[] Changes =
        {
            ("max_net_usage_words", t => t["max_net_usage_words"] = 99),
            ("max_cpu_usage_ms", t => t["max_cpu_usage_ms"] = 99),
            ("delay_sec", t => t["delay_sec"] = 60),
            ("action account", t => t["actions"][0]["account"] = "othergame"),
            ("action name", t => t["actions"][0]["name"] = "burn"),
            ("action data", t => t["actions"][0]["data"] = "02"),
            ("authorization actor", t => t["actions"][0]["authorization"][0]["actor"] = "attacker1111"),
            ("authorization permission", t => t["actions"][0]["authorization"][0]["permission"] = "owner"),
            ("added action", t => ((JArray)t["actions"]).Add(Fee())),
            ("dropped action", t => ((JArray)t["actions"]).Clear()),
            ("context-free action", t => ((JArray)t["context_free_actions"]).Add(Action("mygame", "note", "", "cosign.wam", "paybw"))),
            ("transaction extension", t => t["transaction_extensions"] = new JArray(new JObject { ["type"] = 1, ["data"] = "0011" })),
        };

        static IEnumerable<TestCaseData> ChangedFields()
        {
            foreach (var (name, change) in Changes) yield return new TestCaseData(change).SetName("ChangedField_IsRejected(" + name + ")");
        }

        [TestCaseSource(nameof(ChangedFields))]
        public void ChangedField_IsRejected(Action<JObject> change)
        {
            var error = Assert.Throws<WaxKitException>(() => WebAuthWebWallet.CheckSignedTransaction(Requested, Signed(change)));
            Assert.AreEqual(WaxErrorKind.Wallet, error.Kind);
        }

        [TestCase("")]
        [TestCase("zz")]
        [TestCase("0102")]
        public void Unreadable_IsRejected(string hex)
        {
            var error = Assert.Throws<WaxKitException>(() => WebAuthWebWallet.CheckSignedTransaction(Requested, hex));
            Assert.AreEqual(WaxErrorKind.Wallet, error.Kind);
        }
    }
}
