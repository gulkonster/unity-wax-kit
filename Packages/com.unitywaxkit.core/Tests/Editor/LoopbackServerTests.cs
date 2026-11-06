using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityWaxKit.Platform;

namespace UnityWaxKit.Tests
{
    /// <summary>
    /// The local page server used for My Cloud Wallet and WebAuth Web on Windows and Android: secret path checked first,
    /// body size cap, length rules, connection limit and deadline. Real sockets on 127.0.0.1.
    /// </summary>
    public class LoopbackServerTests
    {
        const string Prefix = "/0123456789abcdef/";

        WaxLoopbackServer server;
        int handled;

        [SetUp]
        public void Start()
        {
            handled = 0;
            server = new WaxLoopbackServer(request =>
            {
                Interlocked.Increment(ref handled);
                if (request.Path.EndsWith("/boom")) throw new InvalidOperationException("secret detail");
                return new WaxLoopbackServer.Response { Body = "len=" + (request.Body?.Length ?? 0) };
            }, Prefix);
        }

        [TearDown]
        public void Stop() => server.Dispose();

        /// <summary>Sends <paramref name="head"/> (and <paramref name="body"/>) and returns the whole answer.</summary>
        string Send(string head, byte[] body = null)
        {
            using var client = new TcpClient("127.0.0.1", server.Port) { ReceiveTimeout = 15000 };
            var stream = client.GetStream();
            var bytes = Encoding.ASCII.GetBytes(head);
            stream.Write(bytes, 0, bytes.Length);
            if (body != null) stream.Write(body, 0, body.Length);
            var answer = new MemoryStream();
            var buffer = new byte[8192];
            for (var n = stream.Read(buffer, 0, buffer.Length); n > 0; n = stream.Read(buffer, 0, buffer.Length)) answer.Write(buffer, 0, n);
            return Encoding.UTF8.GetString(answer.ToArray());
        }

        void AssertAnswer(string answer, int status, bool handlerRan, string body = null)
        {
            StringAssert.StartsWith($"HTTP/1.1 {status} ", answer);
            if (body != null) StringAssert.EndsWith(body, answer);
            Assert.AreEqual(handlerRan, Volatile.Read(ref handled) > 0, "handler called");
        }

        static byte[] Fill(int length)
        {
            var bytes = new byte[length];
            for (var i = 0; i < length; i++) bytes[i] = (byte)'a';
            return bytes;
        }

        [Test]
        public void RightSecret_IsHandled() => AssertAnswer(Send($"GET {Prefix}state HTTP/1.1\r\n\r\n"), 200, true, "len=0");

        [Test]
        public void WrongSecret_Is404WithoutHandler() => AssertAnswer(Send("GET /fedcba9876543210/state HTTP/1.1\r\n\r\n"), 404, false);

        [Test]
        public void SecretWithoutItsSlash_Is404() =>
            AssertAnswer(Send($"GET {Prefix.TrimEnd('/')}x/state HTTP/1.1\r\n\r\n"), 404, false);

        [Test]
        public void WrongSecretWithHugeBody_Is404BeforeTheLength() =>
            AssertAnswer(Send("POST /nope/result HTTP/1.1\r\nContent-Length: 2000000000\r\n\r\n"), 404, false);

        [Test]
        public void BodyAtTheCap_IsRead()
        {
            var max = WaxLoopbackServer.MaxBodyBytes;
            AssertAnswer(Send($"POST {Prefix}result HTTP/1.1\r\nContent-Length: {max}\r\n\r\n", Fill(max)), 200, true, "len=" + max);
        }

        // Refused requests send only the header: the server never reads a refused body.
        [Test]
        public void BodyOverTheCap_Is413() =>
            AssertAnswer(Send($"POST {Prefix}result HTTP/1.1\r\nContent-Length: {WaxLoopbackServer.MaxBodyBytes + 1}\r\n\r\n"), 413, false);

        [TestCase("-5")]
        [TestCase("abc")]
        [TestCase("99999999999")]
        public void BadLength_Is400(string length) =>
            AssertAnswer(Send($"POST {Prefix}result HTTP/1.1\r\nContent-Length: {length}\r\n\r\n"), 400, false);

        [Test]
        public void PostWithoutLength_Is411() => AssertAnswer(Send($"POST {Prefix}result HTTP/1.1\r\n\r\n"), 411, false);

        [Test]
        public void ChunkedBody_Is411() =>
            AssertAnswer(Send($"POST {Prefix}result HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n"), 411, false);

        [Test]
        public void HandlerException_IsGeneric500()
        {
            // The server also logs a warning with only the exception type (from its own thread).
            var answer = Send($"GET {Prefix}boom HTTP/1.1\r\n\r\n");
            AssertAnswer(answer, 500, true, "Internal error");
            StringAssert.DoesNotContain("secret detail", answer);
        }

        [Test]
        public void ConnectionsOverTheLimit_AreClosedAtOnce()
        {
            var held = new List<TcpClient>();
            try
            {
                for (var i = 0; i < WaxLoopbackServer.MaxConnections; i++) held.Add(new TcpClient("127.0.0.1", server.Port));
                Thread.Sleep(500);
                string extra;
                try
                {
                    extra = Send($"GET {Prefix}state HTTP/1.1\r\n\r\n");
                }
                catch (IOException)
                {
                    extra = "";
                }
                Assert.AreEqual("", extra, "the extra connection must be closed without an answer");
            }
            finally
            {
                foreach (var client in held) client.Dispose();
            }
            Thread.Sleep(500);
            AssertAnswer(Send($"GET {Prefix}state HTTP/1.1\r\n\r\n"), 200, true);
        }

        [Test, Timeout(40000)]
        public void SlowClient_IsClosedByTheDeadline()
        {
            using var slow = new TcpClient("127.0.0.1", server.Port);
            var stream = slow.GetStream();
            var head = Encoding.ASCII.GetBytes($"GET {Prefix}state HTTP/1.1\r\nX-Slow: ");
            var started = DateTime.UtcNow;
            var closed = false;
            for (var i = 0; i < 30 && !closed; i++)
            {
                try
                {
                    stream.Write(head, i % head.Length, 1);
                    Thread.Sleep(2000);
                    closed = slow.Client.Poll(0, SelectMode.SelectRead) && slow.Client.Available == 0;
                }
                catch (IOException)
                {
                    closed = true;
                }
            }
            Assert.IsTrue(closed, "closed");
            Assert.Less((DateTime.UtcNow - started).TotalMilliseconds, WaxLoopbackServer.ConnectionMilliseconds + 5000);
            Assert.AreEqual(0, Volatile.Read(ref handled));
        }
    }
}
