// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using X1.Service;

namespace X1.McpBridge.Tests
{
    [TestFixture]
    public class FilterMapperTests
    {
        private const string Table = "MSMail";

        // A resolver seeded with a small, known schema for "MSMail" - no WCF connection is ever
        // touched as long as tests only resolve columns seeded here (a genuine cache miss would
        // try to refresh via the (null) connection and throw, which is itself a useful signal that
        // a test is exercising an unseeded column it should have named in the seed set instead).
        private static ColumnNameResolver SeededResolver()
        {
            var resolver = new ColumnNameResolver(null);
            resolver.SeedForTest(Table, new Dictionary<string, string>
            {
                { "subject", "Subject" },
                { "from", "From" },
                { "date", "Date/Time" },
                { "date_received", "Received" }, // the field that was missing from the old static dictionary
                { "path", "Path" },
                { "name", "Name" },
            });
            return resolver;
        }

        // Seeds a SECOND table ("Teams") alongside "MSMail" on the same resolver instance - used
        // by the cross-table scoping tests below, which need two independently-resolvable tables
        // to prove a "table"-scoped filter/sort entry for one doesn't leak into a call whose
        // primaryTable is the other (the multi-table fan-out scenario: SearchSingleTableAsync
        // calls BuildTermsAsync/BuildSortColumnsAsync once per table with that table as
        // primaryTable, reusing the same raw filters/sort JSON on every call).
        private static ColumnNameResolver SeededTwoTableResolver()
        {
            var resolver = SeededResolver(); // seeds "MSMail"
            resolver.SeedForTest("Teams", new Dictionary<string, string>
            {
                { "message_body", "Message Body" },
                { "sender", "Sender" },
                { "created", "Created" },
            });
            return resolver;
        }

        // ── BuildTermsAsync ──────────────────────────────────────────────────────

        [Test]
        public async Task BuildTerms_NullQueryAndNullFilters_ReturnsMainTermOnly()
        {
            // The X1 service requires a main SearchTerm (columnName="") to always be present,
            // even with no query/filters — matching SearchViewModel.MakeSearchTerms' behavior.
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, null, null);
            Assert.That(terms.Count, Is.EqualTo(1));
            Assert.That(terms[0].table, Is.EqualTo(""));
            Assert.That(terms[0].columnName, Is.EqualTo(""));
            Assert.That(terms[0].term, Is.EqualTo(""));
        }

