using System.Collections.Generic;
using Newtonsoft.Json;

namespace Gulkonster.WaxKit
{
    /// <summary>Arguments of /v1/chain/get_table_rows. Null fields are left out of the request.</summary>
    public sealed class WaxTableQuery
    {
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("scope")] public string Scope { get; set; }
        [JsonProperty("table")] public string Table { get; set; }
        [JsonProperty("lower_bound", NullValueHandling = NullValueHandling.Ignore)] public string LowerBound { get; set; }
        [JsonProperty("upper_bound", NullValueHandling = NullValueHandling.Ignore)] public string UpperBound { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; } = 10;
        /// <summary>"primary", "secondary", "2", "3", ...</summary>
        [JsonProperty("index_position", NullValueHandling = NullValueHandling.Ignore)] public string IndexPosition { get; set; }
        /// <summary>"name", "i64", "i128", "sha256", ...</summary>
        [JsonProperty("key_type", NullValueHandling = NullValueHandling.Ignore)] public string KeyType { get; set; }
        [JsonProperty("reverse")] public bool Reverse { get; set; }
        [JsonProperty("json")] public bool Json => true;
    }

    public sealed class WaxTableResult<T>
    {
        [JsonProperty("rows")] public List<T> Rows { get; set; } = new List<T>();
        [JsonProperty("more")] public bool More { get; set; }
        [JsonProperty("next_key")] public string NextKey { get; set; }
    }
}
