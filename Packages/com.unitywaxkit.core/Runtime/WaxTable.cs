using System.Collections.Generic;
using Newtonsoft.Json;

namespace UnityWaxKit
{
    /// <summary>
    /// Arguments of a contract table read (the chain's /v1/chain/get_table_rows) for <see cref="WaxKit.GetTableRows{T}"/>.
    /// Null fields are left out of the request.
    /// </summary>
    /// <example><code>
    /// // The WAX balance of an account: table "accounts" of contract "eosio.token", scope = the account.
    /// var result = await WaxKit.GetTableRows&lt;JObject&gt;(new WaxTableQuery { Code = "eosio.token", Scope = "mygameplayer", Table = "accounts" });
    /// </code></example>
    public sealed class WaxTableQuery
    {
        /// <summary>The contract account that owns the table, for example "eosio.token".</summary>
        [JsonProperty("code")] public string Code { get; set; }
        /// <summary>The table's scope (often an account name, or the contract itself).</summary>
        [JsonProperty("scope")] public string Scope { get; set; }
        /// <summary>The table name from the contract's ABI, for example "accounts".</summary>
        [JsonProperty("table")] public string Table { get; set; }
        /// <summary>First key to return (inclusive), or null to start at the beginning.</summary>
        [JsonProperty("lower_bound", NullValueHandling = NullValueHandling.Ignore)] public string LowerBound { get; set; }
        /// <summary>Last key to return (inclusive), or null for no end.</summary>
        [JsonProperty("upper_bound", NullValueHandling = NullValueHandling.Ignore)] public string UpperBound { get; set; }
        /// <summary>Most rows to return (default 10). If <see cref="WaxTableResult{T}.More"/> is true, read on from <see cref="WaxTableResult{T}.NextKey"/>.</summary>
        [JsonProperty("limit")] public int Limit { get; set; } = 10;
        /// <summary>"primary", "secondary", "2", "3", ...</summary>
        [JsonProperty("index_position", NullValueHandling = NullValueHandling.Ignore)] public string IndexPosition { get; set; }
        /// <summary>"name", "i64", "i128", "sha256", ...</summary>
        [JsonProperty("key_type", NullValueHandling = NullValueHandling.Ignore)] public string KeyType { get; set; }
        /// <summary>True returns the rows from the last to the first.</summary>
        [JsonProperty("reverse")] public bool Reverse { get; set; }
        /// <summary>Always true: rows come back as JSON objects, not binary.</summary>
        [JsonProperty("json")] public bool Json => true;
    }

    /// <summary>Rows of a table read. <typeparamref name="T"/> is your row class, or JObject for any row.</summary>
    public sealed class WaxTableResult<T>
    {
        /// <summary>The rows, converted to <typeparamref name="T"/>.</summary>
        [JsonProperty("rows")] public List<T> Rows { get; set; } = new List<T>();
        /// <summary>True when the table has more rows than were returned.</summary>
        [JsonProperty("more")] public bool More { get; set; }
        /// <summary>When <see cref="More"/> is true: use it as the next <see cref="WaxTableQuery.LowerBound"/> to read on.</summary>
        [JsonProperty("next_key")] public string NextKey { get; set; }
    }
}
