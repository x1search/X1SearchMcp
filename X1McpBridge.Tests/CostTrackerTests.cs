// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// These tests exist mostly to pin the HONESTY invariants, not the arithmetic. Three earlier
    /// revisions of CostTracker each shipped a plausible number that overstated savings for a
    /// different reason, so the rules that stop that recurring are asserted directly: a path is not
    /// a payload, no claim without evidence, thin evidence widens the interval, and things that are
    /// not token savings never enter the savings figure.
    /// </summary>
    [TestFixture]
    public class CostTrackerTests
    {
        private string _tmpFile;

        [SetUp]
        public void SetUp()
        {
            _tmpFile = Path.Combine(Path.GetTempPath(), "x1mcp_stats_test_" + Guid.NewGuid().ToString("N") + ".json");
            CostTracker.OverrideStatsPath(_tmpFile);
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_tmpFile)) File.Delete(_tmpFile);
            CostTracker.OverrideStatsPath(null);
        }

        private JObject Stats() { return JObject.Parse(File.ReadAllText(_tmpFile)); }

        private static long Low(JObject report)  { return report["comparison"].Value<long>("tokensSavedLow"); }
        private static long High(JObject report) { return report["comparison"].Value<long>("tokensSavedHigh"); }

        // ==================================================================
        // Observed figures — these involve no model and must simply be true
        // ==================================================================

        [Test]
        public void Observed_RecordsCallsTokensDurationAndItems()
        {
            var result = new JObject { ["totalResults"] = 5, ["returned"] = 5, ["results"] = new JArray() };

            CostTracker.RecordSearch(result, 123L);

            var observed = CostTracker.GetReport()["observed"] as JObject;
            Assert.IsNotNull(observed);
            Assert.AreEqual(1, observed.Value<int?>("totalCalls"));
            Assert.AreEqual(123L, observed.Value<long?>("avgDurationMs"));
            Assert.AreEqual(5L, observed.Value<long?>("itemsReturned"));
            Assert.Greater(observed.Value<long?>("x1TokensInContext") ?? 0L, 0L);
        }

        [Test]
        public void Observed_AveragesDurationAcrossCalls()
        {
            var r = new JObject { ["totalResults"] = 1, ["returned"] = 1, ["results"] = new JArray() };
            CostTracker.RecordSearch(r, 100L);
            CostTracker.RecordSearch(r, 300L);

            var observed = CostTracker.GetReport()["observed"] as JObject;
            Assert.AreEqual(200L, observed.Value<long?>("avgDurationMs"));
        }

        // ==================================================================
        // Bounds: the interval must bracket, and must widen when evidence is thin
        // ==================================================================

        [Test]
        public void Bounds_LowIsNeverAboveHigh()
        {
            var search = new JObject { ["totalResults"] = 3, ["returned"] = 3, ["results"] = new JArray() };
            var email = new JObject { ["mode"] = "content", ["text"] = new string('x', 4000) };

            CostTracker.RecordSearch(search, 10L);
            CostTracker.RecordContent(email, "MSMail", "msmail://-1/AAMk", 10L);

            var report = CostTracker.GetReport();
            Assert.LessOrEqual(Low(report), High(report));
        }

        [Test]
        public void Bounds_MetadataSearch_UsesMeasuredMultipleForHighAndDiscountedFloorForLow()
        {
            // Metadata evidence: multiple 2.13, n=2 -> confidence 0.55
            //   low multiple  = 1 + (2.13 - 1) * 0.55 = 1.6215
            //   high multiple = 2.13
            var result = new JObject { ["totalResults"] = 1, ["returned"] = 1, ["results"] = new JArray() };
            CostTracker.RecordSearch(result, 10L);

            var stats = Stats();
            long x1 = stats.Value<long>("x1Tokens");
            long low = stats.Value<long>("baselineLow");
            long high = stats.Value<long>("baselineHigh");

            Assert.AreEqual((long)Math.Round(x1 * 1.6215), low);
            Assert.AreEqual((long)Math.Round(x1 * 2.13), high);
        }

        [Test]
        public void Bounds_TextContent_ThinnerEvidenceGivesProportionallyWiderInterval()
        {
            // Text evidence is n=1 (confidence 0.40); metadata is n=2 (0.55). For the same measured
            // multiple the n=1 category must claim a LOWER floor — that is the whole mechanism.
            var text = new JObject { ["mode"] = "content", ["text"] = new string('x', 40000) };
            CostTracker.RecordContent(text, "MSMail", "msmail://-1/AAMk", 10L);

            var stats = Stats();
            long x1 = stats.Value<long>("x1Tokens");
            long low = stats.Value<long>("baselineLow");
            long high = stats.Value<long>("baselineHigh");

            // low = 1 + (2.17-1)*0.40 = 1.468 ; high = 2.17
            Assert.AreEqual((long)Math.Round(x1 * 1.468), low);
            Assert.AreEqual((long)Math.Round(x1 * 2.17), high);

            double lowFrac = (double)(low - x1) / low;
            double highFrac = (double)(high - x1) / high;
            Assert.Less(lowFrac, highFrac, "The interval must actually span, not collapse to a point.");
        }

        // ==================================================================
        // No claim without evidence
        // ==================================================================

        [Test]
        public void UnmeasuredCategory_ClaimsNothing()
        {
            // Structured/tabular has no paired sample, so it must not borrow the text coefficient.
            var xlsx = new JObject { ["mode"] = "content", ["text"] = new string('x', 8000) };

            CostTracker.RecordContent(xlsx, "Files", @"file://D:\archive\budget.xlsx", 10L);

            var report = CostTracker.GetReport();
            Assert.AreEqual(0L, Low(report));
            Assert.AreEqual(0L, High(report),
                "An unmeasured category must claim nothing at BOTH ends — borrowing a neighbour's " +
                "coefficient is how a previous revision invented savings it had no data for.");
        }

        [Test]
        public void EvidenceTable_ReportsSampleCountAndWhatEachCategoryClaims()
        {
            var report = CostTracker.GetReport();
            var evidence = report["evidence"] as JArray;
            Assert.IsNotNull(evidence);

            bool sawUnmeasured = false;
            foreach (JObject row in evidence)
            {
                Assert.IsNotNull(row["pairedSamples"], "Every evidence row must state its n.");
                Assert.IsFalse(string.IsNullOrEmpty(row.Value<string>("basis")));
                if ((row.Value<string>("claims") ?? "").Contains("nothing")) sawUnmeasured = true;
            }
            Assert.IsTrue(sawUnmeasured, "The unmeasured category must openly say it claims nothing.");
        }

        [Test]
        public void PdfWithDenseText_PricedAsText_NotVision()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "x1mcp_tl_" + Guid.NewGuid().ToString("N") + ".pdf");
            File.WriteAllBytes(tmp, new byte[20000]);
            try
            {
                // 8000 chars out of a 20 KB file => dense => a text layer existed => a local parse
                // would have reached the same text, so no vision bound is available.
                var result = new JObject { ["mode"] = "content", ["text"] = new string('x', 8000) };
                CostTracker.RecordContent(result, "Files", "file://" + tmp, 10L);

                var stats = Stats();
                long x1 = stats.Value<long>("x1Tokens");
                Assert.AreEqual((long)Math.Round(x1 * 2.17), stats.Value<long>("baselineHigh"),
                    "Dense text means the text-content bound applies, not a per-page vision bill.");
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        [Test]
        public void ScannedPdfWithNoText_VisionAppliesToHighBoundOnly()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "x1mcp_scan_" + Guid.NewGuid().ToString("N") + ".pdf");
            File.WriteAllBytes(tmp, new byte[500000]);   // 500 KB => 10 estimated scanned pages
            try
            {
                var result = new JObject { ["mode"] = "content", ["text"] = new string('x', 40) };
                CostTracker.RecordContent(result, "Files", "file://" + tmp, 10L);

                var stats = Stats();
                long x1 = stats.Value<long>("x1Tokens");
                Assert.AreEqual(15000L, stats.Value<long>("baselineHigh"), "10 pages x 1500 tokens/page");
                Assert.AreEqual(x1, stats.Value<long>("baselineLow"),
                    "The per-page figure is cited, not measured, so it must not establish a floor.");
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        [Test]
        public void UnmeasurablePdf_ClaimsNoVisionBound()
        {
            var result = new JObject { ["mode"] = "content", ["text"] = new string('x', 40) };

            CostTracker.RecordContent(result, "Files", @"file://D:\nonexistent\ghost.pdf", 10L);

            var stats = Stats();
            long x1 = stats.Value<long>("x1Tokens");
            // Falls back to the text bound; emphatically not 1500+ per page.
            Assert.AreEqual((long)Math.Round(x1 * 2.17), stats.Value<long>("baselineHigh"));
        }

        // ==================================================================
        // A path is not a payload
        // ==================================================================

        [Test]
        public void PathReturningPreview_CountedInFull_BecauseTheRequestFormAsksForOutOfContextUse()
        {
            var result = new JObject
            {
                ["mode"] = "file",
                ["previewType"] = "pdf",
                ["bytes"] = 3_000_000
            };

            CostTracker.RecordPreview(result, "file", 50L, "Files", @"file://C:\docs\big.pdf");

            var report = CostTracker.GetReport();
            // Asking for output="file" rather than output="inline" IS the request to keep content out
            // of context, so these tokens are avoided rather than postponed and the floor is not zero.
            // The ceiling is what read_resource would have inlined (3,000,000/4, less x1's 80) —
            // measured, not a base64 expansion.
            Assert.AreEqual(749920L, High(report));
            Assert.AreEqual(High(report), Low(report),
                "Under the declared out-of-context-use assumption the floor meets the ceiling.");
            Assert.AreEqual(0L, report["notSavings"].Value<long>("tokensDeferred"),
                "They must not be reported as deferred AND saved — that would double-count them.");
        }

        [Test]
        public void PathReturningPreview_CeilingScalesWithFragmentNotSourceBytes()
        {
            var result = new JObject { ["mode"] = "file", ["previewType"] = "pdf", ["bytes"] = 400000 };
            CostTracker.RecordPreview(result, "file", 50L, "Files", @"file://C:\docs\report.pdf");

            var report = CostTracker.GetReport();
            Assert.AreEqual(100000L - 80L, High(report), "400000 fragment bytes / 4, less x1's 80.");
            Assert.AreEqual(High(report), Low(report));
        }

        [Test]
        public void ContentPreviewMode_ClaimsNothing_NoFragmentToSizeTheCeilingFrom()
        {
            var result = new JObject { ["mode"] = "preview", ["preview"] = @"C:\temp\frag.html" };

            CostTracker.RecordContent(result, "Files", @"file://D:\nonexistent\ghost.pdf", 10L);

            var report = CostTracker.GetReport();
            Assert.AreEqual(0L, Low(report));
            Assert.AreEqual(0L, High(report),
                "mode=preview returns a bare path, and extracted text is a small, highly variable " +
                "fraction of a binary's bytes — so there is nothing to size the ceiling from. " +
                "Under-counts rather than guesses.");
            Assert.AreEqual(0L, report["notSavings"].Value<long>("tokensDeferred"),
                "Equally unmeasurable here, so left unrecorded rather than guessed.");
        }

        [Test]
        public void InlinePdfPreview_ClaimsNothing_BothSidesHoldTheSameUnreadBytes()
        {
            var result = new JObject { ["previewType"] = "pdf", ["html"] = new string('x', 8000) };

            CostTracker.RecordPreview(result, "inline", 50L, "Files", @"file://C:\docs\scan.pdf");

            var report = CostTracker.GetReport();
            Assert.AreEqual(0L, Low(report));
            Assert.AreEqual(0L, High(report));
        }

        [Test]
        public void InlineDocxPreview_UsesTextBound()
        {
            var result = new JObject { ["previewType"] = "docx", ["html"] = new string('x', 40000) };

            CostTracker.RecordPreview(result, "inline", 50L, "Files", @"file://C:\docs\memo.docx");

            var stats = Stats();
            long x1 = stats.Value<long>("x1Tokens");
            Assert.AreEqual(10000L, x1);
            Assert.AreEqual((long)Math.Round(x1 * 2.17), stats.Value<long>("baselineHigh"));
        }

        // ==================================================================
        // Capability gains: reachability only, and cloud-synced paths excluded
        // ==================================================================

        [Test]
        public void CapabilityGain_CountsTrulyLocalFile()
        {
            var result = new JObject { ["mode"] = "content", ["text"] = new string('x', 4000) };
            CostTracker.RecordContent(result, "Files", @"file://D:\archive\report.xlsx", 10L);

            Assert.AreEqual(1, CostTracker.GetReport()["notSavings"].Value<int>("capabilityGainCount"));
        }

        [Test]
        public void CapabilityGain_CountsPlainLocalTextFileToo()
        {
            var result = new JObject { ["mode"] = "content", ["text"] = new string('x', 4000) };
            CostTracker.RecordContent(result, "Files", @"file://D:\archive\notes.txt", 10L);

            Assert.AreEqual(1, CostTracker.GetReport()["notSavings"].Value<int>("capabilityGainCount"),
                "The bar is 'the connector could not reach it', not 'it was a hard file type'.");
        }

        [Test]
        public void CapabilityGain_ExcludesOneDriveSyncedFolder()
        {
            var result = new JObject { ["mode"] = "content", ["text"] = new string('x', 4000) };
            CostTracker.RecordContent(result, "Files",
                @"file://C:\Users\Someone\OneDrive - Contoso, Inc\Documents\report.xlsx", 10L);

            Assert.AreEqual(0, CostTracker.GetReport()["notSavings"].Value<int>("capabilityGainCount"),
                "Every 'capability gain' in the first shipped report was a synced OneDrive file.");
        }

        [Test]
        public void CapabilityGain_ExtractFileRechecksThePath()
        {
            var result = new JObject
            {
                ["text"] = new string('x', 4000),
                ["path"] = @"C:\Users\Someone\OneDrive - Contoso, Inc\Documents\budget.xlsx"
            };
            CostTracker.RecordExtractFile(result, 10L);

            Assert.AreEqual(0, CostTracker.GetReport()["notSavings"].Value<int>("capabilityGainCount"),
                "x1_extract_file used to assert isLocal unconditionally, which over-counted.");
        }

        [TestCase(@"file://C:\Users\S\OneDrive\a.xlsx")]
        [TestCase(@"file://C:\Users\S\OneDrive - X1 Discovery, Inc\Documents\a.xlsx")]
        [TestCase(@"file://C:\Users\S\Dropbox\a.xlsx")]
        [TestCase(@"file://C:\Users\S\Google Drive\a.xlsx")]
        [TestCase(@"C:\Users\S\iCloudDrive\a.xlsx")]
        public void IsUnderCloudSyncRoot_DetectsSyncedRoots(string path)
        {
            Assert.IsTrue(CostTracker.IsUnderCloudSyncRoot(path));
        }

        [TestCase(@"file://D:\archive\a.xlsx")]
        [TestCase(@"file://C:\Users\S\Documents\a.xlsx")]
        // Matched per directory segment, so a file merely NAMED for a service does not count.
        [TestCase(@"file://D:\notes\dropbox-migration-plan.txt")]
        public void IsUnderCloudSyncRoot_IgnoresUnsyncedPaths(string path)
        {
            Assert.IsFalse(CostTracker.IsUnderCloudSyncRoot(path));
        }

        [Test]
        public void CapabilityGain_IgnoresTrivialExtraction()
        {
            var result = new JObject { ["mode"] = "content", ["text"] = "" };
            CostTracker.RecordContent(result, "Files", @"file://D:\archive\empty.xlsx", 10L);

            Assert.AreEqual(0, CostTracker.GetReport()["notSavings"].Value<int>("capabilityGainCount"));
        }

        // ==================================================================
        // Classification
        // ==================================================================

        // Expected category passed as a string because RetrievalCategory is internal and a public
        // test signature cannot expose it.
        [TestCase("MSMail", "msmail://-1/AAMk", "TextContent")]
        [TestCase("Teams", "teams://chat/1", "TextContent")]
        [TestCase("Files", @"file://C:\d\a.xlsx", "StructuredTabular")]
        [TestCase("Files", @"file://C:\d\a.pdf", "ImageOcr")]
        [TestCase("Files", @"file://C:\d\a.docx", "TextContent")]
        // Opaque cloud id: no extension to read, so it falls back to text — an under-count, which is
        // the acceptable direction.
        [TestCase("OneDrive", "onedrive://123/01TUPBILZJ3", "TextContent")]
        public void Classify_MapsTableAndExtension(string table, string uri, string expected)
        {
            Assert.AreEqual(expected, CostTracker.Classify(table, uri).ToString());
        }

        // ==================================================================
        // Report shape and composition invariants
        // ==================================================================

        [Test]
        public void Report_SeparatesObservedFromModelledFromNeither()
        {
            var result = new JObject { ["totalResults"] = 1, ["returned"] = 1, ["results"] = new JArray() };
            CostTracker.RecordSearch(result, 10L);

            var report = CostTracker.GetReport();
            Assert.IsNotNull(report["observed"], "Observed facts must be their own section.");
            Assert.IsNotNull(report["comparison"], "The modelled comparison must be its own section.");
            Assert.IsNotNull(report["notSavings"], "Deferred/capability figures must be their own section.");
            Assert.IsNotNull(report["evidence"]);
            Assert.IsNotNull(report["knownLimitations"]);
        }

        [Test]
        public void Report_StatesHowTheAlternativeActuallyBehaves()
        {
            var result = new JObject { ["totalResults"] = 1, ["returned"] = 1, ["results"] = new JArray() };
            CostTracker.RecordSearch(result, 10L);

            string behaviour = CostTracker.GetReport()["comparison"]
                .Value<string>("howTheAlternativeBehaves");
            Assert.IsFalse(string.IsNullOrEmpty(behaviour),
                "The connector's behaviour drives every binary comparison, so it must be stated.");
            Assert.IsTrue(behaviour.Contains("MEASURED"),
                "It is a measurement now, not the configurable assumption it used to be.");

            // The remaining assumption is about USAGE, which no measurement of the software settles,
            // so it must be declared in the report rather than folded in silently.
            string usage = CostTracker.GetReport()["comparison"].Value<string>("usageAssumption");
            Assert.IsFalse(string.IsNullOrEmpty(usage));
        }

        [Test]
        public void Report_BlendedRangeContainsEveryPerCategoryFigure()
        {
            // Composition sanity check: the blended total cannot be smaller than its largest part.
            // This is the invariant that would have caught revision 2's 1.6M fragment claim at once.
            var search = new JObject { ["totalResults"] = 3, ["returned"] = 3, ["results"] = new JArray() };
            var email  = new JObject { ["mode"] = "content", ["text"] = new string('x', 12000) };
            var preview = new JObject { ["mode"] = "file", ["previewType"] = "pdf", ["bytes"] = 900000 };

            CostTracker.RecordSearch(search, 10L);
            CostTracker.RecordContent(email, "MSMail", "msmail://-1/AAMk", 20L);
            CostTracker.RecordPreview(preview, "file", 30L, "Files", @"file://C:\docs\big.pdf");

            var report = CostTracker.GetReport();
            long totalHigh = High(report);
            var rows = report["comparison"]["byCategory"] as JArray;

            long sumHigh = 0;
            foreach (JObject row in rows)
            {
                long rowHigh = row.Value<long>("tokensSavedHigh");
                Assert.LessOrEqual(rowHigh, totalHigh, "No single category may exceed the blended total.");
                sumHigh += rowHigh;
            }
            Assert.AreEqual(sumHigh, totalHigh, "The blended total must be exactly the sum of its parts.");
        }

        [Test]
        public void Report_EveryCategoryRowNamesItsCounterfactual()
        {
            var search = new JObject { ["totalResults"] = 1, ["returned"] = 1, ["results"] = new JArray() };
            CostTracker.RecordSearch(search, 10L);
            CostTracker.RecordTagOp(new JObject { ["op"] = "add" }, 5L);

            var rows = CostTracker.GetReport()["comparison"]["byCategory"] as JArray;
            foreach (JObject row in rows)
                Assert.IsFalse(string.IsNullOrEmpty(row.Value<string>("counterfactual")),
                    "A row without a stated counterfactual is an unexplained claim.");
        }

        [Test]
        public void Report_ReadingItRepeatedlyDoesNotMoveTheFigureItReports()
        {
            // Authoring marketing copy means calling this many times in a session. Each call is real
            // usage with no counterfactual, so if it entered the comparison denominator the quotable
            // percentage would sag every time it was read — 32.1% to 16.1% over twenty reads in the
            // live sample. The number must be stable under observation to be quotable at all.
            var email = new JObject { ["mode"] = "content", ["text"] = new string('x', 40000) };
            CostTracker.RecordContent(email, "MSMail", "msmail://-1/AAMk", 10L);

            var first = CostTracker.GetReport();
            double fracLow = first["comparison"].Value<double>("savingsFractionLow");
            double fracHigh = first["comparison"].Value<double>("savingsFractionHigh");
            long savedLow = Low(first);

            for (int i = 0; i < 20; i++)
                CostTracker.RecordCostSavingsQuery(990);

            var after = CostTracker.GetReport();
            Assert.AreEqual(fracLow, after["comparison"].Value<double>("savingsFractionLow"), 0.001);
            Assert.AreEqual(fracHigh, after["comparison"].Value<double>("savingsFractionHigh"), 0.001);
            Assert.AreEqual(savedLow, Low(after));

            // ...while the observed call count still tells the truth about what happened.
            Assert.AreEqual(21, after["observed"].Value<int>("totalCalls"));
            Assert.AreEqual(1, after["comparison"].Value<int>("callsCompared"));
        }

        [Test]
        public void Report_BookkeepingCallsAreObservedButNotCompared()
        {
            CostTracker.RecordTagOp(new JObject { ["op"] = "add" }, 5L);
            CostTracker.RecordAction(new JObject { ["action"] = "open" }, 5L);
            CostTracker.RecordSources(new JObject { ["sources"] = new JArray() }, 5L);

            var report = CostTracker.GetReport();
            Assert.AreEqual(3, report["observed"].Value<int>("totalCalls"),
                "They happened, so observed must count them.");
            Assert.AreEqual(0, report["comparison"].Value<int>("callsCompared"),
                "But none had a counterfactual, so none belong in the comparison.");
        }

        [Test]
        public void Report_DollarFigureIsARange()
        {
            var email = new JObject { ["mode"] = "content", ["text"] = new string('x', 400000) };
            CostTracker.RecordContent(email, "MSMail", "msmail://-1/AAMk", 10L);

            string usd = CostTracker.GetReport()["comparison"].Value<string>("costSavingUsd");
            Assert.IsTrue(usd.Contains(" to $"),
                "A single dollar figure ignores prompt caching and reads as a promise; report a span.");
        }

        [Test]
        public void Report_LegacyFlatFieldsCarryTheConservativeFigure()
        {
            var email = new JObject { ["mode"] = "content", ["text"] = new string('x', 40000) };
            CostTracker.RecordContent(email, "MSMail", "msmail://-1/AAMk", 10L);

            var report = CostTracker.GetReport();
            Assert.AreEqual(Low(report), report.Value<long?>("estimatedTokensSaved"),
                "Older readers must land on the floor, never the ceiling.");
        }

        [Test]
        public void Report_NoData_IsZeroedNotEmpty()
        {
            var report = CostTracker.GetReport();
            Assert.AreEqual(0L, report["observed"].Value<long>("x1TokensInContext"));
            Assert.AreEqual(0L, Low(report));
            Assert.AreEqual(0L, High(report));
            Assert.AreEqual(0, report["notSavings"].Value<int>("capabilityGainCount"));
        }

        [Test]
        public void Report_ListsKnownLimitations()
        {
            var limitations = CostTracker.GetReport()["knownLimitations"] as JArray;
            Assert.Greater(limitations.Count, 0,
                "The report must carry its own caveats rather than relying on a reader to know them.");
        }

        // ==================================================================
        // Bookkeeping calls claim nothing
        // ==================================================================

        [Test]
        public void BookkeepingCalls_ClaimNothing()
        {
            CostTracker.RecordTagOp(new JObject { ["op"] = "add", ["affected"] = 2 }, 5L);
            CostTracker.RecordAction(new JObject { ["action"] = "open" }, 5L);
            CostTracker.RecordSources(new JObject { ["sources"] = new JArray() }, 5L);
            CostTracker.RecordCostSavingsQuery(990);

            var report = CostTracker.GetReport();
            Assert.AreEqual(0L, Low(report));
            Assert.AreEqual(0L, High(report));
        }

        // ==================================================================
        // Persistence and reset
        // ==================================================================

        [Test]
        public void Stats_PersistAcrossSessions()
        {
            var result = new JObject { ["totalResults"] = 1, ["returned"] = 1, ["results"] = new JArray() };
            CostTracker.RecordSearch(result, 50L);

            CostTracker.OverrideStatsPath(_tmpFile);   // simulate a fresh bridge reading the same file

            Assert.AreEqual(1, CostTracker.GetReport()["observed"].Value<int>("totalCalls"));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void Load_StatsFromAnOlderMethodology_IsDiscardedNotMerged(int oldVersion)
        {
            var old = new JObject
            {
                ["version"] = oldVersion,
                ["totalCalls"] = 42,
                ["x1Tokens"] = 1000L,
                ["apiBaseline"] = 99999L
            };
            File.WriteAllText(_tmpFile, old.ToString());

            var report = CostTracker.GetReport();
            Assert.AreEqual(0, report["observed"].Value<int>("totalCalls"),
                "Counters accumulated under an older model are not comparable with this one; " +
                "merging them would silently average two definitions of 'saved'.");
        }

        [Test]
        public void Reset_ClearsEverythingIncludingDeferredAndCapabilityCounters()
        {
            var preview = new JObject { ["mode"] = "file", ["previewType"] = "pdf", ["bytes"] = 400000 };
            CostTracker.RecordPreview(preview, "file", 50L, "Files", @"file://D:\archive\report.pdf");
            var xlsx = new JObject { ["mode"] = "content", ["text"] = new string('x', 4000) };
            CostTracker.RecordContent(xlsx, "Files", @"file://D:\archive\budget.xlsx", 10L);

            CostTracker.Reset();

            var report = CostTracker.GetReport();
            Assert.AreEqual(0, report["observed"].Value<int>("totalCalls"));
            Assert.AreEqual(0L, report["notSavings"].Value<long>("tokensDeferred"));
            Assert.AreEqual(0, report["notSavings"].Value<int>("capabilityGainCount"));
        }

        [Test]
        public void Reset_ThenRecord_AccumulatesFromZero()
        {
            var preview = new JObject { ["mode"] = "file", ["previewType"] = "pdf", ["bytes"] = 400000 };
            CostTracker.RecordPreview(preview, "file", 50L, "Files", @"file://D:\archive\report.pdf");

            CostTracker.Reset();

            var result = new JObject { ["totalResults"] = 5, ["returned"] = 5, ["results"] = new JArray() };
            CostTracker.RecordSearch(result, 50L);

            Assert.AreEqual(1, CostTracker.GetReport()["observed"].Value<int>("totalCalls"));
        }
    }
}
