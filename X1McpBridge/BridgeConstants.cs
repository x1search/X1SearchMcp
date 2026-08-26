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
