// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Newtonsoft.Json.Linq;
using X1.Service;

namespace X1.McpBridge
{
    /// <summary>
    /// XS-1678: thrown when <c>CreateSearchSession</c> returns the <c>-1</c> sentinel the server
    /// added in XS-1676 - the requested table isn't available on this connection's license tier
    /// (files-only) rather than any transport/availability failure (which returns <c>0</c> instead,
    /// see the sibling check next to every throw site). Mirrors <see cref="X1McpUnlicensedException"/>'s
    /// role for the older, unrelated whole-plugin-unlicensed gate (XS-1671).
    /// </summary>
    internal sealed class X1McpFilesOnlyLicenseException : Exception
    {
        public X1McpFilesOnlyLicenseException(string table)
            : base(BridgeConstants.FilesOnlyTableRejection(table))
        {
        }
    }

    internal sealed class SearchBridge : IDisposable
    {
        private static readonly ILog Log = BridgeLogger.GetLogger(typeof(SearchBridge));
        private static int nextRequestId = 1;

        // Extra results to fetch beyond `limit` when a sort is requested, so a pinned/displaced
        // "current item" can be corrected by the bridge-side re-sort (see SearchAsync).
        private const int SortOverfetch = 10;

        // Retry pacing for "the service knows the count but a GetSearchResults page came back
        // empty/not-yet-ready" - used both by the count-only recovery path and the normal paging
        // loop in SearchAsync. XS-1694: retries continue until the caller's own timeoutMs budget
        // is nearly exhausted (see RemainingBudgetMs/MinRetryBudgetMs below), not a fixed count -
        // a fixed ~3-attempt cap gave up on slow/large tables in under 1.2s regardless of how much
        // of a generous timeoutMs (default 60000ms) remained unused, which traced back to this
        // ticket's "byTable count correct, rows missing" symptom.
        private const int EmptyRetryDelayMs = 300;
        private const int MinRetryBudgetMs = 500;

        /// <summary>
        /// Milliseconds left in the caller's timeoutMs budget since searchStart. Pure (no WCF/IO),
        /// so the budget arithmetic itself is directly unit-testable - see
        /// SearchBridgeRetryBudgetTests - without needing a live search session.
        /// </summary>
        internal static int RemainingBudgetMs(DateTime searchStart, int timeoutMs) =>
            timeoutMs - (int)(DateTime.UtcNow - searchStart).TotalMilliseconds;

        private readonly SearchManagerCallbacks _callbacks = new SearchManagerCallbacks();
        private readonly ColumnNameResolver _columnResolver;
        private readonly TableSchemaResolver _tableResolver;
        // XS-1678: distinct from _connection below (the search-manager duplex channel) - this is
        // the IX1MCPService connection, needed only to ask IsFullSuiteLicensed() before allowing
        // the arbitrary-file (not-in-index) extract/export operations. Null in tests that construct
        // a SearchBridge with no live service connection; treated as "allow" in that case, mirroring
        // TableSchemaResolver.RebuildAsync's "no connection -> nothing to validate against" seam.
        private readonly X1MCPServiceConnection _serviceConnection;
        private X1MCPSearchConnection _connection;
        private readonly object _gate = new object();

        public SearchBridge(ColumnNameResolver columnResolver, TableSchemaResolver tableResolver,
            X1MCPServiceConnection serviceConnection = null)
        {
            _columnResolver = columnResolver;
            _tableResolver = tableResolver;
            _serviceConnection = serviceConnection;
        }

