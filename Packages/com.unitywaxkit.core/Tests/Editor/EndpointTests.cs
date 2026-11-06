using NUnit.Framework;
using UnityWaxKit.Chain;

namespace UnityWaxKit.Tests
{
    /// <summary>What counts as a real answer from an RPC or AtomicAssets endpoint (anything else moves on to the next one).</summary>
    public class EndpointTests
    {
        [TestCase("{}")]
        [TestCase("{\"rows\":[],\"more\":false}")]
        [TestCase("  {\"a\":{\"b\":[1,2,{\"c\":null}]}}\n")]
        [TestCase("[]")]
        [TestCase("[\"1.00000000 WAX\"]")] // get_currency_balance
        [TestCase("[1,2,3]")]
        public void WholeJsonObjectOrArray_IsAnAnswer(string text) => Assert.IsTrue(WaxEndpointList.IsJsonAnswer(text));

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("<!doctype html><html><body>Bad gateway</body></html>")]
        [TestCase("{\"rows\":[{\"balance\":\"1.0 WAX\"}],\"more\":fal")]
        [TestCase("{\"a\":1")]
        [TestCase("{\"a\":[1,2]")]
        [TestCase("[1,2")]
        [TestCase("\"text\"")]
        [TestCase("42")]
        [TestCase("null")]
        [TestCase("{} trailing")]
        [TestCase("{}{}")]
        [TestCase("[][]")]
        public void AnythingElse_IsNotAnAnswer(string text) => Assert.IsFalse(WaxEndpointList.IsJsonAnswer(text));

        // A call that states the shape it expects: the wrong shape is a broken endpoint (the next one is tried).
        [TestCase("{}", nameof(WaxAnswerShape.Object), true)]
        [TestCase("{\"chain_id\":\"abc\"}", nameof(WaxAnswerShape.Object), true)]
        [TestCase("[]", nameof(WaxAnswerShape.Object), false)]
        [TestCase("[{\"a\":1}]", nameof(WaxAnswerShape.Object), false)]
        [TestCase("<html></html>", nameof(WaxAnswerShape.Object), false)]
        [TestCase("{\"a\":1", nameof(WaxAnswerShape.Object), false)]
        [TestCase("[\"1.00000000 WAX\"]", nameof(WaxAnswerShape.Array), true)]
        [TestCase("{}", nameof(WaxAnswerShape.Array), false)]
        [TestCase("[\"1.00000000 WAX\"]", nameof(WaxAnswerShape.Any), true)]
        [TestCase("{}", nameof(WaxAnswerShape.Any), true)]
        // AtomicAssets / AtomicMarket: an object with "success": true and a "data" field.
        [TestCase("{\"success\":true,\"data\":[],\"query_time\":1}", nameof(WaxAnswerShape.AtomicEnvelope), true)]
        [TestCase("{\"success\":true,\"data\":{\"asset_id\":\"1\"}}", nameof(WaxAnswerShape.AtomicEnvelope), true)]
        [TestCase("{\"data\":null,\"success\":true}", nameof(WaxAnswerShape.AtomicEnvelope), true)]
        [TestCase("{\"success\":false}", nameof(WaxAnswerShape.AtomicEnvelope), false)]
        [TestCase("{\"success\":false,\"data\":[]}", nameof(WaxAnswerShape.AtomicEnvelope), false)]
        [TestCase("{\"success\":true}", nameof(WaxAnswerShape.AtomicEnvelope), false)]
        [TestCase("{\"data\":[]}", nameof(WaxAnswerShape.AtomicEnvelope), false)]
        [TestCase("{\"success\":\"true\",\"data\":[]}", nameof(WaxAnswerShape.AtomicEnvelope), false)]
        [TestCase("{\"success\":true,\"data\":[],\"success\":false}", nameof(WaxAnswerShape.AtomicEnvelope), false)]
        [TestCase("{\"x\":{\"success\":true,\"data\":[]}}", nameof(WaxAnswerShape.AtomicEnvelope), false)]
        [TestCase("[]", nameof(WaxAnswerShape.AtomicEnvelope), false)]
        [TestCase("[{\"success\":true,\"data\":[]}]", nameof(WaxAnswerShape.AtomicEnvelope), false)]
        [TestCase("{\"success\":true,\"data\":[]", nameof(WaxAnswerShape.AtomicEnvelope), false)]
        public void StatedShape(string text, string shape, bool expected) =>
            Assert.AreEqual(expected, WaxEndpointList.IsJsonAnswer(text, (WaxAnswerShape)System.Enum.Parse(typeof(WaxAnswerShape), shape)));
    }
}
