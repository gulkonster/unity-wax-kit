using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityWaxKit.Platform;
using Newtonsoft.Json;

namespace UnityWaxKit.Chain
{
    /// <summary>The kind of 2xx answer a call expects; any other answer counts as a broken endpoint.</summary>
    internal enum WaxAnswerShape
    {
        /// <summary>One whole JSON object or array (generic calls: get_currency_balance answers an array).</summary>
        Any,
        /// <summary>One whole JSON object (nodeos calls such as get_info, get_account, get_table_rows).</summary>
        Object,
        /// <summary>One whole JSON array.</summary>
        Array,
        /// <summary>An AtomicAssets / AtomicMarket answer: a JSON object with "success": true and a "data" field.</summary>
        AtomicEnvelope,
        /// <summary>One whole JSON object, or the bare literal null (Alcor answers null for an unknown pool id).</summary>
        ObjectOrNull,
    }

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
        /// a 5xx as the final answer (nodeos reports transaction errors as HTTP 500 with an error object). A 2xx answer
        /// that is not <paramref name="shape"/> also moves on (see <see cref="IsJsonAnswer(string, WaxAnswerShape)"/>).
        /// With <paramref name="returnRateLimit"/>, a 429 is returned to the caller (which waits and retries) instead.
        /// </summary>
        public async Task<WaxHttpResponse> Send(string method, string path, string body, CancellationToken cancellation = default,
            Func<WaxHttpResponse, bool> isAnswer = null, WaxAnswerShape shape = WaxAnswerShape.Any, bool returnRateLimit = false)
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

                if (response.Status == 429 && returnRateLimit)
                {
                    response.Endpoint = Urls[index];
                    return response;
                }
                if (response.Status != 0 && response.Status != 429 && (response.Status < 500 || isAnswer?.Invoke(response) == true))
                {
                    // A 2xx that is not the expected JSON (a proxy's or provider's HTML page, a cut-off answer, an array where
                    // the call answers an object) is a broken endpoint, not an answer: try the next one. A transaction sent
                    // twice comes back as tx_duplicate.
                    if (response.IsSuccess && !IsJsonAnswer(response.Text, shape))
                    {
                        lastError = $"{url}: HTTP {response.Status}, not {Expected(shape)}: {Shorten(response.Text)}";
                        continue;
                    }
                    preferred = index;
                    response.Endpoint = Urls[index];
                    return response;
                }
                lastError = $"{url}: {(response.Status == 0 ? response.Error : $"HTTP {response.Status} {Shorten(response.Text)}")}";
            }

            throw new WaxKitException(WaxErrorKind.Network, $"All {label} endpoints failed. Last error: {lastError}");
        }

        /// <summary>
        /// True when <paramref name="text"/> is exactly one complete JSON object or array (nodeos answers objects, and arrays
        /// for some calls such as get_currency_balance). Read token by token, nothing built.
        /// </summary>
        internal static bool IsJsonAnswer(string text) => IsJsonAnswer(text, WaxAnswerShape.Any);

        /// <summary>
        /// True when <paramref name="text"/> is exactly one complete JSON value of the given shape. For
        /// <see cref="WaxAnswerShape.AtomicEnvelope"/>, the object's own "success" must be true (the last one wins, as when
        /// parsed) and it must have a "data" field. Read token by token, nothing built.
        /// </summary>
        internal static bool IsJsonAnswer(string text, WaxAnswerShape shape)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
                if (!reader.Read()) return false;
                var first = reader.TokenType;
                if (shape == WaxAnswerShape.ObjectOrNull && first == JsonToken.Null) return !reader.Read();
                var isObject = first == JsonToken.StartObject;
                var isArray = first == JsonToken.StartArray;
                var shapeOk = shape == WaxAnswerShape.Any ? isObject || isArray : shape == WaxAnswerShape.Array ? isArray : isObject;
                if (!shapeOk) return false;

                bool success = false, hasData = false;
                // Reads to the end: anything after the value throws; a cut-off value does not end on its closing token.
                var last = first;
                while (reader.Read())
                {
                    last = reader.TokenType;
                    if (shape != WaxAnswerShape.AtomicEnvelope || last != JsonToken.PropertyName || reader.Depth != 1) continue;
                    var name = (string)reader.Value;
                    if (name == "data") hasData = true;
                    else if (name == "success")
                    {
                        if (!reader.Read()) return false;
                        last = reader.TokenType;
                        success = last == JsonToken.Boolean && (bool)reader.Value;
                    }
                }
                if (reader.Depth != 0 || (last != JsonToken.EndObject && last != JsonToken.EndArray)) return false;
                return shape != WaxAnswerShape.AtomicEnvelope || (success && hasData);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        static string Expected(WaxAnswerShape shape)
        {
            switch (shape)
            {
                case WaxAnswerShape.Object:
                case WaxAnswerShape.ObjectOrNull: return "a JSON object answer";
                case WaxAnswerShape.Array: return "a JSON array answer";
                case WaxAnswerShape.AtomicEnvelope: return "an AtomicAssets answer (success: true with data)";
                default: return "a JSON answer";
            }
        }

        static string Shorten(string text) => text == null ? "" : text.Length > 500 ? text.Substring(0, 500) + "…" : text;
    }
}
