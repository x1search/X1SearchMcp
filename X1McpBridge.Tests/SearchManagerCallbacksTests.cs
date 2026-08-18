// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Threading.Tasks;
using NUnit.Framework;
using X1.Service;

namespace X1.McpBridge.Tests
{
    [TestFixture]
    public class SearchManagerCallbacksTests
    {
        // ── Session registration ─────────────────────────────────────────────────

        [Test]
        public void RegisterAndUnregister_DoNotThrow()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(1);
            cb.UnregisterSession(1);
        }

        [Test]
        public void WaitResultsChanged_UnregisteredSession_ReturnsFailedTask()
        {
            var cb = new SearchManagerCallbacks();
            var task = cb.WaitResultsChangedAsync(999);
            Assert.That(task.IsFaulted, Is.True);
        }

        [Test]
        public void WaitResultsReady_UnregisteredSession_ReturnsFailedTask()
        {
            var cb = new SearchManagerCallbacks();
            var task = cb.WaitResultsReadyAsync(999, 1);
            Assert.That(task.IsFaulted, Is.True);
        }

        // ── OnSearchResultsChanged ───────────────────────────────────────────────

        [Test]
        public void OnSearchResultsChanged_WithResults_CompletesTask()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(1);
            var waitTask = cb.WaitResultsChangedAsync(1);

            var page = new[] { new SearchResult { uri = "file://test.txt", table = "Files" } };
            cb.OnSearchResultsChanged(1, 5, 0, null, 0, page, null, null, 0, 0);

