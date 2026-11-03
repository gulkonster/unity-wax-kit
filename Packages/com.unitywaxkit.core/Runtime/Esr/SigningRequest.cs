using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityWaxKit.Antelope;
using UnityWaxKit.Crypto;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit.Esr
{
    /// <summary>Which signing request library's layout new requests follow.</summary>
    internal enum EsrLayout
    {
        /// <summary>@wharfkit/signing-request: identity requests with a scope are version 3; known chains as chain aliases.</summary>
        WharfKit,
        /// <summary>
        /// @proton/signing-request 5.1 (used by @proton/link, read by WebAuth): every request is version 2, identity
        /// requests have no scope, and the chain is always the full chain id.
        /// </summary>
        ProtonLink,
    }

    /// <summary>
    /// EOSIO signing request (ESR) version 2 / 3, ported from @wharfkit/signing-request.
    /// <see cref="Data"/> is the signing_request_vN struct as JSON (see <see cref="BuiltinAbi"/>).
    /// </summary>
    internal sealed class SigningRequest
    {
        /// <summary>Placeholder the wallet replaces with the signing account.</summary>
        public const string PlaceholderName = "............1";
        /// <summary>Placeholder the wallet replaces with the signing permission.</summary>
        public const string PlaceholderPermission = "............2";

        const byte FlagBroadcast = 1;
        const byte FlagBackground = 2;

        static readonly string[] ChainAliases =
        {
            null,
            "aca376f206b8fc25a6ed44dbdc66547c36c6c33e3a119ffbeaef943642f0e906", // EOS
            "4667b205c6838ef70ff7988f6e8257e8be0e1284a2f59699054a018f743b1d11", // Telos
            "e70aaab8997e1dfce58fbfac80cbbb8fecec7b99cf982a9444273cbc64c41473", // Jungle
            "5fff1dae8dc8e2fc4d5b23b2c7665c97f9e9d8edf2b6485a86ba311c25639191", // Kylin
            "73647cde120091e0a4b85bced2f3cfdb3041e266cbbe95cee59b73235a1b3b6f", // Worbli
            "d5a3d18fbb3c084e3b1f3fa98c21014b5f3db536cc15d08f9f6479517c6a3d86", // BOS
            "cfe6486a83bad4962f232d48003b1824ab5665c36778141034d75e57b956e422", // MeetOne
            "b042025541e25a472bffde2d62edd457b7e70cee943412b1ea0f044f88591664", // Insights
            "b912d19a6abd2b1b05611ae5be473355d64d95aeff0c09bedc8c166cd6468fe4", // BEOS
            "1064487b3cd1a897ce03ae5b6a865651747e2e152090f99c1d19d44e01aea5a4", // WAX
            "384da888112027f0321850a169f737c33e53b388aad48b5adace4bab97f437e0", // Proton
            "21dcae42c0182200e93f954a074011f9048a7624c6fe81d3c9541a614a88bd1c", // FIO
        };

        /// <summary>ESR protocol version (2 or 3).</summary>
        public int Version { get; }
        /// <summary>The request fields (chain, request, flags, callback, info) as JSON.</summary>
        public JObject Data { get; }
        /// <summary>request_signature JSON, or null.</summary>
        public JObject Signature { get; set; }

        SigningRequest(int version, JObject data, JObject signature = null)
        {
            Version = version;
            Data = data;
            Signature = signature;
            if (IsIdentity && Broadcast) throw new FormatException("Invalid request (identity request cannot be broadcast).");
        }

        string StorageType => Version == 2 ? "signing_request_v2" : "signing_request_v3";

        // ---- Creating ----

        /// <summary>
        /// Identity (login) request. A scope makes it version 3, as WharfKit does; with <see cref="EsrLayout.ProtonLink"/>
        /// it is always version 2 without a scope.
        /// </summary>
        public static SigningRequest Identity(string chainId, string callbackUrl, bool background, string scope,
            IEnumerable<KeyValuePair<string, byte[]>> info, EsrLayout layout = EsrLayout.WharfKit)
        {
            var version = scope != null && layout == EsrLayout.WharfKit ? 3 : 2;
            var identity = version == 3
                ? new JObject { ["scope"] = scope, ["permission"] = null }
                : new JObject { ["permission"] = null };
            return Create(version, chainId, new JArray(version == 3 ? "identity_v3" : "identity_v2", identity), false, callbackUrl, background,
                info, layout);
        }

        /// <summary>Transaction request. Actions in <paramref name="transaction"/> carry hex-encoded data.</summary>
        public static SigningRequest Transaction(string chainId, JObject transaction, bool broadcast, string callbackUrl, bool background,
            IEnumerable<KeyValuePair<string, byte[]>> info = null, EsrLayout layout = EsrLayout.WharfKit) =>
            Create(2, chainId, new JArray("transaction", transaction), broadcast, callbackUrl, background, info, layout);

        static SigningRequest Create(int version, string chainId, JArray req, bool broadcast, string callbackUrl, bool background,
            IEnumerable<KeyValuePair<string, byte[]>> info, EsrLayout layout)
        {
            var flags = (broadcast ? FlagBroadcast : 0) | (background ? FlagBackground : 0);
            var infoPairs = new JArray();
            foreach (var pair in info ?? Enumerable.Empty<KeyValuePair<string, byte[]>>())
                infoPairs.Add(new JObject { ["key"] = pair.Key, ["value"] = Hex.Encode(pair.Value) });

            return new SigningRequest(version, new JObject
            {
                ["chain_id"] = ChainVariant(chainId, layout),
                ["req"] = req,
                ["flags"] = flags,
                ["callback"] = callbackUrl ?? "",
                ["info"] = infoPairs,
            });
        }

        static JArray ChainVariant(string chainId, EsrLayout layout)
        {
            var alias = layout == EsrLayout.WharfKit ? Array.IndexOf(ChainAliases, chainId.ToLowerInvariant()) : -1;
            return alias > 0 ? new JArray("chain_alias", alias) : new JArray("chain_id", chainId.ToLowerInvariant());
        }

        // ---- Encoding ----

        /// <summary>Reads an esr: link (compressed or not).</summary>
        public static SigningRequest FromUri(string uri)
        {
            var colon = uri?.IndexOf(':') ?? -1;
            if (colon < 0) throw new FormatException("Invalid request uri.");
            var path = uri.Substring(colon + 1);
            if (path.StartsWith("//", StringComparison.Ordinal)) path = path.Substring(2);
            return FromData(Base64u.Decode(path));
        }

        /// <summary>Reads the binary form (header byte + payload).</summary>
        public static SigningRequest FromData(byte[] data)
        {
            var header = data[0];
            var version = header & 0x7f;
            if (version != 2 && version != 3) throw new FormatException("Unsupported signing request version.");

            var payload = Base58.Slice(data, 1, data.Length - 1);
            if ((header & 0x80) != 0) payload = WaxCrypto.InflateRaw(payload);

            var reader = new AbiReader(payload);
            var abi = BuiltinAbi.Instance;
            var request = (JObject)abi.Read(reader, version == 2 ? "signing_request_v2" : "signing_request_v3");
            var signature = reader.CanRead ? (JObject)abi.Read(reader, "request_signature") : null;
            return new SigningRequest(version, request, signature);
        }

        /// <summary>The request payload in binary.</summary>
        public byte[] GetData() => BuiltinAbi.Instance.Encode(StorageType, Data);

        /// <summary>The request signature in binary (empty when unsigned).</summary>
        public byte[] GetSignatureData() => Signature == null ? new byte[0] : BuiltinAbi.Instance.Encode("request_signature", Signature);

        /// <summary>"esr:" + base64u(header + [deflated] data). Compressed only when smaller.</summary>
        public string Encode(bool compress = true, bool slashes = false, string scheme = "esr:")
        {
            var header = (byte)Version;
            var array = Base58.Concat(GetData(), GetSignatureData());
            if (compress)
            {
                var deflated = WaxCrypto.DeflateRaw(array);
                if (array.Length > deflated.Length)
                {
                    header |= 0x80;
                    array = deflated;
                }
            }
            return scheme + (slashes ? "//" : "") + Base64u.Encode(Base58.Concat(new[] { header }, array));
        }

        public override string ToString() => Encode();

        /// <summary>A deep copy.</summary>
        public SigningRequest Clone() => new SigningRequest(Version, (JObject)Data.DeepClone(), (JObject)Signature?.DeepClone());

        // ---- Fields ----

        /// <summary>True for a login (identity) request.</summary>
        public bool IsIdentity => ((string)Data["req"][0]).StartsWith("identity", StringComparison.Ordinal);
        /// <summary>True when the wallet should broadcast the transaction itself.</summary>
        public bool Broadcast => ((int)Data["flags"] & FlagBroadcast) != 0;
        /// <summary>True when the wallet answers the callback in the background (no browser).</summary>
        public bool Background => ((int)Data["flags"] & FlagBackground) != 0;
        /// <summary>Where the wallet sends its answer.</summary>
        public string Callback => (string)Data["callback"];

        /// <summary>Sets the callback URL and whether it is called in the background.</summary>
        public void SetCallback(string url, bool background)
        {
            Data["callback"] = url;
            Data["flags"] = ((int)Data["flags"] & ~FlagBackground) | (background ? FlagBackground : 0);
        }

        /// <summary>The chain id the request is for.</summary>
        public string ChainId
        {
            get
            {
                var variant = (JArray)Data["chain_id"];
                if ((string)variant[0] == "chain_id") return (string)variant[1];
                var alias = (int)variant[1];
                if (alias <= 0 || alias >= ChainAliases.Length) throw new FormatException($"Unknown chain alias {alias}.");
                return ChainAliases[alias];
            }
        }

        /// <summary>True when the request allows several chains.</summary>
        public bool IsMultiChain
        {
            get
            {
                var variant = (JArray)Data["chain_id"];
                return (string)variant[0] == "chain_alias" && (int)variant[1] == 0;
            }
        }

        /// <summary>The scope of an identity request (v3), else null.</summary>
        public string IdentityScope => Version > 2 && IsIdentity ? (string)Data["req"][1]["scope"] : null;

        /// <summary>A value of the info list, or null.</summary>
        public byte[] GetInfoKey(string key)
        {
            var pair = ((JArray)Data["info"]).FirstOrDefault(p => (string)p["key"] == key);
            return pair == null ? null : Hex.Decode((string)pair["value"]);
        }

        /// <summary>A text value of the info list, or null.</summary>
        public string GetInfoString(string key)
        {
            var value = GetInfoKey(key);
            return value == null ? null : Encoding.UTF8.GetString(value);
        }

        /// <summary>Sets (or replaces) a value of the info list.</summary>
        public void SetInfoKey(string key, byte[] value)
        {
            var info = (JArray)Data["info"];
            var pair = info.FirstOrDefault(p => (string)p["key"] == key);
            if (pair == null) info.Add(new JObject { ["key"] = key, ["value"] = Hex.Encode(value) });
            else pair["value"] = Hex.Encode(value);
        }

        /// <summary>Strings are stored as raw UTF-8, as WharfKit's setInfoKey.</summary>
        public void SetInfoString(string key, string value) => SetInfoKey(key, Encoding.UTF8.GetBytes(value));

        /// <summary>Booleans are stored as one byte.</summary>
        public void SetInfoBool(string key, bool value) => SetInfoKey(key, new[] { value ? (byte)1 : (byte)0 });

        // ---- Resolving ----

        /// <summary>
        /// The transaction for <paramref name="actor"/>@<paramref name="permission"/>. Placeholders are resolved in authorizations
        /// (action data is used as is). TaPoS comes from the request, or from <paramref name="expiration"/> / ref block when it has none.
        /// </summary>
        public ResolvedSigningRequest Resolve(string actor, string permission, DateTime? expiration = null,
            ushort refBlockNum = 0, uint refBlockPrefix = 0, string chainId = null)
        {
            JObject transaction;
            var req = (JArray)Data["req"];
            var kind = (string)req[0];

            if (kind == "transaction")
            {
                transaction = (JObject)req[1].DeepClone();
            }
            else
            {
                transaction = EmptyTransaction();
                var actions = (JArray)transaction["actions"];
                if (kind == "action") actions.Add(req[1].DeepClone());
                else if (kind == "action[]") foreach (var a in (JArray)req[1]) actions.Add(a.DeepClone());
                else actions.Add(IdentityAction(actor, permission));
            }

            var hasTapos = !(AntelopeTime.ToSeconds(AntelopeTime.Parse((string)transaction["expiration"])) == 0
                             && (int)transaction["ref_block_num"] == 0 && (long)transaction["ref_block_prefix"] == 0);
            if (!IsIdentity && !hasTapos)
            {
                if (expiration == null) throw new InvalidOperationException("The request needs a reference block or explicit TaPoS values.");
                transaction["expiration"] = AntelopeTime.FormatSeconds(expiration.Value);
                transaction["ref_block_num"] = refBlockNum;
                transaction["ref_block_prefix"] = refBlockPrefix;
            }
            else if (IsIdentity && Version > 2)
            {
                // From ESR version 3 every identity request has an expiration.
                transaction["expiration"] = AntelopeTime.FormatSeconds(expiration ?? DateTime.UtcNow.AddSeconds(60));
            }

            foreach (var action in (JArray)transaction["actions"])
            foreach (var auth in (JArray)action["authorization"])
            {
                if ((string)auth["actor"] == PlaceholderName) auth["actor"] = actor;
                var perm = (string)auth["permission"];
                // An actor placeholder in a permission also resolves to the permission (backwards compatibility).
                if (perm == PlaceholderPermission || perm == PlaceholderName) auth["permission"] = permission;
            }

            string resolvedChain;
            if (IsMultiChain)
            {
                resolvedChain = chainId ?? throw new InvalidOperationException("Missing chosen chain id for a multi-chain request.");
            }
            else
            {
                resolvedChain = ChainId;
            }

            return new ResolvedSigningRequest(this, actor, permission, transaction, resolvedChain);
        }

        JObject IdentityAction(string actor, string permission)
        {
            var level = new JObject { ["actor"] = actor, ["permission"] = permission };
            var data = Version == 2
                ? BuiltinAbi.Instance.Encode("identity_v2", new JObject { ["permission"] = level })
                : BuiltinAbi.Instance.Encode("identity_v3", new JObject { ["scope"] = IdentityScope, ["permission"] = level });
            return new JObject
            {
                ["account"] = "",
                ["name"] = "identity",
                ["authorization"] = new JArray(level.DeepClone()),
                ["data"] = Hex.Encode(data),
            };
        }

        internal static JObject EmptyTransaction() => new JObject
        {
            ["expiration"] = "1970-01-01T00:00:00",
            ["ref_block_num"] = 0,
            ["ref_block_prefix"] = 0,
            ["max_net_usage_words"] = 0,
            ["max_cpu_usage_ms"] = 0,
            ["delay_sec"] = 0,
            ["context_free_actions"] = new JArray(),
            ["actions"] = new JArray(),
            ["transaction_extensions"] = new JArray(),
        };
    }

    internal sealed class ResolvedSigningRequest
    {
        /// <summary>The original request.</summary>
        public SigningRequest Request { get; }
        /// <summary>Account that signed.</summary>
        public string Actor { get; }
        /// <summary>Permission that signed.</summary>
        public string Permission { get; }
        /// <summary>The resolved transaction.</summary>
        public JObject Transaction { get; }
        /// <summary>The chain id the request is for.</summary>
        public string ChainId { get; }
        /// <summary>The resolved transaction in binary.</summary>
        public byte[] SerializedTransaction { get; }
        /// <summary>What the signer signed.</summary>
        public byte[] SigningDigest => AntelopeTransaction.SigningDigest(ChainId, SerializedTransaction);
        /// <summary>Id of the resolved transaction.</summary>
        public string TransactionId => AntelopeTransaction.Id(SerializedTransaction);

        internal ResolvedSigningRequest(SigningRequest request, string actor, string permission, JObject transaction, string chainId)
        {
            Request = request;
            Actor = actor;
            Permission = permission;
            Transaction = transaction;
            ChainId = chainId;
            SerializedTransaction = AntelopeTransaction.Encode(transaction);
        }

        /// <summary>The callback payload a wallet sends after signing (see CallbackPayload in WharfKit).</summary>
        public JObject GetCallbackPayload(IList<string> signatures, uint? blockNum = null)
        {
            if (signatures == null || signatures.Count == 0) throw new ArgumentException("At least one signature is required.");
            var payload = new JObject
            {
                ["sig"] = signatures[0],
                ["tx"] = TransactionId,
                ["rbn"] = ((int)Transaction["ref_block_num"]).ToString(),
                ["rid"] = ((long)Transaction["ref_block_prefix"]).ToString(),
                ["ex"] = (string)Transaction["expiration"],
                ["req"] = Request.Encode(true, true),
                ["sa"] = Actor,
                ["sp"] = Permission,
                ["cid"] = ChainId,
            };
            for (var i = 1; i < signatures.Count; i++) payload[$"sig{i - 1}"] = signatures[i];
            if (blockNum != null) payload["bn"] = blockNum.Value.ToString();
            return payload;
        }

        /// <summary>Rebuilds the signed transaction from a wallet callback payload.</summary>
        public static ResolvedSigningRequest FromPayload(JObject payload)
        {
            var request = SigningRequest.FromUri((string)payload["req"]);
            var expiration = payload["ex"] != null ? AntelopeTime.Parse((string)payload["ex"]) : (DateTime?)null;
            ushort.TryParse((string)payload["rbn"], out var refBlockNum);
            uint.TryParse((string)payload["rid"], out var refBlockPrefix);
            return request.Resolve((string)payload["sa"], (string)payload["sp"], expiration, refBlockNum, refBlockPrefix,
                (string)payload["cid"] ?? (request.IsMultiChain ? null : request.ChainId));
        }

        /// <summary>All signatures of a callback payload: sig, then sig0, sig1, … (deduplicated).</summary>
        public static List<string> Signatures(JObject payload)
        {
            var result = new List<string>();
            if (payload["sig"] != null) result.Add((string)payload["sig"]);
            var indexed = payload.Properties()
                .Select(p => new { p, match = System.Text.RegularExpressions.Regex.Match(p.Name, @"^sig(\d+)$") })
                .Where(x => x.match.Success)
                .OrderBy(x => int.Parse(x.match.Groups[1].Value));
            foreach (var x in indexed)
                if (!string.IsNullOrEmpty((string)x.p.Value)) result.Add((string)x.p.Value);
            return result.Distinct().ToList();
        }
    }

    /// <summary>URL-safe base64 without padding.</summary>
    internal static class Base64u
    {
        /// <summary>URL-safe base64 without padding.</summary>
        public static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>Reads URL-safe base64 (padding optional).</summary>
        public static byte[] Decode(string text)
        {
            var normal = text.Replace('-', '+').Replace('_', '/');
            switch (normal.Length % 4)
            {
                case 2: normal += "=="; break;
                case 3: normal += "="; break;
            }
            return Convert.FromBase64String(normal);
        }
    }
}
