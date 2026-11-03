using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityWaxKit.Antelope;
using UnityWaxKit.Platform;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit.Chain
{
    /// <summary>Chain API calls through the network's RPC endpoint list, plus transaction building.</summary>
    internal static class WaxChainApi
    {
        // ABIs per chain id + account, for the session.
        static readonly Dictionary<string, Task<Abi>> AbiCache = new Dictionary<string, Task<Abi>>();

        /// <summary>/v1/chain/get_table_rows: the raw JSON answer.</summary>
        public static Task<string> GetTableRows(WaxNetwork network, string queryJson) => PostText(network, "/v1/chain/get_table_rows", queryJson);

        /// <summary>/v1/chain/get_info: head block, chain id and time. Throws WrongChain when the endpoint is on another chain.</summary>
        public static async Task<JObject> GetInfo(WaxNetwork network, CancellationToken cancellation = default)
        {
            const string path = "/v1/chain/get_info";
            var response = await network.Rpc.Send("POST", path, "{}", cancellation, IsNodeosError);
            if (!response.IsSuccess) throw new WaxKitException(WaxErrorKind.Network, $"{path}: {NodeosError(response).message}");

            var info = WaxJson.Parse(response.Text);
            if (!string.Equals((string)info["chain_id"], network.ChainId, StringComparison.OrdinalIgnoreCase))
                throw new WaxKitException(WaxErrorKind.WrongChain, $"{response.Endpoint} is on another chain, not {network.Name}.");
            return info;
        }

        /// <summary>/v1/chain/get_account: permissions and keys of an account.</summary>
        public static async Task<JObject> GetAccount(WaxNetwork network, string account, CancellationToken cancellation = default) =>
            WaxJson.Parse(await PostText(network, "/v1/chain/get_account", new JObject { ["account_name"] = account }.ToString(), cancellation));

        /// <summary>A contract's ABI (fetched once per network and contract, then cached).</summary>
        public static Task<Abi> GetAbi(WaxNetwork network, string account)
        {
            var key = network.ChainId + ":" + account;
            lock (AbiCache)
            {
                if (AbiCache.TryGetValue(key, out var cached) && !cached.IsFaulted) return cached;
                var task = FetchAbi(network, account);
                AbiCache[key] = task;
                return task;
            }
        }

        static async Task<Abi> FetchAbi(WaxNetwork network, string account)
        {
            var json = WaxJson.Parse(await PostText(network, "/v1/chain/get_abi", new JObject { ["account_name"] = account }.ToString()));
            if (!(json["abi"] is JObject abi)) throw new WaxKitException(WaxErrorKind.Wallet, $"Account '{account}' has no contract ABI.");
            return Abi.FromJson(abi);
        }

        /// <summary>
        /// Builds the unsigned transaction: action data serialized through each contract's ABI, TaPoS from the last
        /// irreversible block and expiration head block time + 120 s (as WharfKit).
        /// </summary>
        public static async Task<JObject> BuildTransaction(WaxNetwork network, JArray actions, CancellationToken cancellation = default)
        {
            var packedActions = new JArray();
            foreach (JObject action in actions)
            {
                var account = (string)action["account"];
                var name = (string)action["name"];
                AntelopeName.Encode(account);
                AntelopeName.Encode(name);

                var abi = await GetAbi(network, account);
                var type = abi.ActionType(name) ?? throw new WaxKitException(WaxErrorKind.Wallet, $"Contract '{account}' has no action '{name}'.");
                byte[] data;
                try
                {
                    data = abi.Encode(type, action["data"]);
                }
                catch (FormatException e)
                {
                    throw new WaxKitException(WaxErrorKind.Wallet, $"{account}::{name}: {e.Message}");
                }

                packedActions.Add(new JObject
                {
                    ["account"] = account,
                    ["name"] = name,
                    ["authorization"] = action["authorization"],
                    ["data"] = Hex.Encode(data),
                });
            }

            var info = await GetInfo(network, cancellation);
            var headTime = AntelopeTime.Parse((string)info["head_block_time"]);
            var libId = Hex.Decode((string)info["last_irreversible_block_id"]);

            return new JObject
            {
                ["expiration"] = AntelopeTime.FormatSeconds(headTime + WaxKit.TransactionExpiry),
                ["ref_block_num"] = (long)info["last_irreversible_block_num"] & 0xffff,
                ["ref_block_prefix"] = BitConverter.ToUInt32(libId, 8),
                ["max_net_usage_words"] = 0,
                ["max_cpu_usage_ms"] = 0,
                ["delay_sec"] = 0,
                ["context_free_actions"] = new JArray(),
                ["actions"] = packedActions,
                ["transaction_extensions"] = new JArray(),
            };
        }

        /// <summary>Broadcasts a signed transaction; returns its id.</summary>
        public static async Task<string> SendTransaction(WaxNetwork network, byte[] packed, IEnumerable<string> signatures, CancellationToken cancellation = default)
        {
            var id = AntelopeTransaction.Id(packed);
            var body = new JObject
            {
                ["signatures"] = new JArray(signatures.ToArray()),
                ["compression"] = 0,
                ["packed_context_free_data"] = "",
                ["packed_trx"] = Hex.Encode(packed),
            }.ToString();

            var response = await network.Rpc.Send("POST", "/v1/chain/send_transaction", body, cancellation, IsNodeosError);
            if (response.IsSuccess) return (string)WaxJson.Parse(response.Text)["transaction_id"] ?? id;

            var error = NodeosError(response);
            // Accepted earlier through an endpoint that timed out.
            if (error.name == "tx_duplicate") return id;
            // A contract may have changed while the game runs: read its ABI again on the next try.
            ForgetAbis(network, packed);
            throw new WaxKitException(WaxErrorKind.Wallet, $"The chain rejected the transaction: {error.message}");
        }

        /// <summary>Removes the cached ABIs of the contracts a packed transaction calls.</summary>
        internal static void ForgetAbis(WaxNetwork network, byte[] packed)
        {
            JObject transaction;
            try
            {
                transaction = AntelopeTransaction.Decode(packed);
            }
            catch (FormatException)
            {
                return;
            }
            lock (AbiCache)
            {
                foreach (var action in ((JArray)transaction["actions"]).Concat((JArray)transaction["context_free_actions"]))
                    AbiCache.Remove(network.ChainId + ":" + (string)action["account"]);
            }
        }

        /// <summary>POSTs to a chain API path through the endpoint list (failover) and returns the answer text.</summary>
        public static async Task<string> PostText(WaxNetwork network, string path, string body, CancellationToken cancellation = default)
        {
            var response = await network.Rpc.Send("POST", path, body, cancellation, IsNodeosError);
            if (response.IsSuccess) return response.Text;
            throw new WaxKitException(WaxErrorKind.Network, $"{path}: {NodeosError(response).message}");
        }

        static bool IsNodeosError(WaxHttpResponse response)
        {
            try
            {
                return WaxJson.ParseToken(response.Text ?? "")["error"]?["code"] != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static (string name, string message) NodeosError(WaxHttpResponse response)
        {
            try
            {
                var error = WaxJson.ParseToken(response.Text)["error"];
                var detail = (string)error?["details"]?.FirstOrDefault()?["message"];
                return ((string)error?["name"], detail ?? (string)error?["what"] ?? response.Text);
            }
            catch (Exception)
            {
                return (null, $"HTTP {response.Status} {response.Text}");
            }
        }
    }
}
