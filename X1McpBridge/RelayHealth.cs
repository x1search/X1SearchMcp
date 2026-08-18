// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using Newtonsoft.Json.Linq;

namespace X1.McpBridge
{
    /// <summary>
    /// The shared <c>GET /health</c> contract spoken by both relay flavors, plus the pure decision
    /// logic ProxyMode uses to reconcile "what is answering on the relay port" against "what this
    /// install expects to be answering".
    ///
    /// Kept transport-free on purpose: <see cref="Decide"/> takes a parsed snapshot and returns an
    /// action, with no HttpClient, no Process, and no clock. That is what makes the whole
    /// coexistence matrix (stale daemon, cross-flavor, cross-user, version skew) unit-testable
    /// instead of only reproducible by hand on a machine that happens to be in the right state.
    ///
    /// Why this exists at all: the relay listens on one fixed port regardless of which install
    /// started it, so a perfectly healthy 200 OK does NOT mean the right thing is answering. It can
    /// be a leftover from another install, a different flavor, a different version, or another
    /// Windows user's session. Before this file, the only discriminator was a version string, and
    /// its comparison had a hole that made every Lean install adopt whatever it found (see
    /// ProxyMode.OurRelayVersion).
    /// </summary>
    internal static class RelayHealth
    {
        /// <summary>Full flavor: the net10 daemon (X1McpGraphQL.exe).</summary>
        public const string ComponentDaemon = "X1McpGraphQL";

        /// <summary>Lean flavor: X1McpBridge.exe --host.</summary>
        public const string ComponentHost = "X1McpBridge";

        /// <summary>
        /// A parsed /health body. <see cref="Reachable"/> false means nothing answered (or answered
        /// non-2xx); every other field is best-effort, because a relay predating the widened body
        /// reports only <c>version</c>.
        /// </summary>
        public struct Snapshot
        {
            public bool Reachable;
            public string Version;    // null = not reported
            public string Component;  // null = not reported (pre-flavor build - see Decide)
            public int Pid;           // 0 = not reported
            public string User;       // null = not reported
            public string ExePath;    // null = not reported

            public static Snapshot Unreachable()
            {
                return default(Snapshot);
            }
        }

        /// <summary>
        /// Parses a /health response body. Never throws: a body we can't read is treated as
        /// "healthy but unidentified" rather than unhealthy, so this can't newly break against a
        /// relay that answers fine but predates any of these fields.
        /// </summary>
        public static Snapshot Parse(string body)
        {
            var snap = new Snapshot { Reachable = true };
            if (string.IsNullOrWhiteSpace(body))
                return snap;

            try
            {
                var jo = JObject.Parse(body);
                snap.Version = NullIfBlank(jo.Value<string>("version"));
                snap.Component = NullIfBlank(jo.Value<string>("component"));
                snap.User = NullIfBlank(jo.Value<string>("user"));
                snap.ExePath = NullIfBlank(jo.Value<string>("exePath"));
                var pid = jo.Value<int?>("pid");
                snap.Pid = pid.HasValue && pid.Value > 0 ? pid.Value : 0;
            }
            catch
            {
                // Malformed/partial body - keep Reachable=true and leave the rest unknown.
            }
            return snap;
        }

