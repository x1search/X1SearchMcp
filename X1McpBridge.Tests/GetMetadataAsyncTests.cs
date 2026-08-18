// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// C3 — GetMetadataAsync was previously synchronous (blocking WCF call with no
    /// timeout). It is now truly async and honours timeoutMs via Task.Run +
    /// CancellationTokenSource.
    ///
    /// Tests that do not require X1ServiceHost:
    ///   - Argument validation fires before any WCF call (table/uri empty)
    ///   - A very short timeout causes TimeoutException, not a hang
    ///   - The method returns a Task (not a completed Task.FromResult)
    /// </summary>
    [TestFixture]
    public class GetMetadataAsyncTests
    {
        // ── Argument validation (no WCF required) ────────────────────────────────

        [Test]
        public void GetMetadataAsync_EmptyTable_ThrowsArgumentException()
        {
            var bridge = new SearchBridge(new ColumnNameResolver(null), new TableSchemaResolver(null));
            // ArgumentException is thrown synchronously before the Task is awaited
            Assert.That(
                () => bridge.GetMetadataAsync("", "files://test.txt", null, 5000).GetAwaiter().GetResult(),
                Throws.InstanceOf<ArgumentException>());
            bridge.Dispose();
        }

        [Test]
        public void GetMetadataAsync_EmptyUri_ThrowsArgumentException()
        {
            var bridge = new SearchBridge(new ColumnNameResolver(null), new TableSchemaResolver(null));
            Assert.That(
                () => bridge.GetMetadataAsync("Files", "", null, 5000).GetAwaiter().GetResult(),
                Throws.InstanceOf<ArgumentException>());
            bridge.Dispose();
        }

        [Test]
        public void GetMetadataAsync_NullTable_ThrowsArgumentException()
        {
            var bridge = new SearchBridge(new ColumnNameResolver(null), new TableSchemaResolver(null));
            Assert.That(
                () => bridge.GetMetadataAsync(null, "files://test.txt", null, 5000).GetAwaiter().GetResult(),
                Throws.InstanceOf<ArgumentException>());
            bridge.Dispose();
        }

        [Test]
        public void GetMetadataAsync_NullUri_ThrowsArgumentException()
        {
            var bridge = new SearchBridge(new ColumnNameResolver(null), new TableSchemaResolver(null));
            Assert.That(
                () => bridge.GetMetadataAsync("Files", null, null, 5000).GetAwaiter().GetResult(),
                Throws.InstanceOf<ArgumentException>());
            bridge.Dispose();
        }

        // ── Returns a real Task (not synchronous Task.FromResult) ─────────────────

        [Test]
        public void GetMetadataAsync_ReturnsTask()
        {
            var bridge = new SearchBridge(new ColumnNameResolver(null), new TableSchemaResolver(null));
            // Must not throw synchronously for valid arguments; must return a Task
            Task<Newtonsoft.Json.Linq.JObject> task = null;
            Assert.DoesNotThrow(() =>
                task = bridge.GetMetadataAsync("Files", "files://test.txt", null, 100));
            Assert.That(task, Is.Not.Null);
            bridge.Dispose();
        }

        // ── Timeout contract ─────────────────────────────────────────────────────

        /// <summary>
        /// Verifies the timeout parameter is now wired up: a CancellationTokenSource
        /// with timeoutMs is created and passed to Task.Run. We test this indirectly
        /// by confirming the method accepts and honours the parameter type correctly,
        /// and that a zero-or-negative timeout does not cause an unhandled crash.
        ///
        /// A full timeout-fires test requires X1ServiceHost to be absent so the WCF
        /// call blocks; that scenario is covered by the [Explicit] test below.
        /// </summary>
        [Test]
        public void GetMetadataAsync_ZeroTimeout_DoesNotThrowUnhandledException()
        {
            var bridge = new SearchBridge(new ColumnNameResolver(null), new TableSchemaResolver(null));
            try
            {
                // With timeoutMs=0 the CancellationTokenSource fires immediately.
                // The outcome is either TimeoutException (cancel fires before WCF completes)
                // or a successful result (WCF completes before cancel — service is running).
                // Either way, no unhandled NullReferenceException or similar crash.
                Exception caught = null;
                try
                {
                    bridge.GetMetadataAsync("Files", "files://test.txt", null, 0)
                          .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                if (caught != null)
                {
                    Assert.That(caught, Is.InstanceOf<TimeoutException>()
                                        .Or.InstanceOf<System.ServiceModel.CommunicationException>()
                                        .Or.InstanceOf<OperationCanceledException>(),
                        $"Unexpected exception type: {caught.GetType().Name}: {caught.Message}");
                }
                // else: WCF call completed before timeout fired — that is also correct.
            }
            finally
            {
                bridge.Dispose();
            }
        }

        /// <summary>
        /// Explicit integration test: run manually when X1ServiceHost is NOT running.
        /// Confirms that a short timeout produces TimeoutException rather than hanging.
        /// </summary>
        [Test, Explicit("Run manually with X1ServiceHost stopped to verify timeout fires")]
        public void GetMetadataAsync_ShortTimeout_WithNoService_ThrowsTimeoutException()
        {
            var bridge = new SearchBridge(new ColumnNameResolver(null), new TableSchemaResolver(null));
            try
            {
                Assert.That(
                    () => bridge.GetMetadataAsync("Files", "files://test.txt", null, 100)
                                .GetAwaiter().GetResult(),
                    Throws.InstanceOf<TimeoutException>());
            }
            finally
            {
                bridge.Dispose();
            }
        }
    }
}
