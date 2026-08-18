// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace X1.McpBridge
{
    /// <summary>
    /// Accumulates token-cost statistics for every x1 tool call and bounds what the same retrieval
    /// would have cost through the curated Microsoft Graph / Gmail / OneDrive connector already
    /// installed on the machine. Persisted as JSON beside the exe so data survives bridge restarts.
    ///
    /// DESIGN RULE, and the reason this class looks the way it does: a counterfactual was never run,
    /// so any single number claiming "you saved X" is contestable. Three earlier revisions each
    /// produced a point estimate, and each one turned out to overstate for a different reason. So
    /// this revision does not produce a point estimate at all. It reports:
    ///
    ///   1. OBSERVED   — what actually happened: tokens x1 put in context, latency, items. No model
    ///                   is involved, so these cannot be wrong.
    ///   2. BOUNDED    — a low/high interval for the alternative's cost, where the interval WIDENS
    ///                   when the evidence is thin (see <see cref="Confidence"/>) and a category we
    ///                   never measured claims nothing at all.
    ///   3. NEITHER    — deferred tokens and capability gains, which are not token savings and are
    ///                   never folded into the savings figure.
    ///
    /// Two rules keep it defensible, and both were learned by getting them wrong:
    ///   - A path is not a payload. Handing back a file path is not the same outcome as putting the
    ///     content in context, so those calls contribute 0 to the low bound.
    ///   - No claim without evidence. Vision/OCR pricing requires positive proof no text layer
    ///     existed; an unmeasured category contributes nothing rather than borrowing a coefficient.
    /// </summary>
    internal static class CostTracker
    {
        // ---------------------------------------------------------------------------
        // Pricing constant: Claude Sonnet input pricing, USD per million tokens. The dollar figure
        // derived from it is an UPPER bound only - it prices every token at the uncached rate, and
        // prompt caching bills cached input far lower. Reported as a range for that reason.
        // ---------------------------------------------------------------------------
        private const double UsdPerMillionTokens = 3.0;
        // Fraction of the uncached rate that cached input bills at, used for the low end of the
        // dollar range. Order-of-magnitude figure, not a contract term.
        private const double CachedInputRateFraction = 0.10;

        // Identifies the methodology that produced a figure, not just the coefficient values - r4 and
        // r5 share coefficients but compute the fraction over different denominators, so a number
        // quoted from one is not the same number as from the other.
        internal const string CoefficientsVersion = "2026-07-31r7";

        // ---------------------------------------------------------------------------
        // Evidence table. Multiple = (connector tokens / x1 tokens) for the SAME content. N is the
        // number of paired samples behind it: both paths run against the same real item, sizes
        // compared. N = 0 means we never measured it, and an unmeasured, uncited category claims
        // NOTHING - it does not inherit a neighbour's coefficient.
        // ---------------------------------------------------------------------------
        private sealed class Evidence
        {
            public readonly double Multiple;
            public readonly int N;
            public readonly bool Cited;   // published third-party figure we did not measure ourselves
            public readonly string Basis;

            public Evidence(double multiple, int n, bool cited, string basis)
            {
                Multiple = multiple; N = n; Cited = cited; Basis = basis;
            }

            public bool Measured { get { return N > 0; } }
        }

        // Measured 2026-07-30 by fetching the same real items through x1 and through the managed
        // Outlook connector and comparing character counts. Small n by design of a spot check - the
        // bounds below widen to say so rather than pretending to precision.
        private static readonly Evidence MetadataSearchEvidence = new Evidence(
            2.13, 2, false,
            "paired x1_search rows (570-615 chars) vs outlook_email_search rows (1220-1310 chars), 2026-07-30");

        private static readonly Evidence TextContentEvidence = new Evidence(
            2.17, 1, false,
            "paired x1_get_content extracted text (6,989 chars) vs read_resource HTML body (15,306 chars), 2026-07-30");

        // Never measured, and deliberately NOT seeded from TextContentEvidence: borrowing a
        // coefficient across object types is how revision 2 invented savings it had no data for.
        private static readonly Evidence StructuredTabularEvidence = new Evidence(
            1.0, 0, false,
            "NOT MEASURED - no paired xlsx/csv sample collected; claims nothing until one exists");

        // Cited, not measured: a commonly-quoted per-page vision-token figure. Contributes only to
        // the HIGH bound - we will not assert a floor for a number we did not verify.
        private const int VisionTokensPerPage = 1500;
        private static readonly Evidence ImageOcrEvidence = new Evidence(
            0, 0, true,
            "cited industry figure ~1500 vision tokens per rendered page; not independently measured");

        // Scanned pages run large (image data, no text). 50 KB/page under-counts pages for a real
        // scan, which under-claims the vision bill - the intended direction.
        private const long BytesPerPageEstimate = 50_000;

        // Vision/OCR is only the realistic alternative when the text could not have been parsed out
        // locally at all. Dense extracted text proves a text layer exists, so the honest alternative
        // is "download and parse that same layer". One char per KB of source sits far below any real
        // text layer (~1 char per 10-20 bytes) and far above a true scan (~0 chars).
        private const long NoTextLayerBytesPerChar = 1000;

        // Directory-segment markers for services mirroring a local folder to a cloud the connector
        // reaches via its own API. Matched per path segment (and "marker - Org", as OneDrive names
        // per-tenant folders), never as a bare substring, so "dropbox-notes.txt" does not match.
        private static readonly string[] CloudSyncRootMarkers =
        {
            "onedrive", "dropbox", "google drive", "googledrive", "icloud drive", "iclouddrive",
            "box", "box sync", "sharepoint"
        };

        private const int CapabilityGainMinTokens = 20;

        private static readonly object Sync = new object();
        private static string _statsPath;

        /// <summary>
        /// How much of a measured multiple we are willing to assert as a FLOOR, given how many paired
        /// samples back it. With one sample we know the connector was heavier (we watched it happen)
        /// but not by how much on average, so we claim under half the observed gap and let the high
        /// bound carry the rest. More samples tighten the interval. This is the mechanism that makes
        /// thin evidence produce a visibly wide range instead of a confident wrong number.
        /// </summary>
        private static double Confidence(int n)
        {
            if (n <= 0) return 0.0;
            if (n == 1) return 0.40;
            if (n == 2) return 0.55;
            if (n <= 4) return 0.70;
            if (n <= 9) return 0.80;
            return 0.90;
        }

        /// <summary>
        /// The object type being retrieved, which selects the counterfactual. KNOWN LIMITATION:
        /// cloud tables whose uri is an opaque item id (OneDrive/GDrive/SP365 - the local Files table
        /// DOES carry a real path) cannot be classified by extension and fall back to TextContent,
        /// under-crediting genuinely tabular or scanned cloud content. Under-counting is the
        /// acceptable direction; fixing it needs the search result's extension threaded through to
        /// x1_get_content, whose input schema has no slot for it.
        /// </summary>
        internal enum RetrievalCategory { MetadataSearch, TextContent, StructuredTabular, ImageOcr }

        private static readonly HashSet<string> TabularExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".xlsx", ".xls", ".csv", ".tsv" };

        private static readonly HashSet<string> ImageOcrExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".gif" };

        internal const string CatSearch       = "search";
        internal const string CatMetadata     = "metadata";
        internal const string CatContent      = "content";
        internal const string CatExtractFile  = "extract_file";
        internal const string CatTags         = "tags";
        internal const string CatAction       = "action";
        internal const string CatSources      = "list_sources";
        internal const string CatCostSavings  = "cost_savings";
        private const string PreviewPrefix    = "preview_";

        // Marks a row that returned a path rather than content, so the report can say why it claims
        // nothing on the low side.
        private const string PathOnlyMarker = "PathOnly";

        // --------------------------------------------------------------------------
        // Recording API (called from McpServer after each successful call)
        // --------------------------------------------------------------------------

        public static int RecordSearch(JObject result, long durationMs)
        {
            int x1 = EstimateJsonTokens(result);
            int items = result.Value<int?>("returned") ?? 0;
            Bounds b = BoundsFor(MetadataSearchEvidence, x1);
            Record(CatSearch, x1, b, durationMs, items, RetrievalCategory.MetadataSearch.ToString());
            return x1;
        }

        public static int RecordMetadata(JObject result, long durationMs)
        {
            int x1 = EstimateJsonTokens(result);
            Bounds b = BoundsFor(MetadataSearchEvidence, x1);
            Record(CatMetadata, x1, b, durationMs, items: 1, evidenceKey: RetrievalCategory.MetadataSearch.ToString());
            return x1;
        }

        public static int RecordPreview(JObject result, string outputMode, long durationMs, string table, string uri)
        {
            string previewType = result.Value<string>("previewType") ?? "metadata_card";
            string key = PreviewPrefix + previewType.ToLowerInvariant() + "_" + outputMode.ToLowerInvariant();

            bool isFileLike = string.Equals(outputMode, "file", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(outputMode, "save", StringComparison.OrdinalIgnoreCase);

            int x1;
            Bounds b;
            string evidenceKey;

            if (isFileLike)
            {
                // Only the path JSON (~80 tokens) entered context. See PathOnlyBounds for why the
                // floor is zero and the ceiling is the connector's inlined extracted text.
                x1 = 80;
                int fragmentBytes = result.Value<int?>("bytes") ?? 0;
                // The fragment IS extracted text, so its token count serves twice: what a later read
                // would cost (deferred) and what the connector would have inlined up front (ceiling).
                int extractedTextTokens = fragmentBytes / 4;
                b = PathOnlyBounds(x1, extractedTextTokens);
                // Only bank these as "deferred" when they are NOT already counted as saved. Under the
                // out-of-context-use assumption they are a genuine avoidance and live in the savings
                // figure; reporting them in both places would double-count the same tokens.
                if (!PathOutputConsumedOutsideContext())
                    RecordDeferred(extractedTextTokens);
                evidenceKey = PathOnlyMarker;
            }
            else
            {
                string html = result.Value<string>("html") ?? string.Empty;
                x1 = html.Length / 4;
                if (CategoryForPreviewType(previewType) == RetrievalCategory.ImageOcr)
                {
                    // Inline pdf/image embeds the base64 data: URI itself. x1 extracted nothing, so
                    // both sides hold the same unread bytes - no claim either way.
                    b = Bounds.None(x1);
                    evidenceKey = null;
                }
                else
                {
                    b = BoundsFor(TextContentEvidence, x1);
                    evidenceKey = RetrievalCategory.TextContent.ToString();
                }
            }

            Record(key, x1, b, durationMs, items: 1, evidenceKey: evidenceKey);
            MaybeRecordCapabilityGain(uri, IsLocalFileTableSafe(table), x1,
                "x1_generate_preview " + previewType + " (local file, not cloud-synced)");
            return x1;
        }

        public static int RecordContent(JObject result, string table, string uri, long durationMs)
        {
            string mode = (result.Value<string>("mode") ?? "internal").ToLowerInvariant();
            RetrievalCategory objectCategory = Classify(table, uri);

            int x1;
            Bounds b;
            string catKey, evidenceKey;

            if (mode == "preview")
            {
                // Path only, and unlike file mode there is no fragment size to work from - just a
                // path. The connector's inlined text cannot be sized from the SOURCE bytes either:
                // extracted text is a small and highly variable fraction of a binary (the measured
                // 75 KB PDF yielded ~1,000 tokens, ~5% of what source/4 would have implied). With
                // nothing to measure, nothing is claimed at either end, and the deferral is left
                // unrecorded rather than guessed at. Under-counts; that is the acceptable direction.
                x1 = 50;
                b = PathOnlyBounds(x1, 0);
                catKey = CatContent + "_" + CategorySuffix(objectCategory);
                evidenceKey = PathOnlyMarker;
            }
            else if (mode == "content" || mode == "extract")
            {
                string text = result.Value<string>("text") ?? string.Empty;
                x1 = text.Length / 4;
                b = ContentBounds(objectCategory, x1, text.Length, uri, out evidenceKey);
                catKey = CatContent + "_" + CategorySuffix(objectCategory);
            }
            else
            {
                x1 = EstimateJsonTokens(result);
                b = BoundsFor(MetadataSearchEvidence, x1);
                catKey = CatContent + "_internal";
                evidenceKey = RetrievalCategory.MetadataSearch.ToString();
            }

            Record(catKey, x1, b, durationMs, items: 1, evidenceKey: evidenceKey);
            MaybeRecordCapabilityGain(uri, IsLocalFileTableSafe(table), x1,
                "x1_get_content mode=" + mode + " (local file, not cloud-synced)");
            return x1;
        }

        public static int RecordExtractFile(JObject result, long durationMs)
        {
            string text = result.Value<string>("text") ?? string.Empty;
            string path = result.Value<string>("path");
            RetrievalCategory objectCategory = Classify(table: null, uriOrPath: path);
            int x1 = text.Length / 4;
            string evidenceKey;
            Bounds b = ContentBounds(objectCategory, x1, text.Length, path, out evidenceKey);
            Record(CatExtractFile + "_" + CategorySuffix(objectCategory), x1, b, durationMs, items: 1,
                evidenceKey: evidenceKey);
            // Local path is necessary but not sufficient: a synced OneDrive/Dropbox file is the same
            // item the connector reaches by another route, so the path is re-checked.
            MaybeRecordCapabilityGain(path, tableIsLocal: true, x1Tokens: x1,
                reason: "x1_extract_file (local file, not cloud-synced)");
            return x1;
        }

        public static int RecordTagOp(JObject result, long durationMs)
        {
            int t = EstimateJsonTokens(result);
            Record(CatTags, t, Bounds.None(t), durationMs, items: 0, evidenceKey: null);
            return t;
        }

        public static int RecordAction(JObject result, long durationMs)
        {
            int t = EstimateJsonTokens(result);
            Record(CatAction, t, Bounds.None(t), durationMs, items: 0, evidenceKey: null);
            return t;
        }

        public static int RecordSources(JObject result, long durationMs)
        {
            int t = EstimateJsonTokens(result);
            Record(CatSources, t, Bounds.None(t), durationMs, items: 0, evidenceKey: null);
            return t;
        }

        /// <summary>
        /// Records the cost of the report call itself, from the ACTUAL serialized length rather than a
        /// guess. It used to hardcode 400 tokens; the report has since grown to roughly 1,000, so the
        /// one call whose cost this class could measure exactly was the one it was getting wrong.
        /// Both bounds are the same figure - reading your own statistics has no counterfactual.
        /// </summary>
        public static void RecordCostSavingsQuery(int actualReportTokens)
        {
            int t = Math.Max(1, actualReportTokens);
            Record(CatCostSavings, t, Bounds.None(t), durationMs: 0, items: 0, evidenceKey: null);
        }

        // --------------------------------------------------------------------------
        // Bounds
        // --------------------------------------------------------------------------

        /// <summary>Low/high bound on what the alternative would have put in context.</summary>
        private struct Bounds
        {
            public int Low;
            public int High;
            public static Bounds None(int x1Tokens) { return new Bounds { Low = x1Tokens, High = x1Tokens }; }
        }

        private static Bounds BoundsFor(Evidence e, int x1Tokens)
        {
            if (!e.Measured)
                return Bounds.None(x1Tokens);   // no measurement, no claim

            double lowMultiple = 1.0 + (e.Multiple - 1.0) * Confidence(e.N);
            return new Bounds
            {
                Low  = Math.Max(x1Tokens, (int)Math.Round(x1Tokens * lowMultiple)),
                High = Math.Max(x1Tokens, (int)Math.Round(x1Tokens * e.Multiple))
            };
        }

        /// <summary>
        /// Bounds for a call that returned a PATH rather than content.
        ///
        /// LOW is always "claim nothing": the content never reached the model, and if the caller reads
        /// the fragment later it pays the tokens then, so x1 shifted WHEN the cost lands rather than
        /// whether.
        ///
        /// HIGH is what the curated connector would have put in context for the same item. MEASURED
        /// 2026-07-31: read_resource on a OneDrive .xlsx and on a 75 KB OneDrive .pdf both returned
        /// EXTRACTED TEXT INLINE in the response - no path option, no base64, and it truncated long
        /// content rather than paging it. So the ceiling is the extracted text's token cost, and x1's
        /// own fragment is a direct proxy for it: both are extracted text of the same document.
        ///
        /// Two earlier attempts got this wrong in opposite directions, both by reasoning about the
        /// CLIENT instead of measuring the CONNECTOR. One assumed a local filesystem let the
        /// alternative download and parse without spending context (it cannot - the connector never
        /// hands over bytes), so it claimed nothing. The other priced the ceiling as base64
        /// (source / 3), which for that 75 KB PDF claimed ~25,000 tokens where the connector actually
        /// spent ~1,000 - over by about 25x. The client's capabilities were never the binding
        /// constraint; what the connector will deliver is.
        ///
        /// When there is no fragment to size the ceiling from (mode=preview returns a bare path),
        /// nothing is claimed at either end, per "no claim without evidence".
        /// </summary>
        private static Bounds PathOnlyBounds(int x1Tokens, int extractedTextTokens)
        {
            if (extractedTextTokens <= 0)
                return Bounds.None(x1Tokens);

            int high = Math.Max(x1Tokens, extractedTextTokens);

            // Asking for output="file" INSTEAD OF output="inline" is itself a request to keep the
            // content out of context - that is what the parameter is for. So the floor is not zero:
            // the revealed intent is that these tokens are never spent, not merely postponed. An
            // earlier revision floored this at zero on the reasoning "you might read it back later",
            // which ignored the evidence sitting in the request itself.
            //
            // BLIND SPOT, stated rather than hidden: if the caller later reads the fragment with its
            // own file-read tool, that read never passes through this bridge, so the conversion is
            // invisible here. The floor therefore rests on a declared usage assumption
            // (pathOutputConsumedOutsideContext), not on an observation - which is why it is a
            // setting a deployment can turn off.
            if (PathOutputConsumedOutsideContext())
                return new Bounds { Low = high, High = high };

            return new Bounds { Low = x1Tokens, High = high };
        }

        private static bool PathOutputConsumedOutsideContext()
        {
            try { return BridgeConfig.GetPathOutputConsumedOutsideContext(); }
            catch { return true; }
        }

        /// <summary>
        /// Bounds for text x1 actually returned. For an image/pdf this is where the vision question
        /// gets decided: only sparse extraction against a substantial file proves OCR was the sole
        /// route, and even then vision contributes to the HIGH bound only, because the figure is
        /// cited rather than measured.
        /// </summary>
        private static Bounds ContentBounds(RetrievalCategory category, int x1Tokens, int textLength,
            string uriOrPath, out string evidenceKey)
        {
            if (category == RetrievalCategory.ImageOcr)
            {
                long sourceBytes;
                if (HasNoTextLayerEvidence(uriOrPath, textLength, out sourceBytes))
                {
                    evidenceKey = RetrievalCategory.ImageOcr.ToString();
                    int pages = EstimatePageCountFromSourceBytes(sourceBytes);
                    return new Bounds
                    {
                        Low  = x1Tokens,                                        // cited, so no floor
                        High = Math.Max(x1Tokens, VisionTokensPerPage * pages)
                    };
                }
                // Text extracted cleanly, or we cannot prove it wouldn't have: a local parse reaches
                // the same text, so this is an ordinary text payload.
                evidenceKey = RetrievalCategory.TextContent.ToString();
                return BoundsFor(TextContentEvidence, x1Tokens);
            }

            if (category == RetrievalCategory.StructuredTabular)
            {
                evidenceKey = RetrievalCategory.StructuredTabular.ToString();
                return BoundsFor(StructuredTabularEvidence, x1Tokens);   // unmeasured -> claims nothing
            }

            evidenceKey = RetrievalCategory.TextContent.ToString();
            return BoundsFor(TextContentEvidence, x1Tokens);
        }

        // There was an assumeAlternativeHasFilesystem knob here. It was deleted rather than
        // re-tuned: BOTH of its settings were indefensible, because it asked about the client when
        // the binding constraint is the connector. Measurement settled it (see PathOnlyBounds), and a
        // measured fact does not need a configuration switch.

        // --------------------------------------------------------------------------
        // Not-savings counters
        // --------------------------------------------------------------------------

        /// <summary>
        /// Banks tokens that did not enter context because x1 returned a path. Not a saving: reading
        /// the file later costs them then. Reported on its own so the deferral is visible without
        /// inflating the savings figure.
        /// </summary>
        private static void RecordDeferred(int tokens)
        {
            if (tokens <= 0) return;
            try
            {
                lock (Sync)
                {
                    JObject stats = Load();
                    stats["tokensDeferredTotal"] = (stats.Value<long?>("tokensDeferredTotal") ?? 0L) + tokens;
                    Save(stats);
                }
            }
            catch { }
        }

        /// <summary>
        /// Counts an item the curated connector could not have reached at all: a local file not
        /// mirrored to any cloud it can call. Object type is deliberately not part of the test - an
        /// earlier revision restricted it to xlsx/pdf, which smuggled in "and it was hard to read".
        /// </summary>
        public static void RecordCapabilityGain(string reason)
        {
            try
            {
                lock (Sync)
                {
                    JObject stats = Load();
                    stats["capabilityGainCount"] = (stats.Value<int?>("capabilityGainCount") ?? 0) + 1;
                    if (!(stats["capabilityGainSamples"] is JArray samples))
                        stats["capabilityGainSamples"] = samples = new JArray();
                    if (samples.Count < 5) samples.Add(reason);
                    Save(stats);
                }
            }
            catch { }
        }

        private static void MaybeRecordCapabilityGain(string uriOrPath, bool tableIsLocal, int x1Tokens, string reason)
        {
            if (!tableIsLocal) return;
            if (IsUnderCloudSyncRoot(uriOrPath)) return;
            if (x1Tokens < CapabilityGainMinTokens) return;
            RecordCapabilityGain(reason);
        }

        public static void Reset()
        {
            lock (Sync)
            {
                string path = StatsPath();
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // --------------------------------------------------------------------------
        // Report
        // --------------------------------------------------------------------------

        public static JObject GetReport()
        {
            JObject stats = Load();

            long x1Total    = stats.Value<long?>("x1Tokens") ?? 0;

            // The comparison is computed over retrievals that HAD a counterfactual, never over the
            // whole call log — see the note in Record(). This is what makes the quotable figure
            // stable no matter how many times the report is read.
            int  cmpCalls   = stats.Value<int?>("comparableCalls") ?? 0;
            long cmpX1      = stats.Value<long?>("comparableX1Tokens") ?? 0;
            long baseLow    = stats.Value<long?>("comparableBaselineLow") ?? 0;
            long baseHigh   = stats.Value<long?>("comparableBaselineHigh") ?? 0;
            long savedLow   = Math.Max(0, baseLow - cmpX1);
            long savedHigh  = Math.Max(0, baseHigh - cmpX1);

            int totalCalls        = stats.Value<int?>("totalCalls") ?? 0;
            long totalDurationMs  = stats.Value<long?>("durationMsTotal") ?? 0;
            long avgDurationMs    = totalCalls > 0 ? totalDurationMs / totalCalls : 0;
            long totalItems       = stats.Value<long?>("itemsTotal") ?? 0;
            long totalBytes       = stats.Value<long?>("bytesTotal") ?? 0;
            long tokensDeferred   = stats.Value<long?>("tokensDeferredTotal") ?? 0;
            int capabilityGains   = stats.Value<int?>("capabilityGainCount") ?? 0;
            var capabilitySamples = (stats["capabilityGainSamples"] as JArray) ?? new JArray();

            double fracLow  = baseLow  > 0 ? (double)savedLow  / baseLow  * 100 : 0;
            double fracHigh = baseHigh > 0 ? (double)savedHigh / baseHigh * 100 : 0;

            var observed = new JObject
            {
                ["recordingSince"]      = stats.Value<string>("firstRecordedAt") ?? "unknown",
                ["lastUpdated"]         = stats.Value<string>("lastUpdatedAt") ?? "unknown",
                ["totalCalls"]          = totalCalls,
                ["x1TokensInContext"]   = x1Total,
                ["itemsReturned"]       = totalItems,
                ["avgDurationMs"]       = avgDurationMs,
                ["approxBytesReturned"] = totalBytes,
                ["note"]                = "Directly observed, no modelling involved. approxBytesReturned is derived from the token estimate (tokens x 4) rather than measured, and the token estimate itself is chars/4 - a good approximation for prose, looser for code, URLs and CJK."
            };

            var comparison = new JObject
            {
                ["comparedAgainst"]        = "the curated Microsoft Graph / Gmail / OneDrive connector already installed on this machine, for the same retrievals",
                ["howTheAlternativeBehaves"] = "MEASURED 2026-07-31, not assumed: the connector's read_resource returns EXTRACTED TEXT INLINE in its response for binaries — a OneDrive .xlsx and a 75 KB .pdf both came back as parsed text in context, with no path option, no base64, and long content truncated rather than paged. So the client's own capabilities are not the deciding factor: whether you are in Claude Code or Claude Desktop, reading a file through the connector puts its text in context. An earlier version of this report made that a configurable assumption about the client, which was the wrong question.",
                ["usageAssumption"]        = PathOutputConsumedOutsideContext()
                    ? "Content requested in path form (output=file, mode=preview) is assumed to be consumed OUTSIDE the model's context - opened, rendered, or handed to a local tool - because asking for the path form instead of the inline form is precisely the request to keep it out of context. Those tokens are therefore counted as avoided rather than postponed. This is a declared assumption about usage, not a measurement: a later read back through this bridge would convert it, but a read by the client's own file tool is invisible here. Turn off pathOutputConsumedOutsideContext to floor these rows at zero instead."
                    : "pathOutputConsumedOutsideContext is OFF: content returned as a path is assumed to be read back into context later, so those rows are floored at zero and their tokens reported as deferred rather than saved.",
                ["callsCompared"]          = cmpCalls,
                ["callsComparedNote"]      = cmpCalls + " of " + totalCalls +
                    " recorded calls were retrievals with a counterfactual; the figures below cover only those. " +
                    "Bookkeeping calls (tagging, actions, list_sources, this report) are excluded, so reading " +
                    "the report never moves the number it reports.",
                ["x1TokensCompared"]       = cmpX1,
                ["baselineTokensLow"]      = baseLow,
                ["baselineTokensHigh"]     = baseHigh,
                ["tokensSavedLow"]         = savedLow,
                ["tokensSavedHigh"]        = savedHigh,
                ["savingsFractionLow"]     = Math.Round(fracLow, 1),
                ["savingsFractionHigh"]    = Math.Round(fracHigh, 1),
                ["costSavingUsd"]          = savedHigh > 0
                    ? string.Format("${0:F2} to ${1:F2} at Claude Sonnet input pricing (${2}/M); the low end assumes cached input, the high end uncached - so the upper figure is a ceiling, not an expectation",
                        savedLow / 1_000_000.0 * UsdPerMillionTokens * CachedInputRateFraction,
                        savedHigh / 1_000_000.0 * UsdPerMillionTokens,
                        UsdPerMillionTokens)
                    : "Not enough evidence to put a floor under a dollar figure yet.",
                ["coefficientsVersion"]    = CoefficientsVersion,
                ["howToReadThis"]          = "The interval is not a confidence interval; it is the span between the most and least this evidence can support. It WIDENS when a coefficient rests on few paired samples, so a wide range means thin evidence, not a large saving. Cite the low end.",
                ["byCategory"]             = BuildBreakdown(stats)
            };

            var notSavings = new JObject
            {
                ["tokensDeferred"]         = tokensDeferred,
                ["tokensDeferredMeaning"]  = "Tokens that did not enter context because x1 returned a path instead of content. NOT a saving - reading the file later costs them then. Excluded from the figures above.",
                ["capabilityGainCount"]    = capabilityGains,
                ["capabilityGainSamples"]  = capabilitySamples,
                ["capabilityGainMeaning"]  = "Items the curated connector could not have reached at all: local files not mirrored to any cloud it can call. A capability, not a token ratio - excluded from the figures above."
            };

            return new JObject
            {
                ["observed"]   = observed,
                ["comparison"] = comparison,
                ["notSavings"] = notSavings,
                ["evidence"]   = BuildEvidenceTable(),
                ["knownLimitations"] = new JArray
                {
                    "Coefficients rest on 1-2 paired samples per category and are vendor-derived, not measured on your data. The intervals widen to reflect that; they do not remove it.",
                    "The multiple is applied proportionally, but part of the connector's overhead (tracking URLs, duplicated identifiers) is fixed per item - so short items are under-credited and long ones over-credited.",
                    "Structured/tabular content claims nothing: no paired xlsx/csv sample has been collected.",
                    "The connector's real chain for an email body is search + read_resource (two calls); this compares one call against one, which under-counts its cost.",
                    "Cloud tables (OneDrive/GDrive/SP365) expose opaque item ids, so tabular and scanned cloud content cannot be classified by extension and is priced as text - an under-count.",
                    "Token counts are chars/4, not a real tokenizer. The error largely cancels in a ratio but not in the absolute figures."
                },
                // Flat aliases for older readers. They deliberately carry the LOW (conservative)
                // figures, so anything still reading the previous field names gets the floor rather
                // than a missing value or the ceiling.
                ["estimatedX1Tokens"]        = x1Total,
                ["estimatedTokensSaved"]     = savedLow,
                ["estimatedSavingsFraction"] = Math.Round(fracLow, 1),
                ["totalCalls"]               = totalCalls,
                ["avgDurationMs"]            = avgDurationMs
            };
        }

        private static JArray BuildBreakdown(JObject stats)
        {
            var byCategory = stats["byCategory"] as JObject ?? new JObject();
            var rows = new List<(string label, long low, long high, int calls, long avgMs, long items, string cf)>();

            foreach (JProperty p in byCategory.Properties())
            {
                var cat = p.Value as JObject;
                if (cat == null) continue;
                long cx    = cat.Value<long?>("x1Tokens") ?? 0;
                long cLow  = cat.Value<long?>("baselineLow") ?? 0;
                long cHigh = cat.Value<long?>("baselineHigh") ?? 0;
                int calls  = cat.Value<int?>("calls") ?? 0;
                long ms    = cat.Value<long?>("durationMsTotal") ?? 0;
                rows.Add((FriendlyCategory(p.Name),
                          Math.Max(0, cLow - cx), Math.Max(0, cHigh - cx),
                          calls, calls > 0 ? ms / calls : 0,
                          cat.Value<long?>("itemsTotal") ?? 0,
                          CounterfactualLabel(cat.Value<string>("evidenceKey"))));
            }
            rows.Sort((a, b) => b.high.CompareTo(a.high));

            var arr = new JArray();
            foreach (var r in rows)
            {
                arr.Add(new JObject
                {
                    ["category"]        = r.label,
                    ["calls"]           = r.calls,
                    ["tokensSavedLow"]  = r.low,
                    ["tokensSavedHigh"] = r.high,
                    ["formattedSaved"]  = r.low == r.high
                        ? FormatTokens(r.low)
                        : FormatTokens(r.low) + "-" + FormatTokens(r.high),
                    ["avgDurationMs"]   = r.avgMs,
                    ["itemsReturned"]   = r.items,
                    ["counterfactual"]  = r.cf
                });
            }
            return arr;
        }

        private static JArray BuildEvidenceTable()
        {
            var arr = new JArray();
            arr.Add(EvidenceRow("Metadata / search", MetadataSearchEvidence));
            arr.Add(EvidenceRow("Text-native content", TextContentEvidence));
            arr.Add(EvidenceRow("Structured / tabular", StructuredTabularEvidence));
            arr.Add(new JObject
            {
                ["category"]      = "Image / OCR-required",
                ["multiple"]      = "per-page, not a multiple (~" + VisionTokensPerPage + " tokens/page)",
                ["pairedSamples"] = 0,
                ["claims"]        = "upper bound only",
                ["basis"]         = ImageOcrEvidence.Basis
            });
            return arr;
        }

        private static JObject EvidenceRow(string label, Evidence e)
        {
            return new JObject
            {
                ["category"]      = label,
                ["multiple"]      = e.Measured ? (JToken)Math.Round(e.Multiple, 2) : "n/a",
                ["pairedSamples"] = e.N,
                ["claims"]        = e.Measured
                    ? "low bound at " + Math.Round(1 + (e.Multiple - 1) * Confidence(e.N), 2) + "x, high at " + Math.Round(e.Multiple, 2) + "x"
                    : "nothing - unmeasured",
                ["basis"]         = e.Basis
            };
        }

        private static string CounterfactualLabel(string evidenceKey)
        {
            if (evidenceKey == PathOnlyMarker)
                return PathOutputConsumedOutsideContext()
                    ? "vs. the extracted text the connector would have inlined (measured 2026-07-31: " +
                      "read_resource inlines text, not a path and not base64). Counted in full because " +
                      "requesting the path form rather than the inline form is a request to keep the " +
                      "content out of context - a later read back through this bridge would convert it, " +
                      "though a read by the client's own file tool is invisible here"
                    : "0 to the extracted text the connector would have inlined - floored at zero " +
                      "because pathOutputConsumedOutsideContext is off, so a later read is assumed to " +
                      "pay these tokens";
            if (evidenceKey == RetrievalCategory.MetadataSearch.ToString())
                return "vs. the connector's own search/metadata record (measured " + Math.Round(MetadataSearchEvidence.Multiple, 2) + "x, n=" + MetadataSearchEvidence.N + ")";
            if (evidenceKey == RetrievalCategory.TextContent.ToString())
                return "vs. the connector's rich text/HTML body (measured " + Math.Round(TextContentEvidence.Multiple, 2) + "x, n=" + TextContentEvidence.N + ")";
            if (evidenceKey == RetrievalCategory.StructuredTabular.ToString())
                return "no claim - no paired xlsx/csv measurement exists yet";
            if (evidenceKey == RetrievalCategory.ImageOcr.ToString())
                return "0 to per-page vision/OCR cost (~" + VisionTokensPerPage + " tokens/page, cited not measured), applied only because sparse extraction proved no text layer existed";
            return "no counterfactual - bookkeeping call";
        }

        // --------------------------------------------------------------------------
        // Classification helpers
        // --------------------------------------------------------------------------

        internal static RetrievalCategory Classify(string table, string uriOrPath)
        {
            if (!string.IsNullOrEmpty(table) &&
                (ActionBridge.IsEmailTable(table) || ActionBridge.IsMessageTable(table)))
                return RetrievalCategory.TextContent;

            string ext = SafeExtension(uriOrPath);
            if (TabularExtensions.Contains(ext)) return RetrievalCategory.StructuredTabular;
            if (ImageOcrExtensions.Contains(ext)) return RetrievalCategory.ImageOcr;
            return RetrievalCategory.TextContent;
        }

        private static RetrievalCategory CategoryForPreviewType(string previewType)
        {
            switch ((previewType ?? "").ToLowerInvariant())
            {
                case "pdf":
                case "image": return RetrievalCategory.ImageOcr;
                default:      return RetrievalCategory.TextContent;
            }
        }

        private static string SafeExtension(string uriOrPath)
        {
            if (string.IsNullOrEmpty(uriOrPath)) return "";
            try { return Path.GetExtension(uriOrPath) ?? ""; }
            catch { return ""; }
        }

        private static bool IsLocalFileTableSafe(string table)
        {
            return !string.IsNullOrEmpty(table) && ActionBridge.IsLocalFileTable(table);
        }

        private static string CategorySuffix(RetrievalCategory category)
        {
            switch (category)
            {
                case RetrievalCategory.TextContent:       return "text";
                case RetrievalCategory.StructuredTabular: return "tabular";
                case RetrievalCategory.ImageOcr:          return "image";
                default:                                  return "metadata";
            }
        }

        private static int EstimatePageCountFromSourceBytes(long sourceBytes)
        {
            if (sourceBytes <= 0) return 1;
            return Math.Max(1, (int)Math.Ceiling(sourceBytes / (double)BytesPerPageEstimate));
        }

        /// <summary>
        /// True when the source file is substantial but extraction produced almost no text - the only
        /// case in which a vision/OCR counterfactual is defensible. False whenever the file cannot be
        /// measured, so an unverifiable case never earns the vision bound.
        /// </summary>
        private static bool HasNoTextLayerEvidence(string uriOrPath, int textLength, out long sourceBytes)
        {
            if (!TryGetLocalSourceBytes(uriOrPath, out sourceBytes)) return false;
            if (sourceBytes <= 0) return false;
            return (long)textLength * NoTextLayerBytesPerChar < sourceBytes;
        }

        private static bool TryGetLocalSourceBytes(string uriOrPath, out long bytes)
        {
            bytes = 0;
            if (string.IsNullOrEmpty(uriOrPath)) return false;
            string p = uriOrPath;
            if (p.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                p = p.Substring("file://".Length);
            try
            {
                var info = new FileInfo(p);
                if (!info.Exists) return false;
                bytes = info.Length;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// True when the path sits under a folder a cloud service mirrors. Such a file is reachable by
        /// the curated connector through that service's API, so surfacing it locally is not a
        /// capability the connector lacks.
        /// </summary>
        internal static bool IsUnderCloudSyncRoot(string uriOrPath)
        {
            if (string.IsNullOrEmpty(uriOrPath)) return false;
            string p = uriOrPath;
            if (p.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                p = p.Substring("file://".Length);
            string[] segments = p.Replace('/', '\\').Split('\\');
            // Directory segments only - the last is the file name, and a file merely NAMED
            // "dropbox-migration-plan.txt" is not stored in Dropbox.
            for (int i = 0; i < segments.Length - 1; i++)
            {
                string s = segments[i].Trim().ToLowerInvariant();
                if (s.Length == 0) continue;
                foreach (string marker in CloudSyncRootMarkers)
                {
                    if (s == marker || s.StartsWith(marker + " -", StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        // --------------------------------------------------------------------------
        // Persistence
        // --------------------------------------------------------------------------

        private static void Record(string category, int x1Tokens, Bounds b, long durationMs, int items,
            string evidenceKey)
        {
            try
            {
                lock (Sync)
                {
                    JObject stats = Load();
                    string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                    long bytes = x1Tokens * 4L;

                    if (stats.Value<string>("firstRecordedAt") == null)
                        stats["firstRecordedAt"] = now;
                    stats["lastUpdatedAt"]   = now;
                    stats["totalCalls"]      = (stats.Value<int?>("totalCalls") ?? 0) + 1;
                    stats["x1Tokens"]        = (stats.Value<long?>("x1Tokens") ?? 0L) + x1Tokens;
                    stats["baselineLow"]     = (stats.Value<long?>("baselineLow") ?? 0L) + b.Low;
                    stats["baselineHigh"]    = (stats.Value<long?>("baselineHigh") ?? 0L) + b.High;

                    // Comparable totals drive the savings fraction, and exclude calls that had no
                    // counterfactual at all - tagging, actions, list_sources, and above all
                    // x1_cost_savings itself. Without this split, reading the report ADDS a
                    // zero-saving call to the denominator, so the quotable percentage sags every time
                    // you look at it: 32.1% -> 16.1% over twenty reads, purely from measuring. A
                    // figure that moves when observed is useless for quoting, so the fraction is
                    // computed over retrievals only. Bookkeeping calls stay in `observed`, because
                    // they did really happen.
                    if (evidenceKey != null)
                    {
                        stats["comparableCalls"]        = (stats.Value<int?>("comparableCalls") ?? 0) + 1;
                        stats["comparableX1Tokens"]     = (stats.Value<long?>("comparableX1Tokens") ?? 0L) + x1Tokens;
                        stats["comparableBaselineLow"]  = (stats.Value<long?>("comparableBaselineLow") ?? 0L) + b.Low;
                        stats["comparableBaselineHigh"] = (stats.Value<long?>("comparableBaselineHigh") ?? 0L) + b.High;
                    }
                    stats["durationMsTotal"] = (stats.Value<long?>("durationMsTotal") ?? 0L) + durationMs;
                    stats["bytesTotal"]      = (stats.Value<long?>("bytesTotal") ?? 0L) + bytes;
                    stats["itemsTotal"]      = (stats.Value<long?>("itemsTotal") ?? 0L) + items;

                    if (!(stats["byCategory"] is JObject byCat))
                        stats["byCategory"] = byCat = new JObject();
                    if (!(byCat[category] is JObject catObj))
                        byCat[category] = catObj = new JObject
                        {
                            ["calls"] = 0, ["x1Tokens"] = 0L, ["baselineLow"] = 0L, ["baselineHigh"] = 0L,
                            ["durationMsTotal"] = 0L, ["bytesTotal"] = 0L, ["itemsTotal"] = 0L,
                            ["evidenceKey"] = evidenceKey
                        };

                    catObj["calls"]           = catObj.Value<int?>("calls") + 1;
                    catObj["x1Tokens"]        = (catObj.Value<long?>("x1Tokens") ?? 0L) + x1Tokens;
                    catObj["baselineLow"]     = (catObj.Value<long?>("baselineLow") ?? 0L) + b.Low;
                    catObj["baselineHigh"]    = (catObj.Value<long?>("baselineHigh") ?? 0L) + b.High;
                    catObj["durationMsTotal"] = (catObj.Value<long?>("durationMsTotal") ?? 0L) + durationMs;
                    catObj["bytesTotal"]      = (catObj.Value<long?>("bytesTotal") ?? 0L) + bytes;
                    catObj["itemsTotal"]      = (catObj.Value<long?>("itemsTotal") ?? 0L) + items;

                    Save(stats);
                }
            }
            catch
            {
                // Tracking failures must never propagate and break a tool call.
            }
        }

        // Bumped whenever the METHODOLOGY changes, not just the field layout: a counter accumulated
        // under an older model is not comparable with one accumulated under this model, and merging
        // them would silently average two different definitions of "saved".
        //   v1 -> v2: per-category taxonomy replaced the flat base64 model.
        //   v2 -> v3: vision pricing required evidence; capability gain excluded cloud-synced paths.
        //   v3 -> v4: point baseline replaced by an evidence-weighted low/high interval.
        //   v4 -> v5: savings fraction computed over comparable retrievals only, so bookkeeping
        //             calls (including this report) no longer dilute the figure they report.
        //   v5 -> v6: path-returning calls priced against the connector's MEASURED inlined-text
        //             behaviour instead of an assumption about the client's filesystem.
        //   v6 -> v7: path-output floor raised from zero - requesting the path form is itself the
        //             request to keep content out of context, so those tokens are avoided, not
        //             postponed (pathOutputConsumedOutsideContext).
        // Older files are discarded rather than merged.
        private const int CurrentSchemaVersion = 7;

        private static JObject Load()
        {
            string path = StatsPath();
            if (File.Exists(path))
            {
                try
                {
                    JObject loaded = JObject.Parse(File.ReadAllText(path));
                    if ((loaded.Value<int?>("version") ?? 0) == CurrentSchemaVersion)
                        return loaded;
                }
                catch { /* corrupted - start fresh */ }
            }
            return new JObject { ["version"] = CurrentSchemaVersion };
        }

        private static void Save(JObject stats)
        {
            File.WriteAllText(StatsPath(), stats.ToString(Formatting.Indented));
        }

        internal static string StatsPath()
        {
            if (_statsPath != null) return _statsPath;
            var exeDir = Path.GetDirectoryName(typeof(CostTracker).Assembly.Location) ?? ".";
            _statsPath = Path.Combine(exeDir, "x1mcp_stats.json");
            return _statsPath;
        }

        internal static void OverrideStatsPath(string path)
        {
            lock (Sync) { _statsPath = path; }
        }

        private static int EstimateJsonTokens(JObject j)
        {
            return (j?.ToString(Formatting.None)?.Length ?? 0) / 4;
        }

        private static string FriendlyCategory(string key)
        {
            if (key == CatSearch)      return "x1_search (discovery)";
            if (key == CatMetadata)    return "x1_get_metadata";
            if (key == CatTags)        return "x1_add/remove/clear_tags";
            if (key == CatAction)      return "x1_execute_action";
            if (key == CatSources)     return "x1_list_sources";
            if (key == CatCostSavings) return "x1_cost_savings";
            if (key == CatContent + "_text")     return "x1_get_content (email/docx/text)";
            if (key == CatContent + "_tabular")  return "x1_get_content (xlsx/csv)";
            if (key == CatContent + "_image")    return "x1_get_content (pdf/image)";
            if (key == CatContent + "_internal") return "x1_get_content (raw fields)";
            if (key == CatExtractFile + "_text")    return "x1_extract_file (email/docx/text)";
            if (key == CatExtractFile + "_tabular") return "x1_extract_file (xlsx/csv)";
            if (key == CatExtractFile + "_image")   return "x1_extract_file (pdf/image)";
            if (key.StartsWith(PreviewPrefix))
            {
                var rest = key.Substring(PreviewPrefix.Length);
                int last = rest.LastIndexOf('_');
                if (last > 0)
                    return string.Format("x1_generate_preview  {0} / output={1}",
                        rest.Substring(0, last), rest.Substring(last + 1));
            }
            return key;
        }

        private static string FormatTokens(long tokens)
        {
            if (tokens >= 1_000_000) return string.Format("{0:F1}M", tokens / 1_000_000.0);
            if (tokens >= 1_000)     return string.Format("{0:F1}K", tokens / 1_000.0);
            return tokens.ToString();
        }
    }
}
