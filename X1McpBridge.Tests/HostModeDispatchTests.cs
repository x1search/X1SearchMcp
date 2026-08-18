// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// Pins HostMode's serialization guarantee — the single most important correctness property of
    /// the Lean relay.
    ///
    /// Why it needs its own tests: it is easy to believe this is already handled and it is not.
    /// X1ConcurrencyWorkaround's gate wraps only CallTool, while resources/read -> ReadResource ->
    /// ConnectAndGetHostStatus() reaches WCF outside it. What actually guarantees no overlapping
    /// calls in stdio mode is RunStdio's single-threaded read loop. An HttpListener has no such
    /// loop, so HostMode has to reproduce it — and if it ever stops doing so, the symptom is not a
    /// failing test but X1ServiceHost.exe crashing and cold-restarting under concurrent tool calls,
    /// with nothing logged anywhere (see X1ConcurrencyWorkaround.cs).
    /// </summary>
    [TestFixture]
    public class HostModeDispatchTests
    {
        [Test]
        public void ConcurrentRequests_AreNeverDispatchedInParallel()
        {
            const int requests = 50;
            int concurrent = 0;
            int maxConcurrent = 0;
            var sync = new object();

            Func<JObject, JObject> handler = req =>
            {
                int now = Interlocked.Increment(ref concurrent);
                lock (sync)
                {
                    if (now > maxConcurrent)
                        maxConcurrent = now;
                }
                Thread.Sleep(2);   // widen the window a genuine overlap would land in
                Interlocked.Decrement(ref concurrent);
                return McpProtocol.Ok(req["id"], new JObject());
            };

            var tasks = new List<Task<JObject>>();
            for (int i = 0; i < requests; i++)
            {
                var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = i, ["method"] = "ping" };
                tasks.Add(HostMode.DispatchForTest(request, handler));
            }

            Assert.That(Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(30)), Is.True,
                "dispatch stalled — a single consumer must still drain the whole queue");
            Assert.That(maxConcurrent, Is.EqualTo(1),
                "two requests reached the handler at once; WCF access is no longer serialized");
        }

        [Test]
        public void Dispatch_DoesNotUseTheNonReentrantConcurrencyGate()
        {
            // The obvious "simplification" of this design is to wrap dispatch in
            // X1ConcurrencyWorkaround.RunSerialized. That gate is a non-reentrant SemaphoreSlim(1,1),
            // so it deadlocks the instant CallTool re-enters it — and a deadlock here wedges every
            // future call in the session, since a detached Lean relay has no supervisor to restart.
            // This test reproduces exactly that re-entry: if the outer gate is ever reintroduced,
            // it times out instead of shipping.
            var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call" };

            var task = HostMode.DispatchForTest(
                request,
                req => X1ConcurrencyWorkaround.RunSerialized(() => McpProtocol.Ok(req["id"], new JObject())));

            Assert.That(task.Wait(TimeSpan.FromSeconds(10)), Is.True,
                "dispatch deadlocked — the outer gate must not be X1ConcurrencyWorkaround.RunSerialized");
        }

        [Test]
        public void HandlerThrowing_ReturnsAJsonRpcErrorAndKeepsTheThreadAlive()
        {
            // One bad request must never kill the dispatcher: there is no supervising daemon in the
            // Lean flavor, so losing this thread would silently wedge the session.
            var failing = HostMode.DispatchForTest(
                new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "boom" },
                req => { throw new InvalidOperationException("deliberate"); });

            Assert.That(failing.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(failing.Result["error"], Is.Not.Null);
            Assert.That(failing.Result["error"].Value<int>("code"), Is.EqualTo(-32603));

            var after = HostMode.DispatchForTest(
                new JObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "ping" },
                req => McpProtocol.Ok(req["id"], new JObject { ["ok"] = true }));

            Assert.That(after.Wait(TimeSpan.FromSeconds(10)), Is.True, "the dispatch thread died with the request");
            Assert.That(after.Result["result"], Is.Not.Null);
        }

        [Test]
        public void Notification_DispatchesToNull()
        {
            // ProcessMessage returns null for notifications, and HostMode turns that into 202.
            var task = HostMode.DispatchForTest(
                new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
                req => McpProtocol.IsNotification(req) ? null : new JObject());

            Assert.That(task.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(task.Result, Is.Null);
        }

        [Test]
        public void RequestsAreDispatchedInOrder()
        {
            // Not merely serialized but FIFO, matching stdio's read-loop semantics.
            var seen = new List<int>();
            Func<JObject, JObject> handler = req =>
            {
                lock (seen) { seen.Add(req.Value<int>("id")); }
                return McpProtocol.Ok(req["id"], new JObject());
            };

            var tasks = new List<Task<JObject>>();
            for (int i = 0; i < 20; i++)
            {
                var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = i, ["method"] = "ping" };
                tasks.Add(HostMode.DispatchForTest(request, handler));
                // Enqueue sequentially so ordering is well-defined; concurrency is covered above.
                tasks[tasks.Count - 1].Wait(TimeSpan.FromSeconds(5));
            }

            Assert.That(Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(30)), Is.True);
            var expected = new List<int>();
            for (int i = 0; i < 20; i++) expected.Add(i);
            Assert.That(seen, Is.EqualTo(expected));
        }
    }

    /// <summary>
    /// Covers the single-instance guard shared with the net10 daemon.
    /// </summary>
    [TestFixture]
    public class HostSingleInstanceGuardTests
    {
        [Test]
        public void TheMutexNameIsSharedWithTheDaemon()
        {
            // Load-bearing and counter-intuitive enough to pin: the name is the DAEMON's on purpose,
            // so a Lean host and a Full daemon are mutually exclusive rather than merely each-unique.
            // A host-specific name would let both pass their own guard and open two WCF connections —
            // the X1ServiceHost crash this architecture exists to prevent. It must not be "tidied up"
            // to match the component that now uses it.
            Assert.That(HostSingleInstanceGuard.MutexName, Is.EqualTo("X1McpGraphQL-SingleInstance"));
        }

        [Test]
        public void SecondAcquire_ReturnsNull()
        {
            var name = "X1McpHostGuardTest-" + Guid.NewGuid().ToString("N");
            var first = HostSingleInstanceGuard.TryAcquire(name);
            try
            {
                Assert.That(first, Is.Not.Null);
                Assert.That(HostSingleInstanceGuard.TryAcquire(name), Is.Null);
            }
            finally
            {
                if (first != null) { try { first.ReleaseMutex(); } catch { } first.Dispose(); }
            }
        }

        [Test]
        public void AfterRelease_CanBeReacquired()
        {
            // The named object dies with its last handle, so a relay killed with Stop-Process must
            // leave the name immediately reusable rather than wedging every future launch.
            var name = "X1McpHostGuardTest-" + Guid.NewGuid().ToString("N");

            var first = HostSingleInstanceGuard.TryAcquire(name);
            Assert.That(first, Is.Not.Null);
            first.ReleaseMutex();
            first.Dispose();

            var second = HostSingleInstanceGuard.TryAcquire(name);
            try { Assert.That(second, Is.Not.Null); }
            finally
            {
                if (second != null) { try { second.ReleaseMutex(); } catch { } second.Dispose(); }
            }
        }
    }
}
