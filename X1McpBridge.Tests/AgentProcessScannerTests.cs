// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// XS-1684: AgentProcessScanner.ConcatenateIdentities is the pure join half of
    /// GetConcatenatedIdentity - it folds every distinct detected agent client into the single
    /// (name, version) pair ReportClientInfo accepts, so every one of them reaches the X1 Search
    /// service host when several are running (x1_version reports them as an array). The join is
    /// tested here with hand-built identities rather than a live process scan, which is
    /// non-deterministic across machines/CI - and doubly so now that the scan covers the GitHub
    /// Copilot app as well as the two Claude products.
    /// </summary>
    [TestFixture]
    public class AgentProcessScannerTests
    {
        private static AgentProcessScanner.ClientIdentity Id(string name, string version) =>
            new AgentProcessScanner.ClientIdentity { ProductName = name, ProductVersion = version };

        [Test]
        public void ConcatenateIdentities_Empty_ReturnsNull()
        {
            Assert.That(AgentProcessScanner.ConcatenateIdentities(new AgentProcessScanner.ClientIdentity[0]),
                Is.Null);
        }

        [Test]
        public void ConcatenateIdentities_Single_ReturnsItVerbatimWithNoSeparator()
        {
            var result = AgentProcessScanner.ConcatenateIdentities(new[] { Id("Claude Code", "2.1.222.0") });

            Assert.That(result.HasValue);
            Assert.That(result.Value.ProductName, Is.EqualTo("Claude Code"));
            Assert.That(result.Value.ProductVersion, Is.EqualTo("2.1.222.0"));
        }

        [Test]
        public void ConcatenateIdentities_TwoDistinct_JoinsNamesAndVersionsIndexAligned()
        {
            // The exact shape x1_version reported: desktop app + Claude Code both running.
            var result = AgentProcessScanner.ConcatenateIdentities(new[]
            {
                Id("Claude", "1.26832.0"),
                Id("Claude Code", "2.1.222.0")
            });

            Assert.That(result.HasValue);
            Assert.That(result.Value.ProductName, Is.EqualTo("Claude, Claude Code"));
            Assert.That(result.Value.ProductVersion, Is.EqualTo("1.26832.0, 2.1.222.0"));
        }

        [Test]
        public void ConcatenateIdentities_ClaudeAndCopilot_AreBothReported()
        {
            // The scan is not Claude-only: a machine can have Claude Code and the GitHub Copilot
            // app open at once, both driving sessions through the one shared relay. Nothing in the
            // join is product-specific, and this pins that - a future "which product is this?"
            // special case here would silently drop whichever one it didn't know about.
            var result = AgentProcessScanner.ConcatenateIdentities(new[]
            {
                Id(AgentProcessScanner.ProductNameClaudeCode, "2.1.222.0"),
                Id(AgentProcessScanner.ProductNameCopilot, "1.1.11")
            });

            Assert.That(result.HasValue);
            Assert.That(result.Value.ProductName, Is.EqualTo("Claude Code, GitHub Copilot"));
            Assert.That(result.Value.ProductVersion, Is.EqualTo("2.1.222.0, 1.1.11"));
        }

        [Test]
        public void ConcatenateIdentities_DuplicatePair_IsNotRepeated()
        {
            // Same product+version seen at two install paths must collapse to one entry, so the
            // reported string doesn't read "Claude, Claude".
            var result = AgentProcessScanner.ConcatenateIdentities(new[]
            {
                Id("Claude", "1.26832.0"),
                Id("Claude", "1.26832.0"),
                Id("Claude Code", "2.1.222.0")
            });

            Assert.That(result.Value.ProductName, Is.EqualTo("Claude, Claude Code"));
            Assert.That(result.Value.ProductVersion, Is.EqualTo("1.26832.0, 2.1.222.0"));
        }

        [Test]
        public void ConcatenateIdentities_NullFields_TreatedAsEmptyStrings()
        {
            var result = AgentProcessScanner.ConcatenateIdentities(new[]
            {
                Id("Claude Code", null),
                Id(null, "1.0")
            });

            Assert.That(result.Value.ProductName, Is.EqualTo("Claude Code, "));
            Assert.That(result.Value.ProductVersion, Is.EqualTo(", 1.0"));
        }
    }
}
