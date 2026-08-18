// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System.Threading;

namespace X1.McpBridge
{
    /// <summary>
    /// Guards against two shared relays running at once for the same Windows user. Port-for-port
    /// counterpart of X1McpGraphQL's SingleInstanceGuard, and deliberately using THE SAME mutex
    /// name - see below.
    ///
    /// This matters because the whole point of the relay is to be the ONE thing that ever connects
    /// to X1ServiceHost's dedicated MCP endpoint: X1ServiceHost.exe is confirmed to crash and
    /// silently cold-restart when 2+ client connections race on Connect()/session teardown (see
    /// X1ConcurrencyWorkaround.cs). Two shims can both see "no relay running" and try to launch one
    /// at nearly the same time; this makes the loser exit immediately instead of racing a second
    /// WCF connection into existence.
    ///
    /// WHY THE NAME IS X1McpGraphQL-SingleInstance AND MUST NOT BE "TIDIED UP":
    /// the name is shared with the net10 daemon on purpose, so that a Lean host and a Full daemon
    /// are mutually exclusive rather than merely each-unique. If this used a host-specific name,
    /// both could pass their own guard simultaneously and open two WCF connections - exactly the
    /// crash above. The name reads wrong in a Lean install because the daemon isn't there; it is
    /// still the correct name, because its job is cross-flavor exclusion, not self-description.
    ///
    /// Note this is a second line of defence, not the primary one: both flavors bind the same relay
    /// port, and HTTP.SYS and Kestrel do arbitrate against each other in both directions (verified),
    /// so the port bind alone already prevents two relays. The mutex closes the window between
    /// "decided to launch" and "bound the port", and gives a clean, quiet loser.
    ///
    /// Deliberately session-scoped (no "Global\" prefix) rather than machine-wide: X1ServiceHost's
    /// WCF endpoints are per-Windows-user, so "one relay per user session" is the correct unit of
    /// singleness. The cross-user case a machine-wide mutex would otherwise cover is handled where
    /// it belongs, in RelayHealth.Decide's RefuseWrongUser rule - killing another user's relay was
    /// never an acceptable resolution anyway.
    /// </summary>
    internal static class HostSingleInstanceGuard
    {
        internal const string MutexName = "X1McpGraphQL-SingleInstance";

        /// <summary>
        /// Tries to become the one running relay. Returns null if another instance already holds
        /// the guard - the caller should log and exit immediately, and must not call
        /// McpServer.StartBackend(), since opening the WCF connection is the very thing being
        /// serialized here.
        ///
        /// The caller must keep the returned Mutex referenced for the process's entire lifetime;
        /// disposing it early releases the guard while still running. The named object dies with
        /// its last handle, so a relay killed with Stop-Process leaves the name immediately
        /// reusable rather than wedging every future launch.
        /// </summary>
        /// <param name="mutexName">Overridable only for tests, so they don't collide with a real
        /// running relay on the same machine/user session.</param>
        public static Mutex TryAcquire(string mutexName = MutexName)
        {
            bool createdNew;
            var mutex = new Mutex(true, mutexName, out createdNew);
            if (createdNew)
                return mutex;

            mutex.Dispose();
            return null;
        }
    }
}
