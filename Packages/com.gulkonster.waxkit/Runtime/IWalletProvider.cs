using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit
{
    /// <summary>Platform wallet code. Add Android / Windows providers here without changing <see cref="WaxKit"/>.</summary>
    internal interface IWalletProvider
    {
        void Initialize(WaxNetwork network, string appName);
        Task<WaxSession> Login(WalletType wallet);
        /// <summary>The saved session for the current network, or null.</summary>
        Task<WaxSession> Restore();
        Task Logout();
        /// <summary>Signs and broadcasts the actions; returns the transaction id.</summary>
        Task<string> Transact(JArray actions);
    }

    internal sealed class UnsupportedWalletProvider : IWalletProvider
    {
        const string Message =
            "Wallets work in WebGL builds only for now (not in Editor play mode, Android or Windows). Table reads work everywhere.";

        public void Initialize(WaxNetwork network, string appName) { }
        public Task<WaxSession> Login(WalletType wallet) => throw NotSupported();
        public Task<WaxSession> Restore() => throw NotSupported();
        public Task Logout() => throw NotSupported();
        public Task<string> Transact(JArray actions) => throw NotSupported();

        static WaxKitException NotSupported() => new WaxKitException(WaxErrorKind.NotSupported, Message);
    }
}
