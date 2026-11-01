using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gulkonster.WaxKit.Platform;

namespace Gulkonster.WaxKit.Chain
{
    /// <summary>One endpoint list with the index of the endpoint that worked last (kept for the session).</summary>
    internal sealed class WaxEndpointList
    {
        const int BackoffMilliseconds = 300;

        readonly string label;
        int preferred;

        /// <summary>The endpoints, in the order they are tried.</summary>
        public IReadOnlyList<string> Urls { get; }
        /// <summary>Per-request timeout before the next endpoint is tried.</summary>
        public int TimeoutSeconds { get; set; } = 10;

        /// <summary>A list of endpoints; <paramref name="label"/> names it in error messages.</summary>
        public WaxEndpointList(IReadOnlyList<string> urls, string label)
        {
            Urls = urls;
            this.label = label;
        }

        /// <summary>
        /// Sends the request to each endpoint in order, starting with the one that worked last. A timeout, a network error,
        /// 429 or 5xx moves on to the next endpoint; any other answer is returned. <paramref name="isAnswer"/> can accept
        /// a 5xx as the final answer (nodeos reports transaction errors as HTTP 500 with an error object).
        /// </summary>
        public async Task<WaxHttpResponse> Send(string method, string path, string body, CancellationToken cancellation = default,
            Func<WaxHttpResponse, bool> isAnswer = null)
        {
            if (Urls.Count == 0) throw new WaxKitException(WaxErrorKind.Network, $"No {label} endpoints are set on the network.");

            var bytes = body == null ? null : Encoding.UTF8.GetBytes(body);
            string lastError = null;

            for (var attempt = 0; attempt < Urls.Count; attempt++)
            {
                if (attempt > 0) await WaxPlatform.Current.Delay(BackoffMilliseconds * attempt, cancellation);

                var index = (preferred + attempt) % Urls.Count;
                var url = Urls[index].TrimEnd('/') + path;
                // text/plain keeps the browser from sending a CORS preflight on WebGL.
                var response = await WaxPlatform.Current.Http(method, url, bytes, bytes == null ? null : "text/plain", null, TimeoutSeconds, cancellation);

                if (response.Status != 0 && response.Status != 429 && (response.Status < 500 || isAnswer?.Invoke(response) == true))
                {
                    preferred = index;
                    return response;
                }
                lastError = $"{url}: {(response.Status == 0 ? response.Error : $"HTTP {response.Status} {Shorten(response.Text)}")}";
            }

            throw new WaxKitException(WaxErrorKind.Network, $"All {label} endpoints failed. Last error: {lastError}");
        }

        static string Shorten(string text) => text == null ? "" : text.Length > 500 ? text.Substring(0, 500) + "…" : text;
    }
}
