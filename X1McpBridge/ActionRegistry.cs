// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace X1.McpBridge
{
    /// <summary>
    /// Maps table names to their available Post Search Actions.
    /// </summary>
    internal static class ActionRegistry
    {
        // (action, description) pairs per table
        private static readonly Dictionary<string, (string action, string description)[]> Registry =
            new Dictionary<string, (string, string)[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Files"] = new[]
                {
                    ("get_path",       "Return the full local file path"),
                    ("open",           "Open with the associated application"),
                    ("show_in_folder", "Open the parent folder in Explorer with the item selected"),
                },
                ["MSMail"] = new[]
                {
                    ("open", "Open the email message (via X1 preview)"),
                },
                ["Gmail"] = new[]
                {
                    ("get_url",  "Return the Gmail web URL for this message"),
                    ("open_url", "Open the Gmail message in the default browser"),
                },
                ["GDrive"] = new[]
                {
                    ("get_url",  "Return the Google Drive web URL for this item"),
                    ("open_url", "Open the Google Drive item in the default browser"),
                    ("open",     "Open the item locally (via X1 preview)"),
                },
                ["Dropbox"] = new[]
                {
                    ("open",     "Open the locally cached copy directly (instant if already cached)"),
                    ("open_url", "Open the Dropbox item in the default browser (via X1 preview URL)"),
                },
                ["OneDrive"] = new[]
                {
                    ("open",     "Open the locally cached copy directly (instant if already cached)"),
                    ("open_url", "Open the OneDrive item in the default browser (via X1 preview URL)"),
                },
                ["SharePoint"] = new[]
                {
                    ("open",     "Open the locally cached copy directly (instant if already cached)"),
                    ("open_url", "Open the SharePoint item in the default browser (via X1 preview URL)"),
                },
                ["SP365"] = new[]
                {
                    ("open",     "Open the locally cached copy directly (instant if already cached)"),
                    ("open_url", "Open the SharePoint Online item in the default browser (via X1 preview URL)"),
                },
                ["Teams"] = new[]
                {
                    ("open_url", "Open the Teams message in the default browser (via X1 preview URL)"),
                },
                ["Slack"] = new[]
                {
                    ("open_url", "Open the Slack message in the default browser (via X1 preview URL)"),
                },
            };

        // Tables with a known preview provider — used by x1_list_sources to advertise the
        // "preview" capability. Conservative: a missing entry simply means Claude falls back to
        // trying x1_generate_preview rather than being told preview is unavailable.
        private static readonly HashSet<string> PreviewTables =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "OneDrive", "GDrive", "MSMail", "Exchange", "Gmail", "Files",
            };

        /// <summary>
        /// Returns true when the given table has a known preview provider (so x1_generate_preview
        /// is expected to produce rich output rather than only a metadata-card fallback).
        /// </summary>
        public static bool HasPreview(string table)
        {
            return !string.IsNullOrEmpty(table) && PreviewTables.Contains(table);
        }

        public static IEnumerable<(string action, string description)> GetActions(string table)
        {
            if (string.IsNullOrEmpty(table))
                return Array.Empty<(string, string)>();

            return Registry.TryGetValue(table, out var actions)
                ? actions
                : Array.Empty<(string, string)>();
        }

        /// <summary>
        /// Returns the available actions for a table as a JSON array of { action, description }
        /// objects, suitable for embedding directly in search results or x1_list_sources output.
        /// </summary>
        public static JArray GetActionsJson(string table)
        {
            var arr = new JArray();
            foreach (var (action, description) in GetActions(table))
                arr.Add(new JObject { ["action"] = action, ["description"] = description });
            return arr;
        }

        public static bool IsActionSupported(string table, string action)
        {
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(action))
                return false;

            if (!Registry.TryGetValue(table, out var actions))
                return false;

            foreach (var (a, _) in actions)
            {
                if (string.Equals(a, action, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