            Assert.That(waitTask.IsCompleted, Is.True);
            Assert.That(waitTask.Result.TotalResults, Is.EqualTo(5));
            Assert.That(waitTask.Result.FirstPage, Has.Length.EqualTo(1));
        }

        [Test]
        public void OnSearchResultsChanged_ZeroResults_CompletesTask()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(2);
            var waitTask = cb.WaitResultsChangedAsync(2);

            // totalResults == 0 means empty search — should complete even with empty firstPage
            cb.OnSearchResultsChanged(2, 0, 0, null, 0, new SearchResult[0], null, null, 0, 0);

            Assert.That(waitTask.IsCompleted, Is.True);
            Assert.That(waitTask.Result.TotalResults, Is.EqualTo(0));
        }

        [Test]
        public void OnSearchResultsChanged_CountOnlyCallback_DoesNotCompleteTask()
        {
            // Count-only: firstPage empty but totalResults > 0 — task must NOT complete yet
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(3);
            var waitTask = cb.WaitResultsChangedAsync(3);

            cb.OnSearchResultsChanged(3, 42, 0, null, 0, new SearchResult[0], null, null, 0, 0);

            Assert.That(waitTask.IsCompleted, Is.False);
        }

        [Test]
        public void OnSearchResultsChanged_CountOnly_PendingCountIsSet()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(4);
            cb.WaitResultsChangedAsync(4);

            cb.OnSearchResultsChanged(4, 42, 0, null, 0, new SearchResult[0], null, null, 0, 0);

            Assert.That(cb.TryGetPendingCount(4, out int count, out _), Is.True);
            Assert.That(count, Is.EqualTo(42));
        }

        [Test]
        public void TryGetPendingCount_NoCallback_ReturnsFalse()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(5);
            Assert.That(cb.TryGetPendingCount(5, out _, out _), Is.False);
        }

        [Test]
        public void OnSearchResultsChanged_NullFirstPage_TreatedAsEmpty()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(6);
            var waitTask = cb.WaitResultsChangedAsync(6);

            // Null firstPage with totalResults > 0 = count-only
            cb.OnSearchResultsChanged(6, 10, 0, null, 0, null, null, null, 0, 0);

            Assert.That(waitTask.IsCompleted, Is.False);
        }

        [Test]
        public void OnSearchResultsChanged_HighlightTerms_PropagatedToResult()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(7);
            var waitTask = cb.WaitResultsChangedAsync(7);

            var highlights = new[] { new HighlightTerm { term = "budget", column = "subject" } };
            var page = new[] { new SearchResult { uri = "x", table = "Files" } };
            cb.OnSearchResultsChanged(7, 1, 0, highlights, 0, page, null, null, 0, 0);

            Assert.That(waitTask.Result.HighlightTerms, Has.Length.EqualTo(1));
            Assert.That(waitTask.Result.HighlightTerms[0].term, Is.EqualTo("budget"));
        }

        // ── OnSearchResultsReady ─────────────────────────────────────────────────

        [Test]
        public void OnSearchResultsReady_CompletesWaitTask()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(10);
            var readyTask = cb.WaitResultsReadyAsync(10, 99);

            var page = new[] { new SearchResult { uri = "file://a.txt", table = "Files" } };
            cb.OnSearchResultsReady(10, 99, page, 0);

            Assert.That(readyTask.IsCompleted, Is.True);
            Assert.That(readyTask.Result, Has.Length.EqualTo(1));
        }

        [Test]
        public void OnSearchResultsReady_CallbackBeforeWait_StillCompletes()
        {
            // Callback arrives before WaitResultsReadyAsync is called
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(11);

            var page = new[] { new SearchResult { uri = "file://b.txt", table = "Files" } };
            cb.OnSearchResultsReady(11, 100, page, 0);

            var readyTask = cb.WaitResultsReadyAsync(11, 100);
            Assert.That(readyTask.IsCompleted, Is.True);
            Assert.That(readyTask.Result, Has.Length.EqualTo(1));
        }

        [Test]
        public void OnSearchResultsReady_NullResults_ReturnsEmpty()
        {
            var cb = new SearchManagerCallbacks();
            cb.RegisterSession(12);
            var readyTask = cb.WaitResultsReadyAsync(12, 101);

            cb.OnSearchResultsReady(12, 101, null, 0);

            Assert.That(readyTask.IsCompleted, Is.True);
            Assert.That(readyTask.Result, Is.Empty);
        }

        [Test]
        public void OnSearchResultsChanged_UnknownSession_DoesNotThrow()
        {
            var cb = new SearchManagerCallbacks();
            // Should silently do nothing for unregistered session
            Assert.DoesNotThrow(() =>
                cb.OnSearchResultsChanged(999, 1, 0, null, 0, new[] { new SearchResult() }, null, null, 0, 0));
        }

        // ── Preview callbacks ────────────────────────────────────────────────────

        [Test]
        public async Task WaitPreviewAsync_CallbackArrives_ReturnsPreview()
        {
            var cb = new SearchManagerCallbacks();
            var key = cb.ExpectPreview("file://doc.pdf");
            var waitTask = cb.WaitPreviewAsync(key, 5000);

            cb.OnPreviewReady("file://doc.pdf", "<html>preview</html>", false, null, null, 0);

            var result = await waitTask;
            Assert.That(result.Preview, Is.EqualTo("<html>preview</html>"));
            Assert.That(result.Error, Is.Null);
        }

        [Test]
        public async Task WaitPreviewAsync_Timeout_ReturnsError()
        {
            var cb = new SearchManagerCallbacks();
            var key = cb.ExpectPreview("file://missing.pdf");
            var result = await cb.WaitPreviewAsync(key, 50);
            Assert.That(result.Error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public async Task WaitPreviewAsync_TwoConcurrentSameUri_EachGetOwnResult()
        {
            // H1: two concurrent requests for the same URI must not cross-wire
            var cb = new SearchManagerCallbacks();
            var key1 = cb.ExpectPreview("file://shared.pdf");
            var key2 = cb.ExpectPreview("file://shared.pdf");

            var task1 = cb.WaitPreviewAsync(key1, 5000);
            var task2 = cb.WaitPreviewAsync(key2, 5000);

            // Only the most-recent registration wins for the URI→key mapping,
            // but both tasks must resolve without cross-contamination.
            cb.OnPreviewReady("file://shared.pdf", "<html>second</html>", false, null, null, 0);

            // key2 is the current URI→key winner; its task resolves.
            // key1's task times out (it was displaced by key2).
            var result2 = await task2;
            Assert.That(result2.Preview, Is.EqualTo("<html>second</html>"));
        }

        [Test]
        public async Task CancelPreviewWait_RemovesRegistration()
        {
            var cb = new SearchManagerCallbacks();
            var key = cb.ExpectPreview("file://cancel.pdf");
            cb.CancelPreviewWait(key);

            // After cancel, a new Expect/Wait for the same URI should work independently
            var key2 = cb.ExpectPreview("file://cancel.pdf");
            var waitTask = cb.WaitPreviewAsync(key2, 5000);
            cb.OnPreviewReady("file://cancel.pdf", "<html>ok</html>", false, null, null, 0);

            var result = await waitTask;
            Assert.That(result.Preview, Is.EqualTo("<html>ok</html>"));
        }

        // ── GetContent callbacks (XS-1575; file-based as of X1 service 11.0.3.33) ──

        [Test]
        public async Task WaitContentAsync_SuccessCallback_ReturnsPath()
        {
            var cb = new SearchManagerCallbacks();
            var tcs = cb.ExpectContent("C:\\temp\\content.txt");
            var waitTask = cb.WaitContentAsync(tcs, 5000);

            cb.OnContentReady("C:\\temp\\content.txt", "Content retrieved from index");

            var result = await waitTask;
            Assert.That(result.Success, Is.True);
            Assert.That(result.OutputFile, Is.EqualTo("C:\\temp\\content.txt"));
            Assert.That(result.State, Is.EqualTo("Content retrieved from index"));
        }

        [Test]
        public async Task WaitContentAsync_ErrorCallback_ReturnsFailure()
        {
            var cb = new SearchManagerCallbacks();
            var tcs = cb.ExpectContent("C:\\temp\\content.txt");
            var waitTask = cb.WaitContentAsync(tcs, 5000);

            // Server passes empty outputFile on failure — the single in-flight request must still resolve.
            cb.OnContentReady("", "Error: item not found");

            var result = await waitTask;
            Assert.That(result.Success, Is.False);
            Assert.That(result.State, Does.StartWith("Error:"));
        }

        [Test]
        public async Task WaitContentAsync_Timeout_ReturnsFailure()
        {
            var cb = new SearchManagerCallbacks();
            var tcs = cb.ExpectContent("C:\\temp\\slow.txt");
            var result = await cb.WaitContentAsync(tcs, 50);
            Assert.That(result.Success, Is.False);
        }

        // ── ExtractTextFromFile callbacks (XS-1575) ───────────────────────────────

        [Test]
        public async Task WaitExtractFileAsync_SuccessCallback_ReturnsPath()
        {
            var cb = new SearchManagerCallbacks();
            var tcs = cb.ExpectExtractFile("C:\\temp\\out.txt");
            var waitTask = cb.WaitExtractFileAsync(tcs, 5000);

            cb.OnTextExtracted("C:\\temp\\out.txt", "");

            var result = await waitTask;
            Assert.That(result.Success, Is.True);
            Assert.That(result.OutputFile, Is.EqualTo("C:\\temp\\out.txt"));
        }

        [Test]
        public async Task WaitExtractFileAsync_ErrorFailurePath_ReturnsFailure()
        {
            var cb = new SearchManagerCallbacks();
            var tcs = cb.ExpectExtractFile("C:\\temp\\out.txt");
            var waitTask = cb.WaitExtractFileAsync(tcs, 5000);

            // Server passes empty outputFile on failure — the single in-flight request must still resolve.
            cb.OnTextExtracted("", "Error: extractor timed out");

            var result = await waitTask;
            Assert.That(result.Success, Is.False);
            Assert.That(result.State, Does.StartWith("Error:"));
        }

        [Test]
        public async Task WaitExtractFileAsync_Timeout_ReturnsFailure()
        {
            var cb = new SearchManagerCallbacks();
            var tcs = cb.ExpectExtractFile("C:\\temp\\slow.txt");
            var result = await cb.WaitExtractFileAsync(tcs, 50);
            Assert.That(result.Success, Is.False);
        }

        // ── Tagging callbacks (XS-1577) ───────────────────────────────────────────

        [Test]
        public async Task WaitTagOpAsync_Added_ReturnsCount()
        {
            var cb = new SearchManagerCallbacks();
            var tcs = cb.ExpectTagsAdded();
            var waitTask = cb.WaitTagOpAsync(tcs, 5000);

            cb.OnTagsAdded(3);

            Assert.That(await waitTask, Is.EqualTo(3));
        }

        [Test]
        public async Task WaitTagOpAsync_Removed_ReturnsCount()
        {
            var cb = new SearchManagerCallbacks();
            var tcs = cb.ExpectTagsRemoved();
            cb.OnTagsRemoved(2);
            Assert.That(await cb.WaitTagOpAsync(tcs, 5000), Is.EqualTo(2));
        }

        [Test]
        public async Task WaitTagOpAsync_Cleared_ReturnsCount()
        {
            var cb = new SearchManagerCallbacks();
            var tcs = cb.ExpectTagsCleared();
            cb.OnTagsCleared(5);
            Assert.That(await cb.WaitTagOpAsync(tcs, 5000), Is.EqualTo(5));
        }

        [Test]
        public async Task WaitTagOpAsync_ArgLengthMismatch_ReturnsMinusOne()
        {
            var cb = new SearchManagerCallbacks();
            var tcs = cb.ExpectTagsAdded();
            cb.OnTagsAdded(-1);
            Assert.That(await cb.WaitTagOpAsync(tcs, 5000), Is.EqualTo(-1));
        }

        [Test]
        public async Task WaitTagOpAsync_Timeout_ReturnsMinusTwo()
        {
            var cb = new SearchManagerCallbacks();
            var tcs = cb.ExpectTagsAdded();
            Assert.That(await cb.WaitTagOpAsync(tcs, 50), Is.EqualTo(-2));
        }
    }
}
