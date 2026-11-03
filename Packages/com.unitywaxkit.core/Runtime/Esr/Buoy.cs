using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityWaxKit.Crypto;
using UnityWaxKit.Platform;
using UnityEngine;

namespace UnityWaxKit.Esr
{
    /// <summary>
    /// Greymass buoy message relay (cb.anchor.link), ported from @greymass/buoy. Messages are sent with an HTTP POST and
    /// received on a WebSocket; a message posted while nobody listens is buffered until the socket (re)connects.
    /// </summary>
    internal static class Buoy
    {
        /// <summary>The public relay Anchor uses.</summary>
        public const string DefaultService = "https://cb.anchor.link";

        static readonly TimeSpan Reconnect = TimeSpan.FromMinutes(10);

        /// <summary>A random UUID v4 channel name.</summary>
        public static string NewChannel()
        {
            var b = WaxCrypto.RandomBytes(16);
            b[6] = (byte)((b[6] & 0x0f) | 0x40);
            b[8] = (byte)((b[8] & 0x3f) | 0x80);
            var hex = Antelope.Hex.Encode(b);
            return $"{hex.Substring(0, 8)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}-{hex.Substring(16, 4)}-{hex.Substring(20)}";
        }

        /// <summary>The full URL of a channel on a relay.</summary>
        public static string ChannelUrl(string service, string channel) => $"{service.TrimEnd('/')}/{channel}";

        /// <summary>Posts a message to the channel. Returns "delivered" or "buffered".</summary>
        public static async Task<string> Send(string channelUrl, byte[] message, CancellationToken cancellation)
        {
            var url = channelUrl.StartsWith("ws", StringComparison.Ordinal) ? "http" + channelUrl.Substring(2) : channelUrl;
            var response = await WaxPlatform.Current.Http("POST", url, message, "text/plain", null, 30, cancellation);
            if (!response.IsSuccess)
            {
                var reason = response.Status == 408 ? "Unable to deliver message"
                    : response.Status == 410 ? "Request cancelled"
                    : $"Unexpected status code {response.Status} {response.Error}".Trim();
                throw new WaxKitException(WaxErrorKind.Network, "Buoy: " + reason);
            }
            return response.Headers.TryGetValue("X-Buoy-Delivery", out var delivery) ? delivery : "buffered";
        }

        /// <summary>Waits for one text message on the channel.</summary>
        public static async Task<string> Receive(string service, string channel, CancellationToken cancellation) =>
            Encoding.UTF8.GetString(await ReceiveBytes(service, channel, cancellation));

        /// <summary>Waits for one message on the channel, reconnecting with backoff when the socket drops.</summary>
        public static async Task<byte[]> ReceiveBytes(string service, string channel, CancellationToken cancellation)
        {
            var baseUrl = service.TrimEnd('/');
            if (baseUrl.StartsWith("http", StringComparison.Ordinal)) baseUrl = "ws" + baseUrl.Substring(4);
            var url = $"{baseUrl}/{channel}?v=2";

            for (var retries = 0; ; retries++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (retries > 0) await WaxPlatform.Current.Delay(Backoff(retries - 1), cancellation);

                using var socket = WaxPlatform.Current.CreateSocket();
                // Reconnect every 10 minutes to keep the connection alive (as buoy-client).
                using var session = new WaxDeadline(cancellation, Reconnect);
                try
                {
                    await socket.Connect(url, session.Token);
                    // Diagnostics: no channel name (it gives access to the answer) and no message contents.
                    Debug.Log("Unity WAX Kit: buoy connected.");
                    retries = 0;
                    while (true)
                    {
                        var bytes = await socket.Receive(session.Token);
                        if (bytes == null) break;
                        Debug.Log($"Unity WAX Kit: buoy message arrived ({bytes.Length} bytes).");

                        // Delivery receipt request: acknowledge and strip the 4-byte header.
                        if (bytes.Length >= 4 && bytes[0] == 0x42 && bytes[1] == 0x42 && bytes[2] == 0x01)
                        {
                            await socket.Send(new byte[] { 0x42, 0x42, 0x02, bytes[3] }, session.Token);
                            bytes = Antelope.Base58.Slice(bytes, 4, bytes.Length - 4);
                        }
                        return bytes;
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    // Periodic reconnect.
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    // Socket error: reconnect with backoff.
                    Debug.Log($"Unity WAX Kit: buoy socket error ({e.GetType().Name}), reconnecting.");
                }
            }
        }

        /// <summary>min((tries * 7)^2, 5000) ms as buoy-client, but at least 250 ms so a socket that keeps closing never spins.</summary>
        static int Backoff(int tries) => Math.Max(250, Math.Min((tries * 7) * (tries * 7), 5000));
    }
}