        private static string NullIfBlank(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        public enum Action
        {
            /// <summary>Adopt what's already running; don't launch anything.</summary>
            Use,

            /// <summary>Nothing usable is there - launch our own relay.</summary>
            Launch,

            /// <summary>The wrong relay owns the port - stop it, then launch ours.</summary>
            EvictThenLaunch,

            /// <summary>Another Windows user's relay owns the port. Do not touch it, and do not proceed.</summary>
            RefuseWrongUser,
        }

        public struct Decision
        {
            public Action Action;

            /// <summary>Human-readable justification, logged verbatim. Always set.</summary>
            public string Reason;

            /// <summary>
            /// For <see cref="Action.EvictThenLaunch"/>: the pid to stop, or 0 if the running relay
            /// didn't report one and eviction has to fall back to killing by process name.
            /// </summary>
            public int EvictPid;

            /// <summary>
            /// For <see cref="Action.EvictThenLaunch"/> with no pid: which process name to kill.
            /// Only ever <see cref="ComponentDaemon"/> - see ProxyMode.EvictRelay for why killing
            /// X1McpBridge by bare name is never safe.
            /// </summary>
            public string EvictComponent;
        }

        /// <summary>
        /// Decides what to do about the relay currently answering (or not) on the shared port.
        ///
        /// Order matters, and version outranks flavor deliberately:
        ///
        /// 1. Nothing answering -> Launch.
        /// 2. A different Windows user's relay -> RefuseWrongUser. Never version-compare and never
        ///    kill: per-user WCF endpoints mean that relay is wired to a different index, so
        ///    silently using it would answer questions from the wrong person's data. This closes a
        ///    hole that predates the flavor split (the single-instance mutex is session-scoped but
        ///    the port is machine-wide).
        /// 3. An older relay -> EvictThenLaunch, whatever its flavor. This is the case that matters
        ///    most on a Full -> Lean upgrade: the leftover daemon is what a logon task keeps
        ///    restarting, and adopting it would mean the customer keeps running the net10 build they
        ///    just uninstalled, driving the OLD bridge from the OLD directory.
        /// 4. A newer relay -> Use. Adopting forward is safe and, unlike mutual eviction, converges.
        /// 5. Equal versions, same flavor -> Use (the normal path).
        /// 6. Equal versions, cross-flavor -> the daemon wins. It serves a strict superset of the
        ///    host's contract, so a Lean proxy on a same-version daemon is functionally correct and
        ///    converges immediately. The reverse is not true: a Full proxy adopting a Lean host
        ///    would silently lose the GraphQL/Nitro surface it was installed for, so it evicts.
        /// 7. Indeterminate version, cross-flavor -> evict rather than guess. Version leniency is
        ///    only extended within one flavor, where it preserves the old behaviour against a relay
        ///    too old to report a version at all.
        /// </summary>
        public static Decision Decide(Snapshot snap, RelayMode expected, string ourVersion, string ourUser)
        {
            string expectedComponent = expected == RelayMode.Daemon ? ComponentDaemon : ComponentHost;

            if (!snap.Reachable)
                return Act(Action.Launch, "nothing is answering on the relay port");

            if (snap.User != null && ourUser != null &&
                !string.Equals(snap.User, ourUser, StringComparison.OrdinalIgnoreCase))
            {
                return Act(Action.RefuseWrongUser,
                    "the relay on this port belongs to Windows user '" + snap.User + "', not '" + ourUser +
                    "'; it is connected to that user's index, so it will not be used or stopped");
            }

            // A relay that reports no component can only be a pre-flavor daemon: --host did not
            // exist before this field did.
            string runningComponent = snap.Component ?? ComponentDaemon;
            bool sameFlavor = string.Equals(runningComponent, expectedComponent, StringComparison.OrdinalIgnoreCase);

            int cmp;
            if (TryCompareVersions(snap.Version, ourVersion, out cmp))
            {
                if (cmp < 0)
                {
                    return Evict(snap, runningComponent,
                        "the running " + runningComponent + " relay is version " + snap.Version +
                        ", older than this install's " + ourVersion);
                }
                if (cmp > 0)
                {
                    return Act(Action.Use,
                        "the running " + runningComponent + " relay is version " + snap.Version +
                        ", newer than this install's " + ourVersion + "; adopting it rather than downgrading");
                }
            }
            else if (!sameFlavor)
            {
                return Evict(snap, runningComponent,
                    "a " + runningComponent + " relay owns the port but this install expects " +
                    expectedComponent + ", and neither version could be compared");
            }

            if (sameFlavor)
                return Act(Action.Use, "the running relay is " + runningComponent + " at the expected version");

            if (expected == RelayMode.Host)
            {
                return Act(Action.Use,
                    "a same-version " + ComponentDaemon + " daemon owns the port; it serves a superset of " +
                    "the in-bridge host's contract, so it is being used instead of starting a second relay");
            }

            return Evict(snap, runningComponent,
                "a " + runningComponent + " relay owns the port but this install needs the " +
                ComponentDaemon + " daemon (its GraphQL surface is not served by the in-bridge host)");
        }

        private static Decision Act(Action action, string reason)
        {
            return new Decision { Action = action, Reason = reason };
        }

        private static Decision Evict(Snapshot snap, string runningComponent, string reason)
        {
            return new Decision
            {
                Action = Action.EvictThenLaunch,
                Reason = reason,
                EvictPid = snap.Pid,
                EvictComponent = runningComponent,
            };
        }

        /// <summary>
        /// Compares two four-part version strings. Returns false when either is missing or
        /// unparseable, which callers must treat as "don't know" rather than "equal" - conflating
        /// those two is precisely the defect this replaces.
        /// </summary>
        internal static bool TryCompareVersions(string running, string ours, out int result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(running) || string.IsNullOrWhiteSpace(ours))
                return false;

            Version a, b;
            if (!Version.TryParse(running.Trim(), out a) || !Version.TryParse(ours.Trim(), out b))
            {
                // Fall back to an exact string match so a non-dotted-quad build string still
                // compares equal to itself instead of being reported as unknown.
                result = 0;
                return string.Equals(running.Trim(), ours.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            result = a.CompareTo(b);
            return true;
        }

        /// <summary>
        /// Renders the /health body. Shared so the Lean host and the Full daemon cannot drift in
        /// field names - the daemon's own writer mirrors this shape (see X1McpGraphQL/Program.cs).
        /// </summary>
        public static string BuildBody(string version, string component, int pid, string user, string exePath)
        {
            var jo = new JObject
            {
                ["version"] = version ?? "",
                ["component"] = component ?? "",
                ["mode"] = component == ComponentHost ? "host" : "daemon",
                ["pid"] = pid,
                ["user"] = user ?? "",
                ["exePath"] = exePath ?? "",
            };
            return jo.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
