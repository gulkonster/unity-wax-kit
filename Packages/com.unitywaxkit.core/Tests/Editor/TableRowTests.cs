using System;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnityWaxKit.Tests
{
    /// <summary>WaxKit.GetTableRows keeps the chain's text: time_point strings are not turned into local dates.</summary>
    public class TableRowTests
    {
        const string Json = @"{ ""rows"": [ { ""owner"": ""mygameplayer"", ""created"": ""2026-11-04T05:06:07.500"" } ], ""more"": true, ""next_key"": ""123"" }";

        public class Row
        {
            public string owner;
            public string created;
        }

        public class DatedRow
        {
            public DateTime created;
        }

        CultureInfo culture;

        // A culture whose date format differs from the chain's, as on many players' devices.
        [SetUp]
        public void UseOtherCulture()
        {
            culture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
        }

        [TearDown]
        public void RestoreCulture() => Thread.CurrentThread.CurrentCulture = culture;

        [Test]
        public void JObjectRow_KeepsTimeText()
        {
            var result = WaxKit.ReadTableRows<JObject>(Json);
            Assert.AreEqual(JTokenType.String, result.Rows[0]["created"].Type);
            Assert.AreEqual("2026-11-04T05:06:07.500", (string)result.Rows[0]["created"]);
            Assert.IsTrue(result.More);
            Assert.AreEqual("123", result.NextKey);
        }

        [Test]
        public void StringField_KeepsTimeText() =>
            Assert.AreEqual("2026-11-04T05:06:07.500", WaxKit.ReadTableRows<Row>(Json).Rows[0].created);

        [Test]
        public void DateTimeField_StillReadsTheTime() =>
            Assert.AreEqual(new DateTime(2026, 11, 4, 5, 6, 7, 500), WaxKit.ReadTableRows<DatedRow>(Json).Rows[0].created);
    }
}
