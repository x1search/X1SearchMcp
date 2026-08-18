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
using Newtonsoft.Json.Linq;
using X1.Service;

namespace X1.McpBridge
{
    /// <summary>
    /// Maps MCP JSON filters to X1 <see cref="SearchTerm"/> rows (table, columnName, term).
    ///
    /// Column-name resolution (internal name -> the display name X1's AddTermsByName actually
    /// indexes against) is delegated to <see cref="ColumnNameResolver"/>, which builds a complete
    /// per-table map from the live schema instead of a hand-maintained static dictionary. The
    /// previous ~20-entry dictionary was missing "date_received"/"date_sent" among others, so
    /// filtering by them silently matched zero results instead of erroring.
    /// </summary>
    internal static class FilterMapper
    {
        /// <summary>
        /// Some MCP clients serialize complex arguments (arrays/objects) as a JSON *string* when the
        /// tool schema doesn't declare a concrete type. Coerce such a string back into a JToken so the
        /// parsers below work whether the client sent a real array/object or a stringified one.
        ///
        /// Internal (not private) so SearchBridge.ResolveTablesAsync can apply the same coercion to
        /// the "tables" argument, which needs it for exactly the same reason but lives outside this
        /// class's own filters/displayFields/sort parsing.
        /// </summary>
        internal static JToken CoerceJson(JToken token)
        {
            if (token != null && token.Type == JTokenType.String)
            {
                var s = token.ToString().Trim();
                if (s.Length > 1 && (s[0] == '[' || s[0] == '{'))
                {
                    try { return JToken.Parse(s); }
                    catch { /* not JSON — leave as the original string */ }
                }
            }
            return token;
        }

        /// <summary>
        /// Optional filters: array of { "table", "column", "term" } or { "column", "term" } (table optional
        /// - defaults to <paramref name="primaryTable"/>, the table(s) the search itself targets). Also
        /// supports legacy object map of columnName -> term for empty table.
        ///
        /// A filter column that <paramref name="resolver"/> can't resolve (even after a schema refresh -
        /// a typo, or a field that genuinely doesn't exist) falls back to a general content-field search
        /// (columnName="") rather than silently contributing zero matches for that term.
        /// </summary>
        public static async Task<List<SearchTerm>> BuildTermsAsync(ColumnNameResolver resolver, string primaryTable, string query, JToken filtersToken)
        {
            filtersToken = CoerceJson(filtersToken);
            var list = new List<SearchTerm>();

            // The X1 service expects a main SearchTerm (columnName="") to always be present,
            // even when the user is filtering by column only — matching the UI's behaviour in
            // SearchViewModel.MakeSearchTerms which unconditionally adds the main term first.
            // Omitting it when query is empty causes the service to stall on column-only searches.
            list.Add(new SearchTerm
            {
                table = "",
                columnName = "",
                term = string.IsNullOrWhiteSpace(query) ? "" : query.Trim()
            });

            if (filtersToken == null || filtersToken.Type == JTokenType.Null)
                return list;

            if (filtersToken is JArray arr)
            {
                foreach (JToken item in arr)
                {
                    if (item is JObject o)
                    {
                        string table = o.Value<string>("table") ?? "";
                        string rawColumn = o.Value<string>("column") ?? o.Value<string>("columnName") ?? "";
                        string term = o.Value<string>("term") ?? "";
                        if (string.IsNullOrEmpty(term))
                            continue;
                        // A filter explicitly scoped to a different table than the one this call is
                        // searching doesn't apply here - most relevant during multi-table fan-out,
                        // where BuildTermsAsync is called once per table with that table as
                        // primaryTable. Without this, a "MSMail"-scoped filter would still be sent
                        // into the "Teams" iteration's search session too.
                        if (!string.IsNullOrEmpty(table) && !string.Equals(table, primaryTable, StringComparison.OrdinalIgnoreCase))
                            continue;
                        string resolved = await resolver.ResolveAsync(string.IsNullOrEmpty(table) ? primaryTable : table, rawColumn).ConfigureAwait(false);
                        list.Add(new SearchTerm(table, resolved ?? "", term));
                    }
                }
                return list;
            }

            if (filtersToken is JObject jo)
            {
                foreach (JProperty p in jo.Properties())
                {
                    var term = p.Value?.ToString();
                    if (string.IsNullOrEmpty(term))
                        continue;
                    string resolved = await resolver.ResolveAsync(primaryTable, p.Name).ConfigureAwait(false);
                    list.Add(new SearchTerm("", resolved ?? "", term));
                }
            }

            return list;
        }

