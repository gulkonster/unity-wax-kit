using System;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit
{
    public sealed class WaxPermissionLevel
    {
        public string Actor { get; }
        public string Permission { get; }

        public WaxPermissionLevel(string actor, string permission)
        {
            Actor = actor;
            Permission = permission;
        }
    }

    /// <summary>One contract action. <see cref="Data"/> is any object Newtonsoft can serialize.</summary>
    public sealed class WaxAction
    {
        public string Account { get; }
        public string Name { get; }
        public object Data { get; }

        /// <summary>Leave null to sign as the session's actor@permission.</summary>
        public WaxPermissionLevel[] Authorization { get; set; }

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

    public sealed class WaxTransactResult
    {
        public string TransactionId { get; }

        public WaxTransactResult(string transactionId)
        {
            TransactionId = transactionId;
        }
    }
}
