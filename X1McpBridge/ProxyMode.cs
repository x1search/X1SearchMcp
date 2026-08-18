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

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) })
            {
                EnsureRelayRunningAsync(http, daemonUrl).GetAwaiter().GetResult();

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
                    try
                    {
                        var result = ForwardAsync(http, daemonUrl, request, sessionId).GetAwaiter().GetResult();
                        response = result.Response;
                        sessionId = result.SessionId;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Forwarding to the relay failed (" + ex.Message + "); relaunching and retrying once.");
                        try
                        {
                            EnsureRelayRunningAsync(http, daemonUrl, forceRelaunch: true).GetAwaiter().GetResult();
                            var result = ForwardAsync(http, daemonUrl, request, sessionId).GetAwaiter().GetResult();
                            response = result.Response;
                            sessionId = result.SessionId;
                        }
                        catch (Exception ex2)
                        {
                            Log.Error("Forwarding to the relay failed after relaunch", ex2);
                            response = isNotification
                                ? null
                                : McpProtocol.Err(request["id"], -32603,
                                    "The shared X1 MCP relay (" + BridgeConfig.GetRelayLaunchTarget().FileName +
                                    ") is unavailable.", ex2.Message);
                        }
                    }

                    if (response != null)
                    {
                        mcpOut.Write(response.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                        mcpOut.Flush();
                    }
                }
            }

            Log.Info("X1 MCP Bridge (proxy mode) shutting down (stdin closed)");
            return 0;
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

        private static async Task EnsureRelayRunningAsync(HttpClient http, string daemonUrl, bool forceRelaunch = false)
        {
            var target = BridgeConfig.GetRelayLaunchTarget();

            if (!forceRelaunch)
            {
                var snap = await CheckHealthAsync(http, daemonUrl).ConfigureAwait(false);
                var decision = RelayHealth.Decide(snap, target.Mode, OurRelayVersion.Value, Environment.UserName);

                switch (decision.Action)
                {
                    case RelayHealth.Action.Use:
                        Log.Debug("Using the relay already on " + daemonUrl + ": " + decision.Reason + ".");
                        return;

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

            LaunchRelayDetached(target);

            int startupTimeoutMs = BridgeConfig.GetDaemonStartupTimeoutMs();
            var deadline = DateTime.UtcNow.AddMilliseconds(startupTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (await IsHealthyAsync(http, daemonUrl).ConfigureAwait(false))
                {
                    Log.Info("Relay is up (" + target.ExpectedComponent + ").");
                    return;
                }
                await Task.Delay(300).ConfigureAwait(false);
            }

            Log.Warn("Relay did not become healthy within " + startupTimeoutMs + "ms; proceeding anyway " +
                      "(the first forwarded request will surface a clear connection error if it's still not up).");
        }

        // Identity-aware health check, used only for the initial "is there already a usable relay"
        // decision. The post-launch polling loop above uses the plain IsHealthyAsync: once this shim
        // has launched its own relay, of course it matches - re-checking identity there would just
        // be wasted round trips.
        private static async Task<RelayHealth.Snapshot> CheckHealthAsync(HttpClient http, string daemonUrl)
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
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                using (var resp = await http.GetAsync(daemonUrl + "/health", cts.Token).ConfigureAwait(false))
                    return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
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
        private static void LaunchRelayDetached(RelayLaunchTarget target)
        {
            if (!File.Exists(target.FileName))
            {
                Log.Error("Relay executable not found at '" + target.FileName + "'; cannot launch it. " +
                          "Configure daemonExePath in x1mcp.config.json if it lives elsewhere.");
                return;
            }

            try
            {
                Log.Info("Launching shared relay: " + target.FileName +
                         (string.IsNullOrEmpty(target.Arguments) ? "" : " " + target.Arguments));
                var psi = new ProcessStartInfo
                {
                    FileName = target.FileName,
                    Arguments = target.Arguments ?? "",
                    WorkingDirectory = Path.GetDirectoryName(target.FileName),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to launch the relay at '" + target.FileName + "'", ex);
            }
        }

        // ── JSON-RPC <-> Streamable HTTP relay ──────────────────────────────────

        private struct ForwardResult
        {
            public JObject Response;
            public string SessionId;
        }

        private static async Task<ForwardResult> ForwardAsync(HttpClient http, string daemonUrl, JObject request, string sessionId)
        {
            var content = new StringContent(request.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, daemonUrl + "/graphql/mcp") { Content = content })
            {
                httpRequest.Headers.Accept.ParseAdd("application/json");
                httpRequest.Headers.Accept.ParseAdd("text/event-stream");
                if (sessionId != null)
                    httpRequest.Headers.TryAddWithoutValidation(McpSessionHeaderName, sessionId);

                using (var resp = await http.SendAsync(httpRequest).ConfigureAwait(false))
                {
                    if (resp.Headers.TryGetValues(McpSessionHeaderName, out var sessionValues))
                    {
                        foreach (var v in sessionValues) { sessionId = v; break; }
                    }

                    // 202 Accepted with no body is the Streamable HTTP response for a
                    // notification (e.g. notifications/initialized) — nothing to relay back.
                    if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
                        return new ForwardResult { Response = null, SessionId = sessionId };

                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
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
