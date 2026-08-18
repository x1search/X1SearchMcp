// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// Real HTTP round trips against HostMode, the Lean flavor's in-bridge relay.
    ///
    /// These exist because the contract HostMode has to satisfy is defined by what ProxyMode
    /// actually does, and two of ProxyMode's behaviours turn a mistake here into a SILENT failure
    /// rather than a loud one:
    ///   • it never checks the HTTP status code, so a non-JSON body (e.g. HttpListener's default
    ///     HTML 404) reaches its JObject.Parse, throws, and is reported to the user as "the relay is
    ///     unavailable" after a pointless relaunch;
    ///   • a Content-Type containing "event-stream" sends it into ParseSseLastMessage, which finds no
    ///     "data:" line in plain JSON, returns null, and writes nothing back - hanging the client
    ///     forever on a reply that was actually produced.
    /// Neither shows up in a happy-path smoke test, which is exactly why they are pinned here.
    ///
    /// No live X1ServiceHost is needed: the dispatch handler is substituted, so these cover the
    /// transport, not the tools.
    /// </summary>
    [TestFixture]
    public class HostModeTests
    {
        private string _url;
        private HttpClient _http;

        /// <summary>
        /// Finds a free port by binding one and immediately releasing it. HttpListener cannot bind
        /// port 0, so the usual "let the OS pick" trick has to be done out of band.
        /// </summary>
        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static JObject Echo(JObject request)
        {
            // Stands in for McpServer.ProcessMessage: null for notifications (as ProcessMessage
            // itself returns), a result otherwise.
            if (McpProtocol.IsNotification(request))
                return null;
            return McpProtocol.Ok(request["id"], new JObject { ["echoed"] = request.Value<string>("method") });
        }

        [SetUp]
        public void SetUp()
        {
            _url = "http://localhost:" + FreePort();
            Assert.That(HostMode.StartServingForTest(_url, Echo), Is.True, "relay failed to bind " + _url);
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        [TearDown]
        public void TearDown()
        {
            if (_http != null) _http.Dispose();
            HostMode.StopServingForTest();
        }

        private HttpResponseMessage Post(string path, string body)
        {
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            return _http.PostAsync(_url + path, content).GetAwaiter().GetResult();
        }

        private static string BodyOf(HttpResponseMessage r)
        {
            return r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        // ── /health: what ProxyMode.CheckHealthAsync actually reads ──────────────

        [Test]
        public void Health_Returns200JsonWithAParseableIdentity()
        {
            var resp = _http.GetAsync(_url + "/health").GetAwaiter().GetResult();
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(resp.Content.Headers.ContentType.MediaType, Is.EqualTo("application/json"));

            var snap = RelayHealth.Parse(BodyOf(resp));
            Assert.That(snap.Reachable, Is.True);
            Assert.That(snap.Component, Is.EqualTo(RelayHealth.ComponentHost));
            Assert.That(snap.Version, Is.Not.Null.And.Not.Empty);
            Assert.That(snap.Pid, Is.GreaterThan(0), "a pid is what lets a shim evict exactly this process");
            Assert.That(snap.User, Is.EqualTo(Environment.UserName));
        }

        [Test]
        public void Health_IdentitySatisfiesTheProxysOwnDecision()
        {
            // End-to-end on the decision path: a same-flavor, same-version relay must be adopted.
            var snap = RelayHealth.Parse(BodyOf(_http.GetAsync(_url + "/health").GetAwaiter().GetResult()));
            var decision = RelayHealth.Decide(snap, RelayMode.Host, snap.Version, Environment.UserName);
            Assert.That(decision.Action, Is.EqualTo(RelayHealth.Action.Use));
        }

        [Test]
        public void Health_AnswersWhileALongDispatchIsInFlight()
        {
            // ProxyMode gives /health a 2s budget. If /health queued behind tool calls, a 120s
            // x1_search would make another session conclude "no relay is running", launch a
            // redundant one, fail to bind, and limp through the retry path.
            using (var release = new ManualResetEventSlim(false))
            {
                var blocked = HostMode.DispatchForTest(
                    new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "slow" },
                    req => { release.Wait(TimeSpan.FromSeconds(15)); return McpProtocol.Ok(req["id"], new JObject()); });

                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var resp = _http.GetAsync(_url + "/health").GetAwaiter().GetResult();
                    sw.Stop();

                    Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(sw.ElapsedMilliseconds, Is.LessThan(2000),
                        "/health must be answered off the dispatch queue");
                }
                finally
                {
                    release.Set();
                    blocked.Wait(TimeSpan.FromSeconds(15));
                }
            }
        }

        // ── The two silent traps ─────────────────────────────────────────────────

        [Test]
        public void McpPost_ContentTypeIsApplicationJson_NeverEventStream()
        {
            var resp = Post("/graphql/mcp", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
            var mediaType = resp.Content.Headers.ContentType.MediaType;
            Assert.That(mediaType, Is.EqualTo("application/json"));
            Assert.That(mediaType, Does.Not.Contain("event-stream"),
                "an event-stream content type makes ProxyMode return null and hang the client forever");
        }

        [TestCase("/graphql/mcp", "not json at all")]
        [TestCase("/nope", "{}")]
        public void EveryResponseBodyIsParseableJson(string path, string body)
        {
            // ProxyMode does JObject.Parse on the body regardless of status code.
            var resp = Post(path, body);
            var text = BodyOf(resp);
            Assert.DoesNotThrow(() => JObject.Parse(text),
                "body was not JSON, so ProxyMode would misreport this as 'the relay is unavailable': " + text);
        }

        [Test]
        public void UnparseableBody_ReturnsJsonRpcParseError()
        {
            var resp = Post("/graphql/mcp", "{ this is not json");
            var jo = JObject.Parse(BodyOf(resp));
            Assert.That(jo["error"], Is.Not.Null);
            Assert.That(jo["error"].Value<int>("code"), Is.EqualTo(-32700));
        }

        [Test]
        public void UnknownPath_ReturnsJsonRpcErrorNotHtml()
        {
            var resp = Post("/definitely-not-a-route", "{}");
            var text = BodyOf(resp);
            Assert.That(text, Does.Not.Contain("<HTML"), "HttpListener's default HTML body must never escape");
            Assert.That(JObject.Parse(text)["error"], Is.Not.Null);
        }

        [Test]
        public void GetOnMcpPath_Returns405WithJsonBody()
        {
            // ProxyMode never opens a listening SSE stream, so GET is unsupported - but it still has
            // to fail in a way that parses.
            var resp = _http.GetAsync(_url + "/graphql/mcp").GetAwaiter().GetResult();
            Assert.That((int)resp.StatusCode, Is.EqualTo(405));
            Assert.That(JObject.Parse(BodyOf(resp))["error"], Is.Not.Null);
        }

        // ── Notifications ────────────────────────────────────────────────────────

        [Test]
        public void Notification_Returns202WithZeroLengthBody()
        {
            // Matches the Streamable HTTP behaviour ProxyMode already special-cases: on 202 it
            // returns without reading the body at all.
            var resp = Post("/graphql/mcp", "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            Assert.That((int)resp.StatusCode, Is.EqualTo(202));
            Assert.That(BodyOf(resp), Is.Empty);
        }

        // ── Session id ───────────────────────────────────────────────────────────

        [Test]
        public void Initialize_MintsAnMcpSessionId()
        {
            var resp = Post("/graphql/mcp", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
            IEnumerable<string> values;
            Assert.That(resp.Headers.TryGetValues("Mcp-Session-Id", out values), Is.True);
        }

        [Test]
        public void SuppliedSessionId_IsEchoedBack()
        {
            var content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}", Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, _url + "/graphql/mcp") { Content = content };
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", "abc123");

            var resp = _http.SendAsync(req).GetAwaiter().GetResult();
            IEnumerable<string> values;
            Assert.That(resp.Headers.TryGetValues("Mcp-Session-Id", out values), Is.True);
            Assert.That(values, Does.Contain("abc123"));
        }

        [Test]
        public void NonInitializeWithoutSessionId_IsStillServed()
        {
            // The relay must never reject for a missing session: McpServer's state is process-wide
            // static, so sessions are continuity signalling, not isolation.
            var resp = Post("/graphql/mcp", "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}");
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(JObject.Parse(BodyOf(resp))["result"], Is.Not.Null);
        }

        // ── Shutdown endpoint ────────────────────────────────────────────────────

        [Test]
        public void ShutdownEndpoint_AnswersWithJson()
        {
            // Exists so install.ps1 can stop the relay cleanly instead of Stop-Process, which would
            // drop the WCF connection mid-call.
            var resp = Post("/shutdown", "{}");
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.DoesNotThrow(() => JObject.Parse(BodyOf(resp)));
        }

        // XS-1701: the same trigger McpServer.HandleServiceShutdown uses when X1's OnShutdown
        // callback fires - a third caller of the same StopRequested signal /shutdown and the idle
        // timeout already use, from a caller outside this class.
        [Test]
        public void RequestShutdown_SetsStopRequested()
        {
            Assert.That(HostMode.StopRequestedForTest, Is.False);
            HostMode.RequestShutdown("test");
            Assert.That(HostMode.StopRequestedForTest, Is.True);
        }

        // ── Idle self-shutdown (XS-1692) ──────────────────────────────────────────
        //
        // CheckIdle() is asserted directly against a simulated clock rather than through Run()'s
        // real StopRequested.Wait() loop, which StartServingForTest deliberately never runs (see its
        // header) - waiting out a real hour to test the 1-hour default would make this suite
        // unusable. StartServingForTest already resets UtcNowProvider, IdleShutdownSecondsProvider,
        // and StopRequested for every test, so these don't need to restore them in TearDown.

        [Test]
        public void CheckIdle_DoesNothing_WhenThresholdIsZero()
        {
            // 0 must mean "disabled", restoring the unconditional keep-alive - not "shut down
            // immediately", which a naive elapsed-time-exceeds-zero check would do.
            HostMode.IdleShutdownSecondsProvider = () => 0;
            HostMode.UtcNowProvider = () => DateTime.UtcNow.AddDays(365);

            HostMode.CheckIdle();

            Assert.That(HostMode.StopRequestedForTest, Is.False);
        }

        [Test]
        public void CheckIdle_DoesNothing_BeforeTheThresholdElapses()
        {
            var start = DateTime.UtcNow;
            HostMode.UtcNowProvider = () => start;
            HostMode.RecordActivity();
            HostMode.IdleShutdownSecondsProvider = () => 3600;

            HostMode.UtcNowProvider = () => start.AddMinutes(59);
            HostMode.CheckIdle();

            Assert.That(HostMode.StopRequestedForTest, Is.False);
        }

        [Test]
        public void CheckIdle_RequestsShutdown_AfterTheDefaultOneHourThreshold()
        {
            var start = DateTime.UtcNow;
            HostMode.UtcNowProvider = () => start;
            HostMode.RecordActivity();
            HostMode.IdleShutdownSecondsProvider = () => 3600; // the documented default

            HostMode.UtcNowProvider = () => start.AddHours(1).AddSeconds(1);
            HostMode.CheckIdle();

            Assert.That(HostMode.StopRequestedForTest, Is.True);
        }

        [Test]
        public void DispatchedRequest_ResetsTheIdleClock()
        {
            // The activity signal that matters is a dispatched MCP request, not merely the process
            // being alive - otherwise a relay nobody is using would never self-shut-down at all.
            var start = DateTime.UtcNow;
            HostMode.UtcNowProvider = () => start;
            HostMode.RecordActivity();
            HostMode.IdleShutdownSecondsProvider = () => 60;

            HostMode.UtcNowProvider = () => start.AddSeconds(50);
            HostMode.DispatchForTest(
                new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "ping" }, Echo)
                .Wait(TimeSpan.FromSeconds(5));

            // Without the reset, 50s + 50s would exceed the 60s threshold; with it, only 50s have
            // passed since the dispatch above.
            HostMode.UtcNowProvider = () => start.AddSeconds(100);
            HostMode.CheckIdle();

            Assert.That(HostMode.StopRequestedForTest, Is.False);
        }

        [Test]
        public void HealthPolling_DoesNotResetTheIdleClock()
        {
            // /health is answered inline without ever reaching DispatchAsync (see HostMode's header
            // on this), so another session's ProxyMode polling for liveness must not by itself keep
            // an unused relay alive forever.
            var start = DateTime.UtcNow;
            HostMode.UtcNowProvider = () => start;
            HostMode.RecordActivity();
            HostMode.IdleShutdownSecondsProvider = () => 60;

            HostMode.UtcNowProvider = () => start.AddSeconds(30);
            _http.GetAsync(_url + "/health").GetAwaiter().GetResult();

            HostMode.UtcNowProvider = () => start.AddSeconds(61);
            HostMode.CheckIdle();

            Assert.That(HostMode.StopRequestedForTest, Is.True);
        }

        // ── Idle threshold: env var overrides the registry (XS-1692) ──────────────
        //
        // IdleShutdownSecondsProvider is reset to the real GetConfiguredIdleShutdownSeconds in
        // SetUp (not a test override), so these exercise the actual precedence logic rather than a
        // stand-in for it. The env var exists because a registry value can be hard to verify from
        // outside the exact process reading it (see this class's header on X1McpConnectorIdleShutdown)
        // - it propagates cleanly into whatever launches the exe, which the registry doesn't always.

        [Test]
        public void EnvVarOverride_TakesPrecedenceOverTheRegistry()
        {
            Environment.SetEnvironmentVariable("X1_MCP_IDLE_SHUTDOWN_SECONDS", "42");
            try
            {
                Assert.That(HostMode.IdleShutdownSecondsProvider(), Is.EqualTo(42));
            }
            finally
            {
                Environment.SetEnvironmentVariable("X1_MCP_IDLE_SHUTDOWN_SECONDS", null);
            }
        }

        [Test]
        public void EnvVarOverride_IgnoredWhenUnparseable_FallsBackToRegistryOrDefault()
        {
            Environment.SetEnvironmentVariable("X1_MCP_IDLE_SHUTDOWN_SECONDS", "not-a-number");
            try
            {
                var expected = RegistrySettings.ReadInteger("X1McpConnectorIdleShutdown", 3600);
                Assert.That(HostMode.IdleShutdownSecondsProvider(), Is.EqualTo(expected));
            }
            finally
            {
                Environment.SetEnvironmentVariable("X1_MCP_IDLE_SHUTDOWN_SECONDS", null);
            }
        }

        // ── x1_version's idleShutdownSeconds field (XS-1692) ───────────────────────
        //
        // The diagnostic surface that was missing when the registry value alone was hard to verify:
        // ask the running relay what it's actually using instead of inspecting the registry from a
        // possibly-different vantage point. DispatchForTest(request, null) routes through the REAL
        // McpServer.ProcessMessage (not the Echo stub this fixture otherwise uses), because this is
        // asserting on BuildVersionInfo's actual output.

        private static JObject X1VersionToolCallRequest()
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = "x1_version", ["arguments"] = new JObject() }
            };
        }

        private static JObject ParseToolResultJson(JObject response)
        {
            var text = response["result"]["content"][0]["text"].ToString();
            return JObject.Parse(text);
        }

        [Test]
        public void ToolsCallX1Version_ReportsIdleShutdownSeconds_WhenHostIsRunning()
        {
            HostMode.IdleShutdownSecondsProvider = () => 777;
            HostMode.IsHostRunning = true;
            try
            {
                var response = HostMode.DispatchForTest(X1VersionToolCallRequest(), null).GetAwaiter().GetResult();
                var version = ParseToolResultJson(response);

                Assert.That(version.Value<int?>("idleShutdownSeconds"), Is.EqualTo(777));
            }
            finally
            {
                HostMode.IsHostRunning = false;
            }
        }

        [Test]
        public void ToolsCallX1Version_OmitsIdleShutdownSeconds_WhenNotRunningAsHost()
        {
            // IsHostRunning is never set here: this is the --proxy / plain-stdio case, where
            // reporting the field would describe a timer that isn't actually running in this process.
            var response = HostMode.DispatchForTest(X1VersionToolCallRequest(), null).GetAwaiter().GetResult();
            var version = ParseToolResultJson(response);

            Assert.That(version["idleShutdownSeconds"], Is.Null);
        }

        // ── Port arbitration = the single-relay election ─────────────────────────

        [Test]
        public void SecondListenerOnTheSamePort_FailsToBind()
        {
            // The port bind is the authoritative election, above the shared mutex: verified to
            // arbitrate against Kestrel in both directions too, so this also covers a Full daemon.
            var second = new HttpListener();
            second.Prefixes.Add(_url + "/");
            var ex = Assert.Throws<HttpListenerException>(() => second.Start());
            Assert.That(ex, Is.Not.Null);
            try { second.Close(); } catch { }
        }

        // ── Prefix construction (pure) ───────────────────────────────────────────

        [Test]
        public void BuildPrefixCandidates_AlwaysOffersTheUrlsOwnHostFirst()
        {
            // HttpListener routes by the request's host token, so binding only 127.0.0.1 would not
            // match a client dialling "localhost" - which is exactly what GetDaemonUrl defaults to.
            var candidates = HostMode.BuildPrefixCandidates("http://localhost:5250");
            Assert.That(candidates[0][0], Is.EqualTo("http://localhost:5250/"));
            Assert.That(candidates[0], Does.Contain("http://127.0.0.1:5250/"));
            Assert.That(candidates[0], Does.Contain("http://[::1]:5250/"));
        }

        [Test]
        public void BuildPrefixCandidates_NarrowsToTheUrlHostAlone()
        {
            // The fallbacks exist for machines where one form (typically IPv6) is unbindable for
            // reasons other than contention; the last resort must still be what the client dials.
            var candidates = HostMode.BuildPrefixCandidates("http://localhost:5250");
            var last = candidates[candidates.Count - 1];
            Assert.That(last.Count, Is.EqualTo(1));
            Assert.That(last[0], Is.EqualTo("http://localhost:5250/"));
        }

        [Test]
        public void BuildPrefixCandidates_HonoursANonDefaultPort()
        {
            var candidates = HostMode.BuildPrefixCandidates("http://localhost:6001");
            Assert.That(candidates[0][0], Is.EqualTo("http://localhost:6001/"));
            foreach (var p in candidates[0])
                Assert.That(p, Does.Contain(":6001/"), "the port must never be hardcoded to 5250");
        }

        [Test]
        public void BuildPrefixCandidates_DoesNotDuplicateWhenUrlIsALoopbackLiteral()
        {
            var candidates = HostMode.BuildPrefixCandidates("http://127.0.0.1:5250");
            Assert.That(candidates[0][0], Is.EqualTo("http://127.0.0.1:5250/"));
            Assert.That(candidates[0].FindAll(p => p == "http://127.0.0.1:5250/").Count, Is.EqualTo(1));
        }

        [Test]
        public void BuildPrefixCandidates_MalformedUrl_FallsBackToTheDefault()
        {
            var candidates = HostMode.BuildPrefixCandidates("not a url");
            Assert.That(candidates[0][0], Is.EqualTo("http://localhost:5250/"));
        }
    }
}
