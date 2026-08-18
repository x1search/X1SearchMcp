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
    /// XS-1640: SearchBridge.SearchAsync resolves each requested table BEFORE doing anything else -
    /// including before the "query or filters must supply a search term" check - so an unknown or
    /// ambiguous table always throws its own descriptive error rather than a generic one, and never
    /// requires a live WCF connection to fail correctly (the throw happens before Channel is ever
    /// touched).
    /// </summary>
    [TestFixture]
    public class SearchBridgeTableResolutionTests
    {
        private static TableSchemaResolver SeededTableResolver()
        {
            var resolver = new TableSchemaResolver(null);
            resolver.SeedForTest(
                new Dictionary<string, string[]>
                {
                    { "PST", new[] { "PSTFile", "PSTEmail", "PSTCalendar" } },
                },
                validSchemas: new[] { "Files", "Email" });
            return resolver;
        }

        private static SearchBridge Bridge() =>
            new SearchBridge(new ColumnNameResolver(null), SeededTableResolver());

        [Test]
        public void SearchAsync_UnknownTable_ThrowsDescriptiveArgumentExceptionBeforeTouchingChannel()
        {
            var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
                await Bridge().SearchAsync("hello", Newtonsoft.Json.Linq.JArray.Parse(@"[""NotARealTable""]"),
                    progenitorSearch: false, limit: 20, includeSnippets: true, includeActions: true,
                    filters: null, displayFields: null, sortColumns: null, timeoutMs: 5000));

            Assert.That(ex.Message, Does.Contain("unknown table 'NotARealTable'"));
            Assert.That(ex.Message, Does.Contain("Files"));
            Assert.That(ex.Message, Does.Contain("Email"));
            // Proves table resolution runs before the search-term check - a query was supplied
            // ("hello"), so a "search term" error would mean resolution didn't run first.
            Assert.That(ex.Message, Does.Not.Contain("search term"));
        }

        [Test]
        public void SearchAsync_AmbiguousTable_ThrowsDescriptiveArgumentException()
        {
            var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
                await Bridge().SearchAsync("hello", Newtonsoft.Json.Linq.JArray.Parse(@"[""PST""]"),
                    progenitorSearch: false, limit: 20, includeSnippets: true, includeActions: true,
                    filters: null, displayFields: null, sortColumns: null, timeoutMs: 5000));

            Assert.That(ex.Message, Does.Contain("table 'PST' matches multiple schemas"));
            Assert.That(ex.Message, Does.Contain("PSTFile"));
            Assert.That(ex.Message, Does.Contain("PSTEmail"));
            Assert.That(ex.Message, Does.Contain("PSTCalendar"));
        }

        [Test]
        public void SearchAsync_ResolvableTableWithNoSearchTerm_ThrowsSearchTermErrorNotTableError()
        {
            // Once the table resolves cleanly (here: already a valid schema), the pre-existing
            // "must supply a search term" validation still fires as before - proving the new
            // table-resolution step doesn't swallow or reorder past that check.
            var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
                await Bridge().SearchAsync("", Newtonsoft.Json.Linq.JArray.Parse(@"[""Files""]"),
                    progenitorSearch: false, limit: 20, includeSnippets: true, includeActions: true,
                    filters: null, displayFields: null, sortColumns: null, timeoutMs: 5000));

            Assert.That(ex.Message, Does.Contain("search term"));
        }

        // ── "tables" as a bare string instead of an array (XS-1642 follow-up) ────
        //
        // Defense in depth alongside McpServer.NormalizeAndValidateArgs: even if a misnamed
        // "table" key is correctly renamed to "tables", the VALUE can still be a bare string
        // ("tables": "MSMail") rather than an array - a shape mismatch that previously fell
        // through to BridgeConfig.GetDefaultTables() exactly like the missing-key case, making
        // "wrong shape" indistinguishable from "omitted entirely".

        [Test]
        public void SearchAsync_TablesAsBareString_ReachesSameResolutionPathAsArrayForm()
        {
            // Reuses the "PST" ambiguous-table fixture from SeededTableResolver(): a bare string
            // ("tables": "PST") must reach the exact same per-table resolution code an array entry
            // (["PST"]) would - proven by getting the identical ambiguous-table error - rather than
            // silently falling back to BridgeConfig.GetDefaultTables() the way a missing/null token
            // legitimately does. This never touches Channel (the throw happens during resolution,
            // same as the array-form tests above), so it needs no live WCF connection.
            var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
                await Bridge().SearchAsync("hello", new Newtonsoft.Json.Linq.JValue("PST"),
                    progenitorSearch: false, limit: 20, includeSnippets: true, includeActions: true,
                    filters: null, displayFields: null, sortColumns: null, timeoutMs: 5000));

            Assert.That(ex.Message, Does.Contain("table 'PST' matches multiple schemas"));
        }

        [Test]
        public void SearchAsync_TablesAsBareString_UnknownTable_ThrowsNamingIt()
        {
            // Proves the bare string actually reached table resolution (rather than silently
            // defaulting) - an unresolvable bare-string table name must produce the same
            // descriptive error an unresolvable array entry would.
            var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
                await Bridge().SearchAsync("hello", new Newtonsoft.Json.Linq.JValue("NotARealTable"),
                    progenitorSearch: false, limit: 20, includeSnippets: true, includeActions: true,
                    filters: null, displayFields: null, sortColumns: null, timeoutMs: 5000));

            Assert.That(ex.Message, Does.Contain("unknown table 'NotARealTable'"));
        }

        // ── Multi-table (fan-out) table resolution ───────────────────────────────
        //
        // Table-NAME resolution (ResolveTablesAsync) already loops over the entire array before
        // any search starts, so a bad second entry in a multi-table request must fail the WHOLE
        // call up front - exactly like a bad single entry - never touching Channel and never
        // starting SearchMultiTableAsync's per-table fan-out loop at all.

        [Test]
        public void SearchAsync_MultiTable_SecondEntryUnknown_ThrowsDescriptiveArgumentExceptionBeforeSearching()
        {
            var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
                await Bridge().SearchAsync("hello", Newtonsoft.Json.Linq.JArray.Parse(@"[""Files"",""NotARealTable""]"),
                    progenitorSearch: false, limit: 20, includeSnippets: true, includeActions: true,
                    filters: null, displayFields: null, sortColumns: null, timeoutMs: 5000));

            Assert.That(ex.Message, Does.Contain("unknown table 'NotARealTable'"));
            Assert.That(ex.Message, Does.Not.Contain("search term"));
        }

        [Test]
        public void SearchAsync_MultiTable_SecondEntryAmbiguous_ThrowsDescriptiveArgumentException()
        {
            var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
                await Bridge().SearchAsync("hello", Newtonsoft.Json.Linq.JArray.Parse(@"[""Files"",""PST""]"),
                    progenitorSearch: false, limit: 20, includeSnippets: true, includeActions: true,
                    filters: null, displayFields: null, sortColumns: null, timeoutMs: 5000));

            Assert.That(ex.Message, Does.Contain("table 'PST' matches multiple schemas"));
            Assert.That(ex.Message, Does.Contain("PSTFile"));
            Assert.That(ex.Message, Does.Contain("PSTEmail"));
            Assert.That(ex.Message, Does.Contain("PSTCalendar"));
        }

        // ── XS-1678: GetContentAsync / ExportHtmlAsync now resolve their table too ───
        //
        // Before XS-1678 these two methods reached the channel directly with a raw table name.
        // On a files-only license the server silently drops GetContent/ExportHtml for a disallowed
        // table (no callback ever fires), so an unresolved call would hang for the full timeout
        // instead of failing fast. These prove the resolver call was added before Channel is ever
        // touched, exactly like SearchAsync's own resolution above.

        [Test]
        public void GetContentAsync_UnknownTable_ThrowsDescriptiveArgumentExceptionBeforeTouchingChannel()
        {
            var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
                await Bridge().GetContentAsync("NotARealTable", "uri://x", mode: "content", timeoutMs: 5000));

            Assert.That(ex.Message, Does.Contain("unknown table 'NotARealTable'"));
            Assert.That(ex.Message, Does.Contain("Files"));
        }

        [Test]
        public void ExportHtmlAsync_UnknownTable_ThrowsDescriptiveArgumentExceptionBeforeTouchingChannel()
        {
            var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
                await Bridge().ExportHtmlAsync("NotARealTable", "uri://x", timeoutMs: 5000));

            Assert.That(ex.Message, Does.Contain("unknown table 'NotARealTable'"));
            Assert.That(ex.Message, Does.Contain("Files"));
        }

        // ── XS-1662: files-only tier makes the "unknown table" message licensing-aware ───
        //
        // On a files-only license the non-Files sources aren't in metadata, so a non-Files token lands
        // in ResolveOrThrowAsync's Unknown branch - the exact path QA saw return a bare
        // "unknown table 'Teams'; valid values: Files" schema error. When the resolver can see it's the
        // files-only tier it must instead explain the licensing limit and route to the landing page.

        private static SearchBridge FilesOnlyBridge()
        {
            var resolver = new TableSchemaResolver(null);
            resolver.SeedForTest(new Dictionary<string, string[]>(),
                validSchemas: new[] { "Files" }, fullSuiteLicensed: false);
            return new SearchBridge(new ColumnNameResolver(null), resolver);
        }

        [Test]
        public void SearchAsync_FilesOnlyTier_NonFilesTable_ThrowsLicensingAwareErrorWithLandingPage()
        {
            var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
                await FilesOnlyBridge().SearchAsync("hello", Newtonsoft.Json.Linq.JArray.Parse(@"[""Teams""]"),
                    progenitorSearch: false, limit: 20, includeSnippets: true, includeActions: true,
                    filters: null, displayFields: null, sortColumns: null, timeoutMs: 5000));

            Assert.That(ex.Message, Does.Contain("Teams"));
            Assert.That(ex.Message, Does.Contain("Files only"));
            Assert.That(ex.Message, Does.Contain("MCP-full"));
            Assert.That(ex.Message, Does.Contain(BridgeConstants.McpLandingPageUrl));
            // The whole point of XS-1662: it no longer reads as a schema "unknown table" error.
            Assert.That(ex.Message, Does.Not.Contain("unknown table"));
        }

        [Test]
        public void SearchAsync_FullSuite_UnknownTable_KeepsSchemaErrorNotLicensingMessage()
        {
            // Same seed but full-suite entitlement: a genuinely unknown token must still get the plain
            // schema error, never the licensing message - so a typo is never mislabeled as an
            // entitlement limit.
            var resolver = new TableSchemaResolver(null);
            resolver.SeedForTest(new Dictionary<string, string[]>(),
                validSchemas: new[] { "Files" }, fullSuiteLicensed: true);
            var bridge = new SearchBridge(new ColumnNameResolver(null), resolver);

            var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
                await bridge.SearchAsync("hello", Newtonsoft.Json.Linq.JArray.Parse(@"[""Teams""]"),
                    progenitorSearch: false, limit: 20, includeSnippets: true, includeActions: true,
                    filters: null, displayFields: null, sortColumns: null, timeoutMs: 5000));

            Assert.That(ex.Message, Does.Contain("unknown table 'Teams'"));
            Assert.That(ex.Message, Does.Not.Contain("MCP-full"));
        }
    }
}
