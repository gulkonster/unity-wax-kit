using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Gulkonster.WaxKit.Platform
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

        readonly TcpListener listener;
        readonly Func<Request, Response> handler;
        volatile bool running = true;

        /// <summary>The random port the server listens on.</summary>
        public int Port { get; }
        /// <summary>http://127.0.0.1:{port}</summary>
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        /// <summary>Starts listening; <paramref name="handler"/> answers each request (null = 404).</summary>
        public WaxLoopbackServer(Func<Request, Response> handler)
        {
            this.handler = handler;
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
                new Thread(() => Serve(client)) { IsBackground = true }.Start();
            }
        }

        void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 10000;
                    var stream = client.GetStream();
                    var request = Read(stream);
                    if (request == null) return;

                    Response response;
                    try
                    {
                        response = handler(request) ?? new Response { Status = 404, Body = "Not found" };
                    }
                    catch (Exception e)
                    {
                        response = new Response { Status = 500, Body = e.Message };
                    }
                    Write(stream, response);
                }
                catch (Exception)
                {
                    // The browser went away; nothing to answer.
                }
            }
        }

        static Request Read(NetworkStream stream)
        {
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

            if (request.Headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length) && length > 0)
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
                       "Access-Control-Allow-Origin: *\r\n" +
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
