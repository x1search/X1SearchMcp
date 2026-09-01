// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using Newtonsoft.Json.Linq;

namespace X1.McpBridge
{
    /// <summary>
    /// XS-1746: turns "x1_get_content found no text" into a message that names the actual reason and
    /// the fix, instead of the dead-end <c>"Content extraction failed or timed out."</c> the connector
    /// used to return for every failure alike.
    ///
    /// The reason was always known one layer down and thrown away twice. <c>ScannerManager.GetContent</c>
    /// sets <c>contentRetrievalState = "No extracted content"</c> when the item's <c>extracted_len</c> is
    /// missing or zero; <c>X1SearchManager._getContent</c> then discards that state and reports the flat
    /// <c>OnContentReady("", "No text extracted")</c>; and SearchBridge replaced even that with its own
    /// generic string. An agent shown the old text could not tell "this file type was never
    /// content-indexed" from "the service timed out", and neither could the person reading its answer.
    ///
    /// What makes a specific diagnosis possible is <c>istatus</c> (<c>StdFields.IndexingStatus</c>): every
    /// scanner records why extraction did or didn't happen on the item itself, and
    /// <c>GetItemInternal</c> already returns it. So on the no-text path only, SearchBridge fetches the
    /// item's fields and hands them here to be turned into the real reason.
    ///
    /// Pure (no WCF, no channel, no IO), for the reason <see cref="ServiceAvailability"/> gives: SearchBridge
    /// has no seam to inject a fake channel at, so extracting the classification as a pure function is what
    /// makes it testable at all. See ContentUnavailableTests.
    /// </summary>
    internal static class ContentUnavailable
    {
        // ── Reason slugs ────────────────────────────────────────────────────────
        //
        // Returned to the caller as "reason" so a consumer can branch without parsing prose, and
        // asserted on by the tests so the wording stays free to change.

        internal const string ReasonNotAllowListed   = "file_type_not_content_indexed";
        internal const string ReasonFolderMetaOnly   = "folder_metadata_only";
        internal const string ReasonTooLarge         = "content_size_limit";
        internal const string ReasonEncrypted        = "password_protected";
        internal const string ReasonNoContent        = "no_content";
        internal const string ReasonExtractionFailed = "extraction_failed";
        internal const string ReasonPending          = "indexing_pending";
        internal const string ReasonUnknown          = "not_content_indexed";
        internal const string ReasonTimedOut         = "timed_out";
        internal const string ReasonServerError      = "server_error";

        // ── The service's own vocabulary ────────────────────────────────────────
        //
        // Deliberately duplicated here rather than referenced from ExtractionManager.ExtractionCodes
        // (X1Service/TextExtraction/ExtractionManager/ExtractionCodes.cs), which is the source of these
        // strings. The bridge ships independently of X1 Search and talks to whatever service build is
        // installed on the machine, so it must not take a compile-time dependency on the service tree -
        // and must tolerate a service that is older or newer than it is. Matching is therefore
        // case-insensitive and by substring, and anything unrecognised falls through to the generic
        // message rather than being forced into the wrong bucket.

        private const string StatusNotScannable   = "File extension is not selected for content indexing";
        private const string StatusFolderNotIndexed = "Folder is not marked for content indexing";
        private const string StatusTooBig         = "Document is too large";
        private const string StatusTextSizeLimit  = "Extracted Text Size limit exceeded";
        private const string StatusSubItemLimit   = "Sub Item Text Size limit exceeded";
        private const string StatusDocCountLimit  = "Sub Document Count limit exceeded";
        private const string StatusPartialIndex   = "Content is too large";
        private const string StatusEncrypted      = "Password protected or encrypted";
        private const string StatusNoContent      = "No content";
        private const string StatusExtractionFail = "Extraction fail";
        private const string StatusNoProvider     = "Extraction provider not found";
        private const string StatusUnexpected     = "unexpected error";
        private const string StatusTimeout        = "Extraction timeout";
        private const string StatusCanceled       = "Canceled";
        private const string StatusDeferred       = "Deferred";
        private const string StatusContainer      = "Container indexing";

        /// <summary>The index field carrying the status above - see StdFields.IndexingStatus.</summary>
        private const string IndexingStatusField = "istatus";

