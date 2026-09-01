// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

namespace X1.McpBridge
{
    /// <summary>
    /// XS-1662: shared constants and the user-facing licensing-message text for the connector.
    ///
    /// The connector is open source and the routing/support model behind the MCP offering can change
    /// without a code change, so every user-facing licensing message points ONLY at the landing page
    /// below — never a hard-coded email or named contact. Centralizing the URL and the message text
    /// here (rather than inlining strings at each throw/return site) keeps the wording identical
    /// whether a files-only rejection trips the table resolver or the search-session gate, and lets a
    /// single test assert every message carries the URL and no email (see LicensingMessageTests).
    /// </summary>
    internal static class BridgeConstants
    {
        /// <summary>Public MCP landing page — the single destination every licensing message routes to.</summary>
        public const string McpLandingPageUrl = "https://www.x1.com/solutions/x1-search/x1-search-mcp/";

        /// <summary>
        /// Files-only rejection for a specific non-Files table. Shared by <see cref="TableSchemaResolver"/>
        /// (thrown as an ArgumentException before the channel is touched, the path QA actually hits) and
        /// by <c>X1McpFilesOnlyLicenseException</c> (thrown when the server returns the -1 session
        /// sentinel), so both read identically. Retains the "Files only" / "MCP-full" phrasing asserted
        /// by existing tests.
        /// </summary>
        public static string FilesOnlyTableRejection(string table) =>
            "Table '" + table + "' is not available on this license tier. This X1 Search connection is " +
            "licensed for Files only; the MCP-full entitlement unlocks all data sources. See " +
            McpLandingPageUrl + " for how to enable it.";

        /// <summary>
        /// Files-only rejection for the arbitrary-file (not-in-index) Extract Text / Export HTML path,
        /// which the server gates on the overall entitlement rather than on a specific table.
        /// </summary>
        public static string ArbitraryFileFilesOnlyRejection() =>
            "Extracting text from / exporting an arbitrary local file (not required to be indexed) requires " +
            "the MCP-full license entitlement. This connection is licensed for Files only. Use x1_get_content " +
            "on an indexed item instead. See " + McpLandingPageUrl + " for how to enable the full entitlement.";

        /// <summary>
        /// Shown when X1 Search isn't licensed for the MCP connector at all — i.e. there's no qualifying
        /// X1 Search license, or the service host isn't licensed / doesn't publish the endpoint. Per the
        /// 8/10 freemium decision every X1 Search licensee gets at least the Files-only MCP tier, so this
        /// is specifically the "no qualifying X1 Search license" case, not a "has Search but not MCP" one.
        /// </summary>
        public static string NotLicensedForMcp =>
            "X1 Search isn't licensed for the MCP connector on this machine. Using the MCP connector requires " +
            "an X1 Search license with MCP enabled. See " + McpLandingPageUrl + " for the requirements.";

        /// <summary>
        /// XS-1719: the connector's single down-state message — shown whenever X1ServiceHost can't be
        /// reached, in place of the raw WCF transport text. Lives here, next to the licensing messages,
        /// for the same reason they do: it is reached from two independent paths that must not drift —
        /// <see cref="SearchBridge.ThrowIfSessionCreationFailed"/> (service reachable, but it returned
        /// the 0 "unavailable" session sentinel) and <see cref="ServiceAvailability.DescribeForCaller"/>
        /// (service not reachable at all). Those are different failures at the wire level and identical
        /// from where the caller sits, so they say the same thing.
        ///
        /// Phrased as the one action that actually resolves it. No landing-page URL: unlike the
        /// licensing messages this is not a "you need to buy/enable something" state, and pointing a
        /// stuck user at a marketing page instead of at "start the service" would be a small betrayal.
        /// </summary>
        public const string ServiceUnavailable =
            "The X1 service may be unavailable - confirm X1ServiceHost is running and retry.";

        // ── XS-1746: "there is no text to return, and here is why" ──────────────
        //
        // Lives here for the reason the licensing messages do: reached from more than one path
        // (x1_get_content mode="content" and the terminal fallback of mode="auto"), and one place for a
        // test to assert that every allow-list-family variant carries the reindex call-out. No landing-page
        // URL - unlike the licensing messages this is not "you need to buy something", it is "do this thing
        // in the product", and the product is already installed.

        /// <summary>
        /// The one action that resolves the allow-list and metadata-only cases. "Global Allowlist Settings"
        /// is the verbatim button label in the shipping UI (X1UI2\X1.OneDrivePlugin\View\FoldersSelectionView.xaml,
        /// X1UI2\X1.OutlookPlugin\View\OutlookConfigView.xaml) - not a paraphrase, so a user can find it.
        ///
        /// The reindex call-out is not a footnote: allow-list changes apply at index time, so ticking the
        /// box and retrying immediately looks exactly like the fix not working.
        /// </summary>
        public const string ContentAllowListGuidance =
            "To enable it: in X1 Search open the data source's settings (gear icon) -> Global Allowlist " +
            "Settings, tick the file type(s) you want indexed, then let the collection reindex. The change " +
            "applies only to items indexed after it, so a reindex is required.";

