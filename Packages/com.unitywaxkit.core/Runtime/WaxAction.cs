using System;
using Newtonsoft.Json.Linq;

namespace UnityWaxKit
{
    /// <summary>Who authorizes an action: an account and one of its permissions ("active", "owner" or a custom one).</summary>
    public sealed class WaxPermissionLevel
    {
        /// <summary>Account name, for example "mygameplayer".</summary>
        public string Actor { get; }
        /// <summary>Permission name, usually "active".</summary>
        public string Permission { get; }

        /// <summary>Makes a permission level, for example <c>new WaxPermissionLevel("mygameplayer", "active")</c>.</summary>
        public WaxPermissionLevel(string actor, string permission)
        {
            Actor = actor;
            Permission = permission;
        }
    }

    /// <summary>
    /// One contract action for <see cref="WaxKit.Transact"/>: which contract, which action and its data.
    /// <see cref="Data"/> is any object Newtonsoft can serialize (an anonymous object is the easiest); its field names
    /// must match the contract's ABI, and the kit turns it into the binary form the chain expects.
    /// </summary>
    /// <example><code>
    /// var transfer = new WaxAction("eosio.token", "transfer", new
    /// {
    ///     from = WaxKit.Current.Actor,
    ///     to = "someaccount1",
    ///     quantity = "0.00010000 WAX",   // WAX has 8 decimals
    ///     memo = "hello",
    /// });
    /// </code></example>
    public sealed class WaxAction
    {
        /// <summary>The contract account, for example "eosio.token" or "atomicassets".</summary>
        public string Account { get; }
        /// <summary>The action name, for example "transfer".</summary>
        public string Name { get; }
        /// <summary>The action's arguments (anonymous object, class, dictionary or JObject).</summary>
        public object Data { get; }

        /// <summary>Leave null to sign as the session's actor@permission.</summary>
        public WaxPermissionLevel[] Authorization { get; set; }

        /// <summary>Makes an action. <paramref name="account"/> and <paramref name="name"/> are required.</summary>
        public WaxAction(string account, string name, object data)
        {
            if (string.IsNullOrEmpty(account)) throw new ArgumentException("Account is required.", nameof(account));
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name is required.", nameof(name));

            Account = account;
            Name = name;
            Data = data;
        }

        internal JObject ToJson(WaxSession session)
        {
            var authorization = new JArray();
            if (Authorization == null || Authorization.Length == 0)
            {
                authorization.Add(new JObject { ["actor"] = session.Actor, ["permission"] = session.Permission });
            }
            else
            {
                foreach (var level in Authorization)
                    authorization.Add(new JObject { ["actor"] = level.Actor, ["permission"] = level.Permission });
            }

            return new JObject
            {
                ["account"] = Account,
                ["name"] = Name,
                ["authorization"] = authorization,
                ["data"] = Data == null ? new JObject() : JToken.FromObject(Data),
            };
        }
    }

    /// <summary>What <see cref="WaxKit.Transact"/> returns once the chain accepted the transaction.</summary>
    public sealed class WaxTransactResult
    {
        /// <summary>The transaction id (64 hex characters). Look it up on a block explorer, for example waxblock.io.</summary>
        public string TransactionId { get; }

        /// <summary>Makes a result (the kit does this for you).</summary>
        public WaxTransactResult(string transactionId)
        {
            TransactionId = transactionId;
        }
    }
}
