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
    /// XS-1678: <c>SearchBridge.ThrowIfSessionCreationFailed</c> is the shared check behind both
    /// <c>SearchSingleTableAsync</c> and <c>AcquireCallbackSession</c> - it distinguishes the two
    /// non-positive sentinels <c>CreateSearchSession</c> can return: <c>-1</c> (XS-1676's new
    /// files-only-tier rejection) and <c>0</c> (the pre-existing "service unavailable" case).
    ///
    /// A pure function (no WCF, no channel, no IO), so it's tested directly here rather than via
    /// a live/faked WCF round-trip - X1MCPSearchConnection's endpoint address is derived from the
    /// current Windows username (see X1MCPWcfUtils.MCPSearchManagerEndpointName), with no seam to
    /// redirect it at a FakeServiceHost the way X1ServiceContractWireTests does for IX1MCPService/
    /// IX1MCPSearchManager wire-shape round-trips, so exercising this specific check end-to-end
    /// isn't a practical unit test; the pure-function extraction is what makes it testable at all.
    /// </summary>
    [TestFixture]
    public class SearchBridgeSessionGateTests
    {
        [Test]
        public void SessionId_Positive_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => SearchBridge.ThrowIfSessionCreationFailed(1, "Files"));
        }

        [Test]
        public void SessionId_NegativeOne_ThrowsFilesOnlyLicenseExceptionNamingTable()
        {
            var ex = Assert.Throws<X1McpFilesOnlyLicenseException>(
                () => SearchBridge.ThrowIfSessionCreationFailed(-1, "Teams"));

            Assert.That(ex.Message, Does.Contain("Teams"));
            Assert.That(ex.Message, Does.Contain("Files only"));
            Assert.That(ex.Message, Does.Contain("MCP-full"));
        }

        [Test]
        public void SessionId_Zero_ThrowsInvalidOperationExceptionNotLicenseException()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => SearchBridge.ThrowIfSessionCreationFailed(0, "Files"));

            Assert.That(ex, Is.Not.InstanceOf<X1McpFilesOnlyLicenseException>());
            Assert.That(ex.Message, Does.Contain("unavailable"));
        }
    }
}
