using System.IO;
using Gulkonster.WaxKit.Crypto;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit.Antelope
{
    /// <summary>Types of transactions, signing requests (ESR v2 / v3), Anchor link and identity proofs.</summary>
    internal static class BuiltinAbi
    {
        /// <summary>Types of the chain itself (transaction, action) and of the ESR / Anchor link protocols.</summary>
        public static readonly Abi Instance = Abi.FromJson(WaxJson.Parse(@"{
  ""types"": [
    { ""new_type_name"": ""chain_alias"", ""type"": ""uint8"" },
    { ""new_type_name"": ""chain_id"", ""type"": ""checksum256"" },
    { ""new_type_name"": ""request_flags"", ""type"": ""uint8"" }
  ],
  ""structs"": [
    { ""name"": ""permission_level"", ""base"": """", ""fields"": [
      { ""name"": ""actor"", ""type"": ""name"" }, { ""name"": ""permission"", ""type"": ""name"" } ] },
    { ""name"": ""action"", ""base"": """", ""fields"": [
      { ""name"": ""account"", ""type"": ""name"" }, { ""name"": ""name"", ""type"": ""name"" },
      { ""name"": ""authorization"", ""type"": ""permission_level[]"" }, { ""name"": ""data"", ""type"": ""bytes"" } ] },
    { ""name"": ""extension"", ""base"": """", ""fields"": [
      { ""name"": ""type"", ""type"": ""uint16"" }, { ""name"": ""data"", ""type"": ""bytes"" } ] },
    { ""name"": ""transaction_header"", ""base"": """", ""fields"": [
      { ""name"": ""expiration"", ""type"": ""time_point_sec"" }, { ""name"": ""ref_block_num"", ""type"": ""uint16"" },
      { ""name"": ""ref_block_prefix"", ""type"": ""uint32"" }, { ""name"": ""max_net_usage_words"", ""type"": ""varuint32"" },
      { ""name"": ""max_cpu_usage_ms"", ""type"": ""uint8"" }, { ""name"": ""delay_sec"", ""type"": ""varuint32"" } ] },
    { ""name"": ""transaction"", ""base"": ""transaction_header"", ""fields"": [
      { ""name"": ""context_free_actions"", ""type"": ""action[]"" }, { ""name"": ""actions"", ""type"": ""action[]"" },
      { ""name"": ""transaction_extensions"", ""type"": ""extension[]"" } ] },
    { ""name"": ""identity_v2"", ""base"": """", ""fields"": [
      { ""name"": ""permission"", ""type"": ""permission_level?"" } ] },
    { ""name"": ""identity_v3"", ""base"": """", ""fields"": [
      { ""name"": ""scope"", ""type"": ""name"" }, { ""name"": ""permission"", ""type"": ""permission_level?"" } ] },
    { ""name"": ""info_pair"", ""base"": """", ""fields"": [
      { ""name"": ""key"", ""type"": ""string"" }, { ""name"": ""value"", ""type"": ""bytes"" } ] },
    { ""name"": ""signing_request_v2"", ""base"": """", ""fields"": [
      { ""name"": ""chain_id"", ""type"": ""variant_id"" }, { ""name"": ""req"", ""type"": ""variant_req_v2"" },
      { ""name"": ""flags"", ""type"": ""request_flags"" }, { ""name"": ""callback"", ""type"": ""string"" },
      { ""name"": ""info"", ""type"": ""info_pair[]"" } ] },
    { ""name"": ""signing_request_v3"", ""base"": """", ""fields"": [
      { ""name"": ""chain_id"", ""type"": ""variant_id"" }, { ""name"": ""req"", ""type"": ""variant_req_v3"" },
      { ""name"": ""flags"", ""type"": ""request_flags"" }, { ""name"": ""callback"", ""type"": ""string"" },
      { ""name"": ""info"", ""type"": ""info_pair[]"" } ] },
    { ""name"": ""request_signature"", ""base"": """", ""fields"": [
      { ""name"": ""signer"", ""type"": ""name"" }, { ""name"": ""signature"", ""type"": ""signature"" } ] },
    { ""name"": ""link_create"", ""base"": """", ""fields"": [
      { ""name"": ""session_name"", ""type"": ""name"" }, { ""name"": ""request_key"", ""type"": ""public_key"" },
      { ""name"": ""user_agent"", ""type"": ""string$"" } ] },
    { ""name"": ""link_info"", ""base"": """", ""fields"": [
      { ""name"": ""expiration"", ""type"": ""time_point_sec"" } ] },
    { ""name"": ""sealed_message"", ""base"": """", ""fields"": [
      { ""name"": ""from"", ""type"": ""public_key"" }, { ""name"": ""nonce"", ""type"": ""uint64"" },
      { ""name"": ""ciphertext"", ""type"": ""bytes"" }, { ""name"": ""checksum"", ""type"": ""uint32"" } ] },
    { ""name"": ""identity_proof"", ""base"": """", ""fields"": [
      { ""name"": ""chainId"", ""type"": ""chain_id"" }, { ""name"": ""scope"", ""type"": ""name"" },
      { ""name"": ""expiration"", ""type"": ""time_point_sec"" }, { ""name"": ""signer"", ""type"": ""permission_level"" },
      { ""name"": ""signature"", ""type"": ""signature"" } ] }
  ],
  ""variants"": [
    { ""name"": ""variant_id"", ""types"": [""chain_alias"", ""chain_id""] },
    { ""name"": ""variant_req_v2"", ""types"": [""action"", ""action[]"", ""transaction"", ""identity_v2""] },
    { ""name"": ""variant_req_v3"", ""types"": [""action"", ""action[]"", ""transaction"", ""identity_v3""] }
  ]
}"));
    }

    internal static class AntelopeTransaction
    {
        /// <summary>sha256(chain id + packed transaction + sha256 of the context-free data, here 32 zero bytes).</summary>
        public static byte[] SigningDigest(string chainId, byte[] packedTransaction)
        {
            var writer = new AbiWriter();
            writer.WriteBytes(Hex.Decode(chainId));
            writer.WriteBytes(packedTransaction);
            writer.WriteBytes(new byte[32]);
            return WaxCrypto.Sha256(writer.ToArray());
        }

        /// <summary>Transaction id: sha256 of the packed transaction, as hex.</summary>
        public static string Id(byte[] packedTransaction) => Hex.Encode(WaxCrypto.Sha256(packedTransaction));

        /// <summary>Packs a transaction.</summary>
        public static byte[] Encode(JObject transaction) => BuiltinAbi.Instance.Encode("transaction", transaction);
        /// <summary>Unpacks a transaction.</summary>
        public static JObject Decode(byte[] packed) => (JObject)BuiltinAbi.Instance.Decode("transaction", packed);
    }

    /// <summary>JSON parsing that keeps date-like strings as strings.</summary>
    internal static class WaxJson
    {
        /// <summary>Parses a JSON object without turning date-like strings into dates.</summary>
        public static JObject Parse(string json) => (JObject)ParseToken(json);

        /// <summary>Parses any JSON value the same way.</summary>
        public static JToken ParseToken(string json)
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double };
            return JToken.ReadFrom(reader);
        }
    }
}
