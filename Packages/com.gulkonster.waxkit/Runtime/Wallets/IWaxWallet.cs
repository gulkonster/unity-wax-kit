using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit
{
    /// <summary>
    /// A wallet the kit can log in with and sign through. Anchor and My Cloud Wallet are built in; register more with
    /// <see cref="WaxKit.RegisterWallet"/>. For any ESR wallet (Anchor-compatible), derive from <see cref="WaxEsrWallet"/>.
    /// </summary>
    public interface IWaxWallet
    {
        /// <summary>Unique id, used by <see cref="WaxKit.Login(string)"/> and stored with the session.</summary>
        string Id { get; }
        /// <summary>Name shown to the player ("Open {Name}").</summary>
        string Name { get; }

        /// <summary>True when the wallet works on this network. Use it to enable or disable the wallet's login button.</summary>
        bool SupportsChain(WaxNetwork network);

        /// <summary>Asks the player to log in. Wallet-private data in <see cref="WaxWalletSession.Data"/> is saved with the session.</summary>
        Task<WaxWalletSession> Login(WaxWalletContext context);

        /// <summary>Checks a saved session (no UI). Return false to drop it.</summary>
        Task<bool> Restore(WaxWalletContext context, WaxWalletSession session);

        /// <summary>Called by <see cref="WaxKit.Logout"/> after the kit forgot the session (free wallet resources here).</summary>
        Task Logout(WaxWalletContext context, WaxWalletSession session);

        /// <summary>
        /// Signs the transaction. Return the signatures (the kit broadcasts), optionally a modified transaction,
        /// or a transaction id when the wallet broadcast it itself.
        /// </summary>
        Task<WaxSignResult> Sign(WaxWalletContext context, WaxWalletSession session, WaxSignRequest request);
    }

    /// <summary>The account a wallet logged in, plus the wallet's own data (saved per chain).</summary>
    public sealed class WaxWalletSession
    {
        /// <summary>Account name.</summary>
        public string Actor { get; }
        /// <summary>Permission name ("active" when empty).</summary>
        public string Permission { get; }
        /// <summary>Wallet-private data saved with the session. Never put the player's account keys here.</summary>
        public JObject Data { get; }

        /// <summary>What a wallet's <see cref="IWaxWallet.Login"/> returns.</summary>
        public WaxWalletSession(string actor, string permission, JObject data = null)
        {
            if (string.IsNullOrEmpty(actor)) throw new ArgumentException("Actor is required.", nameof(actor));
            Actor = actor;
            Permission = string.IsNullOrEmpty(permission) ? "active" : permission;
            Data = data ?? new JObject();
        }
    }

    /// <summary>A transaction to sign.</summary>
    public sealed class WaxSignRequest
    {
        /// <summary>The chain the transaction is for.</summary>
        public string ChainId { get; }
        /// <summary>The packed transaction.</summary>
        public byte[] SerializedTransaction { get; }
        /// <summary>sha256(chain id + transaction + 32 zero bytes): what a K1 key signs.</summary>
        public byte[] SigningDigest { get; }
        /// <summary>The transaction as JSON, action data as hex.</summary>
        public JObject Transaction { get; }
        /// <summary>UTC time the transaction expires; the wallet must answer before it.</summary>
        public DateTime ExpiresAt { get; }

        internal WaxSignRequest(string chainId, byte[] serializedTransaction, byte[] signingDigest, JObject transaction, DateTime expiresAt)
        {
            ChainId = chainId;
            SerializedTransaction = serializedTransaction;
            SigningDigest = signingDigest;
            Transaction = transaction;
            ExpiresAt = expiresAt;
        }
    }

    /// <summary>What a wallet's <see cref="IWaxWallet.Sign"/> returns.</summary>
    public sealed class WaxSignResult
    {
        /// <summary>"SIG_K1_…" signatures.</summary>
        public IReadOnlyList<string> Signatures { get; }
        /// <summary>The transaction the wallet signed when it differs from the request (for example an added fee action), else null.</summary>
        public byte[] SerializedTransaction { get; }
        /// <summary>Set when the wallet already broadcast the transaction.</summary>
        public string TransactionId { get; }

        /// <summary>Signatures, plus the changed transaction or the transaction id when they apply.</summary>
        public WaxSignResult(IReadOnlyList<string> signatures, byte[] serializedTransaction = null, string transactionId = null)
        {
            Signatures = signatures ?? Array.Empty<string>();
            SerializedTransaction = serializedTransaction;
            TransactionId = transactionId;
        }
    }
}
