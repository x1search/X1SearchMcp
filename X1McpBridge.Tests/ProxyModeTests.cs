// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// Pure-logic tests for ProxyMode's SSE parsing — the rest of ProxyMode (health-check,
    /// detached launch, HTTP relay) requires a live daemon and is covered by manual end-to-end
    /// verification instead (see the fan-in plan's Phase 4 verification step).
    /// </summary>
    [TestFixture]
    public class ProxyModeTests
    {
        [Test]
        public void ParseSseLastMessage_SingleEvent_ReturnsIt()
        {
            const string sse = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n";
            var result = ProxyMode.ParseSseLastMessage(sse);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Value<int>("id"), Is.EqualTo(1));
        }

        [Test]
        public void ParseSseLastMessage_MultipleEvents_ReturnsLastOne()
        {
            const string sse =
                "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\n" +
                "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":5,\"result\":{\"done\":true}}\n\n";
            var result = ProxyMode.ParseSseLastMessage(sse);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Value<int>("id"), Is.EqualTo(5));
        }

        [Test]
        public void ParseSseLastMessage_NoDataLines_ReturnsNull()
        {
            const string sse = "event: message\n\n";
            var result = ProxyMode.ParseSseLastMessage(sse);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void ParseSseLastMessage_MalformedJsonInOneEvent_SkipsItAndReturnsLastValidOne()
        {
            const string sse =
                "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n" +
                "event: message\ndata: not valid json\n\n";
            var result = ProxyMode.ParseSseLastMessage(sse);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Value<int>("id"), Is.EqualTo(1));
        }

        [Test]
        public void ParseSseLastMessage_EmptyBody_ReturnsNull()
        {
            var result = ProxyMode.ParseSseLastMessage("");
            Assert.That(result, Is.Null);
        }

        // ── XS-1819: replies cut off mid-body ───────────────────────────────────

        /// <summary>
        /// A one-shot fake relay on a loopback port: reads one request, writes
        /// <paramref name="rawResponse"/> verbatim, and closes - which, with a Content-Length larger
        /// than the bytes sent, is exactly the "connection was closed" the customer logs show.
        /// </summary>
        private static string StartFakeRelay(string rawResponse, int requests = 1)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < requests; i++)
                    {
                        using (var client = listener.AcceptTcpClient())
                        using (var stream = client.GetStream())
                        {
                            ReadRequest(stream);
                            var bytes = Encoding.ASCII.GetBytes(rawResponse);
                            stream.Write(bytes, 0, bytes.Length);
                            stream.Flush();
                        }
                    }
                }
                catch { /* test over */ }
                finally { listener.Stop(); }
            });
            return "http://127.0.0.1:" + port;
        }

        // Headers, then Content-Length bytes of body - enough that the client has finished sending.
        private static void ReadRequest(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && stream.Read(one, 0, 1) == 1)
                sb.Append((char)one[0]);
            var m = Regex.Match(sb.ToString(), @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
            int remaining = m.Success ? int.Parse(m.Groups[1].Value) : 0;
            var buf = new byte[4096];
            while (remaining > 0)
            {
                int n = stream.Read(buf, 0, Math.Min(buf.Length, remaining));
                if (n <= 0) break;
                remaining -= n;
            }
        }

        private const string TruncatedJson =
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 200\r\n\r\n{\"jsonrpc\":\"2.0\"";

        private const string GoodJson =
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 36\r\nConnection: close\r\n\r\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}";

        private static HttpClient NewClient()
        {
            return new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        }

        [Test]
        public void CheckHealth_ReplyCutOffMidBody_IsNotHealthy()
        {
            using (var http = NewClient())
            {
                var snap = ProxyMode.CheckHealthAsync(http, StartFakeRelay(TruncatedJson)).GetAwaiter().GetResult();
                Assert.That(snap.Reachable, Is.False);
            }
        }

        [Test]
        public void CheckHealth_RealHostRelay_IsHealthyAndIdentified()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var url = "http://localhost:" + port;

            Assert.That(HostMode.StartServingForTest(url, req => McpProtocol.Ok(req["id"], new JObject())), Is.True);
            try
            {
                using (var http = NewClient())
                {
                    var snap = ProxyMode.CheckHealthAsync(http, url).GetAwaiter().GetResult();
                    Assert.That(snap.Reachable, Is.True);
                    Assert.That(snap.Component, Is.EqualTo(RelayHealth.ComponentHost));
                    Assert.That(snap.Pid, Is.EqualTo(System.Diagnostics.Process.GetCurrentProcess().Id));
                }
            }
            finally
            {
                HostMode.StopServingForTest();
            }
        }

        [Test]
        public void Forward_ReplyCutOffMidBody_FailsSayingHowFarTheReplyGot()
        {
            using (var http = NewClient())
            {
                var request = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
                var ex = Assert.Throws<IOException>(() =>
                    ProxyMode.ForwardAsync(http, StartFakeRelay(TruncatedJson), request, null).GetAwaiter().GetResult());
                Assert.That(ex.Message, Does.Contain("HTTP 200 headers (Content-Length 200)"));
                Assert.That(ex.Message, Does.Contain("connection closed before the body arrived"));
            }
        }

        [Test]
        public void Forward_GoodReply_FreshConnection_IsRelayed()
        {
            using (var http = NewClient())
            {
                var request = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
                var result = ProxyMode.ForwardAsync(http, StartFakeRelay(GoodJson), request, null, freshConnection: true)
                    .GetAwaiter().GetResult();
                Assert.That(result.Response.Value<int>("id"), Is.EqualTo(1));
            }
        }

        [Test]
        public void Forward_NoReplyAtAll_TimesOutAtTheNonToolCap()
        {
            // Accepts, never answers. A non-tool request must give up at its 10s cap, not 120s, and
            // say it timed out - Run() relies on TimeoutException to skip a second full wait.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                using (var http = NewClient())
                {
                    var request = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var ex = Assert.Throws<TimeoutException>(() =>
                        ProxyMode.ForwardAsync(http, "http://127.0.0.1:" + port, request, null).GetAwaiter().GetResult());
                    Assert.That(ex.Message, Does.Contain("no response headers arrived"));
                    Assert.That(sw.Elapsed, Is.GreaterThan(TimeSpan.FromSeconds(8)));
                    Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(14)));
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public void Forward_HeadersThenStalledBody_TimesOutSayingTheBodyNeverCame()
        {
            // The variant the XS-1819 repro found taking 61s: headers arrive, the body never does.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task.Run(() =>
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    var stream = client.GetStream();
                    ReadRequest(stream);
                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 200\r\n\r\n");
                    stream.Write(head, 0, head.Length);
                    stream.Flush();
                    Thread.Sleep(TimeSpan.FromSeconds(30));   // hold the connection open, send nothing
                    client.Close();
                }
                catch { /* test over */ }
            });
            try
            {
                using (var http = NewClient())
                {
                    var request = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var ex = Assert.Throws<TimeoutException>(() =>
                        ProxyMode.ForwardAsync(http, "http://127.0.0.1:" + port, request, null).GetAwaiter().GetResult());
                    Assert.That(ex.Message, Does.Contain("HTTP 200 headers (Content-Length 200) arrived but the body did not"));
                    Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(14)));
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        [TestCase("tools/call", 120)]
        [TestCase("initialize", 10)]
        [TestCase("tools/list", 10)]
        [TestCase("ping", 10)]
        [TestCase("server/discover", 10)]
        public void RequestTimeout_OnlyToolCallsGetTheLongTimeout(string method, int seconds)
        {
            var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method };
            Assert.That(ProxyMode.RequestTimeout(request), Is.EqualTo(TimeSpan.FromSeconds(seconds)));
        }

        [Test]
        public void Flatten_IncludesInnerExceptions()
        {
            var ex = new HttpRequestException("Error while copying content to a stream.",
                new IOException("Unable to read data from the transport connection: The connection was closed."));
            Assert.That(ProxyMode.Flatten(ex), Is.EqualTo(
                "Error while copying content to a stream. -> Unable to read data from the transport connection: The connection was closed."));
        }

        [Test]
        public void ParseSseLastMessage_CrLfLineEndings_ParsesCorrectly()
        {
            const string sse = "event: message\r\ndata: {\"jsonrpc\":\"2.0\",\"id\":42,\"result\":{}}\r\n\r\n";
            var result = ProxyMode.ParseSseLastMessage(sse);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Value<int>("id"), Is.EqualTo(42));
        }
    }
}
