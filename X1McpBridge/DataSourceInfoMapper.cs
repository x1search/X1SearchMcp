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
using X1.Service;

namespace X1.McpBridge
{
    /// <summary>
    /// Builds the x1_list_sources result directly from X1ServiceHost's own
    /// GetDataSourcesInfo() response (ConfiguredDataSourceInfo[]) - the only sources
    /// reported here are ones the service host actually confirmed. Previously the tool
    /// padded this list with every table BridgeConfig knew how to configure, whether or
    /// not the service host had ever reported it - misleading callers into thinking
    /// unconfigured/nonexistent sources were real. The only correction still applied is
    /// unmangling the known XS-1605 dirty-scanner-name bug (see CleanScannerName) -
    /// that's fixing a live entry's name, not inventing a source that wasn't reported.
    ///
    /// XS-1612 added two per-account fields this mapper now surfaces: scannerDisplayName
    /// (a human-facing label, e.g. "MS Online Archive" for Exchange - exposed as
    /// "displayName") and itemCount (this account's own indexed-item count, distinct from
    /// totalCount which is scanner-wide and can repeat across sibling accounts).
    /// </summary>
    internal static class DataSourceInfoMapper
    {
        public static JArray BuildSources(ConfiguredDataSourceInfo[] info)
        {
            var sources = new JArray();
            if (info == null) return sources;

            var byName = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var d in info)
            {
                string cleanName = CleanScannerName(d.scannerName, d.accountName);
                if (!byName.TryGetValue(cleanName, out var accounts))
                {
                    accounts = new JArray();
                    byName[cleanName] = accounts;
                    order.Add(cleanName);
                }

                var acc = new JObject
                {
                    // XS-1612: totalCount is scanner-wide (GetTotalItemCount) - when multiple
                    // accounts share one schema (e.g. several Outlook/IMAP accounts), every
                    // account's row reports the SAME combined number, not this account's own
                    // count. itemCount (GetAccountItemCount) is the one that's actually scoped
                    // to this account - prefer it whenever a true per-account count is needed.
                    ["totalCount"] = d.totalCount,
                    ["itemCount"] = d.itemCount,
                    ["isScanning"] = d.isScanning
                };
                if (!string.IsNullOrEmpty(d.accountName)) acc["accountName"] = d.accountName;
                // Per-account, not per-scanner: e.g. IMAP's displayName depends on that
                // account's AccountType ("IMAP - Gmail" vs "IMAP - Yahoo"), so two accounts
                // grouped under the same scanner name can legitimately show different values.
                if (!string.IsNullOrEmpty(d.scannerDisplayName)) acc["displayName"] = d.scannerDisplayName;
                if (!string.IsNullOrEmpty(d.lastScanTime)) acc["lastScanTime"] = d.lastScanTime;
                if (d.schemas != null && d.schemas.Length > 0) acc["schemas"] = new JArray(d.schemas);
                accounts.Add(acc);
            }

            foreach (var cleanName in order)
            {
                sources.Add(new JObject
                {
                    ["name"] = cleanName,
                    ["columns"] = new JArray(BridgeConfig.GetColumnsForTable(cleanName)),
                    ["capabilities"] = BuildSourceCapabilities(cleanName),
                    ["accounts"] = byName[cleanName]
                });
            }
            return sources;
        }

        /// <summary>
        /// XS-1605: Dropbox and Box never override Schedulable.PluginNameForEDS, so their
        /// scannerName comes back account-suffixed (e.g. "Dropbox-user@x.com") instead of a
        /// clean "Dropbox". Strip that known suffix pattern back off when it's present - this
        /// corrects a real server-side naming bug for a source that genuinely was reported,
        /// it doesn't attach the account to some other unrelated/invented source. Remove this
        /// once XS-1605 is fixed server-side.
        /// </summary>
        internal static string CleanScannerName(string scannerName, string accountName)
        {
            string raw = scannerName ?? "";
            if (!string.IsNullOrEmpty(accountName) &&
                raw.Length > accountName.Length + 1 &&
                raw.EndsWith("-" + accountName, StringComparison.OrdinalIgnoreCase))
            {
                return raw.Substring(0, raw.Length - accountName.Length - 1);
            }
            return raw;
        }

        private static JObject BuildSourceCapabilities(string table)
        {
            var actionNames = new JArray();
            foreach (var (action, _) in ActionRegistry.GetActions(table))
                actionNames.Add(action);
            return new JObject
            {
                ["actions"] = actionNames,
                ["preview"] = ActionRegistry.HasPreview(table)
            };
        }
    }
}
