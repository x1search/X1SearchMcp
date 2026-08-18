// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// XS-1678: the arbitrary-file (not-in-index) Extract Text / Export HTML path is gated on
    /// X1MCPServiceConnection.IsFullSuiteLicensed(), checked by SearchBridge before ExtractFileAsync/
    /// ExportHtmlFromFileAsync ever touch the search-manager channel or the filesystem.
    ///
    /// X1MCPServiceConnection's endpoint address is derived from the current Windows username (see
    /// X1MCPWcfUtils.MCPServiceEndpointName) with no seam to redirect it at a FakeServiceHost, so a
    /// live-connection "blocked" round trip isn't a practical unit test here. What IS both testable
    /// and worth covering:
    ///   - the exact error shape the gate produces (BuildArbitraryFileLicenseError, a pure function
    ///     split out of the gate specifically for this), and
    ///   - that the pre-existing "no service connection wired" test seam (the two-arg SearchBridge
    ///     constructor used throughout this test project) still allows these two methods to reach
    ///     their normal, prior behavior unblocked - proving the new gate didn't regress any existing
    ///     caller that never had a service connection to check in the first place.
    /// </summary>
    [TestFixture]
    public class ArbitraryFileLicenseGateTests
    {
        private static SearchBridge BridgeWithNoServiceConnection() =>
            new SearchBridge(new ColumnNameResolver(null), new TableSchemaResolver(null));

        [Test]
        public void BuildArbitraryFileLicenseError_ExtractFileMode_NamesEntitlementAndAlternative()
        {
            var error = SearchBridge.BuildArbitraryFileLicenseError("extract_file");

            Assert.That(error.Value<string>("mode"), Is.EqualTo("extract_file"));
            var message = error.Value<string>("error");
            Assert.That(message, Does.Contain("MCP-full"));
            Assert.That(message, Does.Contain("Files only"));
            Assert.That(message, Does.Contain("x1_get_content"));
        }

        [Test]
        public void BuildArbitraryFileLicenseError_ExportHtmlMode_SetsCorrectMode()
        {
            var error = SearchBridge.BuildArbitraryFileLicenseError("export_html");

            Assert.That(error.Value<string>("mode"), Is.EqualTo("export_html"));
            Assert.That(error.Value<string>("error"), Does.Contain("MCP-full"));
        }

        [Test]
        public async Task ExtractFileAsync_NoServiceConnectionWired_NotBlockedByGate_ReachesFileNotFoundCheck()
        {
            // No _serviceConnection means nothing to check against - the gate must not block, and
            // execution must reach ExtractFileAsync's own pre-existing file-not-found error, exactly
            // as it did before XS-1678 added the gate.
            var path = Path.Combine(Path.GetTempPath(), "x1mcp_test_nonexistent_" + Guid.NewGuid() + ".txt");
            var result = await BridgeWithNoServiceConnection().ExtractFileAsync(path, 5000);

            Assert.That(result.Value<string>("error"), Does.Contain("File not found"));
            Assert.That(result.Value<string>("error"), Does.Not.Contain("MCP-full"));
        }

        [Test]
        public async Task ExportHtmlFromFileAsync_NoServiceConnectionWired_NotBlockedByGate_ReachesFileNotFoundCheck()
        {
            var path = Path.Combine(Path.GetTempPath(), "x1mcp_test_nonexistent_" + Guid.NewGuid() + ".txt");
            var result = await BridgeWithNoServiceConnection().ExportHtmlFromFileAsync(path, 5000);

            Assert.That(result.Value<string>("error"), Does.Contain("File not found"));
            Assert.That(result.Value<string>("error"), Does.Not.Contain("MCP-full"));
        }
    }
}
