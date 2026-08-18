// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using X1.Service;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// Demonstrates the X1 Search API protocol behaviors that cause observable timeouts
    /// and latency in the MCP bridge. Each test isolates a specific scenario at the
    /// callback layer (no live X1ServiceHost required) so that the X1 product team can
    /// reproduce and diagnose each issue in isolation.
    ///
    /// Scenarios covered:
    ///
    ///   1. Count-only OnSearchResultsChanged callback
    ///      X1 fires an initial OnSearchResultsChanged with totalResults &gt; 0 but an
    ///      empty firstPage. The bridge cannot return results from this callback alone;
    ///      it must make a separate GetSearchResults call — an extra round trip that
    ///      adds latency and complexity.
    ///      RECOMMENDED FIX: Include the first page in the initial OnSearchResultsChanged.
    ///
    ///   2. Federated (multi-table) search never fires OnSearchResultsChanged
    ///      When CreateSearchSession is called with more than one table,
    ///      OnSearchResultsChanged never fires. The bridge hangs for the full timeout
    ///      (60 s by default) and throws TimeoutException.
    ///      RECOMMENDED FIX: Fire OnSearchResultsChanged for multi-table sessions just as
    ///      for single-table sessions, merging results as they arrive.
    ///
    ///   3. GeneratePreview silently dropped for some connectors
    ///      For email (MSMail, Gmail) and Dropbox connectors, X1 accepts the
    ///      GeneratePreview call but never fires OnPreviewReady. The bridge must use a
    ///      shortened auto-preview timeout and fall back to GetItemInternal.
    ///      RECOMMENDED FIX: Fire OnPreviewReady with an error or "not supported" result
    ///      rather than silently dropping the request.
    ///
    ///   4. GetSearchResults sometimes returns an empty firstPage
    ///      Even after OnSearchResultsChanged indicates totalResults &gt; 0, calling
    ///      GetSearchResults may return an empty array on the first attempt. The bridge
    ///      retries up to 3 times with a 300 ms pause between each retry.
    ///      RECOMMENDED FIX: Guarantee that GetSearchResults returns at least one result
    ///      whenever totalResults &gt; 0 has already been signalled.
    /// </summary>
    [TestFixture]
    public class X1TimeoutScenariosTests
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // Scenario 1 — Count-only OnSearchResultsChanged callback
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// X1 fires OnSearchResultsChanged with totalResults &gt; 0 but firstPage empty.
        ///
        /// PROBLEM: The MCP bridge waits on WaitResultsChangedAsync, which only completes
        /// when firstPage is non-empty (or totalResults == 0). A count-only callback does
        /// NOT complete this wait, so the bridge cannot return any results from this
        /// callback alone.
        ///
        /// The bridge detects this via TryGetPendingCount and is then forced to make
        /// a separate GetSearchResults call — an extra round trip.
        /// </summary>
        [Test]
        public void CountOnly_InitialCallback_HasNoFirstPage_DoesNotCompleteResultsChangedWait()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(1);
            var waitTask = cb.WaitResultsChangedAsync(1);

            // X1 sends: count=15, firstPage=empty  (count-only pattern)
            cb.OnSearchResultsChanged(
                sessionID: 1,
                totalResults: 15,
                selectedItemsCount: 0,
                highlightTerms: null,
                firstRow: 0,
                firstPage: new SearchResult[0],   // <-- empty: the problematic behavior
                trackURIs: null,
                trackIndices: null,
                lastSelectionSequence: 0,
                elapsedTime: 0);

            // The bridge's wait task is still pending — it has received a count but no data.
            // Without this behavior the bridge could immediately return results to the caller.
            Assert.That(waitTask.IsCompleted, Is.False,
                "WaitResultsChangedAsync must remain pending when firstPage is empty; " +
                "X1 should include the first page in the initial OnSearchResultsChanged callback.");
        }

        /// <summary>
        /// WaitAnyCallbackAsync completes as soon as any OnSearchResultsChanged fires,
        /// even when the callback is count-only (firstPage empty). This allows SearchBridge
        /// to break out of its wait immediately and call GetSearchResults with the full
        /// remaining timeout budget rather than burning the entire timeout first.
        /// </summary>
        [Test]
        public void CountOnly_WaitAnyCallback_CompletesImmediatelyOnCountOnlySignal()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(40);
            var changedTask = cb.WaitResultsChangedAsync(40);
            var anyTask = cb.WaitAnyCallbackAsync(40);

            cb.OnSearchResultsChanged(40, 17869, 0, null, 0, new SearchResult[0], null, null, 0, 0);

            Assert.That(anyTask.IsCompleted, Is.True,
                "WaitAnyCallbackAsync must complete immediately on a count-only callback so " +
                "SearchBridge can call GetSearchResults with the full remaining timeout budget.");
            Assert.That(changedTask.IsCompleted, Is.False,
                "WaitResultsChangedAsync must still be pending — no first page was delivered.");
        }

        /// <summary>
        /// WaitAnyCallbackAsync also completes when a data-bearing callback arrives,
        /// alongside WaitResultsChangedAsync completing at the same time.
        /// </summary>
        [Test]
        public void CountOnly_WaitAnyCallback_AlsoCompletesOnDataBearingCallback()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(41);
            var changedTask = cb.WaitResultsChangedAsync(41);
            var anyTask = cb.WaitAnyCallbackAsync(41);

            var page = new[] { new SearchResult { uri = "file://doc.txt", table = "Files" } };
            cb.OnSearchResultsChanged(41, 1, 0, null, 0, page, null, null, 0, 0);

            Assert.That(anyTask.IsCompleted, Is.True, "WaitAnyCallbackAsync completes on data-bearing callback.");
            Assert.That(changedTask.IsCompleted, Is.True, "WaitResultsChangedAsync also completes when firstPage is non-empty.");
        }

        /// <summary>
        /// After a count-only callback the pending count IS stored so the bridge can at
        /// least report how many total results exist, even if it cannot return any data.
        /// </summary>
        [Test]
        public void CountOnly_PendingCount_IsStoredAfterCountOnlyCallback()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(2);
            cb.WaitResultsChangedAsync(2);

            cb.OnSearchResultsChanged(2, 42, 0, null, 0, new SearchResult[0], null, null, 0, 0);

            Assert.That(cb.TryGetPendingCount(2, out int count, out _), Is.True);
            Assert.That(count, Is.EqualTo(42));
        }

        /// <summary>
        /// When X1 sends a count-only callback followed by OnSearchResultsReady (in
        /// response to an explicit GetSearchResults call), the bridge can recover the
        /// first page via WaitResultsReadyAsync.
        ///
        /// This two-call sequence is the workaround the bridge uses today, but it
        /// requires an extra GetSearchResults round trip that would not be necessary
        /// if X1 included the first page in the initial OnSearchResultsChanged.
        /// </summary>
        [Test]
        public async Task CountOnly_Recovery_RequiresExplicitGetSearchResults()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(3);
            var changedTask = cb.WaitResultsChangedAsync(3);

            // Step 1: X1 fires count-only — bridge wait does not complete.
            cb.OnSearchResultsChanged(3, 10, 0, null, 0, new SearchResult[0], null, null, 0, 0);
            Assert.That(changedTask.IsCompleted, Is.False, "Step 1: changed task still pending");

            // Step 2: Bridge must call GetSearchResults explicitly (extra round trip).
            const int requestId = 77;
            var readyTask = cb.WaitResultsReadyAsync(3, requestId);

            // Step 3: X1 fires OnSearchResultsReady in response to GetSearchResults.
            var page = new[] { new SearchResult { uri = "files://doc.pdf", table = "Files" } };
            cb.OnSearchResultsReady(3, requestId, page, 0);

            // Step 4: Bridge can now retrieve the first page via readyTask.
            var result = await readyTask;
            Assert.That(result, Has.Length.EqualTo(1),
                "After the explicit GetSearchResults call, the first page is available — " +
                "this entire step would be unnecessary if X1 included data in Step 1.");
        }

        /// <summary>
        /// If OnSearchResultsReady returns an empty array even after the explicit
        /// GetSearchResults call, the bridge falls back to returning a count-only
        /// response: { totalResults: N, returned: 0, results: [] }.
        ///
        /// PROBLEM: The caller receives no actual documents despite X1 reporting N results.
        /// </summary>
        [Test]
        public async Task CountOnly_Recovery_WhenGetSearchResultsReturnsEmpty_NoDataDelivered()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(4);
            _ = cb.WaitResultsChangedAsync(4);

            // Count-only callback arrives.
            cb.OnSearchResultsChanged(4, 7, 0, null, 0, new SearchResult[0], null, null, 0, 0);

            const int requestId = 88;
            var readyTask = cb.WaitResultsReadyAsync(4, requestId);

            // X1 responds to GetSearchResults with an empty array — another protocol gap.
            cb.OnSearchResultsReady(4, requestId, new SearchResult[0], 0);

            var result = await readyTask;

            // The bridge receives an empty page. It cannot return data to the caller.
            Assert.That(result, Is.Empty,
                "GetSearchResults returned an empty page despite totalResults=7. " +
                "X1 must guarantee a non-empty page when GetSearchResults is called after " +
                "OnSearchResultsChanged has already reported totalResults > 0.");
        }

        /// <summary>
        /// If X1 never fires OnSearchResultsReady within the remaining time budget after
        /// a count-only callback, WaitResultsReadyAsync times out and the bridge can
        /// only return a count-only response with zero documents.
        /// </summary>
        [Test]
        public async Task CountOnly_Recovery_WhenGetSearchResultsTimesOut_NoDataDelivered()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(5);
            _ = cb.WaitResultsChangedAsync(5);

            // Count-only callback arrives.
            cb.OnSearchResultsChanged(5, 20, 0, null, 0, new SearchResult[0], null, null, 0, 0);

            // Bridge calls GetSearchResults but X1 never fires OnSearchResultsReady
            // (simulated by not calling cb.OnSearchResultsReady at all).
            const int requestId = 99;
            var readyTask = cb.WaitResultsReadyAsync(5, requestId);

            // Short timeout simulating exhausted time budget.
            var completed = await Task.WhenAny(readyTask, Task.Delay(50));

            Assert.That(completed, Is.Not.SameAs(readyTask),
                "WaitResultsReadyAsync must time out when X1 does not fire OnSearchResultsReady. " +
                "The bridge falls back to returning { totalResults: 20, returned: 0 } with no documents.");
        }

        /// <summary>
        /// When totalResults == 0 (empty search result), the count-only path does NOT
        /// apply. X1 fires OnSearchResultsChanged with empty firstPage AND totalResults=0,
        /// and the bridge correctly completes the wait immediately.
        ///
        /// This test confirms that the zero-results case is handled without a timeout,
        /// and distinguishes it from the count-only bug.
        /// </summary>
        [Test]
        public void EmptySearch_ZeroResults_CompletesImmediatelyWithoutTimeout()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(6);
            var waitTask = cb.WaitResultsChangedAsync(6);

            // totalResults == 0: empty firstPage is correct and the task should complete.
            cb.OnSearchResultsChanged(6, 0, 0, null, 0, new SearchResult[0], null, null, 0, 0);

            Assert.That(waitTask.IsCompleted, Is.True,
                "Zero-results search must complete immediately (no timeout). " +
                "This case is correctly handled by the existing protocol.");
            Assert.That(waitTask.Result.TotalResults, Is.EqualTo(0));
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Scenario 2 — Federated (multi-table) search never fires OnSearchResultsChanged
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// When CreateSearchSession is called with more than one table name, X1 never
        /// fires OnSearchResultsChanged. This causes WaitResultsChangedAsync to pend
        /// for the full timeout duration (60 s by default) before throwing TimeoutException.
        ///
        /// The test simulates this by simply not calling OnSearchResultsChanged at all,
        /// which is exactly what X1 does for multi-table sessions.
        ///
        /// IMPACT: The MCP bridge must restrict clients to single-table searches.
        /// RECOMMENDED FIX: Fire OnSearchResultsChanged for multi-table sessions.
        /// </summary>
        [Test]
        public async Task FederatedSearch_CallbackNeverFires_WaitTimesOut()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(10);
            var changedTask = cb.WaitResultsChangedAsync(10);

            // X1 does NOT fire OnSearchResultsChanged for multi-table sessions.
            // We simulate with a short wait to avoid blocking the test suite.
            var completed = await Task.WhenAny(changedTask, Task.Delay(50));

            Assert.That(completed, Is.Not.SameAs(changedTask),
                "WaitResultsChangedAsync never completes for multi-table X1 sessions. " +
                "The bridge must time out and throw TimeoutException after the full timeout " +
                "period (default: 60 000 ms), blocking the AI assistant for 60 seconds.");
        }

        /// <summary>
        /// For a federated search where the callback never arrives and there is also
        /// no count-only pending count, TryGetPendingCount returns false. This means
        /// the bridge has no information at all — it throws TimeoutException with no
        /// partial result to offer the caller.
        /// </summary>
        [Test]
        public void FederatedSearch_NoPendingCount_TimeoutExceptionWithNoPartialResult()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(11);

            // No OnSearchResultsChanged ever called.

            bool hasPendingCount = cb.TryGetPendingCount(11, out int count, out _);

            Assert.That(hasPendingCount, Is.False,
                "When X1 never fires OnSearchResultsChanged (federated search), there is no " +
                "partial count to return. The bridge throws TimeoutException with no data. " +
                "Compare with Scenario 1: count-only at least provides a totalResults hint.");
            Assert.That(count, Is.EqualTo(0));
        }

        /// <summary>
        /// Confirms that a single-table session DOES fire OnSearchResultsChanged and
        /// completes the bridge wait, which is why the bridge restricts to single-table.
        /// This test illustrates the contrast with the multi-table scenario above.
        /// </summary>
        [Test]
        public void SingleTable_CallbackFires_CompletesImmediately()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(12);
            var waitTask = cb.WaitResultsChangedAsync(12);

            // Single-table: X1 fires OnSearchResultsChanged with data.
            var page = new[] { new SearchResult { uri = "files://report.pdf", table = "Files" } };
            cb.OnSearchResultsChanged(12, 1, 0, null, 0, page, null, null, 0, 0);

            Assert.That(waitTask.IsCompleted, Is.True,
                "Single-table sessions fire OnSearchResultsChanged immediately with data. " +
                "This is the only supported mode until federated search is fixed.");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Scenario 3 — GeneratePreview silently dropped for some connectors
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// For connectors that do not support preview (MSMail, Gmail, Dropbox), X1
        /// accepts the GeneratePreview call without error but never fires OnPreviewReady.
        ///
        /// WaitPreviewAsync times out and returns { Error: "Preview timed out..." }.
        /// The bridge must then fall back to GetItemInternal, adding a second round trip.
        ///
        /// RECOMMENDED FIX: Fire OnPreviewReady with an error or "not supported" signal
        /// so the bridge knows immediately and can fall back without waiting.
        /// </summary>
        [Test]
        public async Task Preview_WhenOnPreviewReadyNeverFires_TimesOut()
        {
            var cb = new SearchManagerCallbacks();
            var key = cb.ExpectPreview("email://inbox/msg123");

            // X1 never fires OnPreviewReady for email connectors — simulate by waiting.
            var result = await cb.WaitPreviewAsync(key, millisecondsTimeout: 50);

            Assert.That(result.Error, Is.Not.Null.And.Not.Empty,
                "WaitPreviewAsync must return a timeout error when X1 does not fire OnPreviewReady. " +
                "For email and Dropbox connectors this always happens, forcing the bridge to " +
                "wait the full auto-preview timeout before falling back to GetItemInternal.");
            Assert.That(result.Preview, Is.Null);
        }

        /// <summary>
        /// When preview is not supported (email/Dropbox), the bridge auto mode uses a
        /// short timeout (configurable, default 10 000 ms) to detect the silent drop
        /// quickly, then falls back to GetItemInternal for the internal field values.
        ///
        /// This test shows the two-wait pattern: first the short preview wait (which
        /// times out), then a second call would go to GetItemInternal. In the worst
        /// case (preview timeout at 10 s + GetItemInternal time) the total latency
        /// for these connectors is noticeably higher.
        ///
        /// RECOMMENDED FIX: Fire OnPreviewReady immediately with an "unsupported" flag.
        /// </summary>
        [Test]
        public async Task Preview_AutoMode_ShortTimeoutFallsBack_TwoRoundTripsRequired()
        {
            var cb = new SearchManagerCallbacks();

            // Round trip 1: auto mode tries GeneratePreview with a short timeout.
            var key = cb.ExpectPreview("email://inbox/msg456");
            var previewResult = await cb.WaitPreviewAsync(key, millisecondsTimeout: 50);
            cb.CancelPreviewWait(key);

            bool previewTimedOut = !string.IsNullOrEmpty(previewResult.Error);

            Assert.That(previewTimedOut, Is.True,
                "Auto mode preview timed out (email connector does not fire OnPreviewReady). " +
                "The bridge must now make a second call — GetItemInternal — to return any data. " +
                "This adds latency proportional to the auto-preview timeout for every email/Dropbox lookup.");

            // Round trip 2 would be GetItemInternal (a synchronous WCF call) — not
            // testable here without a live X1 service, but the bridge always makes it.
        }

        /// <summary>
        /// When the preview callback DOES fire (Files connector, Office documents, etc.),
        /// WaitPreviewAsync completes immediately without any timeout penalty.
        /// This contrasts with the silent-drop behaviour above.
        /// </summary>
        [Test]
        public async Task Preview_WhenOnPreviewReadyFires_CompletesWithoutTimeout()
        {
            var cb = new SearchManagerCallbacks();
            var key = cb.ExpectPreview("files://report.docx");
            var waitTask = cb.WaitPreviewAsync(key, millisecondsTimeout: 5000);

            cb.OnPreviewReady("files://report.docx", "<html>preview content</html>", false, null, null, 0);

            var result = await waitTask;
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Preview, Is.EqualTo("<html>preview content</html>"),
                "Supported connectors fire OnPreviewReady immediately. No timeout occurs.");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Scenario 4 — GetSearchResults may return an empty firstPage (race condition)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Even after OnSearchResultsChanged has fired with totalResults &gt; 0, calling
        /// GetSearchResults may return an empty array on the first attempt. This happens
        /// when the X1 indexer has not yet staged the first page of results internally.
        ///
        /// The bridge detects this and retries up to 3 times with a 300 ms pause, which
        /// adds up to 900 ms of extra latency in the worst case.
        ///
        /// RECOMMENDED FIX: Guarantee a non-empty page when GetSearchResults is called
        /// after totalResults &gt; 0 has already been signalled via OnSearchResultsChanged.
        /// </summary>
        [Test]
        public async Task GetSearchResults_EmptyPageOnFirstAttempt_RequiresRetry()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(20);

            // OnSearchResultsChanged fires with data — bridge starts fetching more pages.
            var changedPage = new[] { new SearchResult { uri = "files://a.pdf", table = "Files" } };
            cb.OnSearchResultsChanged(20, 50, 0, null, 0, changedPage, null, null, 0, 0);

            // Bridge requests page 2 (start=1, need=49).
            const int requestId = 200;
            var readyTask1 = cb.WaitResultsReadyAsync(20, requestId);

            // X1 responds with an empty array — race condition: results not yet staged.
            cb.OnSearchResultsReady(20, requestId, new SearchResult[0], 0);

            var emptyPage = await readyTask1;
            Assert.That(emptyPage, Is.Empty,
                "GetSearchResults returned an empty page even though totalResults=50. " +
                "The bridge must retry (up to 3× with 300 ms delay) to work around this race.");

            // On retry, X1 finally returns data.
            const int retryRequestId = 201;
            var readyTask2 = cb.WaitResultsReadyAsync(20, retryRequestId);

            var retryPage = new[]
            {
                new SearchResult { uri = "files://b.pdf", table = "Files" },
                new SearchResult { uri = "files://c.pdf", table = "Files" }
            };
            cb.OnSearchResultsReady(20, retryRequestId, retryPage, 0);

            var finalPage = await readyTask2;
            Assert.That(finalPage, Has.Length.EqualTo(2),
                "After retry, the page is available. But the 300 ms pause was wasted latency.");
        }

        /// <summary>
        /// If all three retries return empty pages, the bridge gives up and returns
        /// only the results collected so far — which may be fewer than the requested limit.
        ///
        /// IMPACT: Callers may receive truncated result sets with no indication that
        /// more results exist, other than totalResults &gt; returned in the response JSON.
        /// </summary>
        [Test]
        public async Task GetSearchResults_AllRetriesReturnEmpty_ResultsTruncated()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(21);

            // Bridge has the first page (1 result) but needs more (limit=5, totalResults=10).
            var changedPage = new[] { new SearchResult { uri = "files://first.pdf", table = "Files" } };
            cb.OnSearchResultsChanged(21, 10, 0, null, 0, changedPage, null, null, 0, 0);

            // All GetSearchResults calls for pages 2..4 return empty — simulate 3 retries.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int rid = 300 + attempt;
                var t = cb.WaitResultsReadyAsync(21, rid);
                cb.OnSearchResultsReady(21, rid, new SearchResult[0], 0);
                var page = await t;
                Assert.That(page, Is.Empty, $"Retry {attempt + 1}: X1 still returns empty page.");
            }

            // Bridge gives up. Only the initial 1 result from OnSearchResultsChanged is returned.
            // Response will be: { totalResults: 10, returned: 1, results: [...] }
            Assert.Pass(
                "After 3 retries all returning empty, the bridge returns only the " +
                "1 result from the initial OnSearchResultsChanged callback. " +
                "totalResults=10 but returned=1: the caller sees a truncated result set.");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Scenario 5 — Multiple count-only callbacks update the pending total
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// X1 may fire multiple count-only OnSearchResultsChanged callbacks as the
        /// total result count is refined (e.g. 5 → 12 → 23). The bridge stores only
        /// the most recent pending count.
        ///
        /// If WaitResultsChangedAsync times out while these count-only updates are
        /// arriving, the bridge uses the last known count to build a partial response.
        /// </summary>
        [Test]
        public void CountOnly_MultipleCallbacks_LastCountIsStored()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(30);
            cb.WaitResultsChangedAsync(30);

            // X1 fires several count-only updates.
            cb.OnSearchResultsChanged(30, 5,  0, null, 0, new SearchResult[0], null, null, 0, 0);
            cb.OnSearchResultsChanged(30, 12, 0, null, 0, new SearchResult[0], null, null, 0, 0);
            cb.OnSearchResultsChanged(30, 23, 0, null, 0, new SearchResult[0], null, null, 0, 0);

            Assert.That(cb.TryGetPendingCount(30, out int count, out _), Is.True);
            Assert.That(count, Is.EqualTo(23),
                "The bridge stores the last count-only total. If the search times out at this " +
                "point, the response will report totalResults=23 with returned=0 and no documents.");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Combined latency summary
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Summary test that documents the worst-case latency stack for a single MCP
        /// x1_search + x1_get_content call chain, combining all four scenarios.
        ///
        /// Worst-case path (all four issues triggered in sequence):
        ///   1. Count-only callback received; GetSearchResults called         +0 ms
        ///   2. GetSearchResults times out (no OnSearchResultsReady)          +remaining_budget ms
        ///   3. Count-only fallback: returned=0; caller retries with x1_get_content
        ///   4. x1_get_content(mode=auto): GeneratePreview called             +0 ms
        ///   5. OnPreviewReady never fires (email connector)                  +10 000 ms (auto timeout)
        ///   6. GetItemInternal called as fallback                            +WCF round trip
        ///
        /// Total observable delay: up to search_timeout + 10 000 ms + WCF time.
        ///
        /// This test does not make any assertions — it documents the flow for the
        /// X1 product team.
        /// </summary>
        [Test]
        public void Summary_WorstCaseLatencyStack_DocumentedHere()
        {
            // Latency contributions per issue:
            //
            //   Scenario 1 (count-only): 1 extra GetSearchResults round trip
            //     ~ adds one WCF call (typically < 1 s if X1 responds quickly)
            //     ~ if X1 also times out on GetSearchResults: wastes remaining time budget
            //
            //   Scenario 2 (federated search): full timeout (60 000 ms) before any response
            //     ~ completely blocks the AI assistant for 60 seconds per multi-table query
            //     ~ mitigation: bridge restricts to single-table; requires user education
            //
            //   Scenario 3 (preview silent drop): wastes auto-preview timeout (10 000 ms)
            //     ~ every x1_get_content call on email/Dropbox pays this 10-second penalty
            //     ~ mitigation: configurable autoPreviewTimeoutMs; 10 s default
            //
            //   Scenario 4 (empty page race): up to 3 × 300 ms = 900 ms of extra delay
            //     ~ affects paginated result sets; first page from OnSearchResultsChanged
            //       is always returned even if subsequent pages fail
            //
            // X1 fixes that would eliminate these entirely:
            //   1. Always include firstPage in the initial OnSearchResultsChanged
            //   2. Fire OnSearchResultsChanged for multi-table (federated) sessions
            //   3. Fire OnPreviewReady(error) when preview is not supported
            //   4. Guarantee non-empty GetSearchResults response after totalResults > 0

            Assert.Pass("Latency stack documented in test comments — no assertion required.");
        }
    }
}