        /// <summary>
        /// The state <c>X1SearchManager._getContent</c> reports when extraction produced nothing. It is a
        /// flat literal with no detail in it - the specific reason has to come from <c>istatus</c>.
        /// </summary>
        internal const string NoTextExtractedState = "No text extracted";

        /// <summary>
        /// Prefix <c>X1SearchManager._getContent</c> puts on a genuine server-side failure
        /// ("Item not found: ...", "No serializer found for item", "Error extracting text: ...").
        /// </summary>
        private const string ServerErrorPrefix = "Error:";

        /// <summary>
        /// What <see cref="SearchManagerCallbacks.WaitContentAsync"/> synthesizes when the callback never
        /// arrives. Nothing at all is known about the item in that case.
        /// </summary>
        private const string TimedOutState = "Timed out or was cancelled.";

        internal struct Diagnosis
        {
            public string Reason;
            public string Message;
        }

        // ── State classification ────────────────────────────────────────────────

        /// <summary>True when the service reported that extraction produced no text.</summary>
        internal static bool IsNoTextExtracted(string state) =>
            !string.IsNullOrEmpty(state) &&
            state.IndexOf(NoTextExtractedState, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>True when the callback never arrived and the wait synthesized its own state.</summary>
        internal static bool IsTimedOut(string state) =>
            string.IsNullOrEmpty(state) ||
            state.IndexOf("Timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
            state.IndexOf("cancelled", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// True when the service reached the item and answered with a real, different problem. Those
        /// messages are surfaced unchanged rather than reinterpreted - the same principle that keeps
        /// <see cref="ServiceAvailability"/> from swallowing a <c>FaultException</c>. Telling someone
        /// whose URI was simply wrong to go edit the allow-list would send them chasing the wrong thing.
        /// </summary>
        internal static bool IsServerError(string state) =>
            !string.IsNullOrEmpty(state) &&
            state.TrimStart().StartsWith(ServerErrorPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether the item's index fields are worth fetching to explain <paramref name="state"/>. False for
        /// a server error or a timeout: <see cref="Diagnose"/> ignores the fields in both cases, so fetching
        /// them would be a pure extra channel call on an already-failing request. Lives here, next to the
        /// method that consumes them, so the caller's fetch decision and the diagnosis cannot drift apart.
        /// </summary>
        internal static bool WantsIndexFields(string state) => !IsServerError(state) && !IsTimedOut(state);

        // ── Index-field helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Pulls <c>istatus</c> out of the flat alternating key/value array <c>GetItemInternal</c> returns
        /// ("k1","v1","k2","v2",...). Returns null when the array is null, odd-length, or has no such field -
        /// not every schema carries one, and an older service may not populate it.
        /// </summary>
        internal static string IndexingStatusOf(string[] rows) => FieldOf(rows, IndexingStatusField);

        /// <summary>Reads one field out of the same flat array. Null when absent or empty.</summary>
        internal static string FieldOf(string[] rows, string field)
        {
            if (rows == null || string.IsNullOrEmpty(field))
                return null;

            // Step in pairs and stop one short of the end, so a truncated/odd-length array can never
            // read past it. This runs on the error path; it must not turn a bad answer into a throw.
            for (int i = 0; i + 1 < rows.Length; i += 2)
            {
                if (string.Equals(rows[i], field, StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrEmpty(rows[i + 1]) ? null : rows[i + 1];
            }

            return null;
        }

        /// <summary>
        /// The item's file extension, lower-cased and dot-prefixed (".dwg"), for naming the exact type to
        /// tick in the allow-list. Prefers the indexed <c>name</c> field and falls back to the URI's last
        /// segment. Returns null rather than a guess when the leaf has no plausible extension - URIs for
        /// mail and chat items end in opaque ids, and "enable the .a1b2c3d4 file type" would be nonsense.
        /// </summary>
        internal static string ExtensionOf(string uri, string[] rows)
        {
            string leaf = FieldOf(rows, "name") ?? uri;
            if (string.IsNullOrEmpty(leaf))
                return null;

            int cut = leaf.LastIndexOfAny(new[] { '/', '\\' });
            if (cut >= 0) leaf = leaf.Substring(cut + 1);

            int dot = leaf.LastIndexOf('.');
            if (dot < 0 || dot == leaf.Length - 1)
                return null;

            string ext = leaf.Substring(dot + 1);
            if (ext.Length > 8)
                return null;

            foreach (char c in ext)
            {
                if (!char.IsLetterOrDigit(c))
                    return null;
            }

            return "." + ext.ToLowerInvariant();
        }

        // ── istatus -> reason ───────────────────────────────────────────────────

        /// <summary>
        /// Maps the item's indexing status to a reason slug. Order matters where the service's strings
        /// overlap: "Content is too large; document partially indexed" and "Extracted Text Size limit
        /// exceeded" are both size verdicts and are checked before the broader failure buckets.
        /// </summary>
        internal static string ReasonFor(string indexingStatus)
        {
            if (string.IsNullOrEmpty(indexingStatus))
                return ReasonUnknown;

            if (Has(indexingStatus, StatusNotScannable))    return ReasonNotAllowListed;
            if (Has(indexingStatus, StatusFolderNotIndexed)) return ReasonFolderMetaOnly;

            if (Has(indexingStatus, StatusTooBig) ||
                Has(indexingStatus, StatusTextSizeLimit) ||
                Has(indexingStatus, StatusSubItemLimit) ||
                Has(indexingStatus, StatusDocCountLimit) ||
                Has(indexingStatus, StatusPartialIndex))     return ReasonTooLarge;

            if (Has(indexingStatus, StatusEncrypted))        return ReasonEncrypted;

            if (Has(indexingStatus, StatusDeferred) ||
                Has(indexingStatus, StatusContainer))        return ReasonPending;

            if (Has(indexingStatus, StatusExtractionFail) ||
                Has(indexingStatus, StatusNoProvider) ||
                Has(indexingStatus, StatusUnexpected) ||
                Has(indexingStatus, StatusTimeout) ||
                Has(indexingStatus, StatusCanceled))         return ReasonExtractionFailed;

            // Checked last: "No content" is a substring of nothing else, but it is also the weakest
            // signal here - an item can carry it simply because extraction was never attempted.
            if (Has(indexingStatus, StatusNoContent))        return ReasonNoContent;

            return ReasonUnknown;
        }

        private static bool Has(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        // ── The answer ──────────────────────────────────────────────────────────

        /// <summary>
        /// The reason and the message to show the caller. <paramref name="state"/> is the raw
        /// <c>OnContentReady</c> state; <paramref name="internalRows"/> is the item's fields from
        /// <c>GetItemInternal</c>, or null when they could not be fetched (a server error or a timeout,
        /// where they would say nothing useful anyway).
        /// </summary>
        internal static Diagnosis Diagnose(string state, int timeoutMs, string uri, string[] internalRows)
        {
            if (IsServerError(state))
            {
                // Surfaced unchanged, minus the wire prefix. This is the service describing an actual
                // different problem, and it is already the most useful thing we could say.
                string message = state.TrimStart().Substring(ServerErrorPrefix.Length).Trim();
                return new Diagnosis
                {
                    Reason = ReasonServerError,
                    Message = string.IsNullOrEmpty(message) ? BridgeConstants.ContentExtractionFailed : message
                };
            }

            if (!IsNoTextExtracted(state) && IsTimedOut(state))
            {
                return new Diagnosis
                {
                    Reason = ReasonTimedOut,
                    Message = BridgeConstants.ContentExtractionTimedOut(timeoutMs)
                };
            }

            string reason = ReasonFor(IndexingStatusOf(internalRows));
            return new Diagnosis
            {
                Reason = reason,
                Message = BridgeConstants.ContentNotIndexed(reason, ExtensionOf(uri, internalRows))
            };
        }

        /// <summary>
        /// <see cref="Diagnose"/> as the JSON payload x1_get_content returns. <c>error</c> stays the single
        /// human-readable field so existing consumers keep working, <c>reason</c> is the machine-readable
        /// slug, and the raw <c>state</c> rides along for support - demoted, never discarded, the same way
        /// XS-1719 keeps the transport detail in the JSON-RPC error's data member.
        /// </summary>
        internal static JObject Describe(string mode, string state, int timeoutMs, string uri, string[] internalRows)
        {
            Diagnosis d = Diagnose(state, timeoutMs, uri, internalRows);

            var payload = new JObject
            {
                ["mode"] = mode,
                ["error"] = d.Message,
                ["reason"] = d.Reason
            };
            if (!string.IsNullOrEmpty(state))
                payload["state"] = state;

            return payload;
        }
    }
}
