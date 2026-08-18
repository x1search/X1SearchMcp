// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace X1.McpBridge
{
    /// <summary>
    /// The Lean flavor's shared relay: serves the same MCP-JSON-RPC-over-HTTP contract the net10
    /// X1McpGraphQL daemon serves, from inside this net4.8 bridge process, so the customer package
    /// carries no .NET 10 dependency at all (the self-contained daemon is ~160MB of a ~182MB
    /// payload). Started detached as "X1McpBridge.exe --host" by ProxyMode; see ProxyMode's header
    /// for the two-flavor picture and BridgeConfig.GetRelayMode for how a flavor is chosen.
    ///
    /// Unlike the daemon - which relays over stdio to a separate bridge child - this process IS the
    /// bridge: requests go straight into McpServer.ProcessMessage, the same entry point RunStdio
    /// uses. That removes a process and a hop, and makes "exactly one WCF-owning process" simpler
    /// rather than harder, because the relay and the WCF owner are now the same thing.
    ///
    /// THE WIRE CONTRACT IS FIXED BY ProxyMode, NOT BY CHOICE. Everything below is dictated by what
    /// ProxyMode.ForwardAsync and CheckHealthAsync actually do, and two of those behaviours make
    /// mistakes here silent rather than loud:
    ///
    ///   • ProxyMode never inspects the HTTP status code. So EVERY response on EVERY path must have
    ///     a JSON body - an HttpListener default HTML 404 would reach its JObject.Parse, throw, and
    ///     be misreported to the user as "the relay is unavailable", after a pointless relaunch.
    ///   • A Content-Type containing "event-stream" sends ProxyMode down its SSE parser, which finds
    ///     no "data:" line in plain JSON, returns null, and writes nothing back - so the client
    ///     HANGS FOREVER waiting for a reply that was in fact produced. Content-Type must always be
    ///     application/json.
    ///
    /// SSE is deliberately not implemented: no tool here streams progress, and ProxyMode never opens
    /// a listening GET stream, so a plain request/response JSON endpoint is the whole requirement.
    /// </summary>
    internal static class HostMode
    {
        private static readonly log4net.ILog Log = BridgeLogger.GetLogger(typeof(HostMode));

        private const string McpPath = "/graphql/mcp";
        private const string HealthPath = "/health";
        private const string ShutdownPath = "/shutdown";
        private const string McpSessionHeaderName = "Mcp-Session-Id";
        private const string JsonContentType = "application/json";

        private sealed class WorkItem
        {
            public JObject Request;
            public TaskCompletionSource<JObject> Completion;
        }

        // Every ProcessMessage call funnels through this one queue and one consumer thread.
        //
        // This is NOT redundant with X1ConcurrencyWorkaround: that gate wraps only CallTool, while
        // resources/read -> ReadResource -> ConnectAndGetHostStatus() reaches WCF outside it. What
        // actually guarantees no overlap in stdio mode is RunStdio's single-threaded read loop, and
        // an HttpListener has no such loop - so it has to be reproduced here or the
        // two-clients-racing X1ServiceHost crash comes straight back.
        //
        // A single dedicated thread rather than a semaphore, deliberately: it also keeps thread
        // IDENTITY stable across requests. The duplex WCF callbacks (SearchManagerCallbacks) and the
        // native dependencies (PLUSManaged, ChilkatDotNet48) have unverified thread affinity, and
        // one long-lived thread is strictly closer to stdio's proven behaviour than an arbitrary
        // pool thread per call, for about ten extra lines.
        //
        // And it must NOT be X1ConcurrencyWorkaround.RunSerialized: that gate is a non-reentrant
        // SemaphoreSlim(1,1), so wrapping ProcessMessage in it would deadlock the moment CallTool
        // re-entered it. HostModeDispatchTests pins that.
        private static readonly BlockingCollection<WorkItem> Queue = new BlockingCollection<WorkItem>();

        private static HttpListener _listener;
        private static Mutex _singleInstance;
        private static volatile bool _stopping;
        private static readonly ManualResetEventSlim StopRequested = new ManualResetEventSlim(false);
        private static int _dispatchThreadStarted;

        // Cached so /health can be answered without touching WCF, the queue, or the filesystem.
        private static string _healthBody;

        // What the dispatch thread actually invokes. Overridable only by tests, so the serialization
        // guarantee can be asserted precisely (and the RunSerialized deadlock trap pinned) without
        // needing a live X1ServiceHost.
        private static Func<JObject, JObject> _handler = McpServer.ProcessMessage;

        // ── Idle self-shutdown (XS-1692) ─────────────────────────────────────────
        //
        // This host is deliberately kept running after every Claude session ends - that's the whole
        // point of a detached shared relay (see this class's header). The cost is that a resident
        // host holds its own files locked indefinitely, which blocks an upgrade that tries to replace
        // them. install.ps1 defends the standalone install by stopping the relay before copying, but
        // the Cowork/marketplace plugin's update mechanism has no equivalent hook to stop it first.
        //
        // An idle timeout is a partial mitigation for that gap, not a fix: it shrinks "locked until
        // someone notices" down to "locked for at most the idle threshold" by having the host shut
        // itself down - via the same path /shutdown already uses - once nothing has dispatched a
        // request through it for a while. Registry-configured (X1McpConnectorIdleShutdown, seconds,
        // under the same Software\X1 Search key every other bridge setting reads - see
        // RegistrySettings.cs) rather than in x1mcp.config.json: this is a machine/user runtime
        // policy, not a per-source-table content setting. Default 3600s (1 hour); 0 restores today's
        // unconditional keep-alive.
        private const string IdleShutdownRegistryValue = "X1McpConnectorIdleShutdown";
        private const string IdleShutdownEnvVar = "X1_MCP_IDLE_SHUTDOWN_SECONDS";
        private const int DefaultIdleShutdownSeconds = 3600;
        private const int IdleCheckIntervalMs = 30000;

        private static long _lastActivityTicks;
        private static Timer _idleTimer;

        // Test seams: overridden only by tests, so the threshold can be asserted against a simulated
        // clock instead of a real wait (the default is an hour).
        internal static Func<DateTime> UtcNowProvider = () => DateTime.UtcNow;
        internal static Func<int> IdleShutdownSecondsProvider = GetConfiguredIdleShutdownSeconds;

        // True only while this process is actually running the --host loop (set in Run(), cleared in
        // Shutdown()) - never while running as --proxy or plain stdio, neither of which run this
        // timer at all. Lets x1_version report idleShutdownSeconds only when it genuinely describes
        // this process's own behavior, rather than a value that happens to be configured but has no
        // effect here.
        internal static volatile bool IsHostRunning;

        // The env var is checked first because it propagates cleanly into whatever process launches
        // the exe regardless of registry-view quirks across execution contexts (elevation, remote
        // sessions, containerized/sandboxed test tooling) - exactly the failure mode that made the
        // registry value alone hard to verify in an agent-driven test session (XS-1692). The registry
        // value remains the customer-facing, persistent way to configure this; the env var exists for
        // scripted/automated verification, matching the X1_MCP_* precedent in BridgeConfig.cs.
        private static int GetConfiguredIdleShutdownSeconds()
        {
            var env = Environment.GetEnvironmentVariable(IdleShutdownEnvVar);
            int envSeconds;
            if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env.Trim(), out envSeconds))
                return envSeconds;

            return RegistrySettings.ReadInteger(IdleShutdownRegistryValue, DefaultIdleShutdownSeconds);
        }

        // Marks "a request was just dispatched" as the activity signal. Deliberately called from
        // DispatchAsync, not from HandleContextAsync: /health is answered inline without ever
        // reaching the queue (see below), so health polling from other sessions' ProxyMode instances
        // correctly does NOT count as activity and cannot defeat the timer.
        internal static void RecordActivity()
        {
            Interlocked.Exchange(ref _lastActivityTicks, UtcNowProvider().Ticks);
        }

        /// <summary>
        /// Compares time since the last dispatched request against the configured threshold, and
        /// requests shutdown (the same StopRequested path /shutdown and Console.CancelKeyPress use)
        /// if it has been exceeded. Invoked on a timer from Run(); exposed internally so tests can
        /// call it directly against a simulated clock rather than waiting on a real interval.
        /// </summary>
        internal static void CheckIdle()
        {
            int thresholdSeconds = IdleShutdownSecondsProvider();
            if (thresholdSeconds <= 0)
                return;

            var lastActivity = new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
            var idleFor = UtcNowProvider() - lastActivity;
            if (idleFor.TotalSeconds < thresholdSeconds)
                return;

            Log.Info("No activity for " + (int)idleFor.TotalSeconds + "s (threshold " + thresholdSeconds +
                     "s via " + IdleShutdownRegistryValue + "); shutting down.");
            StopRequested.Set();
        }

        public static int Run()
        {
            BridgeLogger.Configure();
            var relayUrl = BridgeConfig.GetDaemonUrl();
            Log.Info("X1 MCP Bridge starting (host transport -> " + relayUrl + ")");

            // ORDER BELOW IS LOAD-BEARING: claim singleness, then claim the port, and only then
            // open the WCF connection. Opening WCF first would mean N racing hosts each briefly
            // holding a second connection to X1ServiceHost - the exact crash this relay exists to
            // make structurally impossible.
            _singleInstance = HostSingleInstanceGuard.TryAcquire();
            if (_singleInstance == null)
            {
                // Quiet, not an error: losing this race is the normal outcome when several sessions
                // start at once, and the winner is already serving.
                Log.Info("Another X1 MCP relay is already running for this user session; exiting.");
                return 0;
            }

            try
            {
                if (!TryStartListener(relayUrl))
                    return 0;

                _healthBody = RelayHealth.BuildBody(
                    typeof(HostMode).Assembly.GetName().Version.ToString(),
                    RelayHealth.ComponentHost,
                    Process.GetCurrentProcess().Id,
                    Environment.UserName,
                    SafeProcessPath());

                McpServer.StartBackend();
                EnsureDispatchThreadStarted();
                InstallShutdownHooks();

                // Baseline "just started" as the last activity, so a fresh host with an empty queue
                // isn't immediately treated as having sat idle since the Unix epoch.
                RecordActivity();
                _idleTimer = new Timer(_ => CheckIdle(), null, IdleCheckIntervalMs, IdleCheckIntervalMs);
                IsHostRunning = true;

                var accept = Task.Run((Func<Task>)AcceptLoopAsync);

                Log.Info("X1 MCP relay is serving on " + relayUrl + " (pid " +
                         Process.GetCurrentProcess().Id + ").");

                StopRequested.Wait();
                Log.Info("X1 MCP relay shutting down.");

                Shutdown();
                try { accept.Wait(2000); } catch { /* accept loop unblocks via listener close */ }
            }
            finally
            {
                // Released last: while this is held, no other relay can decide it is safe to open a
                // second WCF connection.
                try { _singleInstance.ReleaseMutex(); } catch { /* not owned / already released */ }
                _singleInstance.Dispose();
            }

            return 0;
        }

        // ── Listener setup ───────────────────────────────────────────────────────

        /// <summary>
        /// Binds the relay port. Returns false when the port is already owned, which is a normal,
        /// quiet exit: the port bind is the authoritative single-relay election (HTTP.SYS and
        /// Kestrel do arbitrate against each other in both directions, so this covers a Full daemon
        /// too, not just another host).
        /// </summary>
        private static bool TryStartListener(string relayUrl)
        {
            var prefixSets = BuildPrefixCandidates(relayUrl);

            for (int i = 0; i < prefixSets.Count; i++)
            {
                var listener = new HttpListener();
                foreach (var prefix in prefixSets[i])
                    listener.Prefixes.Add(prefix);

                try
                {
                    listener.Start();
                    _listener = listener;
                    Log.Info("Listening on " + string.Join(", ", prefixSets[i].ToArray()) + ".");
                    return true;
                }
                catch (HttpListenerException ex)
                {
                    try { listener.Close(); } catch { }

                    // Last candidate failed too - the port is genuinely taken (or reserved).
                    if (i == prefixSets.Count - 1)
                    {
                        Log.Info("Could not bind the relay port (" + ex.Message + "); another relay " +
                                 "owns it, so this one is exiting. ProxyMode decides which relay wins " +
                                 "and evicts the loser - see RelayHealth.Decide.");
                        return false;
                    }

                    // Otherwise fall through and retry with a narrower prefix set: the failure may
                    // just be one unbindable form (e.g. IPv6 disabled) rather than contention.
                    Log.Debug("Prefix set " + (i + 1) + " failed (" + ex.Message + "); trying a narrower set.");
                }
                catch (Exception ex)
                {
                    try { listener.Close(); } catch { }
                    Log.Error("Unexpected failure binding the relay port", ex);
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Progressively narrower prefix sets to try, most complete first.
        ///
        /// HttpListener routes by the request's host token, so binding only the literal
        /// "http://127.0.0.1:{port}/" would NOT match a client that dials "localhost" - and
        /// ProxyMode dials whatever BridgeConfig.GetDaemonUrl() says, which defaults to localhost.
        /// So the URL's own host form is always included and always first; the loopback literals are
        /// added as extras so the relay answers however a client spells the address. All three forms
        /// were verified to bind non-elevated, and the narrower fallbacks exist for machines where
        /// one form (typically IPv6) is unavailable for reasons other than contention.
        /// </summary>
        internal static List<List<string>> BuildPrefixCandidates(string relayUrl)
        {
            int port;
            string host;
            try
            {
                var uri = new Uri(relayUrl);
                port = uri.Port;
                host = uri.Host;
            }
            catch
            {
                port = 5250;
                host = "localhost";
            }

            // Uri normalises an IPv6 literal's host to bare "::1"; HttpListener prefixes need the
            // bracketed form back.
            string hostToken = host.IndexOf(':') >= 0 && !host.StartsWith("[", StringComparison.Ordinal)
                ? "[" + host + "]"
                : host;

            var primary = "http://" + hostToken + ":" + port + "/";
            var v4 = "http://127.0.0.1:" + port + "/";
            var v6 = "http://[::1]:" + port + "/";

            var all = new List<string> { primary };
            if (!all.Contains(v4)) all.Add(v4);
            if (!all.Contains(v6)) all.Add(v6);

            var withoutV6 = new List<string>(all);
            withoutV6.Remove(v6);

            var candidates = new List<List<string>> { all };
            if (withoutV6.Count != all.Count)
                candidates.Add(withoutV6);
            candidates.Add(new List<string> { primary });
            return candidates;
        }

        private static string SafeProcessPath()
        {
            try { return Process.GetCurrentProcess().MainModule.FileName; }
            catch { return typeof(HostMode).Assembly.Location; }
        }

        // ── Accept loop ──────────────────────────────────────────────────────────

        private static async Task AcceptLoopAsync()
        {
            while (!_stopping)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (_stopping)
                {
                    return;   // listener closed by Shutdown()
                }
                catch (Exception ex)
                {
                    Log.Warn("Accept failed: " + ex.Message);
                    continue;
                }

                // Handled off the accept path so a 120s tool call can never delay accepting - and
                // in particular can never delay /health, which ProxyMode gives only 2s before it
                // concludes no relay is running and launches a redundant one.
                var _ = Task.Run(() => HandleContextAsync(ctx));
            }
        }

        private static async Task HandleContextAsync(HttpListenerContext ctx)
        {
            try
            {
                var path = (ctx.Request.Url == null ? "" : ctx.Request.Url.AbsolutePath).TrimEnd('/');
                if (path.Length == 0)
                    path = "/";
                var method = ctx.Request.HttpMethod;

                if (PathEquals(path, HealthPath) && method == "GET")
                {
                    // Answered inline from a cached string: no queue, no WCF, no disk.
                    WriteJson(ctx, 200, _healthBody);
                    return;
                }

                if (PathEquals(path, ShutdownPath) && method == "POST")
                {
                    Log.Info("Shutdown requested over HTTP.");
                    WriteJson(ctx, 200, "{\"stopping\":true}");
                    StopRequested.Set();
                    return;
                }

                if (PathEquals(path, McpPath))
                {
                    if (method != "POST")
                    {
                        // 405 with a JSON body, never HttpListener's default HTML: ProxyMode would
                        // try to JObject.Parse it.
                        WriteJsonRpcError(ctx, 405, null, -32600,
                            "Only POST is supported on " + McpPath + " (this relay does not serve SSE).");
                        return;
                    }

                    await HandleMcpPostAsync(ctx).ConfigureAwait(false);
                    return;
                }

                WriteJsonRpcError(ctx, 404, null, -32601, "No such endpoint: " + path);
            }
            catch (Exception ex)
            {
                Log.Error("Unhandled error serving a request", ex);
                try { WriteJsonRpcError(ctx, 500, null, -32603, "Internal relay error.", ex.Message); }
                catch { /* client gone */ }
            }
        }

        private static bool PathEquals(string actual, string expected)
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task HandleMcpPostAsync(HttpListenerContext ctx)
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            JObject request;
            try
            {
                request = JObject.Parse(body);
            }
            catch (Exception ex)
            {
                Log.Warn("Rejecting an unparseable request body: " + ex.Message);
                WriteJsonRpcError(ctx, 400, null, -32700, "Parse error", ex.Message);
                return;
            }

            // Session id: minted on initialize and echoed thereafter. ProxyMode captures whatever
            // comes back and returns it on later requests, but tolerates its absence entirely, and
            // McpServer's state is process-wide static, so this is continuity signalling rather than
            // real session isolation - same as in the daemon, where one shared stdio bridge serves
            // every session anyway.
            var sessionId = ctx.Request.Headers[McpSessionHeaderName];
            if (string.IsNullOrEmpty(sessionId) &&
                string.Equals(request.Value<string>("method"), "initialize", StringComparison.Ordinal))
            {
                sessionId = Guid.NewGuid().ToString("N");
            }
            if (!string.IsNullOrEmpty(sessionId))
                ctx.Response.Headers[McpSessionHeaderName] = sessionId;

            var response = await DispatchAsync(request).ConfigureAwait(false);

            if (response == null)
            {
                // Notification: 202 with a zero-length body, matching the Streamable HTTP behaviour
                // ProxyMode already special-cases (it returns without reading the body at all).
                ctx.Response.StatusCode = 202;
                ctx.Response.ContentLength64 = 0;
                ctx.Response.Close();
                return;
            }

            WriteJson(ctx, 200, response.ToString(Formatting.None));
        }

        private static Task<JObject> DispatchAsync(JObject request)
        {
            RecordActivity();

            var item = new WorkItem
            {
                Request = request,
                Completion = new TaskCompletionSource<JObject>(),
            };

            try
            {
                Queue.Add(item);
            }
            catch (Exception ex)
            {
                // Queue completed (shutting down) - answer rather than hang.
                item.Completion.TrySetResult(
                    McpProtocol.Err(request["id"], -32603, "The relay is shutting down.", ex.Message));
            }

            return item.Completion.Task;
        }

        // ── Dispatch thread ──────────────────────────────────────────────────────

        /// <summary>
        /// Starts the one and only dispatch thread. Idempotent, because "exactly one consumer" is
        /// the entire serialization guarantee - a second consumer on this queue would silently
        /// reintroduce concurrent WCF access.
        /// </summary>
        private static void EnsureDispatchThreadStarted()
        {
            if (Interlocked.CompareExchange(ref _dispatchThreadStarted, 1, 0) != 0)
                return;

            var dispatcher = new Thread(DispatchLoop);
            dispatcher.IsBackground = true;
            dispatcher.Name = "X1McpHostDispatch";
            dispatcher.Start();
        }

        private static void DispatchLoop()
        {
            foreach (var item in Queue.GetConsumingEnumerable())
            {
                try
                {
                    item.Completion.TrySetResult(_handler(item.Request));
                }
                catch (Exception ex)
                {
                    // One malformed or unlucky request must never kill this thread: in the Lean
                    // flavor there is no supervising daemon to restart anything, so losing the
                    // dispatcher would silently wedge every future call in the session.
                    Log.Error("Dispatch failed", ex);
                    item.Completion.TrySetResult(
                        McpProtocol.Err(item.Request["id"], -32603, "Internal error handling the request.", ex.Message));
                }
            }
        }

        // ── Shutdown ─────────────────────────────────────────────────────────────

        /// <summary>
        /// XS-1701: lets a caller outside HostMode (McpServer, when X1's OnShutdown callback
        /// fires) request the same graceful shutdown /shutdown and the idle timeout already use.
        /// Safe to call from any thread, including a WCF callback thread - it only sets the event
        /// Run()'s main loop is blocked on; the actual Shutdown() sequence (drain, disconnect,
        /// exit) always runs on Run()'s own thread, never on the caller's thread.
        /// </summary>
        internal static void RequestShutdown(string reason)
        {
            Log.Info("Shutdown requested: " + reason);
            StopRequested.Set();
        }

        /// <summary>
        /// A detached relay has no stdin pipe, so it never gets RunStdio's stdin-EOF exit - the
        /// mechanism the daemon relies on to reap its own bridge child. Without an explicit stop
        /// path this process would be an immortal orphan holding the WCF connection, which is the
        /// very state that makes X1ServiceHost crash when the next relay starts.
        /// </summary>
        private static void InstallShutdownHooks()
        {
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;      // shut down gracefully instead of being torn down mid-call
                StopRequested.Set();
            };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();
        }

        private static void Shutdown()
        {
            if (_stopping)
                return;
            _stopping = true;
            IsHostRunning = false;

            try { if (_idleTimer != null) _idleTimer.Dispose(); } catch { }

            // Stop accepting, then let in-flight work drain before dropping WCF: a half-finished
            // tool call that loses its connection surfaces as a confusing error to the user.
            try { if (_listener != null) _listener.Close(); } catch { }
            try { Queue.CompleteAdding(); } catch { }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Queue.Count > 0 && DateTime.UtcNow < deadline)
                Thread.Sleep(50);

            McpServer.StopBackend();
            StopRequested.Set();
        }

        // ── Response helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// The single exit point for every response body, so no path can accidentally answer with
        /// HttpListener's default HTML or with a Content-Type ProxyMode would misread as SSE. Both
        /// mistakes are silent (see this class's header).
        /// </summary>
        private static void WriteJson(HttpListenerContext ctx, int statusCode, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json ?? "{}");
            try
            {
                ctx.Response.StatusCode = statusCode;
                ctx.Response.ContentType = JsonContentType;
                ctx.Response.ContentEncoding = Encoding.UTF8;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not write response (client likely disconnected): " + ex.Message);
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        private static void WriteJsonRpcError(
            HttpListenerContext ctx, int statusCode, JToken id, int code, string message, string data = null)
        {
            // A JSON-RPC error object rather than a bare HTTP status, because ProxyMode never looks
            // at the status code - it parses the body and relays it. This is what turns a routing or
            // parse mistake into a clear tool error instead of a spurious "the relay is unavailable"
            // plus a pointless relaunch.
            WriteJson(ctx, statusCode, McpProtocol.Err(id, code, message, data).ToString(Formatting.None));
        }

        // ── Test seams ───────────────────────────────────────────────────────────
        //
        // These exist so the two silent-failure traps in this class's header (a non-JSON body, and a
        // Content-Type that sends ProxyMode into its SSE parser and hangs the client forever) are
        // covered by real HTTP round trips rather than by reading the code. They deliberately skip
        // the single-instance mutex and McpServer.StartBackend, so tests neither fight a real
        // running relay for the shared mutex name nor need a live X1ServiceHost.

        /// <summary>Test-only: serves HTTP on <paramref name="relayUrl"/> with no mutex and no WCF.</summary>
        internal static bool StartServingForTest(string relayUrl, Func<JObject, JObject> handler)
        {
            _handler = handler ?? McpServer.ProcessMessage;
            _stopping = false;

            // Idle state is static and would otherwise leak between tests (a prior test's fake clock
            // or threshold override, or a StopRequested left set by the /shutdown endpoint test).
            UtcNowProvider = () => DateTime.UtcNow;
            IdleShutdownSecondsProvider = GetConfiguredIdleShutdownSeconds;
            IsHostRunning = false;
            StopRequested.Reset();
            RecordActivity();

            if (!TryStartListener(relayUrl))
                return false;

            _healthBody = RelayHealth.BuildBody(
                typeof(HostMode).Assembly.GetName().Version.ToString(),
                RelayHealth.ComponentHost,
                Process.GetCurrentProcess().Id,
                Environment.UserName,
                SafeProcessPath());

            EnsureDispatchThreadStarted();
            var _ = Task.Run((Func<Task>)AcceptLoopAsync);
            return true;
        }

        /// <summary>
        /// Test-only: stops serving but deliberately leaves the queue open and the dispatch thread
        /// alive, so a later test can start serving again. CompleteAdding is permanent.
        /// </summary>
        internal static void StopServingForTest()
        {
            _stopping = true;
            try { if (_listener != null) _listener.Close(); } catch { }
            _listener = null;
            _handler = McpServer.ProcessMessage;
        }

        /// <summary>Test-only: pushes a request through the real queue and dispatch thread.</summary>
        internal static Task<JObject> DispatchForTest(JObject request, Func<JObject, JObject> handler)
        {
            _handler = handler ?? McpServer.ProcessMessage;
            EnsureDispatchThreadStarted();
            return DispatchAsync(request);
        }

        /// <summary>
        /// Test-only: whether something has asked this host to stop (CheckIdle, /shutdown, or
        /// Console.CancelKeyPress) - the observable effect of CheckIdle() under StartServingForTest,
        /// which never runs Run()'s own StopRequested.Wait() loop.
        /// </summary>
        internal static bool StopRequestedForTest
        {
            get { return StopRequested.IsSet; }
        }

        /// <summary>
        /// XS-1701 test-only: clears a StopRequested signal left set by a previous test (e.g.
        /// RequestShutdown), without the side effects of StartServingForTest (which also binds a
        /// real HTTP listener) - for tests that only care about the StopRequested/IsHostRunning
        /// signal itself, not the HTTP serving loop.
        /// </summary>
        internal static void ResetStopRequestedForTest()
        {
            StopRequested.Reset();
        }
    }
}
