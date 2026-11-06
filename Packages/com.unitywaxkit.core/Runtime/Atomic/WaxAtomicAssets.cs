using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityWaxKit.Antelope;
using UnityWaxKit.Chain;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit
{
    /// <summary>
    /// Filters, paging and sort of an AtomicAssets / AtomicMarket list call. The properties cover the common ones;
    /// <see cref="Set"/> adds any other parameter the API documents (for example "state", "min_price", "match").
    /// </summary>
    public sealed class WaxAtomicQuery
    {
        readonly SortedDictionary<string, string> values = new SortedDictionary<string, string>();

        /// <summary>1-based page.</summary>
        public int? Page { get => Int("page"); set => Set("page", value?.ToString()); }
        /// <summary>Results per page (the API default is 100).</summary>
        public int? Limit { get => Int("limit"); set => Set("limit", value?.ToString()); }
        /// <summary>Sort column, for example "created", "asset_id", "price".</summary>
        public string Sort { get => Get("sort"); set => Set("sort", value); }
        /// <summary>"asc" or "desc".</summary>
        public string Order { get => Get("order"); set => Set("order", value); }
        /// <summary>Only assets of this account.</summary>
        public string Owner { get => Get("owner"); set => Set("owner", value); }
        /// <summary>Only this collection.</summary>
        public string CollectionName { get => Get("collection_name"); set => Set("collection_name", value); }
        /// <summary>Only this schema.</summary>
        public string SchemaName { get => Get("schema_name"); set => Set("schema_name", value); }
        /// <summary>Only this template.</summary>
        public string TemplateId { get => Get("template_id"); set => Set("template_id", value); }
        /// <summary>Comma-separated ids.</summary>
        public string Ids { get => Get("ids"); set => Set("ids", value); }
        /// <summary>Account for transfers (sender or recipient).</summary>
        public string Account { get => Get("account"); set => Set("account", value); }

        /// <summary>Sets any API parameter; null removes it.</summary>
        public WaxAtomicQuery Set(string name, string value)
        {
            if (value == null) values.Remove(name);
            else values[name] = value;
            return this;
        }

        /// <summary>The value of any API parameter, or null.</summary>
        public string Get(string name) => values.TryGetValue(name, out var value) ? value : null;

        internal string ToQueryString() =>
            values.Count == 0 ? "" : "?" + string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

        int? Int(string name) => int.TryParse(Get(name), out var value) ? value : (int?)null;
    }

    /// <summary>
    /// Read client for the AtomicAssets and AtomicMarket APIs (the data AtomicHub shows), on the network's
    /// <see cref="WaxNetwork.AtomicEndpoints"/> with the same failover as RPC. Get it from <see cref="WaxKit.AtomicAssets"/>.
    /// </summary>
    public sealed class WaxAtomicAssets
    {
        readonly WaxNetwork network;

        internal WaxAtomicAssets(WaxNetwork network) => this.network = network;

        // ---- AtomicAssets ----

        /// <summary>NFTs matching the query (for example <c>Owner</c> and <c>CollectionName</c>).</summary>
        public Task<List<WaxAtomicAsset>> GetAssets(WaxAtomicQuery query) => GetList<WaxAtomicAsset>("/atomicassets/v1/assets", query);
        /// <summary>One NFT by its asset id.</summary>
        public Task<WaxAtomicAsset> GetAsset(string assetId) => GetOne<WaxAtomicAsset>($"/atomicassets/v1/assets/{Escape(assetId)}");

        /// <summary>Templates matching the query.</summary>
        public Task<List<WaxAtomicTemplate>> GetTemplates(WaxAtomicQuery query) => GetList<WaxAtomicTemplate>("/atomicassets/v1/templates", query);
        /// <summary>One template of a collection.</summary>
        public Task<WaxAtomicTemplate> GetTemplate(string collectionName, string templateId) =>
            GetOne<WaxAtomicTemplate>($"/atomicassets/v1/templates/{Escape(collectionName)}/{Escape(templateId)}");

        /// <summary>Schemas matching the query.</summary>
        public Task<List<WaxAtomicSchema>> GetSchemas(WaxAtomicQuery query) => GetList<WaxAtomicSchema>("/atomicassets/v1/schemas", query);
        /// <summary>One schema of a collection.</summary>
        public Task<WaxAtomicSchema> GetSchema(string collectionName, string schemaName) =>
            GetOne<WaxAtomicSchema>($"/atomicassets/v1/schemas/{Escape(collectionName)}/{Escape(schemaName)}");

        /// <summary>Collections matching the query.</summary>
        public Task<List<WaxAtomicCollection>> GetCollections(WaxAtomicQuery query) => GetList<WaxAtomicCollection>("/atomicassets/v1/collections", query);
        /// <summary>One collection by name.</summary>
        public Task<WaxAtomicCollection> GetCollection(string collectionName) =>
            GetOne<WaxAtomicCollection>($"/atomicassets/v1/collections/{Escape(collectionName)}");

        /// <summary>Collections and asset counts an account holds.</summary>
        public Task<WaxAtomicAccount> GetAccount(string account, WaxAtomicQuery query = null) =>
            GetOne<WaxAtomicAccount>($"/atomicassets/v1/accounts/{Escape(account)}", query);

        /// <summary>NFT transfers matching the query (for example <c>Account</c>).</summary>
        public Task<List<WaxAtomicTransfer>> GetTransfers(WaxAtomicQuery query) => GetList<WaxAtomicTransfer>("/atomicassets/v1/transfers", query);

        // ---- AtomicMarket ----

        /// <summary>Sales; listings are <c>Set("state", "1")</c>.</summary>
        public Task<List<WaxAtomicSale>> GetSales(WaxAtomicQuery query) => GetList<WaxAtomicSale>("/atomicmarket/v2/sales", query);
        /// <summary>One sale by id.</summary>
        public Task<WaxAtomicSale> GetSale(string saleId) => GetOne<WaxAtomicSale>($"/atomicmarket/v1/sales/{Escape(saleId)}");

        /// <summary>Current listings (sales in state 1) matching the query.</summary>
        public Task<List<WaxAtomicSale>> GetListings(WaxAtomicQuery query) =>
            GetList<WaxAtomicSale>("/atomicmarket/v2/sales", Copy(query).Set("state", "1"));

        /// <summary>
        /// The cheapest current listing of a template (sales in state 1, lowest price first), or null when none.
        /// When the network's <see cref="WaxNetwork.CoreSymbol"/> is set, only sales paid in it count, and
        /// <see cref="WaxAtomicSale.Price"/> is the price in it. <see cref="WaxAtomicSale.ListingPrice"/> can be in another
        /// symbol (a listing priced in USD and paid in WAX).
        /// </summary>
        public Task<WaxAtomicSale> GetLowestListing(string templateId) => FirstSale(templateId, "1", "price", "asc");

        /// <summary>
        /// The latest sold sale of a template (sales in state 3, most recently updated first), or null when none.
        /// When the network's <see cref="WaxNetwork.CoreSymbol"/> is set, only sales paid in it count, and
        /// <see cref="WaxAtomicSale.Price"/> is what the buyer paid in it.
        /// </summary>
        public Task<WaxAtomicSale> GetLastSale(string templateId) => FirstSale(templateId, "3", "updated", "desc");

        async Task<WaxAtomicSale> FirstSale(string templateId, string state, string sort, string order)
        {
            if (string.IsNullOrEmpty(templateId)) throw new ArgumentException("A template id is required.", nameof(templateId));
            var query = new WaxAtomicQuery { TemplateId = templateId, Sort = sort, Order = order, Limit = 1 }
                .Set("state", state)
                .Set("symbol", string.IsNullOrEmpty(network.CoreSymbol) ? null : network.CoreSymbol);
            var sales = await GetSales(query);
            return sales.Count > 0 ? sales[0] : null;
        }

        /// <summary>Price statistics per template (median, average, min, max, sales).</summary>
        public Task<List<WaxAtomicTemplatePrice>> GetTemplatePrices(WaxAtomicQuery query) =>
            GetList<WaxAtomicTemplatePrice>("/atomicmarket/v1/prices/templates", query);

        // ---- Raw ----

        /// <summary>
        /// GET any AtomicAssets / AtomicMarket path (for example "/atomicmarket/v1/auctions") and returns the response JSON
        /// ({ success, data, query_time }).
        /// </summary>
        public Task<string> GetJson(string path, WaxAtomicQuery query = null) => GetJson(path, query, WaxAnswerShape.Any);

        /// <summary>
        /// As <see cref="GetJson(string, WaxAtomicQuery)"/>; a 2xx answer that is not <paramref name="shape"/> counts as a
        /// failed endpoint (the kit's own calls ask for the API's envelope: success true and a data field).
        /// </summary>
        internal async Task<string> GetJson(string path, WaxAtomicQuery query, WaxAnswerShape shape)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '/') throw new ArgumentException("Path must start with '/'.", nameof(path));
            var response = await network.Atomic.Send("GET", path + (query?.ToQueryString() ?? ""), null, shape: shape);
            if (response.IsSuccess) return response.Text;
            throw new WaxKitException(WaxErrorKind.Network, $"AtomicAssets {path}: HTTP {response.Status} {Message(response.Text)}");
        }

        async Task<List<T>> GetList<T>(string path, WaxAtomicQuery query) =>
            Data(await GetJson(path, query, WaxAnswerShape.AtomicEnvelope)).ToObject<List<T>>();

        async Task<T> GetOne<T>(string path, WaxAtomicQuery query = null) =>
            Data(await GetJson(path, query, WaxAnswerShape.AtomicEnvelope)).ToObject<T>();

        static JToken Data(string json)
        {
            var root = WaxJson.Parse(json);
            if ((bool?)root["success"] == false) throw new WaxKitException(WaxErrorKind.Network, "AtomicAssets: " + ((string)root["message"] ?? json));
            return root["data"] ?? JValue.CreateNull();
        }

        static string Message(string text)
        {
            try
            {
                return (string)WaxJson.Parse(text)["message"] ?? text;
            }
            catch (Exception)
            {
                return text;
            }
        }

        static WaxAtomicQuery Parse(string queryString)
        {
            var query = new WaxAtomicQuery();
            foreach (var pair in queryString.Split('&').Where(p => p.Length > 0))
            {
                var eq = pair.IndexOf('=');
                query.Set(Uri.UnescapeDataString(eq < 0 ? pair : pair.Substring(0, eq)), eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1)));
            }
            return query;
        }

        static WaxAtomicQuery Copy(WaxAtomicQuery query) => Parse((query?.ToQueryString() ?? "").TrimStart('?'));

        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("A value is required.");
            return Uri.EscapeDataString(value);
        }
    }
}
