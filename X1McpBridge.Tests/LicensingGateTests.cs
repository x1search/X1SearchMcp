// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// XS-1671 — Verifies X1MCPServiceConnection's detection of the "Unlicensed" sentinel that
    /// X1MCPService.Connect() returns (instead of a version string) when
    /// LicenseManager.Instance.IsPaidPluginEnabled(Plugin.MCP) is false.
    ///
    /// X1MCPServiceConnection always creates a real DuplexChannelFactory&lt;IX1MCPService&gt;, with
    /// no seam to inject a fake channel (same constraint noted in WcfChannelFaultTests), and dev
    /// machines running an actual X1ServiceHost make "no live host" an unsafe assumption for a
    /// portable test (this suite's own run against a real, briefly-unlicensed X1 Search 12.0.0.6
    /// install is what first surfaced that: EnsureConnectedChannel() correctly threw
    /// X1McpUnlicensedException instead of quietly connecting). So instead of assuming a
    /// particular environment, EnsureConnectedChannel_ResultInvariant_HoldsRegardlessOfHostState
    /// asserts the invariant that must hold in ALL three cases (no host / licensed host /
    /// unlicensed host) rather than picking one. The sentinel-matching logic itself is also
    /// mirrored directly, the same way WcfChannelFaultTests mirrors GetChannel()'s
    /// CommunicationState guard.
    /// </summary>
    [TestFixture]
    public class LicensingGateTests
    {
        [Test]
        public void IsUnlicensed_DefaultsFalse_OnFreshConnection()
        {
            var conn = new X1MCPServiceConnection();
            Assert.That(conn.IsUnlicensed, Is.False);
            conn.Dispose();
        }

        /// <summary>
        /// Mirrors the exact sentinel comparison used in EnsureConnectedChannel() —
        /// any change to the production check must also be reflected here.
        /// </summary>
        [TestCase("Unlicensed", true)]
        [TestCase("11.0.3.46", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void SentinelMatch_MirrorsProductionCheck(string connectResult, bool expectedUnlicensed)
        {
            bool isUnlicensed = connectResult == "Unlicensed";
            Assert.That(isUnlicensed, Is.EqualTo(expectedUnlicensed));
        }

        /// <summary>
        /// Whatever this machine's actual X1ServiceHost state is right now — not running,
        /// running and licensed, or running and unlicensed — exactly one of three outcomes must
        /// hold, and IsUnlicensed must never disagree with which one occurred:
        ///   1. No reachable host: Connect() itself throws a transport-level exception before any
        ///      string is ever returned → must NOT be X1McpUnlicensedException, and IsUnlicensed
        ///      must stay false (a connectivity failure is not a licensing verdict).
        ///   2. Reachable + licensed: EnsureConnectedChannel() returns normally, IsUnlicensed false.
        ///   3. Reachable + unlicensed: throws X1McpUnlicensedException specifically, IsUnlicensed
        ///      true.
        /// </summary>
        [Test]
        public void EnsureConnectedChannel_ResultInvariant_HoldsRegardlessOfHostState()
        {
            var conn = new X1MCPServiceConnection();

            Exception thrown = null;
            try { conn.EnsureConnectedChannel(); }
            catch (Exception ex) { thrown = ex; }

            if (thrown == null)
            {
                Assert.That(conn.IsUnlicensed, Is.False,
                    "A successful connect means a real (non-'Unlicensed') version string came back");
            }
            else if (thrown is X1McpUnlicensedException)
            {
                Assert.That(conn.IsUnlicensed, Is.True,
                    "X1McpUnlicensedException must only be thrown when IsUnlicensed is set");
            }
            else
            {
                Assert.That(conn.IsUnlicensed, Is.False,
                    "A connectivity/transport failure must not be misreported as an unlicensed condition");
            }

            conn.Dispose();
        }

        [Test]
        public void X1McpUnlicensedException_HasActionableMessage()
        {
            var ex = new X1McpUnlicensedException();
            Assert.That(ex.Message, Does.Contain("licensed"));
        }
    }
}
