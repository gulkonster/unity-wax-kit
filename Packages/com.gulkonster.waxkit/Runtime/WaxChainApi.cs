using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Gulkonster.WaxKit
{
    /// <summary>Read-only chain API calls. Tries each RPC endpoint in order and moves on when one is down or busy.</summary>
    internal static class WaxChainApi
    {
        const int TimeoutSeconds = 10;
        const float BackoffSeconds = 0.3f;

        public static Task<string> GetTableRows(WaxNetwork network, string queryJson) =>
            Post(network, "/v1/chain/get_table_rows", queryJson);

        static async Task<string> Post(WaxNetwork network, string path, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            string lastError = null;

            for (var i = 0; i < network.RpcEndpoints.Count; i++)
            {
                if (i > 0) await Awaitable.WaitForSecondsAsync(BackoffSeconds * i);

                var url = network.RpcEndpoints[i].TrimEnd('/') + path;
                using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
                {
                    // text/plain keeps the browser from sending a CORS preflight on WebGL.
                    uploadHandler = new UploadHandlerRaw(bytes) { contentType = "text/plain" },
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = TimeoutSeconds,
                };

                await request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                    return request.downloadHandler.text;

                var code = request.responseCode;
                lastError = $"{url}: {request.error} {request.downloadHandler?.text}".Trim();

                var retry = request.result == UnityWebRequest.Result.ConnectionError || code == 0 || code == 429 || code >= 500;
                if (!retry) throw new WaxKitException(WaxErrorKind.Network, lastError);
            }

            throw new WaxKitException(WaxErrorKind.Network, "All RPC endpoints failed. Last error: " + lastError);
        }
    }
}
