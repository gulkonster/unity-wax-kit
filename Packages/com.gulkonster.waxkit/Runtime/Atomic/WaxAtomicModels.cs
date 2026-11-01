using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit
{
    /// <summary>Fields of AtomicAssets API objects that are not modelled stay in <see cref="Extra"/>.</summary>
    public abstract class WaxAtomicModel
    {
        /// <summary>Every field of the API answer that has no property here, by its JSON name.</summary>
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
    }

    /// <summary>An NFT collection (its name, author, image and settings). Property names follow the API fields.</summary>
    public sealed class WaxAtomicCollection : WaxAtomicModel
    {
        [JsonProperty("collection_name")] public string CollectionName { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary>IPFS hash of the collection image.</summary>
        [JsonProperty("img")] public string Image { get; set; }
        [JsonProperty("author")] public string Author { get; set; }
        [JsonProperty("allow_notify")] public bool AllowNotify { get; set; }
        [JsonProperty("authorized_accounts")] public List<string> AuthorizedAccounts { get; set; }
        [JsonProperty("market_fee")] public double MarketFee { get; set; }
        [JsonProperty("data")] public JObject Data { get; set; }
        [JsonProperty("created_at_time")] public string CreatedAtTime { get; set; }
    }

    /// <summary>One attribute of a schema: its name and type (for example "name", "string").</summary>
    public sealed class WaxAtomicSchemaField
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
    }

    /// <summary>A schema: the list of attributes the NFTs of a collection have.</summary>
    public sealed class WaxAtomicSchema : WaxAtomicModel
    {
        [JsonProperty("schema_name")] public string SchemaName { get; set; }
        [JsonProperty("format")] public List<WaxAtomicSchemaField> Format { get; set; }
        [JsonProperty("collection")] public WaxAtomicCollection Collection { get; set; }
        [JsonProperty("created_at_time")] public string CreatedAtTime { get; set; }
    }

    /// <summary>A template: shared, unchangeable data of a group of NFTs (for example one card type) and its supply.</summary>
    public sealed class WaxAtomicTemplate : WaxAtomicModel
    {
        [JsonProperty("template_id")] public string TemplateId { get; set; }
        [JsonProperty("collection")] public WaxAtomicCollection Collection { get; set; }
        [JsonProperty("schema")] public WaxAtomicSchema Schema { get; set; }
        [JsonProperty("immutable_data")] public JObject ImmutableData { get; set; }
        [JsonProperty("is_transferable")] public bool IsTransferable { get; set; }
        [JsonProperty("is_burnable")] public bool IsBurnable { get; set; }
        /// <summary>0 = unlimited.</summary>
        [JsonProperty("max_supply")] public string MaxSupply { get; set; }
        [JsonProperty("issued_supply")] public string IssuedSupply { get; set; }
        [JsonProperty("created_at_time")] public string CreatedAtTime { get; set; }
    }

    /// <summary>One NFT: id, owner, collection, schema, template and its data (<see cref="Data"/> has everything merged).</summary>
    public sealed class WaxAtomicAsset : WaxAtomicModel
    {
        [JsonProperty("asset_id")] public string AssetId { get; set; }
        [JsonProperty("owner")] public string Owner { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("is_transferable")] public bool IsTransferable { get; set; }
        [JsonProperty("is_burnable")] public bool IsBurnable { get; set; }
        [JsonProperty("collection")] public WaxAtomicCollection Collection { get; set; }
        [JsonProperty("schema")] public WaxAtomicSchema Schema { get; set; }
        /// <summary>Null for assets without a template.</summary>
        [JsonProperty("template")] public WaxAtomicTemplate Template { get; set; }
        [JsonProperty("mutable_data")] public JObject MutableData { get; set; }
        [JsonProperty("immutable_data")] public JObject ImmutableData { get; set; }
        /// <summary>Template, immutable and mutable data merged.</summary>
        [JsonProperty("data")] public JObject Data { get; set; }
        [JsonProperty("template_mint")] public string TemplateMint { get; set; }
        [JsonProperty("minted_at_time")] public string MintedAtTime { get; set; }
    }

    /// <summary>An NFT transfer between two accounts, with the assets moved.</summary>
    public sealed class WaxAtomicTransfer : WaxAtomicModel
    {
        [JsonProperty("transfer_id")] public string TransferId { get; set; }
        [JsonProperty("sender_name")] public string Sender { get; set; }
        [JsonProperty("recipient_name")] public string Recipient { get; set; }
        [JsonProperty("memo")] public string Memo { get; set; }
        [JsonProperty("txid")] public string TransactionId { get; set; }
        [JsonProperty("assets")] public List<WaxAtomicAsset> Assets { get; set; }
        [JsonProperty("created_at_time")] public string CreatedAtTime { get; set; }
    }

    /// <summary>A price in a token (amount as an integer string, see <see cref="TokenPrecision"/>).</summary>
    public sealed class WaxAtomicPrice : WaxAtomicModel
    {
        [JsonProperty("token_contract")] public string TokenContract { get; set; }
        [JsonProperty("token_symbol")] public string TokenSymbol { get; set; }
        [JsonProperty("token_precision")] public int TokenPrecision { get; set; }
        /// <summary>Integer amount; divide by 10^<see cref="TokenPrecision"/>.</summary>
        [JsonProperty("amount")] public string Amount { get; set; }
        [JsonProperty("median")] public string Median { get; set; }
    }

    /// <summary>An AtomicMarket sale (a listing while <see cref="State"/> is 1).</summary>
    public sealed class WaxAtomicSale : WaxAtomicModel
    {
        [JsonProperty("sale_id")] public string SaleId { get; set; }
        [JsonProperty("seller")] public string Seller { get; set; }
        [JsonProperty("buyer")] public string Buyer { get; set; }
        [JsonProperty("offer_id")] public string OfferId { get; set; }
        [JsonProperty("price")] public WaxAtomicPrice Price { get; set; }
        [JsonProperty("listing_price")] public string ListingPrice { get; set; }
        [JsonProperty("listing_symbol")] public string ListingSymbol { get; set; }
        [JsonProperty("assets")] public List<WaxAtomicAsset> Assets { get; set; }
        [JsonProperty("collection_name")] public string CollectionName { get; set; }
        /// <summary>0 waiting, 1 listed, 2 cancelled, 3 sold, 4 invalid.</summary>
        [JsonProperty("state")] public int State { get; set; }
        [JsonProperty("created_at_time")] public string CreatedAtTime { get; set; }
    }

    /// <summary>Price statistics of a template (atomicmarket/v1/prices/templates).</summary>
    public sealed class WaxAtomicTemplatePrice : WaxAtomicModel
    {
        [JsonProperty("collection_name")] public string CollectionName { get; set; }
        [JsonProperty("template_id")] public string TemplateId { get; set; }
        [JsonProperty("token_symbol")] public string TokenSymbol { get; set; }
        [JsonProperty("token_precision")] public int TokenPrecision { get; set; }
        [JsonProperty("median")] public string Median { get; set; }
        [JsonProperty("average")] public string Average { get; set; }
        [JsonProperty("min")] public string Min { get; set; }
        [JsonProperty("max")] public string Max { get; set; }
        [JsonProperty("sales")] public string Sales { get; set; }
        [JsonProperty("suggested_median")] public string SuggestedMedian { get; set; }
    }

    /// <summary>One collection in an account summary and how many of its assets the account holds.</summary>
    public sealed class WaxAtomicAccountCollection
    {
        [JsonProperty("collection")] public WaxAtomicCollection Collection { get; set; }
        [JsonProperty("assets")] public string Assets { get; set; }
    }

    /// <summary>atomicassets/v1/accounts/{account}: what an account holds.</summary>
    public sealed class WaxAtomicAccount : WaxAtomicModel
    {
        [JsonProperty("collections")] public List<WaxAtomicAccountCollection> Collections { get; set; }
        [JsonProperty("assets")] public string Assets { get; set; }
    }
}