        /// <summary>
        /// Build display columns from explicit caller input, resolved to each column's INTERNAL
        /// name: X1's ItemList mechanism (which backs both displayFields and sort - see
        /// BuildSortColumnsAsync) indexes by internal field name, the opposite convention from
        /// AddTermsByName (which backs filters and needs the DISPLAY name - see BuildTermsAsync).
        /// Passing a display name through unresolved here produces "Cannot find field '...'"
        /// server-side and silently empty results - confirmed via X1ServiceHost's own log.
        ///
        /// A column ResolveInternalAsync can't resolve (even after a schema refresh) falls back to
        /// the caller's raw string rather than erroring: unlike a filter, there is no safe universal
        /// substitute for a display/sort column, and the raw string may already be the correct
        /// internal name - this is never worse than the previous unconditional passthrough.
        ///
        /// Returns Column[0] when none supplied — the service then returns bare results
        /// (uri/table/keywords) which works safely across all table types.
        /// </summary>
        public static async Task<Column[]> BuildDisplayColumnsAsync(ColumnNameResolver resolver, string primaryTable, JToken displayFieldsToken)
        {
            displayFieldsToken = CoerceJson(displayFieldsToken);
            if (displayFieldsToken is JArray a && a.Count > 0)
            {
                var cols = new Column[a.Count];
                for (int i = 0; i < a.Count; i++)
                {
                    string raw = a[i].ToString();
                    string resolved = await resolver.ResolveInternalAsync(primaryTable, raw).ConfigureAwait(false);
                    cols[i] = new Column("", resolved ?? raw);
                }
                return cols;
            }

            // No columns: service returns bare results (uri/table/keywords) — safe for all tables
            return new Column[0];
        }

        /// <summary>
        /// Resolved to each column's INTERNAL name - see BuildDisplayColumnsAsync for why, and for
        /// the raw-string fallback rationale on an unresolvable column. X1ServiceHost's own log
        /// confirms the failure mode this fixes: passing a display name ("Received") as a sort
        /// column produced "[ERROR] ItemList - Cannot find field 'Received'" on every single
        /// GetSearchResults call, silently returning zero rows.
        /// </summary>
        public static async Task<SortColumn[]> BuildSortColumnsAsync(ColumnNameResolver resolver, string primaryTable, JToken sortToken)
        {
            sortToken = CoerceJson(sortToken);
            if (!(sortToken is JArray a) || a.Count == 0)
                return new SortColumn[0];

            var list = new List<SortColumn>();
            foreach (JToken item in a)
            {
                if (item is JObject o)
                {
                    string col = o.Value<string>("column") ?? o.Value<string>("name");
                    string table = o.Value<string>("table") ?? "";
                    if (string.IsNullOrEmpty(col))
                        continue;
                    // See the matching check in BuildTermsAsync - a sort column explicitly scoped
                    // to a different table than the one this call is searching doesn't apply here.
                    if (!string.IsNullOrEmpty(table) && !string.Equals(table, primaryTable, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string resolved = await resolver.ResolveInternalAsync(string.IsNullOrEmpty(table) ? primaryTable : table, col).ConfigureAwait(false);
                    list.Add(new SortColumn { table = table, name = resolved ?? col, direction = ParseDirection(o.Value<string>("direction")) });
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// Maps a free-form direction string to X1's SortDirection. X1 only has Forwards
        /// (ascending) and Backwards (descending), but callers reach for many spellings, so accept
        /// the common ones: anything that reads as "descending / newest first" -> Backwards;
        /// everything else (including null) -> Forwards (the ascending default).
        /// </summary>
        public static SortDirection ParseDirection(string direction)
        {
            var d = (direction ?? "").Trim().ToLowerInvariant();
            bool descending =
                d.StartsWith("back") ||   // backwards
                d.StartsWith("desc") ||   // desc / descending
                d.StartsWith("down") ||
                d == "-" || d == "z-a" || d == "9-0" || d == "newest" || d == "latest";
            return descending ? SortDirection.Backwards : SortDirection.Forwards;
        }
    }
}