        /// <summary>
        /// Suggested next step wherever a separate, on-demand extraction path might still succeed. Phrased
        /// as "may" on purpose: x1_extract_file calls ExtractTextFromFile rather than the content store, so
        /// it genuinely can succeed where index-time extraction did not - but it is not guaranteed to, and
        /// promising a fix that then fails is worse than not suggesting it.
        /// </summary>
        private const string TryExtractFile =
            "x1_extract_file on the item's local path uses a separate on-demand extraction path and may " +
            "return text where the index did not.";

        /// <summary>
        /// Last-resort text for a failure with no state at all to classify. Kept close to the string the
        /// connector returned for every failure before XS-1746, so an old report is still recognisable.
        /// </summary>
        public const string ContentExtractionFailed =
            "Content extraction failed. " + TryExtractFile;

        /// <summary>
        /// The callback never arrived. Deliberately says nothing about content indexing: a timeout means we
        /// learned nothing about the item, and guessing "your file type isn't allow-listed" here is exactly
        /// the conflation XS-1746 exists to end.
        /// </summary>
        public static string ContentExtractionTimedOut(int timeoutMs) =>
            "Content extraction timed out after " + timeoutMs + "ms - this says nothing about whether the " +
            "item has text. Large PDFs and containers can exceed the budget: retry with a larger timeoutMs, " +
            "or " + TryExtractFile;

        /// <summary>
        /// The message for an item X1 indexed but has no text for. <paramref name="reason"/> is one of
        /// <see cref="ContentUnavailable"/>'s slugs; <paramref name="extension"/> is the item's file type
        /// (".dwg") when one could be determined, else null.
        ///
        /// Each reason gets only the guidance that actually applies to it. A password-protected PDF and an
        /// over-size container are not fixed by editing the allow-list, and sending their reader there would
        /// just be the old dead end with more words.
        /// </summary>
        public static string ContentNotIndexed(string reason, string extension)
        {
            string type = string.IsNullOrEmpty(extension) ? "this file type" : extension + " files";

            switch (reason)
            {
                case ContentUnavailable.ReasonNotAllowListed:
                    return "X1 indexed this item's metadata but not its text: " + type + " are not selected " +
                           "for content indexing in X1's global allow-list. " + ContentAllowListGuidance;

                case ContentUnavailable.ReasonFolderMetaOnly:
                    return "X1 indexed this item's metadata but not its text: the folder it lives in is set " +
                           "to index names and attributes only, not content. To enable it: in X1 Search open " +
                           "the data source's settings (gear icon), set that folder to index content, then " +
                           "let the collection reindex. The change applies only to items indexed after it, " +
                           "so a reindex is required.";

                case ContentUnavailable.ReasonTooLarge:
                    return "X1 has no indexed text for this item: it exceeded X1's content-size limit when " +
                           "it was indexed. " + TryExtractFile;

                case ContentUnavailable.ReasonEncrypted:
                    return "X1 could not extract text from this item: it is password-protected or encrypted. " +
                           "There is no connector-side workaround - the item has to be decrypted before X1 " +
                           "can index its text.";

                case ContentUnavailable.ReasonNoContent:
                    return "This item has no extractable text - X1 indexed it and found none. Nothing is " +
                           "misconfigured; there is simply no body text to return.";

                case ContentUnavailable.ReasonPending:
                    return "X1 has no indexed text for this item yet: its container is still being indexed. " +
                           "Retry once indexing finishes, or " + TryExtractFile;

                case ContentUnavailable.ReasonExtractionFailed:
                    return "X1 failed to extract text from this item when it was indexed. " + TryExtractFile;

                default:
                    // The reason could not be narrowed - typically because the item carries no istatus (not
                    // every schema does) or the installed service reports one this build doesn't know. Give
                    // the full list rather than picking one and being confidently wrong.
                    return "X1 has no indexed text for this item, so there is nothing to return. The likely " +
                           "causes are: " + type + " are not in X1's content allow-list; the folder is " +
                           "indexed for metadata only; the item exceeded X1's content-size limit; or it " +
                           "genuinely has no text. " + ContentAllowListGuidance;
            }
        }

        /// <summary>
        /// XS-1673: one-time welcome banner shown on this connector's first-ever tool call, for a
        /// connection licensed for Files-only.
        /// </summary>
        public static string FirstUseFilesOnlyBanner =>
            "Welcome to the X1 Search MCP connector — you're set up with the Files-only tier. " +
            "See " + McpLandingPageUrl + " for setup details, FAQs, or how to unlock email/cloud/chat sources.";

        /// <summary>
        /// XS-1673: one-time welcome banner shown on this connector's first-ever tool call, for a
        /// connection licensed for the full data-source suite.
        /// </summary>
        public static string FirstUseFullSuiteBanner =>
            "Welcome to the X1 Search MCP connector — you're fully licensed. " +
            "See " + McpLandingPageUrl + " for setup details and FAQs.";
    }
}
