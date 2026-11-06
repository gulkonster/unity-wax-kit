using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace UnityWaxKit.Platform
{
    /// <summary>
    /// Tiny HTTP/1.1 server on 127.0.0.1 (random port) that serves the kit's own pages to the in-app browser.
    /// Handlers run on the server's background thread, so they work while the game is paused behind the browser.
    /// </summary>
    internal sealed class WaxLoopbackServer : IDisposable
    {
        internal sealed class Request
        {
            /// <summary>HTTP method.</summary>
            public string Method;
            /// <summary>Path with the query string.</summary>
            public string Path;
            /// <summary>Request body (UTF-8).</summary>
            public string Body;
            /// <summary>Request headers.</summary>
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        internal sealed class Response
        {
            /// <summary>HTTP status.</summary>
            public int Status = 200;
            /// <summary>Content-Type header.</summary>
            public string ContentType = "text/plain; charset=utf-8";
            /// <summary>Response body.</summary>
            public string Body = "";
        }

        /// <summary>
        /// Largest request body accepted (1 MiB). The biggest real one, a wallet's answer with a signed transaction whose
        /// bytes arrive as JSON numbers, is tens of kilobytes. Bigger bodies, and bodies without a Content-Length, are
        /// refused before any memory is allocated for them.
        /// </summary>
        internal const int MaxBodyBytes = 1024 * 1024;

        readonly TcpListener listener;
        readonly Func<Request, Response> handler;
        volatile bool running = true;

        /// <summary>The random port the server listens on.</summary>
        public int Port { get; }
        /// <summary>http://127.0.0.1:{port}</summary>
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        /// <summary>Connections served at the same time; more are closed at once. The bridge page needs about three.</summary>
        internal const int MaxConnections = 16;
        /// <summary>Longest time one connection may take in total (reading, handling, answering).</summary>
        internal const int ConnectionMilliseconds = 15000;

        readonly string pathPrefix;
        int connections;

        /// <summary>
        /// Starts listening; <paramref name="handler"/> answers each request (null = 404). With a
        /// <paramref name="pathPrefix"/> (the flow's secret, "/{token}/"), any other path gets 404 right after the headers:
        /// its body is never read and the handler never runs.
        /// </summary>
        public WaxLoopbackServer(Func<Request, Response> handler, string pathPrefix = null)
        {
            this.handler = handler;
            this.pathPrefix = pathPrefix;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(AcceptLoop) { IsBackground = true, Name = "WaxKit loopback server" }.Start();
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    return;
                }
                if (Interlocked.Increment(ref connections) > MaxConnections)
                {
                    Interlocked.Decrement(ref connections);
                    client.Close();
                    continue;
                }
                new Thread(() => Serve(client)) { IsBackground = true }.Start();
            }
        }

        void Serve(TcpClient client)
        {
            // The overall deadline closes the connection, so a client sending one byte at a time cannot keep a slot.
            using var deadline = new Timer(_ => client.Close(), null, ConnectionMilliseconds, Timeout.Infinite);
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 10000;
                    var stream = client.GetStream();
                    var request = Read(stream, pathPrefix, out var refused);
                    if (request == null) return;

                    Response response;
                    if (refused != null) response = refused;
                    else
                    {
                        try
                        {
                            response = handler(request) ?? new Response { Status = 404, Body = "Not found" };
                        }
                        catch (Exception e)
                        {
                            // Only the exception type: the path holds the flow's secret, the message may quote the body.
                            WaxKit.LogWarning($"the local wallet page could not handle a request ({e.GetType().Name}).");
                            response = new Response { Status = 500, Body = "Internal error" };
                        }
                    }
                    Write(stream, response);
                }
            }
            catch (Exception)
            {
                // The browser went away (or the deadline closed the connection); nothing to answer.
            }
            finally
            {
                Interlocked.Decrement(ref connections);
            }
        }

        /// <summary>
        /// Reads one request, or null when the connection ended early. <paramref name="refused"/> is set (and the body is
        /// not read) when the path does not start with <paramref name="pathPrefix"/> (404), or the body is missing a valid
        /// Content-Length or is larger than <see cref="MaxBodyBytes"/>.
        /// </summary>
        internal static Request Read(Stream stream, string pathPrefix, out Response refused)
        {
            refused = null;
            var head = new MemoryStream();
            var last = new byte[4];
            while (true)
            {
                var b = stream.ReadByte();
                if (b < 0) return null;
                head.WriteByte((byte)b);
                last[0] = last[1];
                last[1] = last[2];
                last[2] = last[3];
                last[3] = (byte)b;
                if (last[0] == '\r' && last[1] == '\n' && last[2] == '\r' && last[3] == '\n') break;
                if (head.Length > 64 * 1024) return null;
            }

            var lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;

            var request = new Request { Method = first[0], Path = first[1] };
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0) request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            // The secret first: a request for any other path learns nothing and costs nothing more.
            if (pathPrefix != null && !request.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
            {
                refused = new Response { Status = 404, Body = "Not found" };
                return request;
            }

            // Only plain Content-Length bodies (browsers send one for fetch and sendBeacon); no chunked bodies.
            var hasLength = request.Headers.TryGetValue("Content-Length", out var lengthText);
            if (request.Headers.ContainsKey("Transfer-Encoding") || (!hasLength && request.Method != "GET" && request.Method != "HEAD"))
            {
                refused = new Response { Status = 411, Body = "Length required" };
                return request;
            }
            var length = 0;
            if (hasLength && !int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out length))
            {
                refused = new Response { Status = 400, Body = "Bad request" };
                return request;
            }
            if (length > MaxBodyBytes)
            {
                refused = new Response { Status = 413, Body = "Request too large" };
                return request;
            }

            if (length > 0)
            {
                var body = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = stream.Read(body, read, length - read);
                    if (n <= 0) break;
                    read += n;
                }
                request.Body = Encoding.UTF8.GetString(body, 0, read);
            }
            return request;
        }

        static void Write(NetworkStream stream, Response response)
        {
            var body = Encoding.UTF8.GetBytes(response.Body ?? "");
            var head = $"HTTP/1.1 {response.Status} {(response.Status < 400 ? "OK" : "Error")}\r\n" +
                       $"Content-Type: {response.ContentType}\r\n" +
                       $"Content-Length: {body.Length}\r\n" +
                       "Cache-Control: no-store\r\n" +
                       "Connection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            stream.Write(bytes, 0, bytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        /// <summary>Stops listening.</summary>
        public void Dispose()
        {
            running = false;
            listener.Stop();
        }
    }
}
