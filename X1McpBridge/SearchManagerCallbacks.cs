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
using System.ServiceModel;
using System.Xml.Serialization;
using X1.Service;

namespace X1.McpBridge
{
    [CallbackBehavior(ConcurrencyMode = ConcurrencyMode.Multiple, UseSynchronizationContext = false, MaxItemsInObjectGraph = int.MaxValue)]
    internal sealed class SearchManagerCallbacks : IX1SearchManagerCallbacks
    {
        private static readonly log4net.ILog Log = BridgeLogger.GetLogger(typeof(SearchManagerCallbacks));
        private readonly object _sync = new object();

        private readonly Dictionary<int, SessionCallbacks> _sessions = new Dictionary<int, SessionCallbacks>();

        private sealed class SessionCallbacks
        {
            public TaskCompletionSource<ResultsChangedArgs> ResultsChanged = new TaskCompletionSource<ResultsChangedArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Fires on the very first OnSearchResultsChanged regardless of whether firstPage is populated.
            // Allows SearchBridge to break out of its wait immediately on a count-only callback and
            // call GetSearchResults with the full remaining timeout budget rather than burning the
            // entire timeout before entering the recovery path.
            public TaskCompletionSource<bool> AnyCallbackSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly Dictionary<int, TaskCompletionSource<SearchResult[]>> ResultsReady = new Dictionary<int, TaskCompletionSource<SearchResult[]>>();
            // Holds the latest count from count-only OnSearchResultsChanged callbacks (firstPage empty)
            // so the timeout path can still report the right totalResults.
            public int PendingTotalResults;
            public HighlightTerm[] PendingHighlightTerms;
        }

        internal struct ResultsChangedArgs
        {
            public int TotalResults;
            public SearchResult[] FirstPage;
            public HighlightTerm[] HighlightTerms;
        }

        public void RegisterSession(int sessionId)
        {
            lock (_sync)
            {
                _sessions[sessionId] = new SessionCallbacks();
            }
        }

        public void UnregisterSession(int sessionId)
        {
            lock (_sync)
            {
                _sessions.Remove(sessionId);
            }
        }

        public Task<ResultsChangedArgs> WaitResultsChangedAsync(int sessionId)
        {
            lock (_sync)
            {
                if (!_sessions.TryGetValue(sessionId, out var s))
                    return Task.FromException<ResultsChangedArgs>(new InvalidOperationException("Session not registered: " + sessionId));
                return s.ResultsChanged.Task;
            }
        }

        /// <summary>
        /// Completes as soon as any OnSearchResultsChanged fires for the session — whether it
        /// carries a first page (data) or is a count-only signal. Used by SearchBridge to break
        /// out of the initial wait immediately so GetSearchResults can be called with the full
        /// remaining timeout budget rather than after it has been spent waiting.
        /// </summary>
        public Task WaitAnyCallbackAsync(int sessionId)
        {
            lock (_sync)
            {
                if (!_sessions.TryGetValue(sessionId, out var s))
                    return Task.FromException(new InvalidOperationException("Session not registered: " + sessionId));
                return s.AnyCallbackSignal.Task;
            }
        }

        /// <summary>
        /// Returns the total-results count received in any count-only OnSearchResultsChanged
        /// callback that arrived before a data-bearing callback completed the TCS.
        /// Used by the timeout path so we can still report the right totalResults.
        /// </summary>
        public bool TryGetPendingCount(int sessionId, out int totalResults, out HighlightTerm[] highlightTerms)
        {
            lock (_sync)
            {
                if (_sessions.TryGetValue(sessionId, out var s) && s.PendingTotalResults > 0)
                {
                    totalResults = s.PendingTotalResults;
                    highlightTerms = s.PendingHighlightTerms;
                    return true;
                }
            }
            totalResults = 0;
            highlightTerms = null;
            return false;
        }

        public Task<SearchResult[]> WaitResultsReadyAsync(int sessionId, int requestId)
        {
            TaskCompletionSource<SearchResult[]> tcs;
            lock (_sync)
            {
                if (!_sessions.TryGetValue(sessionId, out var s))
                    return Task.FromException<SearchResult[]>(new InvalidOperationException("Session not registered: " + sessionId));
                if (!s.ResultsReady.TryGetValue(requestId, out tcs))
                {
                    tcs = new TaskCompletionSource<SearchResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                    s.ResultsReady[requestId] = tcs;
                }
            }
            return tcs.Task;
        }

