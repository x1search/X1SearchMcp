// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// TableSchemaResolver resolves a caller-supplied table token (scanner name, per-account
    /// displayName, or an already-correct schema) to the one schema value x1_search /
    /// x1_get_schema_fields actually need (XS-1640). Tests use SeedForTest so no live WCF
    /// connection (X1MCPServiceConnection(null)) is ever touched.
    /// </summary>
    [TestFixture]
    public class TableSchemaResolverTests
    {
        // Mirrors a PST-scanner-like fixture: one ambiguous token ("PST") mapping to several real
        // schemas, plus a couple of unambiguous name -> schema mappings, plus a displayName that
        // differs from its scanner name.
        private static TableSchemaResolver Seeded()
        {
            var resolver = new TableSchemaResolver(null);
            resolver.SeedForTest(
                new Dictionary<string, string[]>
                {
                    { "OutlookEmail", new[] { "Email" } },
                    { "MS Online Archive", new[] { "Exchange" } }, // scannerDisplayName example
                    { "PST", new[] { "PSTFile", "PSTEmail", "PSTCalendar", "PSTContact", "PSTNote" } },
                },
                validSchemas: new[] { "Email", "Exchange", "PSTFile", "PSTEmail", "PSTCalendar", "PSTContact", "PSTNote", "Files" });
            return resolver;
        }

        [Test]
        public async Task Resolve_ExactSchemaMatch_IsIdentityPassthrough()
        {
            var result = await Seeded().ResolveAsync("Email");
            Assert.That(result.Kind, Is.EqualTo(TableSchemaResolver.Kind.Identity));
            Assert.That(result.Schema, Is.EqualTo("Email"));
        }

        [Test]
        public async Task Resolve_UniqueScannerName_ResolvesSilently()
        {
            var result = await Seeded().ResolveAsync("OutlookEmail");
            Assert.That(result.Kind, Is.EqualTo(TableSchemaResolver.Kind.Resolved));
            Assert.That(result.Schema, Is.EqualTo("Email"));
        }

        [Test]
        public async Task Resolve_ScannerDisplayName_ResolvesSameAsScannerName()
        {
            var result = await Seeded().ResolveAsync("MS Online Archive");
            Assert.That(result.Kind, Is.EqualTo(TableSchemaResolver.Kind.Resolved));
            Assert.That(result.Schema, Is.EqualTo("Exchange"));
        }

        [Test]
        public async Task Resolve_AmbiguousScannerName_ReturnsAllCandidates()
        {
            var result = await Seeded().ResolveAsync("PST");
            Assert.That(result.Kind, Is.EqualTo(TableSchemaResolver.Kind.Ambiguous));
            Assert.That(result.Candidates, Is.EquivalentTo(
                new[] { "PSTFile", "PSTEmail", "PSTCalendar", "PSTContact", "PSTNote" }));
        }

        [Test]
        public async Task Resolve_UnknownToken_ReturnsUnknown()
        {
            var result = await Seeded().ResolveAsync("DefinitelyNotARealTable");
            Assert.That(result.Kind, Is.EqualTo(TableSchemaResolver.Kind.Unknown));
        }

        [Test]
        public void Resolve_EmptyCache_TrustsCallerTokenUnchanged()
        {
            // Safety valve: a resolver that has never learned any real schema (service unreachable,
            // or - as in most of this test project - no live X1MCPServiceConnection at all) cannot
            // tell "genuinely unknown table" from "we don't know anything yet". It must trust the
            // caller's token rather than inventing a validation failure - this is also what lets
            // every other test file's `new TableSchemaResolver(null)` keep working unseeded.
            var resolver = new TableSchemaResolver(null);
            var result = resolver.ResolveAsync("Files").GetAwaiter().GetResult();
            Assert.That(result.Kind, Is.EqualTo(TableSchemaResolver.Kind.Identity));
            Assert.That(result.Schema, Is.EqualTo("Files"));
        }

        [Test]
        public void ResolveOrThrowAsync_AmbiguousToken_ErrorListsCandidates()
        {
            var ex = Assert.ThrowsAsync<ArgumentException>(
                async () => await Seeded().ResolveOrThrowAsync("PST"));
            Assert.That(ex.Message, Does.Contain("PST"));
            Assert.That(ex.Message, Does.Contain("PSTFile"));
            Assert.That(ex.Message, Does.Contain("PSTEmail"));
            Assert.That(ex.Message, Does.Contain("specify one"));
        }

        [Test]
        public void ResolveOrThrowAsync_UnknownToken_ErrorListsValidSchemas()
        {
            var ex = Assert.ThrowsAsync<ArgumentException>(
                async () => await Seeded().ResolveOrThrowAsync("DefinitelyNotARealTable"));
            Assert.That(ex.Message, Does.Contain("unknown table 'DefinitelyNotARealTable'"));
            Assert.That(ex.Message, Does.Contain("Email"));
            Assert.That(ex.Message, Does.Contain("Files"));
        }

        [Test]
        public async Task ResolveOrThrowAsync_ResolvableToken_ReturnsSchemaWithoutThrowing()
        {
            var schema = await Seeded().ResolveOrThrowAsync("OutlookEmail");
            Assert.That(schema, Is.EqualTo("Email"));
        }

        [Test]
        public async Task Resolve_NullOrEmptyToken_ReturnsIdentityUnchanged()
        {
            var resolver = Seeded();
            var nullResult = await resolver.ResolveAsync(null);
            var emptyResult = await resolver.ResolveAsync("");
            Assert.That(nullResult.Kind, Is.EqualTo(TableSchemaResolver.Kind.Identity));
            Assert.That(emptyResult.Kind, Is.EqualTo(TableSchemaResolver.Kind.Identity));
        }
    }
}
