// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// XS-1746: x1_get_content returned <c>"Content extraction failed or timed out."</c> for every failure
    /// alike — a file type that was never content-indexed, a password-protected PDF, a wrong URI, and a real
    /// timeout all produced the same dead end. <see cref="ContentUnavailable"/> is the classifier that turns
    /// the state the service already reported, plus the item's own <c>istatus</c> field, into the actual
    /// reason and the actual fix.
    ///
    /// Tested directly rather than through SearchBridge for the reason ServiceAvailabilityTests gives:
    /// X1MCPSearchConnection's endpoint is derived from the current Windows username with no seam to
    /// redirect it at a fake host, so extracting the classification as a pure function is what makes it
    /// testable at all. The istatus strings below are the genuine ExtractionCodes values, so the inputs
    /// under test are real.
    /// </summary>
    [TestFixture]
    public class ContentUnavailableTests
    {
        /// <summary>
        /// The verbatim state X1SearchManager._getContent reports when extraction produced nothing. Pinned
        /// as a literal so this fixture still describes the original defect years from now.
        /// </summary>
        private const string NoText = "No text extracted";

        private const int Timeout = 120000;

        /// <summary>Builds the flat alternating key/value array GetItemInternal returns.</summary>
        private static string[] Rows(params string[] pairs) => pairs;

        private static string[] RowsWithStatus(string istatus) =>
            Rows("ItemNum", "42", "name", "site-plan.dwg", "istatus", istatus);

        // ── The reported defect ──────────────────────────────────────────────────

        [Test]
        public void FileTypeNotAllowListed_NamesTheAllowListAndTheReindex()
        {
            var d = ContentUnavailable.Diagnose(
                NoText, Timeout, "file://C:/plans/site-plan.dwg",
                RowsWithStatus("File extension is not selected for content indexing"));

            Assert.That(d.Reason, Is.EqualTo(ContentUnavailable.ReasonNotAllowListed));
            Assert.That(d.Message, Does.Contain("Global Allowlist Settings"));
            Assert.That(d.Message, Does.Contain("reindex"));
            Assert.That(d.Message, Does.Contain(".dwg"),
                "the message should name the exact file type to tick, not just 'this file type'");
        }

        [Test]
        public void FileTypeNotAllowListed_DoesNotReadAsAFailure()
        {
            var d = ContentUnavailable.Diagnose(
                NoText, Timeout, "file://C:/plans/site-plan.dwg",
                RowsWithStatus("File extension is not selected for content indexing"));

            // The old text claimed extraction "failed or timed out" for an item where nothing failed and
            // nothing timed out — X1 simply was never asked to index this type's content.
            Assert.That(d.Message, Does.Not.Contain("timed out"));
            Assert.That(d.Message, Does.Not.Contain("failed"));
        }

        // ── istatus -> reason ────────────────────────────────────────────────────

        [TestCase("File extension is not selected for content indexing", ContentUnavailable.ReasonNotAllowListed)]
        [TestCase("Folder is not marked for content indexing",            ContentUnavailable.ReasonFolderMetaOnly)]
        [TestCase("Document is too large",                                ContentUnavailable.ReasonTooLarge)]
        [TestCase("Extracted Text Size limit exceeded",                   ContentUnavailable.ReasonTooLarge)]
        [TestCase("Sub Item Text Size limit exceeded",                    ContentUnavailable.ReasonTooLarge)]
        [TestCase("Sub Document Count limit exceeded",                    ContentUnavailable.ReasonTooLarge)]
        [TestCase("Content is too large; document partially indexed",     ContentUnavailable.ReasonTooLarge)]
        [TestCase("Password protected or encrypted",                      ContentUnavailable.ReasonEncrypted)]
        [TestCase("No content",                                           ContentUnavailable.ReasonNoContent)]
        [TestCase("Extraction timeout",                                   ContentUnavailable.ReasonExtractionFailed)]
        [TestCase("Canceled",                                             ContentUnavailable.ReasonExtractionFailed)]
        [TestCase("FAILED: Extraction provider not found.",               ContentUnavailable.ReasonExtractionFailed)]
        [TestCase("Error - Extraction fail (IOError)",                    ContentUnavailable.ReasonExtractionFailed)]
        [TestCase("Error - X1 encountered unexpected error",              ContentUnavailable.ReasonExtractionFailed)]
        [TestCase("Deferred container indexing",                          ContentUnavailable.ReasonPending)]
        [TestCase("Deferred item indexing within container",              ContentUnavailable.ReasonPending)]
        [TestCase("Container indexing",                                   ContentUnavailable.ReasonPending)]
        public void EveryExtractionCode_MapsToItsOwnReason(string istatus, string expected)
        {
            Assert.That(ContentUnavailable.ReasonFor(istatus), Is.EqualTo(expected));
        }

        [Test]
        public void ReasonFor_IsCaseInsensitive()
        {
            Assert.That(ContentUnavailable.ReasonFor("FILE EXTENSION IS NOT SELECTED FOR CONTENT INDEXING"),
                Is.EqualTo(ContentUnavailable.ReasonNotAllowListed));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Something a newer X1 build invented")]
        public void UnrecognisedOrAbsentStatus_FallsBackToTheGenericReason(string istatus)
        {
            Assert.That(ContentUnavailable.ReasonFor(istatus), Is.EqualTo(ContentUnavailable.ReasonUnknown));
        }

        [Test]
        public void NoIstatusAtAll_ListsEveryLikelyCauseRatherThanGuessingOne()
        {
            var d = ContentUnavailable.Diagnose(NoText, Timeout, "msmail://acct/AAMkAD", Rows("ItemNum", "7"));

            Assert.That(d.Reason, Is.EqualTo(ContentUnavailable.ReasonUnknown));
            Assert.That(d.Message, Does.Contain("allow-list"));
            Assert.That(d.Message, Does.Contain("metadata only"));
            Assert.That(d.Message, Does.Contain("content-size limit"));
            Assert.That(d.Message, Does.Contain("genuinely has no text"));
            Assert.That(d.Message, Does.Contain("Global Allowlist Settings"));
        }

        // ── Reasons the allow-list does NOT fix ──────────────────────────────────

        [TestCase("Password protected or encrypted")]
        [TestCase("Document is too large")]
        [TestCase("No content")]
        [TestCase("Error - Extraction fail (IOError)")]
        public void ReasonsTheAllowListCannotFix_DoNotSendTheUserToTheAllowList(string istatus)
        {
            var d = ContentUnavailable.Diagnose(NoText, Timeout, "file://C:/x/report.pdf", RowsWithStatus(istatus));

            Assert.That(d.Message, Does.Not.Contain("Global Allowlist Settings"),
                "ticking a box in the allow-list does not decrypt a PDF or shrink an over-size document; " +
                "sending its reader there is the old dead end with more words");
        }

        [Test]
        public void FolderMetadataOnly_PointsAtTheFolderSetting_AndStillRequiresAReindex()
        {
            var d = ContentUnavailable.Diagnose(
                NoText, Timeout, "file://C:/archive/notes.txt",
                RowsWithStatus("Folder is not marked for content indexing"));

            Assert.That(d.Reason, Is.EqualTo(ContentUnavailable.ReasonFolderMetaOnly));
            Assert.That(d.Message, Does.Contain("folder"));
            Assert.That(d.Message, Does.Contain("reindex"));
        }

        // ── States that are not "no text" ────────────────────────────────────────

        [Test]
        public void ServerError_IsSurfacedVerbatim_NotReinterpreted()
        {
            var d = ContentUnavailable.Diagnose(
                "Error: Item not found: file://C:/gone.docx", Timeout, "file://C:/gone.docx", null);

            Assert.That(d.Reason, Is.EqualTo(ContentUnavailable.ReasonServerError));
            Assert.That(d.Message, Is.EqualTo("Item not found: file://C:/gone.docx"));
            Assert.That(d.Message, Does.Not.Contain("Global Allowlist Settings"),
                "the service reached the item and described a different, real problem — telling someone " +
                "whose URI was simply wrong to go edit the allow-list sends them chasing the wrong thing");
        }

        [Test]
        public void ServerError_WithNoDetail_StillSaysSomethingUseful()
        {
            var d = ContentUnavailable.Diagnose("Error:", Timeout, "file://C:/x.docx", null);

            Assert.That(d.Message, Is.EqualTo(BridgeConstants.ContentExtractionFailed));
            Assert.That(d.Message, Is.Not.Empty);
        }

        [Test]
        public void Timeout_KeepsTimeoutWording_AndClaimsNothingAboutIndexing()
        {
            var d = ContentUnavailable.Diagnose("Timed out or was cancelled.", 45000, "file://C:/huge.pdf", null);

            Assert.That(d.Reason, Is.EqualTo(ContentUnavailable.ReasonTimedOut));
            Assert.That(d.Message, Does.Contain("45000"));
            Assert.That(d.Message, Does.Contain("timeoutMs"));
            Assert.That(d.Message, Does.Not.Contain("Global Allowlist Settings"),
                "a timeout means we learned nothing about the item; guessing 'your file type isn't " +
                "allow-listed' here is exactly the conflation XS-1746 exists to end");
        }

        [Test]
        public void EmptyState_IsTreatedAsATimeout_NotAsAnAllowListProblem()
        {
            var d = ContentUnavailable.Diagnose("", Timeout, "file://C:/x.pdf", null);

            Assert.That(d.Reason, Is.EqualTo(ContentUnavailable.ReasonTimedOut));
        }

        [Test]
        public void NoTextExtracted_WinsOverTheTimeoutHeuristic()
        {
            // "No text extracted" arrives on a successful round-trip. It must never be re-read as a timeout
            // just because IsTimedOut is the broader matcher.
            Assert.That(ContentUnavailable.IsNoTextExtracted(NoText), Is.True);

            var d = ContentUnavailable.Diagnose(NoText, Timeout, "file://C:/x.dwg",
                RowsWithStatus("File extension is not selected for content indexing"));

            Assert.That(d.Reason, Is.EqualTo(ContentUnavailable.ReasonNotAllowListed));
        }

        [Test]
        public void IsServerError_DoesNotMatchAnOrdinaryState()
        {
            Assert.That(ContentUnavailable.IsServerError(NoText), Is.False);
            Assert.That(ContentUnavailable.IsServerError("Content retrieved from index"), Is.False);
            Assert.That(ContentUnavailable.IsServerError(null), Is.False);
        }

        // ────────────── Whether the extra GetItemInternal call is worth making ──────────────

        [Test]
        public void WantsIndexFields_TrueWhenTheItemCouldExplainItself()
        {
            Assert.That(ContentUnavailable.WantsIndexFields(NoText), Is.True);
            Assert.That(ContentUnavailable.WantsIndexFields("Content retrieved from index"), Is.True,
                "a state that is neither an error nor a timeout still describes a reachable item");
        }

        [TestCase("Error: Item not found: file://C:/gone.docx")]
        [TestCase("Timed out or was cancelled.")]
        [TestCase("")]
        [TestCase(null)]
        public void WantsIndexFields_FalseWhereTheFieldsWouldSayNothing(string state)
        {
            // Diagnose ignores the fields for these states, so fetching them would be a pure extra
            // channel call on an already-failing request.
            Assert.That(ContentUnavailable.WantsIndexFields(state), Is.False);
        }

        [Test]
        public void WantsIndexFields_AgreesWithWhatDiagnoseActuallyUses()
        {
            // The guard and the consumer must not drift: wherever the fields are skipped, passing them
            // anyway must make no difference to the answer.
            foreach (string state in new[] { "Error: Item not found: x", "Timed out or was cancelled.", "" })
            {
                var withFields = ContentUnavailable.Diagnose(
                    state, Timeout, "file://C:/x.dwg",
                    RowsWithStatus("File extension is not selected for content indexing"));
                var withoutFields = ContentUnavailable.Diagnose(state, Timeout, "file://C:/x.dwg", null);

                Assert.That(withFields.Reason, Is.EqualTo(withoutFields.Reason), "state: " + state);
                Assert.That(withFields.Message, Is.EqualTo(withoutFields.Message), "state: " + state);
            }
        }

        // ── Every message is actionable and leaks nothing ────────────────────────

        [TestCase(ContentUnavailable.ReasonNotAllowListed)]
        [TestCase(ContentUnavailable.ReasonFolderMetaOnly)]
        [TestCase(ContentUnavailable.ReasonUnknown)]
        public void EveryAllowListFamilyMessage_SaysAReindexIsRequired(string reason)
        {
            // Allow-list changes apply at index time. Ticking the box and retrying immediately looks exactly
            // like the fix not working, so the call-out is not optional in any of these variants.
            Assert.That(BridgeConstants.ContentNotIndexed(reason, ".dwg"), Does.Contain("reindex"));
        }

        [TestCase(ContentUnavailable.ReasonNotAllowListed)]
        [TestCase(ContentUnavailable.ReasonFolderMetaOnly)]
        [TestCase(ContentUnavailable.ReasonTooLarge)]
        [TestCase(ContentUnavailable.ReasonEncrypted)]
        [TestCase(ContentUnavailable.ReasonNoContent)]
        [TestCase(ContentUnavailable.ReasonPending)]
        [TestCase(ContentUnavailable.ReasonExtractionFailed)]
        [TestCase(ContentUnavailable.ReasonUnknown)]
        public void NoMessageLeaksInternalDetail(string reason)
        {
            foreach (string message in new[]
                     {
                         BridgeConstants.ContentNotIndexed(reason, ".dwg"),
                         BridgeConstants.ContentNotIndexed(reason, null)
                     })
            {
                Assert.That(message, Is.Not.Empty);
                Assert.That(message, Does.Not.Contain("outputFile"));
                Assert.That(message, Does.Not.Contain("x1mcp_content_"));
                Assert.That(message, Does.Not.Contain("net.pipe"));
                Assert.That(message, Does.Not.Contain("istatus"));
            }
        }

        [Test]
        public void WithNoExtensionKnown_TheMessageStillReads()
        {
            string message = BridgeConstants.ContentNotIndexed(ContentUnavailable.ReasonNotAllowListed, null);

            Assert.That(message, Does.Contain("this file type"));
            Assert.That(message, Does.Not.Contain("null"));
        }

        // ── Reading the flat index-field array ───────────────────────────────────

        [Test]
        public void IndexingStatusOf_FindsTheField()
        {
            Assert.That(ContentUnavailable.IndexingStatusOf(RowsWithStatus("No content")), Is.EqualTo("No content"));
        }

        [Test]
        public void IndexingStatusOf_IsCaseInsensitiveOnTheFieldName()
        {
            Assert.That(ContentUnavailable.IndexingStatusOf(Rows("ISTATUS", "No content")), Is.EqualTo("No content"));
        }

        /// <summary>
        /// Shapes GetItemInternal can plausibly hand back. Supplied via TestCaseSource rather than
        /// TestCase because NUnit splats a bare array attribute argument into separate parameters.
        /// </summary>
        private static readonly object[] MalformedOrEmptyRows =
        {
            new object[] { new string[0] },
            new object[] { new[] { "name", "a.txt" } },
            new object[] { new[] { "istatus" } },                    // odd-length: key with no value
            new object[] { new[] { "name", "a.txt", "istatus" } },   // truncated mid-pair
            new object[] { new[] { "istatus", "" } }                 // present but empty
        };

        [TestCaseSource(nameof(MalformedOrEmptyRows))]
        public void IndexingStatusOf_ReturnsNullRatherThanThrowing(string[] rows)
        {
            // This runs while an error response is already being built; it must never turn a bad answer
            // into a thrown one.
            Assert.That(ContentUnavailable.IndexingStatusOf(rows), Is.Null);
        }

        [Test]
        public void IndexingStatusOf_HandlesNull()
        {
            Assert.That(ContentUnavailable.IndexingStatusOf(null), Is.Null);
        }

        // ── Naming the file type ─────────────────────────────────────────────────

        [Test]
        public void ExtensionOf_PrefersTheIndexedName()
        {
            Assert.That(ContentUnavailable.ExtensionOf("file://C:/a/b", Rows("name", "Site Plan.DWG")),
                Is.EqualTo(".dwg"));
        }

        [Test]
        public void ExtensionOf_FallsBackToTheUri()
        {
            Assert.That(ContentUnavailable.ExtensionOf("file://C:/plans/site-plan.dwg", null), Is.EqualTo(".dwg"));
        }

        [TestCase("msmail://acct/AAMkADQ2Zjk3LTRhYmMtOTk5OS0x")]   // opaque mail id
        [TestCase("file://C:/folder/README")]                       // genuinely no extension
        [TestCase("file://C:/folder/archive.")]                     // trailing dot
        [TestCase("teams://team/channel/1699999999999")]            // numeric id, no dot
        [TestCase("")]
        [TestCase(null)]
        public void ExtensionOf_ReturnsNullRatherThanAGuess(string uri)
        {
            // "Enable the .a1b2c3d4 file type" would be nonsense advice. Better to say "this file type".
            Assert.That(ContentUnavailable.ExtensionOf(uri, null), Is.Null);
        }

        // ── The payload x1_get_content actually returns ──────────────────────────

        [Test]
        public void Describe_CarriesModeErrorReasonAndTheRawState()
        {
            var payload = ContentUnavailable.Describe(
                "content", NoText, Timeout, "file://C:/plans/site-plan.dwg",
                RowsWithStatus("File extension is not selected for content indexing"));

            Assert.That(payload.Value<string>("mode"), Is.EqualTo("content"));
            Assert.That(payload.Value<string>("reason"), Is.EqualTo(ContentUnavailable.ReasonNotAllowListed));
            Assert.That(payload.Value<string>("error"), Does.Contain("Global Allowlist Settings"));

            // Demoted, never discarded — support still gets what the service actually said.
            Assert.That(payload.Value<string>("state"), Is.EqualTo(NoText));
        }

        [Test]
        public void Describe_OmitsStateWhenThereIsNone()
        {
            var payload = ContentUnavailable.Describe("content", "", Timeout, "file://C:/x.pdf", null);

            Assert.That(payload["state"], Is.Null);
            Assert.That(payload.Value<string>("error"), Is.Not.Empty);
        }

        [Test]
        public void Describe_CarriesNoTextField_SoCostTrackerRecordsNothing()
        {
            // CostTracker.RecordContent reads result["text"] for mode="content"; an absent field must read
            // as zero extracted characters rather than inflating the savings report with a failure.
            var payload = ContentUnavailable.Describe("content", NoText, Timeout, "file://C:/x.dwg",
                RowsWithStatus("File extension is not selected for content indexing"));

            Assert.That(payload["text"], Is.Null);
        }
    }
}
