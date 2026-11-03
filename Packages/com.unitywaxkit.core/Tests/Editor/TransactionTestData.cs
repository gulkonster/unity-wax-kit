using UnityWaxKit.Antelope;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit.Tests
{
    /// <summary>Builds transactions and actions for the tests (no chain needed).</summary>
    static class TransactionTestData
    {
        public const string Player = "player1111";
        public const string Permission = "active";

        static readonly Abi TokenAbi = Abi.FromJson(WaxJson.Parse(@"{ ""structs"": [
            { ""name"": ""transfer"", ""base"": """", ""fields"": [ { ""name"": ""from"", ""type"": ""name"" }, { ""name"": ""to"", ""type"": ""name"" },
              { ""name"": ""quantity"", ""type"": ""asset"" }, { ""name"": ""memo"", ""type"": ""string"" } ] },
            { ""name"": ""buyrambytes"", ""base"": """", ""fields"": [ { ""name"": ""payer"", ""type"": ""name"" }, { ""name"": ""receiver"", ""type"": ""name"" },
              { ""name"": ""bytes"", ""type"": ""uint32"" } ] } ] }"));

        /// <summary>A transaction with TaPoS set, in the JSON form a decoded transaction has.</summary>
        public static JObject Transaction(params JObject[] actions) => Normalize(new JObject
        {
            ["expiration"] = "2026-11-02T12:00:00",
            ["ref_block_num"] = 1234,
            ["ref_block_prefix"] = 56789,
            ["max_net_usage_words"] = 0,
            ["max_cpu_usage_ms"] = 0,
            ["delay_sec"] = 0,
            ["context_free_actions"] = new JArray(),
            ["actions"] = new JArray(actions),
            ["transaction_extensions"] = new JArray(),
        });

        /// <summary>Encodes and decodes, as the wallet code compares decoded transactions.</summary>
        public static JObject Normalize(JObject transaction) => AntelopeTransaction.Decode(AntelopeTransaction.Encode(transaction));

        public static JObject Action(string account, string name, string dataHex, string actor = Player, string permission = Permission) => new JObject
        {
            ["account"] = account,
            ["name"] = name,
            ["authorization"] = new JArray(new JObject { ["actor"] = actor, ["permission"] = permission }),
            ["data"] = dataHex,
        };

        /// <summary>A game action signed by the player.</summary>
        public static JObject GameAction(string data = "01") => Action("mygame", "play", data);

        public static JObject Transfer(string from, string to, string quantity, string memo, string actor = Player, string permission = Permission) =>
            Action("eosio.token", "transfer", Hex.Encode(TokenAbi.Encode("transfer",
                new JObject { ["from"] = from, ["to"] = to, ["quantity"] = quantity, ["memo"] = memo })), actor, permission);

        public static JObject Fee(string quantity = "0.00012345 WAX") => Transfer(Player, "txfee.wam", quantity, "WAX fee for mygame::play");

        public static JObject BuyRam(string payer = Player, string receiver = Player, uint bytes = 1024) =>
            Action("eosio", "buyrambytes", Hex.Encode(TokenAbi.Encode("buyrambytes",
                new JObject { ["payer"] = payer, ["receiver"] = receiver, ["bytes"] = bytes })));
    }
}
