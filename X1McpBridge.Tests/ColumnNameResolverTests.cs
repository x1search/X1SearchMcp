// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// ColumnNameResolver.ResolveInternalAsync - the reverse direction of ResolveAsync. Filters
    /// (AddTermsByName) need the DISPLAY name; sort/displayFields (ItemList) need the INTERNAL
    /// name instead - see FilterMapper's own doc comments. Both maps are built from the same
    /// schema fetch / the same SeedForTest call, so one seed populates both directions.
    /// </summary>
    [TestFixture]
    public class ColumnNameResolverTests
    {
        private const string Table = "MSMail";

        private static ColumnNameResolver Seeded()
        {
            var resolver = new ColumnNameResolver(null);
            resolver.SeedForTest(Table, new Dictionary<string, string>
            {
                { "subject", "Subject" },
                { "date_received", "Received" },
            });
            return resolver;
        }

        [Test]
        public async Task ResolveInternalAsync_DisplayName_ResolvesToInternalName()
        {
            // Exact reverse of FilterMapperTests' BuildTerms_DateReceivedInternalName_ResolvesToDisplayName.
            var resolved = await Seeded().ResolveInternalAsync(Table, "Received");
            Assert.That(resolved, Is.EqualTo("date_received"));
        }

        [Test]
        public async Task ResolveInternalAsync_AlreadyInternalName_ResolvesToItself()
        {
            var resolved = await Seeded().ResolveInternalAsync(Table, "date_received");
            Assert.That(resolved, Is.EqualTo("date_received"));
        }

        [Test]
        public async Task ResolveInternalAsync_Unresolvable_ReturnsNull()
        {
            // Mirrors ResolveAsync's existing null-on-miss contract (a null connection means the
            // refresh-on-miss attempt fails silently and the original miss stands).
            var resolved = await Seeded().ResolveInternalAsync(Table, "totally_made_up_field");
            Assert.That(resolved, Is.Null);
        }

        [Test]
        public async Task ResolveInternalAsync_EmptyColumn_ReturnsUnchanged()
        {
            var resolved = await Seeded().ResolveInternalAsync(Table, "");
            Assert.That(resolved, Is.EqualTo(""));
        }

        [Test]
        public async Task SeedForTest_PopulatesBothForwardAndReverseMaps()
        {
            var resolver = Seeded();
            var displayName = await resolver.ResolveAsync(Table, "subject");
            var internalName = await resolver.ResolveInternalAsync(Table, "Subject");
            Assert.That(displayName, Is.EqualTo("Subject"));
            Assert.That(internalName, Is.EqualTo("subject"));
        }
    }
}
