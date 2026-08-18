// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// H2 — Verifies that SearchAsync.finally calls DestroySearchSession on the
    /// already-captured channel reference (ch) rather than re-acquiring it through
    /// the Channel property.
    ///
    /// Before the fix, the finally block used Channel.DestroySearchSession(), which
    /// re-acquired the lock and could create a brand-new X1MCPSearchConnection after
    /// a fault had torn down the original one. This means:
    ///   1. A superfluous channel-creation round-trip on every search teardown.
    ///   2. On a faulted connection, a new connection might be opened just to send
    ///      a teardown message to a session that no longer exists.
    ///
    /// Because SearchBridge has no seam for injecting a mock channel we test the
    /// fix indirectly:
    ///   - Dispose() after a failed search must not throw.
    ///   - Multiple SearchAsync calls on the same bridge do not compound teardown
    ///     errors into unhandled exceptions.
    ///   - The bridge remains usable (no ObjectDisposedException) after a search
    ///     that threw because the service was unavailable or timed out.
    /// </summary>
    [TestFixture]
    public class SearchBridgeCleanupTests
    {
        /// <summary>
        /// A SearchAsync call that fails (no service / timeout) must not leave the
        /// bridge in an unusable state or cause Dispose() to throw.
        /// </summary>
        [Test]
        public void SearchAsync_AfterFailure_DisposeDoesNotThrow()
        {
            var bridge = new SearchBridge(new ColumnNameResolver(null), new TableSchemaResolver(null));
            try
            {
                // Fire-and-forget with a 1 ms timeout — the WCF call will fail or time out.
                // We only care that the bridge cleans up without unhandled exceptions.
                try
                {
                    bridge.SearchAsync("budget", null, false, 5, false, false, null, null, null, 1)
                          .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    // Expected: TimeoutException, CommunicationException, ArgumentException.
                    // Any of these is acceptable — what matters is no NullReferenceException
                    // or ObjectDisposedException escaping from the finally cleanup path.
                    Assert.That(ex,
                        Is.InstanceOf<TimeoutException>()
                          .Or.InstanceOf<System.ServiceModel.CommunicationException>()
                          .Or.InstanceOf<ArgumentException>()
                          .Or.InstanceOf<InvalidOperationException>(),
                        $"Unexpected exception type from SearchAsync: {ex.GetType().Name}: {ex.Message}");
                }
            }
            finally
            {
                Assert.DoesNotThrow(() => bridge.Dispose(),
                    "Dispose() must not throw after a failed search");
            }
        }

        /// <summary>
        /// Verifies that calling SearchAsync twice on the same bridge does not
        /// compound teardown errors. Before H2, the second finally block would
        /// call Channel (property) and could create a new connection; after the fix
        /// it simply calls ch.DestroySearchSession on the already-captured reference.
        /// </summary>
        [Test]
        public void SearchAsync_CalledTwice_DoesNotThrowOnSecondDispose()
        {
            var bridge = new SearchBridge(new ColumnNameResolver(null), new TableSchemaResolver(null));
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    try
                    {
                        bridge.SearchAsync("invoice", null, false, 5, false, false, null, null, null, 1)
                              .GetAwaiter().GetResult();
                    }
                    catch (Exception ex) when (
                        ex is TimeoutException ||
                        ex is System.ServiceModel.CommunicationException ||
                        ex is ArgumentException ||
                        ex is InvalidOperationException)
                    {
                        // Expected on each iteration — continue to the next.
                    }
                }
            }
            finally
            {
                Assert.DoesNotThrow(() => bridge.Dispose());
            }
        }

        /// <summary>
        /// GetMetadataAsync after a SearchAsync teardown must not throw
        /// ObjectDisposedException — the channel should be reusable.
        /// </summary>
        [Test]
        public void GetMetadataAsync_AfterSearchFailure_DoesNotThrowObjectDisposed()
        {
            var bridge = new SearchBridge(new ColumnNameResolver(null), new TableSchemaResolver(null));
            try
            {
                // Let a search fail/timeout to exercise the finally path.
                try
                {
                    bridge.SearchAsync("test", null, false, 5, false, false, null, null, null, 1)
                          .GetAwaiter().GetResult();
                }
                catch { /* expected */ }

                // Now attempt metadata — must not throw ObjectDisposedException.
                Exception caught = null;
                try
                {
                    bridge.GetMetadataAsync("Files", "files://test.txt", null, 1)
                          .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                if (caught != null)
                    Assert.That(caught, Is.Not.InstanceOf<ObjectDisposedException>(),
                        "Bridge must remain usable after a search teardown");
            }
            finally
            {
                bridge.Dispose();
            }
        }
    }
}
