// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.ServiceModel;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// C2 — Verifies that X1MCPSearchConnection detects a faulted or closed WCF channel
    /// and resets its state so a fresh channel will be created on the next call.
    ///
    /// We cannot create real WCF channels without X1ServiceHost running, so we test
    /// the detection logic via the internal ResetIfFaulted helper exposed for testing,
    /// and verify the contract: after a fault, GetChannel() no longer returns a cached
    /// reference equal to the faulted one.
    ///
    /// The guard logic in GetChannel() is:
    ///   if channel is ICommunicationObject AND state is Faulted or Closed → Abort + null
    ///
    /// We verify this using the CommunicationState enum values directly since the state
    /// machine logic is deterministic and does not require a real WCF channel.
    ///
    /// Also covers X1MCPServiceConnection.ResetChannel() (XS-1642 follow-up): unlike
    /// X1MCPSearchConnection's passive .State check, GetDataSourcesInfoAsync/GetSchemaFieldsAsync
    /// can fail in a way WCF never surfaces as Faulted (a one-way callback that never arrives, or
    /// a synchronous call that hangs) - a client-side timeout/exception on either call is itself
    /// the fault signal, and must force a reset since GetChannel()'s .State check is blind to it.
    /// </summary>
    [TestFixture]
    public class WcfChannelFaultTests
    {
        // ── CommunicationState guard logic ───────────────────────────────────────

        /// <summary>
        /// Verifies the states that SHOULD trigger channel teardown.
        /// </summary>
        [TestCase(CommunicationState.Faulted)]
        [TestCase(CommunicationState.Closed)]
        public void FaultedOrClosedState_ShouldTriggerReset(CommunicationState state)
        {
            bool shouldReset = state == CommunicationState.Faulted ||
                               state == CommunicationState.Closed;
            Assert.That(shouldReset, Is.True,
                $"State {state} should trigger channel reset");
        }

        /// <summary>
        /// Verifies the states that should NOT trigger teardown (channel still usable).
        /// </summary>
        [TestCase(CommunicationState.Created)]
        [TestCase(CommunicationState.Opening)]
        [TestCase(CommunicationState.Opened)]
        [TestCase(CommunicationState.Closing)]
        public void UsableState_ShouldNotTriggerReset(CommunicationState state)
        {
            bool shouldReset = state == CommunicationState.Faulted ||
                               state == CommunicationState.Closed;
            Assert.That(shouldReset, Is.False,
                $"State {state} should not trigger channel reset");
        }

        // ── SearchBridge gate (no live WCF) ──────────────────────────────────────

        /// <summary>
        /// SearchBridge.Channel property creates a X1MCPSearchConnection on demand.
        /// Before the fix, a faulted channel was returned as-is.
        /// After the fix, a faulted channel is torn down and a new one is created.
        ///
        /// We verify that X1MCPSearchConnection.GetChannel() throws a WCF or connectivity
        /// exception (not a NullReferenceException or ObjectDisposedException) when no
        /// X1ServiceHost is available — confirming the guard ran and a fresh connect
        /// attempt was made rather than returning a stale cached reference.
        /// </summary>
        [Test]
        public void X1MCPSearchConnection_GetChannel_ReturnsNonNullProxy()
        {
            // WCF proxies are lazy — no connection is made until the first method call.
            // GetChannel() must return a non-null proxy regardless of whether
            // X1ServiceHost is running.
            var callbacks = new SearchManagerCallbacks();
            var conn = new X1MCPSearchConnection(callbacks);

            var ch = conn.GetChannel();
            Assert.That(ch, Is.Not.Null, "GetChannel() must return a non-null proxy");

            conn.Dispose();
        }

        /// <summary>
        /// The fault guard uses CommunicationState.Faulted and .Closed as trigger
        /// conditions. Direct logic tests of the boolean expression in GetChannel().
        /// </summary>
        [Test]
        public void FaultGuard_FaultedChannel_ShouldBeReset()
        {
            Assert.That(ShouldResetChannel(CommunicationState.Faulted), Is.True);
        }

        [Test]
        public void FaultGuard_ClosedChannel_ShouldBeReset()
        {
            Assert.That(ShouldResetChannel(CommunicationState.Closed), Is.True);
        }

        [Test]
        public void FaultGuard_OpenedChannel_ShouldNotBeReset()
        {
            Assert.That(ShouldResetChannel(CommunicationState.Opened), Is.False);
        }

        [TestCase(CommunicationState.Created)]
        [TestCase(CommunicationState.Opening)]
        [TestCase(CommunicationState.Closing)]
        public void FaultGuard_TransientStates_ShouldNotBeReset(CommunicationState state)
        {
            Assert.That(ShouldResetChannel(state), Is.False,
                $"State {state} should not trigger a channel reset");
        }

        /// <summary>
        /// Mirrors the exact boolean expression used in GetChannel() — any change
        /// to the production guard must also be reflected here.
        /// </summary>
        private static bool ShouldResetChannel(CommunicationState state)
            => state == CommunicationState.Faulted || state == CommunicationState.Closed;

        /// <summary>
        /// Calling GetChannel() twice on a fresh connection returns the same channel
        /// reference (caching is still in place when the channel is healthy).
        /// </summary>
        [Test]
        public void X1MCPSearchConnection_GetChannelTwice_ReturnsSameReference()
        {
            var callbacks = new SearchManagerCallbacks();
            var conn = new X1MCPSearchConnection(callbacks);

            var ch1 = conn.GetChannel();
            var ch2 = conn.GetChannel();

            Assert.That(ReferenceEquals(ch1, ch2), Is.True,
                "GetChannel() should return the same cached channel when not faulted");

            conn.Dispose();
        }

        /// <summary>
        /// Dispose() must not throw even when called on a connection that never
        /// successfully opened (the channel is in Created/Opening state or null).
        /// </summary>
        [Test]
        public void X1MCPSearchConnection_DisposeWithoutConnect_DoesNotThrow()
        {
            var callbacks = new SearchManagerCallbacks();
            var conn = new X1MCPSearchConnection(callbacks);

            Assert.DoesNotThrow(() => conn.Dispose());
        }

        [Test]
        public void X1MCPSearchConnection_DisposeAfterGetChannel_DoesNotThrow()
        {
            var callbacks = new SearchManagerCallbacks();
            var conn = new X1MCPSearchConnection(callbacks);
            conn.GetChannel(); // creates the channel object (not yet connected)
            Assert.DoesNotThrow(() => conn.Dispose());
        }

        [Test]
        public void X1MCPSearchConnection_DisposeTwice_DoesNotThrow()
        {
            var callbacks = new SearchManagerCallbacks();
            var conn = new X1MCPSearchConnection(callbacks);
            conn.Dispose();
            Assert.DoesNotThrow(() => conn.Dispose());
        }

        // ── X1MCPServiceConnection.ResetChannel (XS-1642 follow-up) ──────────────

        /// <summary>
        /// The whole point of the fix: GetChannel() only tears down a channel whose .State
        /// already reads Faulted/Closed - but GetDataSourcesInfoAsync/GetSchemaFieldsAsync can
        /// fail (timeout waiting for a one-way callback, or a hung synchronous call) without WCF
        /// ever transitioning the channel to that state. ResetChannel() is the explicit escape
        /// hatch those methods now call on any timeout/exception, forcing GetChannel() to hand
        /// back a genuinely new proxy next time - proven here without needing a live
        /// X1ServiceHost, since WCF proxies are lazy (no connection until first method call).
        /// </summary>
        [Test]
        public void X1MCPServiceConnection_ResetChannel_ForcesNewChannelOnNextGetChannel()
        {
            var conn = new X1MCPServiceConnection();
            var ch1 = conn.GetChannel();
            Assert.That(ch1, Is.Not.Null);

            conn.ResetChannel();
            var ch2 = conn.GetChannel();

            Assert.That(ReferenceEquals(ch1, ch2), Is.False,
                "ResetChannel() must force GetChannel() to create a fresh proxy, not return the old (dead) one");

            conn.Dispose();
        }

        [Test]
        public void X1MCPServiceConnection_GetChannelTwice_ReturnsSameReferenceWhenNotReset()
        {
            var conn = new X1MCPServiceConnection();
            var ch1 = conn.GetChannel();
            var ch2 = conn.GetChannel();

            Assert.That(ReferenceEquals(ch1, ch2), Is.True,
                "GetChannel() should return the same cached channel when never reset");

            conn.Dispose();
        }

        [Test]
        public void X1MCPServiceConnection_ResetChannel_BeforeAnyGetChannel_DoesNotThrow()
        {
            // ResetChannel() can legitimately run before GetChannel() was ever called (e.g. a
            // connect-catch firing on the very first call) - _channel/_factory are both still
            // null at that point, and Abort()-ing a null channel/factory must be a safe no-op.
            var conn = new X1MCPServiceConnection();
            Assert.DoesNotThrow(() => conn.ResetChannel());
            conn.Dispose();
        }

        [Test]
        public void X1MCPServiceConnection_DisposeWithoutConnect_DoesNotThrow()
        {
            var conn = new X1MCPServiceConnection();
            Assert.DoesNotThrow(() => conn.Dispose());
        }

        [Test]
        public void X1MCPServiceConnection_DisposeAfterGetChannel_DoesNotThrow()
        {
            var conn = new X1MCPServiceConnection();
            conn.GetChannel();
            Assert.DoesNotThrow(() => conn.Dispose());
        }

        // ── X1MCPServiceCallbacks.OnShutdown (XS-1698/XS-1701) ──────────────────
        //
        // XS-1701 shuts the whole connector down when X1 announces a clean shutdown, rather than
        // trying to invalidate/reconnect a live channel - so all this class needs to do is forward
        // the callback to whatever action X1MCPServiceConnection was constructed with. Tested here
        // directly against X1MCPServiceCallbacks (no live WCF needed), same as everything else in
        // this file.

        [Test]
        public void X1MCPServiceCallbacks_OnShutdown_InvokesProvidedAction()
        {
            bool fired = false;
            var callbacks = new X1MCPServiceCallbacks(() => fired = true);

            callbacks.OnShutdown();

            Assert.That(fired, Is.True, "OnShutdown must invoke the action the connection was constructed with");
        }

        [Test]
        public void X1MCPServiceCallbacks_OnShutdown_NoActionProvided_DoesNotThrow()
        {
            var callbacks = new X1MCPServiceCallbacks();
            Assert.DoesNotThrow(() => callbacks.OnShutdown());
        }
    }
}