        public void OnSearchResultsChanged(int sessionID, int totalResults, int selectedItemsCount, HighlightTerm[] highlightTerms,
            int firstRow, SearchResult[] firstPage, string[] trackURIs, int[] trackIndices, int lastSelectionSequence, int elapsedTime)
        {
            // elapsedTime accepted for interface compliance (XS-1671); not yet surfaced to callers.
            var page = firstPage ?? new SearchResult[0];
            lock (_sync)
            {
                if (!_sessions.TryGetValue(sessionID, out var s))
                    return;
                s.PendingTotalResults = totalResults;
                s.PendingHighlightTerms = highlightTerms;
                // Signal that at least one callback has arrived (even if count-only) so SearchBridge
                // can immediately proceed to GetSearchResults rather than waiting the full timeout.
                s.AnyCallbackSignal.TrySetResult(true);
                // Only complete the TCS once we have actual result data, or the search is empty.
                // The service fires an initial count-only notification (firstPage empty) then a
                // second callback with the actual firstPage once results are ready.
                if (page.Length > 0 || totalResults == 0)
                {
                    s.ResultsChanged.TrySetResult(new ResultsChangedArgs
                    {
                        TotalResults = totalResults,
                        FirstPage = page,
                        HighlightTerms = highlightTerms
                    });
                }
            }
        }

        public void OnSearchResultsChangedMMF(int sessionID, int totalResults, int selectedItemsCount, HighlightTerm[] highlightTerms,
            int firstRow, string mmfName, string[] trackURIs, int[] trackIndices, int lastSelectionSequence, int elapsedTime)
        {
            // elapsedTime accepted for interface compliance (XS-1671); not yet surfaced to callers.
            var xml = MemoryMappedFileReader.ReadStringFromMemoryMappedFile(mmfName);
            if (string.IsNullOrWhiteSpace(xml))
                return;
            try
            {
                SearchResult[] firstPage;
                using (var sr = new System.IO.StringReader(xml))
                using (var xr = System.Xml.XmlReader.Create(sr))
                {
                    var serializer = new XmlSerializer(typeof(SearchResult[]));
                    firstPage = (SearchResult[])serializer.Deserialize(xr);
                }
                if (firstPage != null)
                    OnSearchResultsChanged(sessionID, totalResults, selectedItemsCount, highlightTerms,
                        firstRow, firstPage, trackURIs, trackIndices, lastSelectionSequence, elapsedTime);
            }
            catch (Exception ex)
            {
                Log.Error("OnSearchResultsChangedMMF deserialization failed", ex);
            }
        }

        public void OnSearchResultsReady(int sessionID, int requestID, SearchResult[] searchResults, int elapsedTime)
        {
            // elapsedTime accepted for interface compliance (XS-1671); not yet surfaced to callers.
            lock (_sync)
            {
                if (!_sessions.TryGetValue(sessionID, out var s))
                    return;
                if (s.ResultsReady.TryGetValue(requestID, out var tcs))
                    tcs.TrySetResult(searchResults ?? new SearchResult[0]);
                else
                {
                    tcs = new TaskCompletionSource<SearchResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                    tcs.TrySetResult(searchResults ?? new SearchResult[0]);
                    s.ResultsReady[requestID] = tcs;
                }
            }
        }

        public void OnSearchResultsReadyMMF(int sessionID, int requestID, string mmfName, int elapsedTime)
        {
            // elapsedTime accepted for interface compliance (XS-1671); not yet surfaced to callers.
            var xml = MemoryMappedFileReader.ReadStringFromMemoryMappedFile(mmfName);
            if (string.IsNullOrWhiteSpace(xml))
                return;
            try
            {
                SearchResult[] results;
                using (var sr = new System.IO.StringReader(xml))
                using (var xr = System.Xml.XmlReader.Create(sr))
                {
                    var serializer = new XmlSerializer(typeof(SearchResult[]));
                    results = (SearchResult[])serializer.Deserialize(xr);
                }
                if (results != null)
                    OnSearchResultsReady(sessionID, requestID, results, elapsedTime);
            }
            catch (Exception ex)
            {
                Log.Error("OnSearchResultsReadyMMF deserialization failed", ex);
            }
        }

