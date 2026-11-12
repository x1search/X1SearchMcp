// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace X1.McpBridge
{
    /// <summary>
    /// Fan-in shim: Claude Code/Desktop spawn this over stdio exactly like the real bridge, but
    /// instead of talking WCF to X1ServiceHost itself, it proxies MCP JSON-RPC to the single
    /// shared relay's HTTP endpoint, lazily launching that relay if it isn't already running. This
    /// is what makes "exactly one bridge process, ever" achievable without losing the zero-touch
    /// "just install the plugin" experience stdio transport gives for free — see
    /// X1ConcurrencyWorkaround.cs for why that matters (X1ServiceHost crashes when 2+ clients
    /// race Connect()/session teardown).
    ///
    /// The relay is one of two interchangeable things, decided per install by
    /// BridgeConfig.GetRelayMode():
    ///   • Full flavor  — the bundled net10 X1McpGraphQL.exe daemon, which spawns its own bridge child.
    ///   • Lean flavor  — a detached "X1McpBridge.exe --host" (HostMode.cs), which IS the bridge, so
    ///                    the customer package carries no .NET 10 dependency at all.
    /// Both serve the identical contract on the identical URL, so everything below is flavor-blind
    /// apart from EnsureRelayRunningAsync's launch/eviction decisions.
    ///
    /// Deliberately a "dumb pipe": this does not implement MCP semantics itself. It reads a
    /// JSON-RPC line from stdin, POSTs it to the relay's /graphql/mcp Streamable HTTP endpoint,
    /// and writes back whatever the relay answers. This file has zero tool-specific knowledge, so
    /// the tool set can change without ever touching this proxy.
    /// </summary>
    internal static class ProxyMode
    {
        private static readonly log4net.ILog Log = BridgeLogger.GetLogger(typeof(ProxyMode));
        private const string McpSessionHeaderName = "Mcp-Session-Id";

        public static int Run(TextWriter mcpOut)
        {
            BridgeLogger.Configure();
            var daemonUrl = BridgeConfig.GetDaemonUrl();
            Log.Info("X1 MCP Bridge starting (proxy mode -> " + daemonUrl + ", expecting a " +
                     BridgeConfig.GetRelayMode() + " relay)");

            // UseProxy=false: the relay is always loopback, so a machine-wide WinINET/PAC proxy has no
            // business seeing these requests. Timeout is per request instead (see RequestTimeout).
            using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan })
            {
                // Non-null while the last attempt to get a usable relay failed. While it is set, each
                // request re-checks the relay first and, if it is still unusable, answers with the
                // error at once instead of forwarding into a request timeout (XS-1819 AC4).
                string relayFailure = EnsureRelayRunningAsync(http, daemonUrl).GetAwaiter().GetResult();
                var relayCheckedAt = DateTime.UtcNow;
                if (relayFailure != null)
                    Log.Error("No usable relay at startup: " + relayFailure + ". Will retry on the first request.");

                string sessionId = null;
                string line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    JObject request;
                    try
                    {
                        request = JObject.Parse(line);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Failed to parse incoming JSON-RPC line", ex);
                        continue;
                    }

                    bool isNotification = McpProtocol.IsNotification(request);
                    JObject response;

                    if (relayFailure != null)
                    {
                        // A check that just failed is the answer; re-checking it again at once would
                        // only push the client's error past the point it is useful.
                        if (DateTime.UtcNow - relayCheckedAt >= RelayRecheckInterval)
                        {
                            try { relayFailure = EnsureRelayRunningAsync(http, daemonUrl, afterFailure: true).GetAwaiter().GetResult(); }
                            catch (Exception exCheck) { relayFailure = exCheck.Message; }
                            relayCheckedAt = DateTime.UtcNow;
                        }

                        if (relayFailure != null)
                        {
                            Log.Error("Not forwarding " + DescribeRequest(request) + ": " + relayFailure + ".");
                            if (!isNotification)
                                WriteResponse(mcpOut, RelayUnavailableError(request, daemonUrl, relayFailure, null));
                            continue;
                        }
                    }

                    try
                    {
                        var result = ForwardAsync(http, daemonUrl, request, sessionId).GetAwaiter().GetResult();
                        response = result.Response;
                        sessionId = result.SessionId;
                    }
                    catch (Exception ex)
                    {
                        // XS-1819: this used to force-launch a second relay and retry. That can never
                        // help - if a relay holds the single-instance lock the new one exits at once,
                        // and the retry lands on the same broken relay. Instead, find out what is
                        // actually wrong: a healthy relay gets a retry on a fresh connection, a stale
                        // one gets replaced, and anything else becomes a clear error.
                        Log.Warn("Forwarding " + DescribeRequest(request) + " to the relay failed (" + Flatten(ex) +
                                 "); checking the relay before retrying once.");
                        string failure;
                        try
                        {
                            failure = EnsureRelayRunningAsync(http, daemonUrl, afterFailure: true).GetAwaiter().GetResult();
                        }
                        catch (Exception exCheck)
                        {
                            failure = exCheck.Message;
                        }

                        if (failure == null)
                        {
                            try
                            {
                                var result = ForwardAsync(http, daemonUrl, request, sessionId, freshConnection: true).GetAwaiter().GetResult();
                                response = result.Response;
                                sessionId = result.SessionId;
                            }
                            catch (Exception ex2)
                            {
                                Log.Error("Forwarding " + DescribeRequest(request) + " to the relay failed again on a fresh connection", ex2);
                                failure = "the relay is answering health checks but the request failed twice (" + Flatten(ex2) + ")";
                                response = null;
                            }
                        }
                        else
                        {
                            // No usable relay at all: later requests answer at once until one is.
                            relayFailure = failure;
                            relayCheckedAt = DateTime.UtcNow;
                            response = null;
                        }

                        if (failure != null)
                        {
                            Log.Error("Giving up on " + DescribeRequest(request) + ": " + failure + ".");
                            response = isNotification ? null : RelayUnavailableError(request, daemonUrl, failure, ex);
                        }
                    }

                    if (response != null)
                        WriteResponse(mcpOut, response);
                }
            }

            Log.Info("X1 MCP Bridge (proxy mode) shutting down (stdin closed)");
            return 0;
        }

        private static void WriteResponse(TextWriter mcpOut, JObject response)
        {
            mcpOut.Write(response.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            mcpOut.Flush();
        }

        private static JObject RelayUnavailableError(JObject request, string daemonUrl, string failure, Exception cause)
        {
            return McpProtocol.Err(request["id"], -32603,
                "The shared X1 MCP relay on " + daemonUrl + " is not responding: " + failure +
                ". Details are in %LOCALAPPDATA%\\X1 Discovery\\McpBridge\\logs\\X1McpBridge.log.",
                cause == null ? null : Flatten(cause));
        }

        // ── Relay lifecycle ──────────────────────────────────────────────────────

        // The version a freshly-launched relay from THIS install would report. Lazy + cached: read
        // once, not on every health check.
        //
        // In Daemon mode this is the bundled daemon exe's file version. In Host mode it is this
        // assembly's own version - which is always knowable, and that is the point. The previous
        // implementation only ever read the daemon exe's version and returned null when that file
        // was missing, i.e. always in a Lean install; combined with a "null means it matches"
        // fallback, every Lean proxy would have silently adopted whatever was already on the port,
        // including an older leftover daemon from the install it had just replaced.
        private static readonly Lazy<string> OurRelayVersion = new Lazy<string>(() =>
        {
            try
            {
                if (BridgeConfig.GetRelayMode() == RelayMode.Host)
                    return typeof(ProxyMode).Assembly.GetName().Version.ToString();

                var path = BridgeConfig.GetDaemonExePath();
                return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;
            }
            catch
            {
                return null;
            }
        });

        // A Lean host binds its port in well under a second (the customer logs in XS-1819 show
        // ~150ms), so waiting the full daemonStartupTimeoutMs - sized for the Full daemon's
        // self-extract - would only delay the clear error the user should get instead.
        private const int HostStartupWaitCapMs = 5000;

        // A --host process younger than this is assumed to be another proxy's fresh launch that just
        // hasn't bound the port yet, never a stale relay - see RelayLauncher.SelectStaleHosts.
        private static readonly TimeSpan StaleHostMinAge = TimeSpan.FromSeconds(10);

        // While no relay is usable, how stale the last failed check may be before a request triggers
        // a fresh one rather than being answered with that failure straight away.
        private static readonly TimeSpan RelayRecheckInterval = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Makes sure a usable relay is answering. Returns null on success, or a one-line reason
        /// fit for the user's error message on failure.
        ///
        /// <paramref name="afterFailure"/> means a forward just failed. A relay that still passes the
        /// health check is then kept (the caller retries on a fresh connection); one that doesn't is
        /// treated as stale rather than merely "not started yet".
        /// </summary>
        private static async Task<string> EnsureRelayRunningAsync(HttpClient http, string daemonUrl, bool afterFailure = false)
        {
            var target = BridgeConfig.GetRelayLaunchTarget();

            var snap = await CheckHealthAsync(http, daemonUrl).ConfigureAwait(false);
            {
                var decision = RelayHealth.Decide(snap, target.Mode, OurRelayVersion.Value, Environment.UserName);

                switch (decision.Action)
                {
                    case RelayHealth.Action.Use:
                        if (afterFailure)
                            Log.Info("The relay still passes its health check (" + DescribeSnapshot(snap) + ").");
                        else
                            Log.Debug("Using the relay already on " + daemonUrl + ": " + decision.Reason + ".");
                        return null;

                    case RelayHealth.Action.RefuseWrongUser:
                        // Deliberately fatal-ish: returning here means every forwarded call in this
                        // session would answer from another user's index. Better a loud, explained
                        // failure on the first call than silently correct-looking wrong answers.
                        Log.Error("Refusing to use the relay on " + daemonUrl + ": " + decision.Reason + ".");
                        throw new InvalidOperationException(
                            "The X1 MCP relay on " + daemonUrl + " " + decision.Reason + ".");

                    case RelayHealth.Action.EvictThenLaunch:
                        Log.Warn("Stopping the relay on " + daemonUrl + " before starting ours: " +
                                 decision.Reason + ".");
                        EvictRelay(decision);
                        await WaitForPortReleaseAsync(http, daemonUrl).ConfigureAwait(false);
                        break;

                    case RelayHealth.Action.Launch:
                        Log.Info("No relay is answering on " + daemonUrl + "; starting one.");
                        break;
                }
            }

            if (await LaunchAndWaitAsync(http, daemonUrl, target).ConfigureAwait(false))
                return null;

            // Nothing healthy came up. In the Lean flavor the usual reason is a --host that still
            // holds the single-instance mutex but isn't answering (hung, suspended, wedged): every
            // relay we launch sees the mutex and exits, forever. Replace it.
            if (target.Mode == RelayMode.Host)
            {
                var stale = RelayLauncher.SelectStaleHosts(
                    RelayLauncher.FindHostProcesses(),
                    Process.GetCurrentProcess().SessionId,
                    Process.GetCurrentProcess().Id,
                    DateTime.UtcNow,
                    StaleHostMinAge);

                if (stale.Count > 0)
                {
                    foreach (var h in stale)
                    {
                        Log.Warn("Relay pid " + h.Pid + " (started " +
                                 (h.StartedUtc == DateTime.MinValue ? "at an unknown time" : h.StartedUtc.ToString("o")) +
                                 ") holds the single-instance lock but is not answering /health on " + daemonUrl +
                                 "; stopping it.");
                        KillProcessById(h.Pid);
                    }

                    if (await LaunchAndWaitAsync(http, daemonUrl, target).ConfigureAwait(false))
                        return null;

                    return "a stale relay (pid " + string.Join(", ", stale.ConvertAll(h => h.Pid.ToString()).ToArray()) +
                           ") was replaced, but the new relay did not answer either";
                }
            }

            return DescribeUnreachable(daemonUrl, target);
        }

        /// <summary>
        /// Launches the relay and waits for it to pass a health check. "Relay is up" is logged only
        /// here, and only after /health has actually answered with a body (XS-1819).
        /// </summary>
        private static async Task<bool> LaunchAndWaitAsync(HttpClient http, string daemonUrl, RelayLaunchTarget target)
        {
            int launchedPid = LaunchRelayDetached(target);

            int waitMs = BridgeConfig.GetDaemonStartupTimeoutMs();
            if (target.Mode == RelayMode.Host)
                waitMs = Math.Min(waitMs, HostStartupWaitCapMs);

            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (DateTime.UtcNow < deadline)
            {
                var snap = await CheckHealthAsync(http, daemonUrl).ConfigureAwait(false);
                if (snap.Reachable)
                {
                    Log.Info("Relay is up (" + DescribeSnapshot(snap) +
                             (launchedPid > 0 && snap.Pid > 0 && snap.Pid != launchedPid
                                 ? "; answered by an existing relay, not the one just launched (pid " + launchedPid + ")"
                                 : "") + ").");
                    return true;
                }
                await Task.Delay(300).ConfigureAwait(false);
            }

            Log.Warn("No relay answered /health on " + daemonUrl + " within " + waitMs + "ms of launching " +
                     (launchedPid > 0 ? "pid " + launchedPid : "the relay") + ".");
            return false;
        }

        private static string DescribeUnreachable(string daemonUrl, RelayLaunchTarget target)
        {
            int port = 0;
            try { port = new Uri(daemonUrl).Port; } catch { }

            bool portHasListener = false;
            try
            {
                foreach (var ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                {
                    if (ep.Port == port)
                    {
                        portHasListener = true;
                        break;
                    }
                }
            }
            catch { /* diagnostic only */ }

            if (portHasListener)
                return "something is listening on port " + port + " but it is not answering as an X1 relay " +
                       "(another program may be using the port; 'netstat -ano | findstr :" + port + "' shows its pid)";

            return "the relay (" + target.FileName + (string.IsNullOrEmpty(target.Arguments) ? "" : " " + target.Arguments) +
                   ") was launched but never started listening on port " + port;
        }

        private static string DescribeSnapshot(RelayHealth.Snapshot snap)
        {
            return (snap.Component ?? "unidentified relay") +
                   (snap.Pid > 0 ? " pid " + snap.Pid : "") +
                   (snap.Version != null ? " v" + snap.Version : "") +
                   (snap.ExePath != null ? " at " + snap.ExePath : "");
        }

        /// <summary>
        /// Health check. A relay counts as reachable only if /health returns success AND its whole
        /// body can be read - the read is what fails when replies are being cut off. GetAsync
        /// buffers the body by default, so a truncated reply surfaces here as an exception.
        /// </summary>
        internal static async Task<RelayHealth.Snapshot> CheckHealthAsync(HttpClient http, string daemonUrl)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                using (var resp = await http.GetAsync(daemonUrl + "/health", cts.Token).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                        return RelayHealth.Snapshot.Unreachable();

                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return RelayHealth.Parse(body);
                }
            }
            catch
            {
                return RelayHealth.Snapshot.Unreachable();
            }
        }

        // After an eviction, give the OS a moment to actually free the port before we launch into
        // it. Without this, the new relay can lose its own bind race against the corpse of the old
        // one and exit immediately, leaving nothing on the port at all.
        private static async Task WaitForPortReleaseAsync(HttpClient http, string daemonUrl)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (!await IsHealthyAsync(http, daemonUrl).ConfigureAwait(false))
                    return;
                await Task.Delay(200).ConfigureAwait(false);
            }
            Log.Warn("The evicted relay is still answering on " + daemonUrl + " after 5s; launching anyway.");
        }

        private static async Task<bool> IsHealthyAsync(HttpClient http, string daemonUrl)
        {
            return (await CheckHealthAsync(http, daemonUrl).ConfigureAwait(false)).Reachable;
        }

        // Stops the relay that currently owns the port.
        //
        // Preferred path is the pid the relay itself reported in /health: by definition that is the
        // one process holding this port, which makes eviction exact instead of a machine-wide sweep
        // by process name, and - crucially - makes it safe to evict a Lean host. Killing
        // "X1McpBridge" by bare name could never be safe: it would take out every other session's
        // --proxy shim, and it would take out the Full daemon's own managed bridge child, which is
        // the orphan-still-holding-a-WCF-connection case described below.
        private static void EvictRelay(RelayHealth.Decision decision)
        {
            if (decision.EvictPid > 0)
            {
                KillProcessById(decision.EvictPid);
                return;
            }

            // No pid reported, so this is a relay predating the widened /health body - which can
            // only be an X1McpGraphQL daemon, since --host never existed before that field did.
            // Fall back to the historical machine-wide kill by name, which is correct precisely
            // because the daemon is shared on a fixed port regardless of which install started it,
            // so "the daemon" is unambiguous even with several installs on disk.
            //
            // The daemon's own managed real bridge (spawned with no args, not --proxy) is
            // deliberately not touched here: it's a plain child process whose stdin pipe is owned by
            // the daemon, so it exits on its own via stdin-EOF once the daemon is gone (the same
            // path behind the "X1 MCP Bridge shutting down (stdin closed)" log line) - it is not run
            // under a Job Object and would otherwise survive as an orphan still holding a WCF
            // connection, which is exactly the 2-connections-racing crash this whole proxy exists to
            // avoid. Other sessions' own --proxy shims are untouched either way: they only talk to
            // the relay over HTTP and reconnect via their own retry-once-and-relaunch path in Run().
            if (!string.Equals(decision.EvictComponent, RelayHealth.ComponentDaemon, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("Cannot evict the relay: it reported no pid and is not an " +
                         RelayHealth.ComponentDaemon + " process, so there is no safe way to identify it by name.");
                return;
            }

            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(RelayHealth.ComponentDaemon);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not enumerate " + RelayHealth.ComponentDaemon +
                         " processes to stop the stale daemon: " + ex.Message);
                return;
            }

            foreach (var p in procs)
            {
                try
                {
                    Log.Info("Stopping stale daemon process (pid " + p.Id + ").");
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not stop stale daemon process (pid " + p.Id + "): " + ex.Message);
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        private static void KillProcessById(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    Log.Info("Stopping the relay that owns the port (pid " + pid + ").");
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch (ArgumentException)
            {
                // Already gone between the health check and now - the desired end state anyway.
                Log.Debug("Relay pid " + pid + " had already exited.");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not stop relay pid " + pid + ": " + ex.Message);
            }
        }

        // Launched detached and never awaited/tracked: this process must outlive this shim so
        // every future session (this one's, and every other client's) finds it already running.
        // In Lean mode the target is this very exe re-launched as --host, which is why the launch
        // has to stay detached rather than becoming an in-process thread: Cowork tears down
        // per-task sandboxes, and a relay owned by one session's process tree dies with it.
        //
        // XS-1819: "detached" now means it - no inherited handles and out of the caller's job. See
        // RelayLauncher for why Process.Start could not do that. Returns the new pid, or 0.
        private static int LaunchRelayDetached(RelayLaunchTarget target)
        {
            if (!File.Exists(target.FileName))
            {
                Log.Error("Relay executable not found at '" + target.FileName + "'; cannot launch it. " +
                          "Configure daemonExePath in x1mcp.config.json if it lives elsewhere.");
                return 0;
            }

            Log.Info("Launching shared relay: " + target.FileName +
                     (string.IsNullOrEmpty(target.Arguments) ? "" : " " + target.Arguments) +
                     " (this proxy is pid " + Process.GetCurrentProcess().Id +
                     (RelayLauncher.IsCurrentProcessInJob() ? ", running inside a job object" : "") + ").");
            return RelayLauncher.LaunchDetached(target.FileName, target.Arguments, Path.GetDirectoryName(target.FileName));
        }

        // ── JSON-RPC <-> Streamable HTTP relay ──────────────────────────────────

        internal struct ForwardResult
        {
            public JObject Response;
            public string SessionId;
        }

        // Tool calls can legitimately run long (search, extraction); everything else - initialize,
        // tools/list, ping, discovery probes - is answered in milliseconds, so a client should not
        // sit for two minutes on one that has stalled (XS-1819 saw ~95-120s hangs).
        internal static TimeSpan RequestTimeout(JObject request)
        {
            var method = request == null ? null : request.Value<string>("method");
            return string.Equals(method, "tools/call", StringComparison.Ordinal)
                ? TimeSpan.FromSeconds(120)
                : TimeSpan.FromSeconds(30);
        }

        internal static async Task<ForwardResult> ForwardAsync(
            HttpClient http, string daemonUrl, JObject request, string sessionId, bool freshConnection = false)
        {
            var content = new StringContent(request.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, daemonUrl + "/graphql/mcp") { Content = content })
            using (var cts = new CancellationTokenSource(RequestTimeout(request)))
            {
                httpRequest.Headers.Accept.ParseAdd("application/json");
                httpRequest.Headers.Accept.ParseAdd("text/event-stream");
                if (sessionId != null)
                    httpRequest.Headers.TryAddWithoutValidation(McpSessionHeaderName, sessionId);

                // No "Expect: 100-continue" round trip (on by default for .NET Framework POSTs): the
                // body is a few hundred bytes to a loopback relay, and the extra interim response is
                // one more thing for a local proxy or security filter to mishandle.
                httpRequest.Headers.ExpectContinue = false;

                // The retry after a failure uses a brand-new connection, so a pooled keep-alive
                // connection that went bad can't fail the same request twice.
                if (freshConnection)
                    httpRequest.Headers.ConnectionClose = true;

                // Headers first, body second, so a failure can say how far the reply got.
                using (var resp = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    if (resp.Headers.TryGetValues(McpSessionHeaderName, out var sessionValues))
                    {
                        foreach (var v in sessionValues) { sessionId = v; break; }
                    }

                    // 202 Accepted with no body is the Streamable HTTP response for a
                    // notification (e.g. notifications/initialized) — nothing to relay back.
                    if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
                        return new ForwardResult { Response = null, SessionId = sessionId };

                    string body;
                    try
                    {
                        // ReadAsStringAsync takes no token on .NET Framework; disposing the response
                        // is what aborts a body that stalls past the request timeout.
                        using (cts.Token.Register(() => resp.Dispose()))
                            body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw new IOException(
                            "the relay sent HTTP " + (int)resp.StatusCode + " headers (Content-Length " +
                            (resp.Content.Headers.ContentLength.HasValue ? resp.Content.Headers.ContentLength.Value.ToString() : "unset") +
                            ") but the connection closed before the body arrived: " + Flatten(ex), ex);
                    }

                    if (string.IsNullOrEmpty(body))
                        return new ForwardResult { Response = null, SessionId = sessionId };

                    var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
                    var parsed = mediaType.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0
                        ? ParseSseLastMessage(body)
                        : JObject.Parse(body);
                    return new ForwardResult { Response = parsed, SessionId = sessionId };
                }
            }
        }

        private static string DescribeRequest(JObject request)
        {
            var method = request.Value<string>("method") ?? "(no method)";
            var id = request["id"];
            return method + (id == null ? "" : " id=" + id.ToString(Newtonsoft.Json.Formatting.None));
        }

        // HttpClient's own messages ("Error while copying content to a stream.") hide the useful
        // part in InnerException; the whole chain on one line is what a support log needs.
        internal static string Flatten(Exception ex)
        {
            var sb = new StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0)
                    sb.Append(" -> ");
                sb.Append(e is TaskCanceledException ? "timed out" : e.Message);
            }
            return sb.ToString();
        }

        // Streamable HTTP responses can arrive as SSE ("event: message\ndata: {...}\n\n",
        // possibly several events for one request). Our tools are all synchronous request/
        // response (no progress streaming), so relaying the LAST data event is sufficient and
        // simplest — nothing in this system currently emits intermediate progress notifications
        // on these calls.
        internal static JObject ParseSseLastMessage(string sseBody)
        {
            JObject last = null;
            foreach (var rawLine in sseBody.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var payload = line.Substring(5).Trim();
                if (payload.Length == 0)
                    continue;

                try { last = JObject.Parse(payload); }
                catch { /* ignore malformed/partial event fragments */ }
            }
            return last;
        }
    }
}