        [Test]
        public async Task BuildTerms_WhitespaceQuery_ReturnsMainTermWithEmptyTerm()
        {
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "   ", null);
            Assert.That(terms.Count, Is.EqualTo(1));
            Assert.That(terms[0].term, Is.EqualTo(""));
        }

        [Test]
        public async Task BuildTerms_QueryOnly_ReturnsSingleGlobalTerm()
        {
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "hello world", null);
            Assert.That(terms.Count, Is.EqualTo(1));
            Assert.That(terms[0].term, Is.EqualTo("hello world"));
            Assert.That(terms[0].table, Is.EqualTo(""));
            Assert.That(terms[0].columnName, Is.EqualTo(""));
        }

        [Test]
        public async Task BuildTerms_QueryTrimsWhitespace()
        {
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "  trimmed  ", null);
            Assert.That(terms[0].term, Is.EqualTo("trimmed"));
        }

        [Test]
        public async Task BuildTerms_ArrayFilter_ColumnAndTerm()
        {
            // "subject" is an internal field name that the resolver maps to its registered
            // X1 display name ("Subject") since AddTermsByName looks up columns by display name.
            var filters = JArray.Parse(@"[{""column"":""subject"",""term"":""invoice""}]");
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "", filters);
            Assert.That(terms.Count, Is.EqualTo(2)); // main term (empty) + filter term
            Assert.That(terms[1].columnName, Is.EqualTo("Subject"));
            Assert.That(terms[1].term, Is.EqualTo("invoice"));
            Assert.That(terms[1].table, Is.EqualTo(""));
        }

        [Test]
        public async Task BuildTerms_ArrayFilter_WithTable()
        {
            var filters = JArray.Parse(@"[{""table"":""MSMail"",""column"":""from"",""term"":""alice""}]");
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "", filters);
            Assert.That(terms.Count, Is.EqualTo(2)); // main term (empty) + filter term
            Assert.That(terms[1].table, Is.EqualTo("MSMail"));
            Assert.That(terms[1].columnName, Is.EqualTo("From"));
            Assert.That(terms[1].term, Is.EqualTo("alice"));
        }

        // ── Cross-table scoping (multi-table fan-out follow-up) ─────────────────────
        //
        // BuildTermsAsync is called once per table during multi-table fan-out
        // (SearchBridge.SearchSingleTableAsync), with that table as primaryTable and the SAME raw
        // filters JSON reused across every call. A filter entry's "table" field previously only
        // picked which schema the column resolved against - it never excluded the entry from a
        // call whose primaryTable didn't match, so a filter meant for "Teams" would still be sent
        // into the "MSMail" search session too.

        [Test]
        public async Task BuildTerms_FilterScopedToDifferentTable_IsExcluded()
        {
            var filters = JArray.Parse(@"[{""table"":""Teams"",""column"":""sender"",""term"":""alice""}]");
            var terms = await FilterMapper.BuildTermsAsync(SeededTwoTableResolver(), "MSMail", "", filters);
            // Only the always-present main term survives - the Teams-scoped filter must not leak
            // into a call whose primaryTable is "MSMail".
            Assert.That(terms.Count, Is.EqualTo(1));
            Assert.That(terms[0].table, Is.EqualTo(""));
            Assert.That(terms[0].columnName, Is.EqualTo(""));
        }

        [Test]
        public async Task BuildTerms_FilterScopedToSameTable_IsIncluded()
        {
            var filters = JArray.Parse(@"[{""table"":""MSMail"",""column"":""from"",""term"":""alice""}]");
            var terms = await FilterMapper.BuildTermsAsync(SeededTwoTableResolver(), "MSMail", "", filters);
            Assert.That(terms.Count, Is.EqualTo(2));
            Assert.That(terms[1].table, Is.EqualTo("MSMail"));
            Assert.That(terms[1].columnName, Is.EqualTo("From"));
            Assert.That(terms[1].term, Is.EqualTo("alice"));
        }

        [Test]
        public async Task BuildTerms_MixedTableScopes_OnlyMatchingSurvivesPerCall()
        {
            // The exact multi-table fan-out shape: one filters array containing entries scoped to
            // two different tables, called once per table (as SearchSingleTableAsync does).
            var filters = JArray.Parse(@"[
        {""table"":""MSMail"",""column"":""from"",""term"":""alice""},
        {""table"":""Teams"",""column"":""sender"",""term"":""bob""}
      ]");
            var resolver = SeededTwoTableResolver();

            var mailTerms = await FilterMapper.BuildTermsAsync(resolver, "MSMail", "", filters);
            Assert.That(mailTerms.Count, Is.EqualTo(2), "MSMail call should only see its own filter");
            Assert.That(mailTerms[1].columnName, Is.EqualTo("From"));
            Assert.That(mailTerms[1].term, Is.EqualTo("alice"));

            var teamsTerms = await FilterMapper.BuildTermsAsync(resolver, "Teams", "", filters);
            Assert.That(teamsTerms.Count, Is.EqualTo(2), "Teams call should only see its own filter");
            Assert.That(teamsTerms[1].columnName, Is.EqualTo("Sender"));
            Assert.That(teamsTerms[1].term, Is.EqualTo("bob"));
        }

        [Test]
        public async Task BuildTerms_ArrayFilter_AcceptsColumnNameAlias()
        {
            var filters = JArray.Parse(@"[{""columnName"":""subject"",""term"":""test""}]");
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "", filters);
            Assert.That(terms.Count, Is.EqualTo(2)); // main term (empty) + filter term
            Assert.That(terms[1].columnName, Is.EqualTo("Subject"));
        }

        [Test]
        public async Task BuildTerms_ArrayFilter_SkipsEntryWithEmptyTerm()
        {
            var filters = JArray.Parse(@"[{""column"":""from"",""term"":""""}]");
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "", filters);
            // The empty-term filter entry is skipped, but the always-present main term remains.
            Assert.That(terms.Count, Is.EqualTo(1));
            Assert.That(terms[0].term, Is.EqualTo(""));
        }

        [Test]
        public async Task BuildTerms_QueryAndArrayFilter_ReturnsBoth()
        {
            var filters = JArray.Parse(@"[{""column"":""subject"",""term"":""invoice""}]");
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "report", filters);
            Assert.That(terms.Count, Is.EqualTo(2));
            Assert.That(terms[0].term, Is.EqualTo("report"));
            Assert.That(terms[1].columnName, Is.EqualTo("Subject"));
        }

        [Test]
        public async Task BuildTerms_MultipleArrayFilters_AllIncluded()
        {
            var filters = JArray.Parse(@"[
        {""column"":""from"",""term"":""alice""},
        {""column"":""subject"",""term"":""budget""}
      ]");
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "", filters);
            Assert.That(terms.Count, Is.EqualTo(3)); // main term (empty) + 2 filter terms
        }

        [Test]
        public async Task BuildTerms_LegacyObjectFilter_ConvertsAllProperties()
        {
            var filters = JObject.Parse(@"{""from"":""bob"",""subject"":""meeting""}");
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "", filters);
            Assert.That(terms.Count, Is.EqualTo(3)); // main term (empty) + 2 filter terms
            Assert.That(terms.All(t => t.table == ""), Is.True);
            var columns = terms.Select(t => t.columnName).ToList();
            Assert.That(columns, Does.Contain("From"));
            Assert.That(columns, Does.Contain("Subject"));
        }

        [Test]
        public async Task BuildTerms_NullFiltersToken_ReturnsQueryOnly()
        {
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "test", JValue.CreateNull());
            Assert.That(terms.Count, Is.EqualTo(1));
        }

        // ── Regression: the field the old static dictionary was missing ────────────

        [Test]
        public async Task BuildTerms_DateReceivedInternalName_ResolvesToDisplayName()
        {
            // This is the exact case that silently returned zero results before this fix:
            // "date_received" (internal name) must resolve to "Received" (X1's registered
            // display name), not pass through unchanged.
            var filters = JArray.Parse(@"[{""column"":""date_received"",""term"":""7/14/2026""}]");
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "", filters);
            Assert.That(terms[1].columnName, Is.EqualTo("Received"));
        }

        [Test]
        public async Task BuildTerms_AlreadyCorrectDisplayName_ResolvesToItself()
        {
            var filters = JArray.Parse(@"[{""column"":""Received"",""term"":""7/14/2026""}]");
            var resolver = SeededResolver();
            var terms = await FilterMapper.BuildTermsAsync(resolver, Table, "", filters);
            Assert.That(terms[1].columnName, Is.EqualTo("Received"));
        }

        [Test]
        public async Task BuildTerms_UnresolvableColumn_FallsBackToGeneralContentSearch()
        {
            // A column that isn't seeded (and, with a null connection, can't be refreshed either)
            // falls back to a general content-field search (columnName="") rather than being
            // silently sent through unresolved - the failure mode this whole fix addresses.
            // The term itself is preserved so it still contributes to matching.
            var filters = JArray.Parse(@"[{""column"":""totally_made_up_field"",""term"":""whatever""}]");
            var resolver = new ColumnNameResolver(null); // nothing seeded at all
            var terms = await FilterMapper.BuildTermsAsync(resolver, Table, "", filters);
            Assert.That(terms.Count, Is.EqualTo(2));
            Assert.That(terms[1].columnName, Is.EqualTo(""));
            Assert.That(terms[1].term, Is.EqualTo("whatever"));
        }

        [Test]
        public async Task BuildTerms_RefreshFailsOnCacheMiss_RetainsAlreadyCachedColumns()
        {
            // A resolver that already has a successfully-cached table (via SeedForTest, standing
            // in for a real prior build) must keep serving those known columns even when a later
            // refresh attempt - triggered by an unrelated cache miss - fails (the null connection
            // makes GetSchemaFieldsAsync throw, simulating X1ServiceHost being unreachable).
            var resolver = SeededResolver();

            // Trigger a refresh attempt via a genuine miss; it fails (null connection) and must not
            // wipe the table's already-cached entries.
            var unknownFilters = JArray.Parse(@"[{""column"":""not_a_real_field"",""term"":""x""}]");
            await FilterMapper.BuildTermsAsync(resolver, Table, "", unknownFilters);

            // The previously-seeded "subject" -> "Subject" mapping must still resolve correctly.
            var knownFilters = JArray.Parse(@"[{""column"":""subject"",""term"":""invoice""}]");
            var terms = await FilterMapper.BuildTermsAsync(resolver, Table, "", knownFilters);
            Assert.That(terms[1].columnName, Is.EqualTo("Subject"));
        }

        [Test]
        public async Task BuildTerms_InternalAndDisplayNameBothResolve_RegressionGuardForSortWork()
        {
            // Pins ColumnNameResolver's existing dual-direction filter behavior (internal name OR
            // display name -> display name) so it can't silently regress while ResolveInternalAsync
            // (sort/displayFields' reverse map, added alongside it) evolves.
            var resolver = SeededResolver();
            var byInternal = await FilterMapper.BuildTermsAsync(resolver, Table, "",
                JArray.Parse(@"[{""column"":""subject"",""term"":""x""}]"));
            var byDisplay = await FilterMapper.BuildTermsAsync(resolver, Table, "",
                JArray.Parse(@"[{""column"":""Subject"",""term"":""x""}]"));
            Assert.That(byInternal[1].columnName, Is.EqualTo("Subject"));
            Assert.That(byDisplay[1].columnName, Is.EqualTo("Subject"));
        }

        // ── BuildDisplayColumnsAsync ─────────────────────────────────────────────
        // Resolved to each column's INTERNAL name (see FilterMapper.BuildDisplayColumnsAsync's own
        // doc comment): X1's ItemList mechanism indexes displayFields/sort by internal name, the
        // opposite convention from AddTermsByName (filters), which needs the DISPLAY name.
        // Confirmed by X1ServiceHost's own log showing "Cannot find field 'Received'" when the
        // display name was (incorrectly) sent as a sort column before this fix.

        [Test]
        public async Task BuildDisplayColumnsAsync_NullToken_ReturnsEmptyArray()
        {
            var cols = await FilterMapper.BuildDisplayColumnsAsync(SeededResolver(), Table, null);
            Assert.That(cols, Is.Empty);
        }

        [Test]
        public async Task BuildDisplayColumnsAsync_EmptyArray_ReturnsEmptyArray()
        {
            var cols = await FilterMapper.BuildDisplayColumnsAsync(SeededResolver(), Table, new JArray());
            Assert.That(cols, Is.Empty);
        }

        [Test]
        public async Task BuildDisplayColumnsAsync_AlreadyInternalNames_ResolveToThemselves()
        {
            var fields = JArray.Parse(@"[""subject"",""from"",""date""]");
            var cols = await FilterMapper.BuildDisplayColumnsAsync(SeededResolver(), Table, fields);
            Assert.That(cols.Length, Is.EqualTo(3));
            Assert.That(cols[0].name, Is.EqualTo("subject"));
            Assert.That(cols[1].name, Is.EqualTo("from"));
            Assert.That(cols[2].name, Is.EqualTo("date"));
        }

        [Test]
        public async Task BuildDisplayColumnsAsync_SingleField_ReturnsSingleColumn()
        {
            var fields = JArray.Parse(@"[""name""]");
            var cols = await FilterMapper.BuildDisplayColumnsAsync(SeededResolver(), Table, fields);
            Assert.That(cols.Length, Is.EqualTo(1));
            Assert.That(cols[0].name, Is.EqualTo("name"));
        }

        [Test]
        public async Task BuildDisplayColumnsAsync_DisplayNameColumn_ResolvesToInternalName()
        {
            // "Received" is the DISPLAY name; the seeded schema's internal name is "date_received".
            // Passing the display name here must resolve to the internal name X1's ItemList expects.
            var fields = JArray.Parse(@"[""Received""]");
            var cols = await FilterMapper.BuildDisplayColumnsAsync(SeededResolver(), Table, fields);
            Assert.That(cols[0].name, Is.EqualTo("date_received"));
        }

        [Test]
        public async Task BuildDisplayColumnsAsync_UnresolvableColumn_FallsBackToRawString()
        {
            // Confirmed policy: an unresolvable column falls back to the caller's raw string
            // (not blanked/dropped like filters' "" fallback) - it may already be correct, and
            // there is no safe universal substitute for a display/sort column.
            var resolver = new ColumnNameResolver(null); // nothing seeded, null connection
            var fields = JArray.Parse(@"[""totally_made_up_field""]");
            var cols = await FilterMapper.BuildDisplayColumnsAsync(resolver, Table, fields);
            Assert.That(cols[0].name, Is.EqualTo("totally_made_up_field"));
        }

        // ── BuildSortColumnsAsync ────────────────────────────────────────────────
        // Same resolution rationale as BuildDisplayColumnsAsync above.

        [Test]
        public async Task BuildSortColumnsAsync_NullToken_ReturnsEmpty()
        {
            var cols = await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table, null);
            Assert.That(cols, Is.Empty);
        }

        [Test]
        public async Task BuildSortColumnsAsync_EmptyArray_ReturnsEmpty()
        {
            var cols = await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table, new JArray());
            Assert.That(cols, Is.Empty);
        }

        [Test]
        public async Task BuildSortColumnsAsync_ForwardsIsDefault()
        {
            var sort = JArray.Parse(@"[{""column"":""date""}]");
            var cols = await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table, sort);
            Assert.That(cols.Length, Is.EqualTo(1));
            Assert.That(cols[0].name, Is.EqualTo("date"));
            Assert.That(cols[0].direction, Is.EqualTo(SortDirection.Forwards));
        }

        [Test]
        public async Task BuildSortColumnsAsync_BackwardsDirection()
        {
            var sort = JArray.Parse(@"[{""column"":""date"",""direction"":""backwards""}]");
            var cols = await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table, sort);
            Assert.That(cols[0].direction, Is.EqualTo(SortDirection.Backwards));
        }

        [Test]
        public async Task BuildSortColumnsAsync_BackDirectionPrefix_Accepted()
        {
            var sort = JArray.Parse(@"[{""column"":""date"",""direction"":""back""}]");
            var cols = await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table, sort);
            Assert.That(cols[0].direction, Is.EqualTo(SortDirection.Backwards));
        }

        [Test]
        public async Task BuildSortColumnsAsync_NoColumnName_SkipsEntry()
        {
            var sort = JArray.Parse(@"[{""direction"":""forwards""}]");
            var cols = await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table, sort);
            Assert.That(cols, Is.Empty);
        }

        [Test]
        public async Task BuildSortColumnsAsync_NameAlias_Accepted()
        {
            var sort = JArray.Parse(@"[{""name"":""subject""}]");
            var cols = await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table, sort);
            Assert.That(cols.Length, Is.EqualTo(1));
            Assert.That(cols[0].name, Is.EqualTo("subject"));
        }

        [Test]
        public async Task BuildSortColumnsAsync_WithTable_Propagated()
        {
            var sort = JArray.Parse(@"[{""column"":""date"",""table"":""MSMail""}]");
            var cols = await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table, sort);
            Assert.That(cols[0].table, Is.EqualTo("MSMail"));
        }

        // ── Cross-table scoping (multi-table fan-out follow-up) ─────────────────────
        // Same rationale as the BuildTermsAsync cross-table tests above.

        [Test]
        public async Task BuildSortColumnsAsync_SortScopedToDifferentTable_IsExcluded()
        {
            var sort = JArray.Parse(@"[{""table"":""Teams"",""column"":""created"",""direction"":""desc""}]");
            var cols = await FilterMapper.BuildSortColumnsAsync(SeededTwoTableResolver(), "MSMail", sort);
            Assert.That(cols, Is.Empty, "a sort column scoped to Teams must not apply to an MSMail call");
        }

        [Test]
        public async Task BuildSortColumnsAsync_SortScopedToSameTable_IsIncluded()
        {
            var sort = JArray.Parse(@"[{""table"":""MSMail"",""column"":""date"",""direction"":""desc""}]");
            var cols = await FilterMapper.BuildSortColumnsAsync(SeededTwoTableResolver(), "MSMail", sort);
            Assert.That(cols.Length, Is.EqualTo(1));
            Assert.That(cols[0].name, Is.EqualTo("date"));
            Assert.That(cols[0].direction, Is.EqualTo(SortDirection.Backwards));
        }

        [Test]
        public async Task BuildSortColumnsAsync_MixedTableScopes_OnlyMatchingSurvivesPerCall()
        {
            var sort = JArray.Parse(@"[
        {""table"":""MSMail"",""column"":""date"",""direction"":""desc""},
        {""table"":""Teams"",""column"":""created"",""direction"":""desc""}
      ]");
            var resolver = SeededTwoTableResolver();

            var mailCols = await FilterMapper.BuildSortColumnsAsync(resolver, "MSMail", sort);
            Assert.That(mailCols.Length, Is.EqualTo(1));
            Assert.That(mailCols[0].name, Is.EqualTo("date"));

            var teamsCols = await FilterMapper.BuildSortColumnsAsync(resolver, "Teams", sort);
            Assert.That(teamsCols.Length, Is.EqualTo(1));
            Assert.That(teamsCols[0].name, Is.EqualTo("created"));
        }

        [Test]
        public async Task BuildSortColumnsAsync_DisplayNameColumn_ResolvesToInternalName()
        {
            // This is the exact XS-1640-adjacent failure mode: sorting by "Received" (display name)
            // used to reach X1's ItemList unresolved and silently no-op ("Cannot find field
            // 'Received'"). It must now resolve to the internal name "date_received".
            var sort = JArray.Parse(@"[{""column"":""Received""}]");
            var cols = await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table, sort);
            Assert.That(cols[0].name, Is.EqualTo("date_received"));
        }

        [Test]
        public async Task BuildSortColumnsAsync_UnresolvableColumn_FallsBackToRawString()
        {
            var resolver = new ColumnNameResolver(null); // nothing seeded, null connection
            var sort = JArray.Parse(@"[{""column"":""totally_made_up_field""}]");
            var cols = await FilterMapper.BuildSortColumnsAsync(resolver, Table, sort);
            Assert.That(cols[0].name, Is.EqualTo("totally_made_up_field"));
        }

        // ── Direction vocabulary (desc/asc spellings, not just forwards/backwards) ─

        [Test]
        public async Task BuildSortColumnsAsync_DescAndDescending_AreBackwards()
        {
            foreach (var d in new[] { "desc", "descending", "DESC", "Descending" })
            {
                var cols = await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table,
                    JArray.Parse(@"[{""column"":""date"",""direction"":""" + d + @"""}]"));
                Assert.That(cols[0].direction, Is.EqualTo(SortDirection.Backwards), "direction=" + d);
            }
        }

        [Test]
        public async Task BuildSortColumnsAsync_AscSpellings_AreForwards()
        {
            foreach (var d in new[] { "asc", "ascending", "forwards", "up", "oldest", "" })
            {
                var cols = await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table,
                    JArray.Parse(@"[{""column"":""date"",""direction"":""" + d + @"""}]"));
                Assert.That(cols[0].direction, Is.EqualTo(SortDirection.Forwards), "direction=" + d);
            }
        }

        [Test]
        public void ParseDirection_DescendingSynonyms_AreBackwards()
        {
            foreach (var d in new[] { "desc", "descending", "backwards", "back", "down", "-", "newest", "latest" })
                Assert.That(FilterMapper.ParseDirection(d), Is.EqualTo(SortDirection.Backwards), "direction=" + d);
        }

        [Test]
        public void ParseDirection_AscendingOrUnknown_AreForwards()
        {
            foreach (var d in new[] { "asc", "ascending", "forwards", "up", "oldest", "", null, "sideways" })
                Assert.That(FilterMapper.ParseDirection(d), Is.EqualTo(SortDirection.Forwards), "direction=" + (d ?? "null"));
        }

        // ── Stringified-JSON coercion (some MCP clients send arrays as a JSON string) ──

        [Test]
        public async Task BuildSortColumnsAsync_StringifiedJsonArray_IsParsed()
        {
            // The live client sent sort as a JSON *string* rather than an array.
            JToken token = new JValue(@"[{""column"":""date"",""direction"":""desc""}]");
            var cols = await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table, token);
            Assert.That(cols.Length, Is.EqualTo(1));
            Assert.That(cols[0].name, Is.EqualTo("date"));
            Assert.That(cols[0].direction, Is.EqualTo(SortDirection.Backwards));
        }

        [Test]
        public async Task BuildDisplayColumnsAsync_StringifiedJsonArray_IsParsed()
        {
            JToken token = new JValue(@"[""subject"",""date""]");
            var cols = await FilterMapper.BuildDisplayColumnsAsync(SeededResolver(), Table, token);
            Assert.That(cols.Select(c => c.name), Is.EqualTo(new[] { "subject", "date" }));
        }

        [Test]
        public async Task BuildTerms_StringifiedJsonFilters_AreParsed()
        {
            JToken token = new JValue(@"{""path"":""woodworking""}");
            var terms = await FilterMapper.BuildTermsAsync(SeededResolver(), Table, "chair", token);
            Assert.That(terms.Any(t => t.columnName == "Path" && t.term == "woodworking"), Is.True);
        }

        [Test]
        public async Task BuildSortColumnsAsync_PlainStringNonJson_ReturnsEmpty()
        {
            // A non-JSON string must not throw — it just yields no sort columns.
            JToken token = new JValue("not json");
            Assert.That(await FilterMapper.BuildSortColumnsAsync(SeededResolver(), Table, token), Is.Empty);
        }
    }
}