        // ── Preview callbacks ────────────────────────────────────────────────────
        //
        // Keyed by a per-request token (GUID string) on the wait side, with a
        // separate URI→token reverse map for the WCF callback side.
        // This eliminates the URI collision bug: two concurrent requests for the
        // same URI each get their own TCS and are never cross-wired.

        internal struct PreviewArgs
        {
            public string Preview;
            public string Error;
            public string AdditionalData;
        }

        // token → TCS
        private readonly Dictionary<string, TaskCompletionSource<PreviewArgs>> _previewWaitByKey =
            new Dictionary<string, TaskCompletionSource<PreviewArgs>>();

        // uri → token (most-recent request wins for a given URI; single-flight in practice)
        private readonly Dictionary<string, string> _previewUriToKey =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Returns a unique request token to pass to WaitPreviewAsync / CancelPreviewWait.</summary>
        public string ExpectPreview(string uri)
        {
            var key = Guid.NewGuid().ToString("N");
            lock (_sync)
            {
                _previewWaitByKey[key] = new TaskCompletionSource<PreviewArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                _previewUriToKey[uri] = key;
            }
            return key;
        }

        public void CancelPreviewWait(string key)
        {
            lock (_sync)
            {
                if (_previewWaitByKey.TryGetValue(key, out _))
                {
                    // Clean up the uri→key reverse mapping
                    string uriToRemove = null;
                    foreach (KeyValuePair<string, string> kv in _previewUriToKey)
                    {
                        if (kv.Value == key) { uriToRemove = kv.Key; break; }
                    }
                    if (uriToRemove != null)
                        _previewUriToKey.Remove(uriToRemove);
                    _previewWaitByKey.Remove(key);
                }
            }
        }

        public async Task<PreviewArgs> WaitPreviewAsync(string key, int millisecondsTimeout)
        {
            TaskCompletionSource<PreviewArgs> tcs;
            lock (_sync)
            {
                if (!_previewWaitByKey.TryGetValue(key, out tcs))
                {
                    tcs = new TaskCompletionSource<PreviewArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _previewWaitByKey[key] = tcs;
                }
            }
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(millisecondsTimeout)).ConfigureAwait(false);
            if (completed == tcs.Task && tcs.Task.Status == TaskStatus.RanToCompletion)
                return tcs.Task.Result;
            return new PreviewArgs { Error = "Preview timed out or was cancelled." };
        }

        public void OnPreviewReady(string uri, string preview, bool isForOpen, string error, string additionalData, int elapsedTime)
        {
            // elapsedTime accepted for interface compliance (XS-1671); not yet surfaced to callers.
            lock (_sync)
            {
                if (_previewUriToKey.TryGetValue(uri, out var key) &&
                    _previewWaitByKey.TryGetValue(key, out var tcs))
                    tcs.TrySetResult(new PreviewArgs { Preview = preview, Error = error, AdditionalData = additionalData });
            }
        }

        private readonly Dictionary<int, TaskCompletionSource<FieldStringsArgs>> _fieldWait = new Dictionary<int, TaskCompletionSource<FieldStringsArgs>>();

        internal struct FieldStringsArgs
        {
            public string[] Uris;
            public string[][] FieldStrings;
        }