        private IX1MCPSearchManager Channel
        {
            get
            {
                lock (_gate)
                {
                    if (_connection == null)
                        _connection = new X1MCPSearchConnection(_callbacks);
                    return _connection.GetChannel();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _connection?.Dispose();
                _connection = null;
            }
        }

        /// <summary>
        /// XS-1694: resolves the requested tables and dispatches to a single-table search (the common
        /// case, byte-identical to this method's pre-fan-out behavior) or a multi-table fan-out.
        /// Fan-out runs N single-table searches sequentially and merges them - see SearchMultiTableAsync
        /// for why this can never pass more than one table into a single CreateSearchSession call, and
        /// why the fan-out itself must stay sequential rather than parallel. Re-enabled after the real
        /// root cause (SearchSingleTableAsync's retry loops giving up after a fixed ~1s regardless of
        /// timeoutMs, abandoning a slow table's session while it might still be materializing - see
        /// RemainingBudgetMs) was fixed; that fix stands on its own and protects single-table calls too.
        /// </summary>
        public async Task<JObject> SearchAsync(string query, JToken tablesToken, bool progenitorSearch, int limit,
            bool includeSnippets, bool includeActions, JToken filters, JToken displayFields, JToken sortColumns, int timeoutMs)
        {
            if (limit < 1)
                limit = 20;
            if (limit > 500)
                limit = 500;

            string[] tables = await ResolveTablesAsync(tablesToken).ConfigureAwait(false);

            if (tables.Length <= 1)
            {
                string table = tables.Length > 0 ? tables[0] : "";
                return await SearchSingleTableAsync(table, query, progenitorSearch, limit, includeSnippets,
                    includeActions, filters, displayFields, sortColumns, timeoutMs).ConfigureAwait(false);
            }

            return await SearchMultiTableAsync(tables, query, progenitorSearch, limit, includeSnippets,
                includeActions, filters, displayFields, sortColumns, timeoutMs).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs N single-table searches sequentially (one per requested table) and merges the
        /// results. MUST stay sequential (never Task.WhenAll/Parallel): CallTool wraps the whole
        /// tool dispatch in X1ConcurrencyWorkaround.RunSerialized specifically because concurrent
        /// calls into the X1 WCF service crash X1ServiceHost ("confirmed reproducible via concurrent
        /// x1_search calls alone" - see X1ConcurrencyWorkaround.cs). SearchAsync itself doesn't
        /// re-acquire that gate, so a parallel fan-out here wouldn't deadlock - it would silently
        /// recreate the exact crash the gate exists to prevent. A plain sequential loop keeps "only
        /// one X1 call in flight at a time" true with no extra locking needed.
        ///
        /// A table search that fails after its NAME already resolved cleanly (WCF error, timeout
        /// specific to that table, etc.) is recorded as that table's error in the merged response's
        /// byTable entry rather than aborting the remaining tables - see MergeTableResults. Table-name
        /// resolution failures happen earlier, in ResolveTablesAsync, and still abort the whole call.
        /// </summary>
        private async Task<JObject> SearchMultiTableAsync(string[] tables, string query, bool progenitorSearch, int limit,
            bool includeSnippets, bool includeActions, JToken filters, JToken displayFields, JToken sortColumns, int timeoutMs)
        {
            var outcomes = new List<TableSearchOutcome>(tables.Length);

            foreach (string table in tables)
            {
                try
                {
                    JObject result = await SearchSingleTableAsync(table, query, progenitorSearch, limit,
                        includeSnippets, includeActions, filters, displayFields, sortColumns, timeoutMs)
                        .ConfigureAwait(false);
                    outcomes.Add(new TableSearchOutcome(table, result, null));
                }
                catch (Exception ex)
                {
                    Log.Warn("SearchAsync (multi-table fan-out): table '" + table + "' failed: " + ex.Message);
                    outcomes.Add(new TableSearchOutcome(table, null, ex.Message));
                }
            }

            return MergeTableResults(outcomes);
        }

        /// <summary>
        /// Runs one single-table search using the session lifecycle X1 has always supported. This is
        /// the extracted body of what was, before multi-table fan-out, the entirety of SearchAsync -
        /// single-table callers (the overwhelming majority) get byte-identical behavior via
        /// SearchAsync's dispatcher calling this once and returning its result as-is.
        /// </summary>
        private async Task<JObject> SearchSingleTableAsync(string table, string query, bool progenitorSearch, int limit,
            bool includeSnippets, bool includeActions, JToken filters, JToken displayFields, JToken sortColumns, int timeoutMs)
        {
            var searchTerms = (await FilterMapper.BuildTermsAsync(_columnResolver, table, query ?? "", filters).ConfigureAwait(false)).ToArray();
            if (searchTerms.Length == 1 && string.IsNullOrEmpty(searchTerms[0].term) && searchTerms[0].columnName == "")
                throw new ArgumentException("query or filters must supply at least one search term.");

            var displayCols = await FilterMapper.BuildDisplayColumnsAsync(_columnResolver, table, displayFields).ConfigureAwait(false);
            // When no explicit displayFields are given, fall back to the config columns for this
            // table so that fields like x1tag are returned without the caller having to ask.
            // This mirrors what GetSearchResults already does via BridgeConfig.GetColumnsForTable.
            if (displayCols.Length == 0)
            {
                var configured = BridgeConfig.GetColumnsForTable(table);
                if (configured.Length > 0)
                    displayCols = Array.ConvertAll(configured, c => new Column("", c));
            }
            var sortCols = await FilterMapper.BuildSortColumnsAsync(_columnResolver, table, sortColumns).ConfigureAwait(false);
            bool sortRequested = sortCols.Length > 0;
            // X1 can pin a "current/tracked" item to the top of the returned window regardless of the
            // requested sort (it even displaces a legitimately-ranked result out of the window). When
            // a sort is requested we therefore over-fetch a small buffer and re-sort the page in the
            // bridge so the order is correct. For that we must actually fetch the sort columns, so
            // make sure they're in the display set (this also forces fields to be returned when the
            // caller asked for bare results).
            displayCols = EnsureSortColumnsFetched(displayCols, sortCols);
            var mergeCols = new MergeColumn[0];
            int pageSize = Math.Max(limit, 50);

            // XS-1578: query text must never appear here - Verbosity=DEBUG is routinely turned on
            // for unrelated support requests, and a customer asked to send debug logs should never
            // be surprised to find their search terms in them. Query content is only ever logged
            // behind the dedicated, off-by-default X1McpQueryLog flag (see McpServer.LogPerf).
            Log.Debug("SearchAsync table=" + table + " limit=" + limit);
            IX1MCPSearchManager ch = Channel;
            int sessionId = -1;
            // getKeywordStats drives population of SearchResult.keywords (used below to build
            // the "snippet" field) — only request it when the caller actually wants snippets,
            // per Jogy's guidance that keyword-stat gathering costs extra processing.
            // A one-element array literal here is deliberate and load-bearing: X1 never fires
            // OnSearchResultsChanged when CreateSearchSession is given more than one table (a
            // confirmed X1 service-side defect - see X1TimeoutScenariosTests.cs's Scenario 2), so
            // this call must never receive more than the single table this method is searching.
            sessionId = ch.CreateSearchSession(new[] { table }, progenitorSearch, getKeywordStats: includeSnippets);
            // XS-1678: this is a backstop for BridgeConfig.GetDefaultTables()'s "trusted as-is,
            // not re-validated" path (see ResolveTablesAsync) - a caller-supplied table has already
            // been resolved/validated by TableSchemaResolver before reaching here. See
            // ThrowIfSessionCreationFailed for what -1 vs. 0 means.
            ThrowIfSessionCreationFailed(sessionId, table);
            _callbacks.RegisterSession(sessionId);
            try
            {
                ch.SetSearchTerms(sessionId, searchTerms, sortCols, displayCols, mergeCols, pageSize);

                var searchStart = DateTime.UtcNow;
                var changedTask = _callbacks.WaitResultsChangedAsync(sessionId);
                var anyCallbackTask = _callbacks.WaitAnyCallbackAsync(sessionId);
                // Race: either a data-bearing callback completes changedTask, any callback
                // (including count-only) completes anyCallbackTask, or the timeout fires.
                // Using anyCallbackTask lets us enter the GetSearchResults recovery path
                // immediately on a count-only signal rather than burning the full timeout first.
                var completed = await Task.WhenAny(changedTask, anyCallbackTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != changedTask)
                {
                    // Either a count-only callback arrived (anyCallbackTask) or the timeout fired.
                    // In both cases attempt recovery via GetSearchResults if a pending count exists.
                    if (_callbacks.TryGetPendingCount(sessionId, out int pendingTotal, out var pendingHighlight))
                    {
                        Log.Debug("SearchAsync count-only callback received (totalResults=" + pendingTotal + "); fetching first page via GetSearchResults");

                        // A sorted search always lands here on a cold/small result set (it deliberately
                        // ignores any first page bundled with the initial callback - see the sortRequested
                        // check below changedTask - so it depends entirely on this explicit fetch). A single
                        // attempt was prone to a false "no results" when X1 hadn't finished materializing the
                        // page yet, even though the count was already known. Retry with the same short-pause
                        // pattern the changedTask path below already uses, rather than giving up after one try.
                        // XS-1694: bounded only by the caller's own timeoutMs (via the remainingMs check
                        // below), not a fixed attempt count - a fixed ~1s cap gave up on slow/large tables
                        // long before a generous timeoutMs was exhausted, which is the root cause this
                        // ticket traced the "byTable count correct, rows missing" symptom back to.
                        SearchResult[] firstPage = null;
                        for (int attempt = 0; firstPage == null; attempt++)
                        {
                            if (attempt > 0)
                                await Task.Delay(EmptyRetryDelayMs).ConfigureAwait(false);

                            int remainingMs = RemainingBudgetMs(searchStart, timeoutMs);
                            if (remainingMs <= MinRetryBudgetMs)
                                break;

                            int rid = Interlocked.Increment(ref nextRequestId);
                            var readyTask = _callbacks.WaitResultsReadyAsync(sessionId, rid);
                            ch.GetSearchResults(sessionId, rid, 0, pageSize);
                            var readyDone = await Task.WhenAny(readyTask, Task.Delay(remainingMs)).ConfigureAwait(false);
                            if (readyDone != readyTask)
                                break; // out of overall search budget entirely - no point retrying further

                            var page = await readyTask.ConfigureAwait(false);
                            if (page != null && page.Length > 0)
                                firstPage = page;
                        }

                        if (firstPage != null)
                        {
                            // Got results — build a synthetic ChangedArgs and fall through to normal result building
                            var pendingHighlightArr = pendingHighlight ?? new HighlightTerm[0];
                            var recoveryHighlight = new JArray();
                            foreach (HighlightTerm h in pendingHighlightArr)
                                recoveryHighlight.Add(new JObject { ["term"] = h.term, ["column"] = h.column, ["findType"] = h.findType });

                            // Enforce the requested order here too (this page is GetSearchResults,
                            // which can still lead with a pinned item on a warm service).
                            var pageList = new List<SearchResult>(firstPage);
                            if (sortRequested)
                            {
                                DedupByUri(pageList);
                                ApplySortClientSide(pageList, sortCols, displayCols);
                            }

                            var recoveryResults = new JArray();
                            int recoveryTake = Math.Min(limit, pageList.Count);
                            for (int i = 0; i < recoveryTake; i++)
                            {
                                var r = pageList[i];
                                var jo = new JObject { ["uri"] = r.uri, ["table"] = r.table, ["keywords"] = r.keywords };
                                if (includeActions)
                                    jo["actions"] = ActionRegistry.GetActionsJson(r.table);
                                if (r.fields != null)
                                {
                                    var fieldsObj = new JObject();
                                    for (int f = 0; f < r.fields.Length && f < displayCols.Length; f++)
                                        fieldsObj[displayCols[f].name] = r.fields[f];
                                    jo["fields"] = fieldsObj;
                                }
                                if (includeSnippets && !string.IsNullOrEmpty(r.keywords))
                                    jo["snippet"] = r.keywords;
                                recoveryResults.Add(jo);
                            }

                            Log.Debug("SearchAsync count-only recovery complete: totalResults=" + pendingTotal + " returned=" + recoveryTake);
                            MaybePrefetchPreviews(recoveryResults);
                            return new JObject
                            {
                                ["totalResults"] = pendingTotal,
                                ["returned"] = recoveryTake,
                                ["results"] = recoveryResults,
                                ["highlightTerms"] = recoveryHighlight
                            };
                        }

                        // Could not retrieve first page even after retries — return count with empty results
                        Log.Warn("SearchAsync count-only: could not retrieve first page after retries; returning count only");
                        var emptyHighlight = new JArray();
                        if (pendingHighlight != null)
                            foreach (HighlightTerm h in pendingHighlight)
                                emptyHighlight.Add(new JObject { ["term"] = h.term, ["column"] = h.column, ["findType"] = h.findType });
                        return new JObject
                        {
                            ["totalResults"] = pendingTotal,
                            ["returned"] = 0,
                            ["results"] = new JArray(),
                            ["highlightTerms"] = emptyHighlight
                        };
                    }
                    // Distinguish 0-result callback from genuine timeout:
                    // anyCallbackTask fires whenever OnSearchResultsChanged fires (even for 0 results).
                    // If it completed, a callback arrived — the search succeeded with 0 results.
                    if (completed == anyCallbackTask)
                    {
                        Log.Debug("SearchAsync 0-result callback received; returning empty results");
                        return new JObject
                        {
                            ["totalResults"] = 0,
                            ["returned"] = 0,
                            ["results"] = new JArray(),
                            ["highlightTerms"] = new JArray()
                        };
                    }
                    throw new TimeoutException("Search did not return results within " + timeoutMs + " ms.");
                }

                var changed = await changedTask.ConfigureAwait(false);
                var all = new List<SearchResult>();

                // When a sort is requested, ignore the callback's first page (X1 can lead it with a
                // pinned "current" item) and pull a clean window via GetSearchResults below, over-
                // fetching a small buffer so a pinned/displaced item gets corrected by the re-sort.
                if (!sortRequested && changed.FirstPage != null)
                    all.AddRange(changed.FirstPage);
                int fetchTarget = sortRequested
                    ? Math.Min(changed.TotalResults, limit + SortOverfetch)
                    : limit;

                while (all.Count < fetchTarget && all.Count < changed.TotalResults)
                {
                    int start = all.Count;
                    int need = Math.Min(fetchTarget - all.Count, pageSize);
                    int rid = Interlocked.Increment(ref nextRequestId);
                    var readyTask = _callbacks.WaitResultsReadyAsync(sessionId, rid);
                    ch.GetSearchResults(sessionId, rid, start, need);
                    var done = await Task.WhenAny(readyTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                    if (done != readyTask)
                        break;
                    var page = await readyTask.ConfigureAwait(false);
                    if (page == null || page.Length == 0)
                    {
                        // Service responded but has no items yet — retry with a short pause.
                        // XS-1694: bounded by the caller's own timeoutMs (via remainingMs), not a fixed
                        // retry count - see the identical fix and rationale on the count-only recovery
                        // loop above.
                        bool fetched = false;
                        while (true)
                        {
                            int remainingMs = RemainingBudgetMs(searchStart, timeoutMs);
                            if (remainingMs <= MinRetryBudgetMs)
                                break;
                            await Task.Delay(EmptyRetryDelayMs).ConfigureAwait(false);
                            remainingMs = RemainingBudgetMs(searchStart, timeoutMs);
                            if (remainingMs <= MinRetryBudgetMs)
                                break;

                            int retryRid = Interlocked.Increment(ref nextRequestId);
                            var retryTask = _callbacks.WaitResultsReadyAsync(sessionId, retryRid);
                            ch.GetSearchResults(sessionId, retryRid, start, need);
                            var retryDone = await Task.WhenAny(retryTask, Task.Delay(remainingMs)).ConfigureAwait(false);
                            if (retryDone != retryTask)
                                break;
                            var retryPage = await retryTask.ConfigureAwait(false);
                            if (retryPage != null && retryPage.Length > 0)
                            {
                                all.AddRange(retryPage);
                                fetched = true;
                                break;
                            }
                        }
                        if (!fetched)
                            break;
                        continue;
                    }
                    all.AddRange(page);
                }

                // Safety net: if the explicit sorted fetch came back empty (a rare race where the
                // callback fired but GetSearchResults isn't ready), fall back to the callback's first
                // page so we still return results rather than nothing.
                if (sortRequested && all.Count == 0 && changed.FirstPage != null && changed.FirstPage.Length > 0)
                    all.AddRange(changed.FirstPage);

                // Enforce the requested order in the bridge: dedup, then re-sort the over-fetched
                // window so any pinned/out-of-order item lands in its correct place (or falls out of
                // the top `limit`).
                if (sortRequested)
                {
                    DedupByUri(all);
                    ApplySortClientSide(all, sortCols, displayCols);
                }

                var resultsArray = new JArray();
                int take = Math.Min(limit, all.Count);
                for (int i = 0; i < take; i++)
                {
                    var r = all[i];
                    var jo = new JObject
                    {
                        ["uri"] = r.uri,
                        ["table"] = r.table,
                        ["keywords"] = r.keywords
                    };
                    if (includeActions)
                        jo["actions"] = ActionRegistry.GetActionsJson(r.table);
                    if (r.fields != null)
                    {
                        var fieldsObj = new JObject();
                        for (int f = 0; f < r.fields.Length && f < displayCols.Length; f++)
                            fieldsObj[displayCols[f].name] = r.fields[f];
                        jo["fields"] = fieldsObj;
                    }
                    if (includeSnippets && !string.IsNullOrEmpty(r.keywords))
                        jo["snippet"] = r.keywords;
                    resultsArray.Add(jo);
                }

                var highlight = new JArray();
                if (changed.HighlightTerms != null)
                {
                    foreach (HighlightTerm h in changed.HighlightTerms)
                    {
                        highlight.Add(new JObject
                        {
                            ["term"] = h.term,
                            ["column"] = h.column,
                            ["findType"] = h.findType
                        });
                    }
                }

                Log.Debug("SearchAsync complete: totalResults=" + changed.TotalResults + " returned=" + take);
                MaybePrefetchPreviews(resultsArray);
                return new JObject
                {
                    ["totalResults"] = changed.TotalResults,
                    ["returned"] = take,
                    ["results"] = resultsArray,
                    ["highlightTerms"] = highlight
                };
            }
            finally
            {
                if (sessionId >= 0)
                {
                    try
                    {
                        ch.DestroySearchSession(sessionId);
                    }
                    catch
                    {
                        // ignore
                    }
                    _callbacks.UnregisterSession(sessionId);
                }
            }
        }

        /// <summary>
        /// Resolves each caller-supplied table token (scanner name, per-account displayName, or an
        /// already-correct schema) to the one schema value the underlying WCF call actually needs
        /// (XS-1640). Fails fast with a descriptive ArgumentException naming the bad/ambiguous token
        /// on the first one that doesn't resolve, rather than letting a wrong name reach the service
        /// as an opaque failure. The operator-configured default (BridgeConfig.GetDefaultTables(),
        /// used when the caller omits tables entirely) is trusted as-is and NOT re-validated here -
        /// it's operator config, not caller input.
        /// </summary>
        private async Task<string[]> ResolveTablesAsync(JToken tablesToken)
        {
            if (tablesToken == null || tablesToken.Type == JTokenType.Null)
                return BridgeConfig.GetDefaultTables();

            // Same JSON-string coercion FilterMapper applies to filters/displayFields/sort - some
            // MCP clients (and, confirmed in practice, an LLM caller typing the argument by hand)
            // send "tables" as a bare or stringified value rather than a real array.
            JToken coerced = FilterMapper.CoerceJson(tablesToken);

            JArray a;
            if (coerced is JArray arr)
            {
                a = arr;
            }
            else if (coerced.Type == JTokenType.String && !string.IsNullOrWhiteSpace(coerced.ToString()))
            {
                // The single most common shape mistake: "tables": "MSMail" instead of ["MSMail"].
                // Previously this fell through to BridgeConfig.GetDefaultTables() - indistinguishable
                // from tables being omitted entirely, so a misshapen-but-present value silently
                // searched the wrong table with no error. Treat it as a one-element list instead.
                Log.Warn("SearchBridge: 'tables' was a bare string ('" + coerced +
                          "') instead of an array - treating it as a single-element list.");
                a = new JArray(coerced);
            }
            else
            {
                return BridgeConfig.GetDefaultTables();
            }

            if (a.Count == 0)
                return BridgeConfig.GetDefaultTables();

            var resolved = new string[a.Count];
            for (int i = 0; i < a.Count; i++)
                resolved[i] = await _tableResolver.ResolveOrThrowAsync(a[i].ToString()).ConfigureAwait(false);
            return resolved;
        }

        // ── Multi-table fan-out merge ─────────────────────────────────────────────

        /// <summary>Per-table outcome of one SearchMultiTableAsync loop iteration: either a built
        /// result JObject (Error null), or an error message (Result null). Never both.</summary>
        internal readonly struct TableSearchOutcome
        {
            public readonly string Table;
            public readonly JObject Result;
            public readonly string Error;

            public TableSearchOutcome(string table, JObject result, string error)
            {
                Table = table;
                Result = result;
                Error = error;
            }
        }

        /// <summary>
        /// Pure aggregation of already-completed per-table search outcomes into one merged response.
        /// No WCF/async involved, so this is independently unit-testable with hand-built JObjects (see
        /// SearchBridgeMultiTableMergeTests). Order-preserving: results/byTable entries appear in the
        /// same order as <paramref name="outcomes"/> (i.e. the order the caller's tables were listed) -
        /// all of one table's results, then the next table's, never interleaved.
        ///
        /// Rows are deliberately jagged: each result's "fields" object reflects whatever displayFields
        /// resolved against THAT result's own table (already resolved per-table by SearchSingleTableAsync
        /// via FilterMapper), which can legitimately differ from another table's fields shape. This is
        /// not normalized into a common column set - an intersect-only shape would drop data valid for
        /// other tables, and a union-with-nulls shape would pad in meaningless empty fields. Every result
        /// already carries its own "table" field (from the underlying X1 service), which is the
        /// discriminator a caller uses to interpret a jagged row correctly.
        /// </summary>
        internal static JObject MergeTableResults(IReadOnlyList<TableSearchOutcome> outcomes)
        {
            var mergedResults = new JArray();
            var mergedHighlights = new JArray();
            var seenHighlights = new HashSet<string>(StringComparer.Ordinal);
            var byTable = new JArray();
            int totalResults = 0;
            int totalReturned = 0;

            foreach (var o in outcomes)
            {
                var entry = new JObject { ["table"] = o.Table };

                if (o.Error != null)
                {
                    entry["totalResults"] = 0;
                    entry["returned"] = 0;
                    entry["error"] = o.Error;
                    byTable.Add(entry);
                    continue;
                }

                int tableTotal = o.Result.Value<int?>("totalResults") ?? 0;
                int tableReturned = o.Result.Value<int?>("returned") ?? 0;
                totalResults += tableTotal;
                totalReturned += tableReturned;
                entry["totalResults"] = tableTotal;
                entry["returned"] = tableReturned;
                byTable.Add(entry);

                if (o.Result["results"] is JArray tableResults)
                    foreach (JToken item in tableResults)
                        mergedResults.Add(item.DeepClone());

                if (o.Result["highlightTerms"] is JArray tableHighlights)
                    foreach (JToken h in tableHighlights)
                    {
                        string key = h.ToString(Newtonsoft.Json.Formatting.None);
                        if (seenHighlights.Add(key))
                            mergedHighlights.Add(h.DeepClone());
                    }
            }

            return new JObject
            {
                ["totalResults"] = totalResults,
                ["returned"] = totalReturned,
                ["results"] = mergedResults,
                ["highlightTerms"] = mergedHighlights,
                ["byTable"] = byTable
            };
        }

        // ── Sort enforcement ──────────────────────────────────────────────────────

        /// <summary>
        /// Ensures every requested sort column is part of the fetched display columns, so the bridge
        /// actually receives the values it needs to re-sort by. Appends any missing sort column.
        /// </summary>
        internal static Column[] EnsureSortColumnsFetched(Column[] displayCols, SortColumn[] sortCols)
        {
            if (sortCols.Length == 0)
                return displayCols;

            var list = new List<Column>(displayCols ?? new Column[0]);
            foreach (var sc in sortCols)
            {
                if (string.IsNullOrEmpty(sc.name))
                    continue;
                if (!list.Any(c => string.Equals(c.name, sc.name, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new Column("", sc.name));
            }
            return list.ToArray();
        }

        /// <summary>Removes duplicate results by URI, keeping the first occurrence.</summary>
        private static void DedupByUri(List<SearchResult> all)
        {
            if (all == null || all.Count < 2)
                return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int w = 0;
            for (int r = 0; r < all.Count; r++)
            {
                if (seen.Add(all[r].uri ?? ""))
                    all[w++] = all[r];
            }
            if (w < all.Count)
                all.RemoveRange(w, all.Count - w);
        }

        /// <summary>
        /// Re-sorts results in the bridge by the requested sort columns. Each column is located in
        /// the display layout (so its value is available in r.fields); numeric values (e.g. OA dates)
        /// compare numerically, otherwise lexically. Backwards == descending.
        /// </summary>
        internal static void ApplySortClientSide(List<SearchResult> all, SortColumn[] sortCols, Column[] displayCols)
        {
            if (all == null || all.Count < 2 || sortCols.Length == 0)
                return;

            var keys = new List<KeyValuePair<int, bool>>();   // fieldIndex -> descending?
            foreach (var sc in sortCols)
            {
                int idx = Array.FindIndex(displayCols, c => string.Equals(c.name, sc.name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    keys.Add(new KeyValuePair<int, bool>(idx, sc.direction == SortDirection.Backwards));
            }
            if (keys.Count == 0)
                return;   // sort column not among fetched fields — leave the service order

            all.Sort((x, y) =>
            {
                foreach (var k in keys)
                {
                    string xv = (x.fields != null && k.Key < x.fields.Length) ? x.fields[k.Key] : null;
                    string yv = (y.fields != null && k.Key < y.fields.Length) ? y.fields[k.Key] : null;
                    int cmp = CompareFieldValues(xv, yv);
                    if (cmp != 0)
                        return k.Value ? -cmp : cmp;
                }
                return 0;
            });
        }

        private static int CompareFieldValues(string a, string b)
        {
            bool ae = string.IsNullOrEmpty(a), be = string.IsNullOrEmpty(b);
            if (ae && be) return 0;
            if (ae) return -1;   // empty sorts as the smallest value
            if (be) return 1;
            if (double.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out double ad) &&
                double.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out double bd))
                return ad.CompareTo(bd);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // Cloud-file connectors whose previews are produced by downloading the actual file to a
        // local cache. Prefetching warms that cache so a later preview/open is instant rather than
        // racing the OAuth token check and download on the first attempt. Restricted to OneDrive and
        // GDrive per the UX plan — these are the connectors with the synchronous-token-check problem.
        private static readonly HashSet<string> PrefetchableTables =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OneDrive", "GDrive" };

        // Hard cap on prefetch fan-out per search: the same WCF channel serves search, so an
        // unbounded number of concurrent GeneratePreview calls would starve it.
        private const int MaxPrefetchPerSearch = 3;

        /// <summary>
        /// Fires background preview generation for the top OneDrive/GDrive results so the connector
        /// downloads and caches each file before the user asks to preview or open it. Marks each
        /// result for which prefetch fired with <c>prefetchInitiated: true</c>. Best-effort:
        /// failures are swallowed and never affect the search response.
        /// </summary>
        private void MaybePrefetchPreviews(JArray results)
        {
            if (results == null || results.Count == 0)
                return;
            int budget = Math.Min(BridgeConfig.GetPrefetchPreviewCount(), MaxPrefetchPerSearch);
            if (budget <= 0)
                return;

            int fired = 0;
            foreach (JToken item in results)
            {
                if (fired >= budget)
                    break;
                var jo = item as JObject;
                if (jo == null)
                    continue;
                string table = jo.Value<string>("table");
                string uri = jo.Value<string>("uri");
                if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(uri))
                    continue;
                if (!PrefetchableTables.Contains(table))
                    continue;

                PrefetchPreviewAsync(table, uri);
                jo["prefetchInitiated"] = true;
                fired++;
            }
        }

        /// <summary>
        /// Fire-and-forget-with-cleanup: kicks off a preview on a background thread so the cloud
        /// connector downloads/caches the file, then releases the wait registration in a finally
        /// block so an orphaned key never makes a later real preview call wait the full timeout.
        /// </summary>
        private void PrefetchPreviewAsync(string table, string uri)
        {
            IX1MCPSearchManager ch;
            try
            {
                ch = Channel;
            }
            catch (Exception ex)
            {
                Log.Debug("PrefetchPreviewAsync channel unavailable for " + table + "/" + uri + ": " + ex.Message);
                return;
            }

            // XS-1583-CONCURRENCY-WORKAROUND: this fire-and-forget task outlives the CallTool
            // dispatch that triggered it (SearchAsync returns before this completes), so it must
            // take the same serialization gate itself — otherwise it can still race a later
            // tool call's X1 service access. See X1ConcurrencyWorkaround.cs.
            var ignored = X1ConcurrencyWorkaround.RunSerializedAsync(async () =>
            {
                string key = _callbacks.ExpectPreview(uri);
                try
                {
                    ch.GeneratePreview(table, uri, false, null);
                    await _callbacks.WaitPreviewAsync(key, 60000).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Debug("PrefetchPreviewAsync failed for " + table + "/" + uri + ": " + ex.Message);
                }
                finally
                {
                    _callbacks.CancelPreviewWait(key);
                }
            });
            GC.KeepAlive(ignored);

            Log.Debug("PrefetchPreviewAsync fired for " + table + "/" + uri);
        }

        public async Task<JObject> GetMetadataAsync(string table, string uri, JToken fieldsToken, int timeoutMs)
        {
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(uri))
                throw new ArgumentException("table and uri are required.");

            Log.Debug("GetMetadataAsync table=" + table + " uri=" + uri);
            // GetItemInternal is a synchronous WCF call. Run it on a thread-pool thread so
            // the caller can impose a timeout without blocking the message-loop thread.
            // Capture Channel before entering Task.Run to avoid lock re-entrancy issues.
            var ch = Channel;
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                string[] rows;
                try
                {
                    rows = await Task.Run(() => ch.GetItemInternal(table, uri), cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Log.Warn("GetMetadataAsync timed out after " + timeoutMs + " ms: table=" + table + " uri=" + uri);
                    throw new TimeoutException(
                        $"x1_get_metadata did not complete within {timeoutMs} ms.");
                }

                // GetItemInternal returns alternating [fieldName, fieldValue, ...] pairs
                var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (rows != null)
                {
                    for (int i = 0; i + 1 < rows.Length; i += 2)
                        all[rows[i]] = rows[i + 1];
                }

                // Determine which fields to include
                string[] wanted = null;
                if (fieldsToken is JArray fa && fa.Count > 0)
                    wanted = fa.Select(f => f.ToString()).ToArray();
                else
                {
                    var configured = BridgeConfig.GetColumnsForTable(table);
                    if (configured.Length > 0)
                        wanted = configured;
                }

                var result = new JObject();
                if (wanted != null)
                {
                    foreach (string f in wanted)
                    {
                        if (all.TryGetValue(f, out var v))
                            result[f] = v;
                    }
                }
                else
                {
                    foreach (KeyValuePair<string, string> kv in all)
                        result[kv.Key] = kv.Value;
                }

                return new JObject
                {
                    ["table"] = table,
                    ["uri"] = uri,
                    ["fields"] = result
                };
            }
        }

        public async Task<JObject> GetContentAsync(string table, string uri, string mode, int timeoutMs)
        {
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(uri))
                throw new ArgumentException("table and uri are required.");

            // XS-1678: resolve/validate before touching the channel - on a files-only license the
            // server silently drops GetContent/GeneratePreview for a disallowed table (no callback
            // ever fires), which would otherwise hang this call for the full timeout instead of
            // failing fast with a clear message.
            table = await _tableResolver.ResolveOrThrowAsync(table).ConfigureAwait(false);

            Log.Debug("GetContentAsync table=" + table + " uri=" + uri + " mode=" + (mode ?? "auto"));
            var m = (mode ?? "auto").ToLowerInvariant();
            // "extract"/"text" are back-compat aliases for the new "content" mode.
            if (m == "extract" || m == "text") m = "content";
            var ch = Channel;

            if (m == "preview")
            {
                var previewKey = _callbacks.ExpectPreview(uri);
                ch.GeneratePreview(table, uri, false, null);
                var p = await _callbacks.WaitPreviewAsync(previewKey, timeoutMs).ConfigureAwait(false);
                _callbacks.CancelPreviewWait(previewKey);
                if (!string.IsNullOrEmpty(p.Error))
                    return new JObject { ["error"] = p.Error, ["mode"] = "preview" };
                return new JObject
                {
                    ["mode"] = "preview",
                    ["preview"] = p.Preview,
                    ["additionalData"] = p.AdditionalData
                };
            }

            if (m == "content")
            {
                var contentResult = await GetContentTextAsync(ch, table, uri, timeoutMs).ConfigureAwait(false);
                if (contentResult != null) return contentResult;
                return new JObject { ["error"] = "Content extraction failed or timed out.", ["mode"] = "content" };
            }

            if (m == "auto")
            {
                // 1. GetContent — the fastest path when the content store is populated,
                //    and the only mode that returns real text for emails and cloud files.
                var contentResult = await GetContentTextAsync(ch, table, uri, timeoutMs).ConfigureAwait(false);
                if (contentResult != null) return contentResult;

                // 2. Preview — HTML for docx, mail-card for MSMail, image embed / extracted text for files.
                int autoPreviewTimeoutMs = BridgeConfig.GetAutoPreviewTimeoutMs();
                var previewKey = _callbacks.ExpectPreview(uri);
                ch.GeneratePreview(table, uri, false, null);
                var p = await _callbacks.WaitPreviewAsync(previewKey, autoPreviewTimeoutMs).ConfigureAwait(false);
                _callbacks.CancelPreviewWait(previewKey);
                if (string.IsNullOrEmpty(p.Error))
                    return new JObject
                    {
                        ["mode"] = "preview",
                        ["preview"] = p.Preview,
                        ["additionalData"] = p.AdditionalData
                    };

                // 3. Raw index fields — always available.
                var rows = ch.GetItemInternal(table, uri);
                return new JObject
                {
                    ["mode"] = "internal",
                    ["note"] = "Content and preview not available for this item; returning raw index fields.",
                    ["rows"] = rows == null ? new JArray() : JArray.FromObject(rows)
                };
            }

            if (m == "internal")
            {
                var rows = ch.GetItemInternal(table, uri);
                return new JObject { ["mode"] = "internal", ["rows"] = rows == null ? new JArray() : JArray.FromObject(rows) };
            }

            throw new ArgumentException("Unknown mode: use auto, content, preview, or internal.");
        }

        /// <summary>
        /// XS-1575: <c>IX1MCPSearchManager.GetContent</c> — pulls extracted text for any indexed item
        /// (Files, MSMail, Gmail, Exchange, OneDrive, SP365, Teams, …) using the content store
        /// when populated. First call for an item back-fills the store; subsequent calls are effectively free.
        /// Returns null on failure or timeout so the caller can decide how to report it.
        /// </summary>
        private async Task<JObject> GetContentTextAsync(IX1MCPSearchManager ch, string table, string uri, int timeoutMs)
        {
            // As of X1 service 11.0.3.33, GetContent writes the extracted content to outputFile
            // and OnContentReady returns that path — read the file rather than the callback string.
            string outputFile = Path.Combine(Path.GetTempPath(), "x1mcp_content_" + Guid.NewGuid().ToString("N") + ".txt");
            int sessionId = AcquireCallbackSession(ch, table);
            var tcs = _callbacks.ExpectContent(outputFile);
            try
            {
                ch.GetContent(table, uri, outputFile);
                var result = await _callbacks.WaitContentAsync(tcs, timeoutMs).ConfigureAwait(false);
                _callbacks.CancelContentWait(outputFile);

                if (!result.Success || string.IsNullOrEmpty(result.OutputFile) || !File.Exists(result.OutputFile))
                    return null;

                try
                {
                    string text = File.ReadAllText(result.OutputFile, Encoding.UTF8);
                    bool truncated = false;
                    if (text.Length > 512 * 1024)
                    {
                        text = text.Substring(0, 512 * 1024) + "\n... (truncated)";
                        truncated = true;
                    }
                    var payload = new JObject
                    {
                        ["mode"] = "content",
                        ["text"] = text,
                        ["state"] = result.State ?? "",
                        ["cached"] = (result.State ?? "").IndexOf("from index", StringComparison.OrdinalIgnoreCase) >= 0
                    };
                    if (truncated) payload["truncated"] = true;
                    return payload;
                }
                finally
                {
                    try { File.Delete(result.OutputFile); } catch { /* ignore */ }
                }
            }
            finally { ReleaseCallbackSession(ch, sessionId); }
        }

        /// <summary>
        /// XS-1678: the arbitrary-file (not-in-index) Extract Text / Export HTML operations are
        /// gated on <see cref="X1MCPServiceConnection.IsFullSuiteLicensed"/> directly rather than a
        /// table - the server itself gates <c>ExtractTextFromFile</c>/<c>ExportHtmlFromFile</c> on
        /// the overall entitlement (no "Files" table exception applies to these two specifically),
        /// silently dropping the callback when not entitled. Checking here avoids the resulting
        /// full-timeout hang and lets the tier auto-unlock the moment the account is upgraded, with
        /// no connector change needed. <paramref name="mode"/> matches each method's own existing
        /// error-payload <c>"mode"</c> field. Returns false (no error) when <see cref="_serviceConnection"/>
        /// is null - a test-only seam with no live connection to check.
        /// </summary>
        private bool TryBuildArbitraryFileLicenseError(string mode, out JObject error)
        {
            if (_serviceConnection == null || _serviceConnection.IsFullSuiteLicensed())
            {
                error = null;
                return false;
            }

            error = BuildArbitraryFileLicenseError(mode);
            return true;
        }

        /// <summary>
        /// Pure JObject-building half of <see cref="TryBuildArbitraryFileLicenseError"/>, split out
        /// so the exact error shape is unit-testable without a live/faked service connection - see
        /// ArbitraryFileLicenseGateTests.
        /// </summary>
        internal static JObject BuildArbitraryFileLicenseError(string mode) => new JObject
        {
            ["error"] = BridgeConstants.ArbitraryFileFilesOnlyRejection(),
            ["mode"] = mode
        };

        /// <summary>
        /// XS-1575: <c>IX1MCPSearchManager.ExtractTextFromFile</c> — extract text from an arbitrary
        /// local file (not required to be indexed). Server writes to <paramref name="outputFile"/>
        /// and reports the path via <c>OnTextExtracted</c>.
        /// </summary>
        public async Task<JObject> ExtractFileAsync(string file, int timeoutMs)
        {
            if (string.IsNullOrEmpty(file))
                throw new ArgumentException("file is required.");
            if (TryBuildArbitraryFileLicenseError("extract_file", out var licenseError))
                return licenseError;
            if (!File.Exists(file))
                return new JObject { ["error"] = "File not found: " + file };

            string outputFile = Path.Combine(Path.GetTempPath(), "x1mcp_extract_" + Guid.NewGuid().ToString("N") + ".txt");
            Log.Debug("ExtractFileAsync file=" + file + " -> " + outputFile);

            var ch = Channel;
            var tcs = _callbacks.ExpectExtractFile(outputFile);
            ch.ExtractTextFromFile(file, outputFile);
            var result = await _callbacks.WaitExtractFileAsync(tcs, timeoutMs).ConfigureAwait(false);
            _callbacks.CancelExtractFileWait(outputFile);

            if (!result.Success || string.IsNullOrEmpty(result.OutputFile) || !File.Exists(result.OutputFile))
                return new JObject
                {
                    ["error"] = string.IsNullOrEmpty(result.State) ? "Text extraction failed or timed out." : result.State,
                    ["mode"] = "extract_file"
                };
            try
            {
                var text = File.ReadAllText(result.OutputFile, Encoding.UTF8);
                bool truncated = false;
                if (text.Length > 512 * 1024)
                {
                    text = text.Substring(0, 512 * 1024) + "\n... (truncated)";
                    truncated = true;
                }
                var payload = new JObject { ["text"] = text, ["path"] = file };
                if (truncated) payload["truncated"] = true;
                return payload;
            }
            finally
            {
                try { File.Delete(result.OutputFile); } catch { /* ignore */ }
            }
        }

        // ── ExportHtml / ExportHtmlFromFile ──────────────────────────────────────
        //
        // Native HTML export (preserves tables/formatting; may emit sibling image files
        // into the same folder as outputFile, per Jogy). Output lands under the same
        // x1mcp_previews temp root used by preview fragments (ActionBridge.PreviewFileDir) —
        // self-cleaning across reboots, and the caller doesn't need to know it's temp. The
        // folder is not deleted after reading since the returned assetFolder may still be
        // needed to resolve sibling image references.

        public async Task<JObject> ExportHtmlAsync(string table, string uri, int timeoutMs)
        {
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(uri))
                throw new ArgumentException("table and uri are required.");

            // XS-1678: resolve/validate before touching the channel - see GetContentAsync's
            // identical comment for why (silent no-callback drop on a disallowed table).
            table = await _tableResolver.ResolveOrThrowAsync(table).ConfigureAwait(false);

            string folder = Path.Combine(ActionBridge.PreviewFileDir, "export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string outputFile = Path.Combine(folder, "export.html");

            var ch = Channel;
            int sessionId = AcquireCallbackSession(ch, table);
            var tcs = _callbacks.ExpectExportHtml(outputFile);
            try
            {
                ch.ExportHtml(table, uri, outputFile);
                var result = await _callbacks.WaitExportHtmlAsync(tcs, timeoutMs).ConfigureAwait(false);
                _callbacks.CancelExportHtmlWait(outputFile);
                return BuildExportHtmlResult(result, folder);
            }
            finally { ReleaseCallbackSession(ch, sessionId); }
        }

        public async Task<JObject> ExportHtmlFromFileAsync(string file, int timeoutMs)
        {
            if (string.IsNullOrEmpty(file))
                throw new ArgumentException("file is required.");
            if (TryBuildArbitraryFileLicenseError("export_html", out var licenseError))
                return licenseError;
            if (!File.Exists(file))
                return new JObject { ["error"] = "File not found: " + file };

            string folder = Path.Combine(ActionBridge.PreviewFileDir, "export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string outputFile = Path.Combine(folder, "export.html");

            var ch = Channel;
            var tcs = _callbacks.ExpectExportHtml(outputFile);
            ch.ExportHtmlFromFile(file, outputFile);
            var result = await _callbacks.WaitExportHtmlAsync(tcs, timeoutMs).ConfigureAwait(false);
            _callbacks.CancelExportHtmlWait(outputFile);
            return BuildExportHtmlResult(result, folder);
        }

        private static JObject BuildExportHtmlResult(SearchManagerCallbacks.ExportHtmlArgs result, string folder)
        {
            if (!result.Success || string.IsNullOrEmpty(result.OutputFile) || !File.Exists(result.OutputFile))
                return new JObject
                {
                    ["error"] = string.IsNullOrEmpty(result.State) ? "HTML export failed or timed out." : result.State,
                    ["mode"] = "export_html"
                };

            string html = File.ReadAllText(result.OutputFile, Encoding.UTF8);
            bool truncated = false;
            if (html.Length > 512 * 1024)
            {
                html = html.Substring(0, 512 * 1024) + "\n... (truncated)";
                truncated = true;
            }
            var payload = new JObject
            {
                ["html"] = html,
                ["path"] = result.OutputFile,
                ["assetFolder"] = folder
            };
            if (truncated) payload["truncated"] = true;
            return payload;
        }

        // ── XS-1577: Tagging ────────────────────────────────────────────────────

        // X1SearchManager is InstanceContextMode.Single — its `callbacks` field is only
        // set inside CreateSearchSession. Calling AddTags/RemoveTags/ClearTags/GetContent
        // without first creating a session means the singleton uses whatever callback
        // channel was registered last (typically the X1 UI, which ignores these callbacks).
        // We therefore create a throw-away session before each operation and destroy it
        // after, ensuring `callbacks` points at the bridge for the duration.
        private int AcquireCallbackSession(IX1MCPSearchManager ch, string table)
        {
            int sessionId = ch.CreateSearchSession(new[] { table }, progenitorSearch: false, getKeywordStats: false);
            ThrowIfSessionCreationFailed(sessionId, table);
            _callbacks.RegisterSession(sessionId);
            return sessionId;
        }

        /// <summary>
        /// XS-1678: <c>CreateSearchSession</c> returns <c>-1</c> (new XS-1676 sentinel) when the
        /// requested table isn't available on this connection's license tier (files-only), or
        /// <c>0</c> for the pre-existing "service unavailable" case. Left unchecked, callers would
        /// proceed to SetSearchTerms/GetContent/etc. against a session that doesn't exist and hang
        /// until their own timeout, surfacing a generic "timed out" error instead of this clear one.
        /// A pure function (no WCF/IO) so it's directly unit-testable - see
        /// SearchBridgeSessionGateTests.
        /// </summary>
        internal static void ThrowIfSessionCreationFailed(int sessionId, string table)
        {
            if (sessionId > 0)
                return;
            throw sessionId == -1
                ? (Exception)new X1McpFilesOnlyLicenseException(table)
                : new InvalidOperationException(
                    "X1 Search could not create a session for table '" + table + "' (returned " +
                    sessionId + "). The X1 service may be unavailable - confirm X1ServiceHost is running and retry.");
        }

        private void ReleaseCallbackSession(IX1MCPSearchManager ch, int sessionId)
        {
            try { ch.DestroySearchSession(sessionId); } catch { /* best-effort */ }
            _callbacks.UnregisterSession(sessionId);
        }

        public async Task<JObject> AddTagsAsync(string table, string[] uris, string[] tags, int timeoutMs)
        {
            ValidateTagArgs(table, uris, tags, allowNullTags: false);
            table = await _tableResolver.ResolveOrThrowAsync(table).ConfigureAwait(false);
            IX1MCPSearchManager ch = Channel;
            int sessionId = AcquireCallbackSession(ch, table);
            try
            {
                var tcs = _callbacks.ExpectTagsAdded();
                ch.AddTags(table, uris, tags);
                int count = await _callbacks.WaitTagOpAsync(tcs, timeoutMs).ConfigureAwait(false);
                return BuildTagResult("add", count, uris.Length);
            }
            finally { ReleaseCallbackSession(ch, sessionId); }
        }

        public async Task<JObject> RemoveTagsAsync(string table, string[] uris, string[] tags, int timeoutMs)
        {
            ValidateTagArgs(table, uris, tags, allowNullTags: false);
            table = await _tableResolver.ResolveOrThrowAsync(table).ConfigureAwait(false);
            IX1MCPSearchManager ch = Channel;
            int sessionId = AcquireCallbackSession(ch, table);
            try
            {
                var tcs = _callbacks.ExpectTagsRemoved();
                ch.RemoveTags(table, uris, tags);
                int count = await _callbacks.WaitTagOpAsync(tcs, timeoutMs).ConfigureAwait(false);
                return BuildTagResult("remove", count, uris.Length);
            }
            finally { ReleaseCallbackSession(ch, sessionId); }
        }

        public async Task<JObject> ClearTagsAsync(string table, string[] uris, int timeoutMs)
        {
            ValidateTagArgs(table, uris, tags: null, allowNullTags: true);
            table = await _tableResolver.ResolveOrThrowAsync(table).ConfigureAwait(false);
            IX1MCPSearchManager ch = Channel;
            int sessionId = AcquireCallbackSession(ch, table);
            try
            {
                var tcs = _callbacks.ExpectTagsCleared();
                ch.ClearTags(table, uris);
                int count = await _callbacks.WaitTagOpAsync(tcs, timeoutMs).ConfigureAwait(false);
                return BuildTagResult("clear", count, uris.Length);
            }
            finally { ReleaseCallbackSession(ch, sessionId); }
        }

        private static void ValidateTagArgs(string table, string[] uris, string[] tags, bool allowNullTags)
        {
            if (string.IsNullOrEmpty(table))
                throw new ArgumentException("table is required.");
            if (uris == null || uris.Length == 0)
                throw new ArgumentException("uris is required.");
            if (!allowNullTags)
            {
                if (tags == null || tags.Length == 0)
                    throw new ArgumentException("tags is required.");
                if (uris.Length != tags.Length)
                    throw new ArgumentException("uris and tags must have the same length; got " + uris.Length + " uris and " + tags.Length + " tags.");
            }
        }

        private static JObject BuildTagResult(string op, int count, int requested)
        {
            if (count == -2)
                return new JObject { ["op"] = op, ["error"] = "Tag operation timed out." };
            if (count == -1)
                return new JObject { ["op"] = op, ["error"] = "Server rejected: uris and tags length mismatch." };
            return new JObject
            {
                ["op"] = op,
                ["requested"] = requested,
                ["affected"] = count
            };
        }
    }
}
