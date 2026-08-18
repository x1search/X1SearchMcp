// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace X1.McpBridge
{
    /// <summary>
    /// WORKAROUND for a server-side X1ServiceHost crash (reported to Jogy, X1 service team,
    /// July 2026): X1ServiceHost.exe crashes — no exception logged anywhere, no managed fault,
    /// the process simply dies and cold-restarts a few seconds later — when 2+ client
    /// connections to IX1Service/IX1SearchManager race on Connect()/session teardown. Every
    /// occurrence is preceded in X1ServiceHost.log by:
    ///   [WARN] X1Service - Connect received with client already connected. Disconnecting previous client.
    ///
    /// Confirmed reproducible via concurrent x1_search calls alone — unrelated to
    /// x1_get_schema_fields (a separate, already-fixed serialization bug in 11.0.3.36), and
    /// unrelated to background scanner activity (reproduced identically with the SharePoint
    /// scanner explicitly paused). Sequential calls into the same connections never reproduce
    /// the crash, no matter how many are issued back to back.
    ///
    /// Until this is fixed server-side, serialize every call this bridge makes into
    /// IX1Service/IX1SearchManager — both the synchronous tool-dispatch path (McpServer.CallTool)
    /// and any fire-and-forget background work that also touches those connections (e.g.
    /// SearchBridge.PrefetchPreviewAsync) — so the bridge itself can never issue overlapping
    /// calls, regardless of how the MCP client batches/parallelizes tool invocations.
    ///
    /// TO BACK OUT once the server-side fix is confirmed stable: delete this file, then search
    /// the solution for "XS1583ConcurrencyWorkaroundGate" and "X1ConcurrencyWorkaround" and
    /// remove every call site (they're each a thin wrapper, not load-bearing logic).
    /// </summary>
    internal static class X1ConcurrencyWorkaround
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        /// <summary>Serializes a synchronous, blocking unit of work (e.g. McpServer.CallTool's dispatch).</summary>
        public static T RunSerialized<T>(Func<T> action)
        {
            Gate.Wait();
            try { return action(); }
            finally { Gate.Release(); }
        }

        /// <summary>Serializes an async unit of work (e.g. fire-and-forget background prefetch).</summary>
        public static async Task RunSerializedAsync(Func<Task> action)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try { await action().ConfigureAwait(false); }
            finally { Gate.Release(); }
        }
    }
}