        public void ExpectFieldStrings(int requestId)
        {
            lock (_sync)
            {
                _fieldWait[requestId] = new TaskCompletionSource<FieldStringsArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public async Task<FieldStringsArgs> WaitFieldStringsAsync(int requestId, int millisecondsTimeout)
        {
            TaskCompletionSource<FieldStringsArgs> tcs;
            lock (_sync)
            {
                if (!_fieldWait.TryGetValue(requestId, out tcs))
                    tcs = new TaskCompletionSource<FieldStringsArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                _fieldWait[requestId] = tcs;
            }
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(millisecondsTimeout)).ConfigureAwait(false);
            if (completed == tcs.Task && tcs.Task.Status == TaskStatus.RanToCompletion)
                return tcs.Task.Result;
            return new FieldStringsArgs();
        }

        public void OnFieldStringsReady(int requestID, string[] uris, string[][] fieldStrings)
        {
            lock (_sync)
            {
                if (_fieldWait.TryGetValue(requestID, out var tcs))
                    tcs.TrySetResult(new FieldStringsArgs { Uris = uris, FieldStrings = fieldStrings });
            }
        }

        // ── GetContent callbacks (XS-1575) ───────────────────────────────────────
        //
        // As of X1 service 11.0.3.33, GetContent genuinely writes the extracted content to
        // the outputFile path we pass in, and OnContentReady(outputFile, extractionResult)
        // returns that same path (not the text itself) — this avoids WCF problems
        // serializing very large strings over the callback channel. We correlate by
        // outputFile, same pattern as ExtractFileArgs/OnTextExtracted below.

        internal struct ContentArgs
        {
            public string OutputFile;
            public string State;
            public bool Success;
        }

        private readonly Dictionary<string, TaskCompletionSource<ContentArgs>> _contentWaits =
            new Dictionary<string, TaskCompletionSource<ContentArgs>>(StringComparer.OrdinalIgnoreCase);

        public TaskCompletionSource<ContentArgs> ExpectContent(string outputFile)
        {
            var tcs = new TaskCompletionSource<ContentArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
                _contentWaits[outputFile] = tcs;
            return tcs;
        }

        public async Task<ContentArgs> WaitContentAsync(TaskCompletionSource<ContentArgs> tcs, int millisecondsTimeout)
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(millisecondsTimeout)).ConfigureAwait(false);
            if (completed == tcs.Task && tcs.Task.Status == TaskStatus.RanToCompletion)
                return tcs.Task.Result;
            return new ContentArgs { Success = false, State = "Timed out or was cancelled." };
        }

        public void CancelContentWait(string outputFile)
        {
            lock (_sync)
                _contentWaits.Remove(outputFile);
        }

        public void OnContentReady(string outputFile, string extractionResult)
        {
            TaskCompletionSource<ContentArgs> tcs = null;
            lock (_sync)
            {
                // The server hands back the outputFile it wrote to on success and an
                // empty string on failure — try both to correlate.
                if (!string.IsNullOrEmpty(outputFile) && _contentWaits.TryGetValue(outputFile, out tcs))
                    _contentWaits.Remove(outputFile);
                else if (_contentWaits.Count == 1)
                {
                    // Failure path: outputFile is empty; if only one request is in flight, resolve it.
                    foreach (var kv in _contentWaits) { tcs = kv.Value; }
                    _contentWaits.Clear();
                }
            }
            if (tcs == null) return;
            bool ok = !string.IsNullOrEmpty(outputFile) &&
                      (extractionResult == null ||
                       !extractionResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase));
            tcs.TrySetResult(new ContentArgs
            {
                OutputFile = outputFile ?? "",
                State = extractionResult ?? "",
                Success = ok
            });
        }

        // ── ExtractTextFromFile callbacks (XS-1575) ──────────────────────────────
        //
        // OnTextExtracted(outputFile, extractionResult) returns the outputFile
        // path we passed in, so we can key by outputFile.  Extraction is only
        // requested via the bridge with a Guid temp path, so collisions are
        // impossible in practice.

        internal struct ExtractFileArgs
        {
            public string OutputFile;
            public string State;
            public bool Success;
        }

        private readonly Dictionary<string, TaskCompletionSource<ExtractFileArgs>> _extractFileWaits =
            new Dictionary<string, TaskCompletionSource<ExtractFileArgs>>(StringComparer.OrdinalIgnoreCase);

        public TaskCompletionSource<ExtractFileArgs> ExpectExtractFile(string outputFile)
        {
            var tcs = new TaskCompletionSource<ExtractFileArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
                _extractFileWaits[outputFile] = tcs;
            return tcs;
        }

        public async Task<ExtractFileArgs> WaitExtractFileAsync(TaskCompletionSource<ExtractFileArgs> tcs, int millisecondsTimeout)
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(millisecondsTimeout)).ConfigureAwait(false);
            if (completed == tcs.Task && tcs.Task.Status == TaskStatus.RanToCompletion)
                return tcs.Task.Result;
            return new ExtractFileArgs { Success = false, State = "Timed out or was cancelled." };
        }

        public void CancelExtractFileWait(string outputFile)
        {
            lock (_sync)
                _extractFileWaits.Remove(outputFile);
        }

        public void OnTextExtracted(string outputFile, string extractionResult)
        {
            TaskCompletionSource<ExtractFileArgs> tcs = null;
            lock (_sync)
            {
                // The server hands back the outputFile it wrote to on success and an
                // empty string on failure — try both to correlate.
                if (!string.IsNullOrEmpty(outputFile) && _extractFileWaits.TryGetValue(outputFile, out tcs))
                    _extractFileWaits.Remove(outputFile);
                else if (_extractFileWaits.Count == 1)
                {
                    // Failure path: outputFile is empty; if only one request is in flight, resolve it.
                    foreach (var kv in _extractFileWaits) { tcs = kv.Value; }
                    _extractFileWaits.Clear();
                }
            }
            if (tcs == null) return;
            bool ok = !string.IsNullOrEmpty(outputFile) &&
                      (extractionResult == null ||
                       !extractionResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase));
            tcs.TrySetResult(new ExtractFileArgs
            {
                OutputFile = outputFile ?? "",
                State = extractionResult ?? "",
                Success = ok
            });
        }

        // ── ExportHtml / ExportHtmlFromFile callbacks ────────────────────────────
        //
        // OnExportHtmlReady(outputFile, extractionResult) returns the outputFile path
        // we passed in — same pattern as OnTextExtracted above.

        internal struct ExportHtmlArgs
        {
            public string OutputFile;
            public string State;
            public bool Success;
        }

        private readonly Dictionary<string, TaskCompletionSource<ExportHtmlArgs>> _exportHtmlWaits =
            new Dictionary<string, TaskCompletionSource<ExportHtmlArgs>>(StringComparer.OrdinalIgnoreCase);

        public TaskCompletionSource<ExportHtmlArgs> ExpectExportHtml(string outputFile)
        {
            var tcs = new TaskCompletionSource<ExportHtmlArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
                _exportHtmlWaits[outputFile] = tcs;
            return tcs;
        }

        public async Task<ExportHtmlArgs> WaitExportHtmlAsync(TaskCompletionSource<ExportHtmlArgs> tcs, int millisecondsTimeout)
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(millisecondsTimeout)).ConfigureAwait(false);
            if (completed == tcs.Task && tcs.Task.Status == TaskStatus.RanToCompletion)
                return tcs.Task.Result;
            return new ExportHtmlArgs { Success = false, State = "Timed out or was cancelled." };
        }

        public void CancelExportHtmlWait(string outputFile)
        {
            lock (_sync)
                _exportHtmlWaits.Remove(outputFile);
        }

        public void OnExportHtmlReady(string outputFile, string extractionResult)
        {
            TaskCompletionSource<ExportHtmlArgs> tcs = null;
            lock (_sync)
            {
                // The server hands back the outputFile it wrote to on success and an
                // empty string on failure — try both to correlate.
                if (!string.IsNullOrEmpty(outputFile) && _exportHtmlWaits.TryGetValue(outputFile, out tcs))
                    _exportHtmlWaits.Remove(outputFile);
                else if (_exportHtmlWaits.Count == 1)
                {
                    // Failure path: outputFile is empty; if only one request is in flight, resolve it.
                    foreach (var kv in _exportHtmlWaits) { tcs = kv.Value; }
                    _exportHtmlWaits.Clear();
                }
            }
            if (tcs == null) return;
            bool ok = !string.IsNullOrEmpty(outputFile) &&
                      (extractionResult == null ||
                       !extractionResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase));
            tcs.TrySetResult(new ExportHtmlArgs
            {
                OutputFile = outputFile ?? "",
                State = extractionResult ?? "",
                Success = ok
            });
        }

        // ── Tagging callbacks (XS-1577) ──────────────────────────────────────────
        //
        // OnTagsAdded/Removed/Cleared(count) carry only the affected-item count;
        // no correlation. Serialize with FIFO queues per operation kind.

        private readonly Queue<TaskCompletionSource<int>> _tagAddedQueue = new Queue<TaskCompletionSource<int>>();
        private readonly Queue<TaskCompletionSource<int>> _tagRemovedQueue = new Queue<TaskCompletionSource<int>>();
        private readonly Queue<TaskCompletionSource<int>> _tagClearedQueue = new Queue<TaskCompletionSource<int>>();

        public TaskCompletionSource<int> ExpectTagsAdded() => EnqueueTagTcs(_tagAddedQueue);
        public TaskCompletionSource<int> ExpectTagsRemoved() => EnqueueTagTcs(_tagRemovedQueue);
        public TaskCompletionSource<int> ExpectTagsCleared() => EnqueueTagTcs(_tagClearedQueue);

        private TaskCompletionSource<int> EnqueueTagTcs(Queue<TaskCompletionSource<int>> q)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) q.Enqueue(tcs);
            return tcs;
        }

        public async Task<int> WaitTagOpAsync(TaskCompletionSource<int> tcs, int millisecondsTimeout)
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(millisecondsTimeout)).ConfigureAwait(false);
            if (completed == tcs.Task && tcs.Task.Status == TaskStatus.RanToCompletion)
                return tcs.Task.Result;
            return -2; // sentinel for timeout — server uses -1 for arg-length mismatch
        }

        public void OnTagsAdded(int count) => DrainTagCallback(_tagAddedQueue, count);
        public void OnTagsRemoved(int count) => DrainTagCallback(_tagRemovedQueue, count);
        public void OnTagsCleared(int count) => DrainTagCallback(_tagClearedQueue, count);

        private void DrainTagCallback(Queue<TaskCompletionSource<int>> q, int count)
        {
            TaskCompletionSource<int> tcs;
            lock (_sync)
            {
                if (q.Count == 0) return;
                tcs = q.Dequeue();
            }
            tcs.TrySetResult(count);
        }

        // Interface stub — never used by the bridge, but WCF resolves the vtable.
        public void OnExtractTextComplete(string uri, string fileName, bool success) { }

        public void OnDownloadProgress(string uri, string progress) { }
        public void OnDownloadFinished(string uri) { }
        public void OnDownloadError(string uri, string error) { }
        public void OnPSACompleted(int sessionID, int psaRequestID, bool success, string resultDescription) { }
        public void OnSelectionCountChanged(int sessionID, int sequence, int count) { }
        public void OnSelectionIterationReady(int sessionID, int requestID, int selectionCount) { }
        public void OnNextSelectedItems(int sessionID, int requestID, SearchResult[] fieldStrings) { }
        public void OnSerializationComplete(string uri, bool success) { }
        public void OnExtractPiiComplete(string uri, string fileName, bool success, string error) { }
        public void OnExtractPiiProgress(string uri, string progress) { }
        public void OnSelectionPartitionResult(int requestID, int totalSelected, int[] itemCounts) { }
        public void OnExportResultsProgress(int exportedCount, int totalCount) { }
        public void OnExportResultsFinished(int exportedCount, int totalCount) { }
        public void OnExportResultsError(string error) { }
        public void OnExportTagsProgress(int exportedCount, int processedCount, int totalCount) { }
        public void OnExportTagsFinished(int exportedCount, int processedCount, int totalCount) { }
        public void OnExportTagsError(string error) { }
        public void OnImportTagsProgress(int importedCount, int processedCount, int totalCount) { }
        public void OnImportTagsFinished(int importedCount, int processedCount, int totalCount, string reportFile) { }
        public void OnImportTagsError(string error) { }
        public void OnBrokeredSearchFinished(string scannerName, BrokeredSearchResult result) { }
        public void OnBrokeredSearchError(string scannerName, BrokeredSearchError error) { }
        public void OnBrokeredSearchExportProgress(string scannerName, string table, string accountName, int[] receivedCounts, int[] totalCounts) { }
        public void OnBrokeredSearchExportFinished(string scannerName, string table, string accountName, int[] totalCounts) { }
        public void OnBrokeredSearchUpdateMetadataFinished(string scannerName, string table, string accountName) { }
        public void OnBrokeredSearchQueryCanUpdateMetadataFinished(string scannerName, string table, string accountName, bool canUpdateMetadata) { }
        public void OnEnterpriseSearchExportResultsProgress(EnterpriseSearchExportProgress progress) { }
        public void OnEnterpriseSearchExportResultsFinished(EnterpriseSearchExportProgress progress) { }
        public void OnEnterpriseSearchExportResultsError(string error, bool isFatal) { }
        public void OnEnterpriseSearchUpgradeRequired() { }
        public void OnFindDuplicateURIsProgress(double percent, string progress) { }
        public void OnFindDuplicateURIsComplete(string[] uris) { }
        public void OnCheckURISortProgress(double percent, string progress) { }
        public void OnCheckURISortComplete(string[] uris) { }
        public void OnAggregateResultsProgress(AggregateColumnResults result) { }
        public void OnAggregateResultsFinished(AggregateColumnResults result) { }
        public void OnAggregateResultsError(string error) { }
        public void OnGroupAggregateProgress(int processedCount, int totalCount) { }
        public void OnGroupAggregateFinished(int processedCount, int totalCount, string fileName) { }
        public void OnGroupAggregateError(string error) { }
    }
}
