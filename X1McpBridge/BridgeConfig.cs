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
using Newtonsoft.Json.Linq;

namespace X1.McpBridge
{
    /// <summary>
    /// Optional JSON config beside the executable: x1mcp.config.json
    ///
    /// Example:
    /// {
    ///   "defaultTables": ["Files", "Outlook"],
    ///   "autoPreviewTimeoutMs": 10000,
    ///   "prefetchPreviewCount": 3,
    ///   "tempMaxAgeHours": 168,
    ///   "tempMaxTotalMB": 500,
    ///   "sources": {
    ///     "Files":   ["Name", "Path", "Size", "Date", "Extension"],
    ///     "Outlook": ["Subject", "From", "To", "Date", "Path"],
    ///     "Gmail":   ["Subject", "From", "To", "Date"]
    ///   }
    /// }
    /// </summary>
    internal static class BridgeConfig
    {
        private static string[] defaultTablesCache;
        private static Dictionary<string, string[]> sourceColumnsCache;
        private static int autoPreviewTimeoutMsCache = -1;  // -1 = not loaded
        private static int prefetchPreviewCountCache = -1;  // -1 = not loaded
        private static bool pathOutputOutsideContextCache = true;
        private static int tempMaxAgeHoursCache = -1;       // -1 = not loaded
        private static int tempMaxTotalMBCache = -1;        // -1 = not loaded
        private static string savedContentDirCache;         // null = not loaded
        private static string daemonUrlCache;                // null = not loaded
        private static string daemonExePathCache;             // null = not loaded
        private static int daemonStartupTimeoutMsCache = -1; // -1 = not loaded
        // null = no explicit override; GetRelayMode() then probes the filesystem. Not defaulted in
        // EnsureLoaded like the others, because "unset" is a meaningful third state here.
        private static string relayModeOverrideCache;
        private static readonly object Sync = new object();

        /// <summary>
        /// Timeout in milliseconds for the preview attempt in <c>auto</c> mode before
        /// falling back to internal fields.  Default 10 000 ms; configurable via
        /// <c>autoPreviewTimeoutMs</c> in x1mcp.config.json.
        /// </summary>
        public static int GetAutoPreviewTimeoutMs()
        {
            EnsureLoaded();
            return autoPreviewTimeoutMsCache;
        }

        /// <summary>
        /// Number of top cloud-file results (OneDrive, GDrive, SharePoint, Dropbox) for which the
        /// bridge fires a background preview after a search, so the connector downloads and caches
        /// the file before <c>x1_generate_preview</c> / <c>open</c> is called. 0 disables prefetch.
        /// Default 3; configurable via <c>prefetchPreviewCount</c> in x1mcp.config.json.
        /// </summary>
        public static int GetPrefetchPreviewCount()
        {
            EnsureLoaded();
            return prefetchPreviewCountCache;
        }

        /// <summary>
        /// Whether content requested in path-returning form (<c>x1_generate_preview output="file"</c>,
        /// <c>x1_get_content mode="preview"</c>) is normally consumed OUTSIDE the model's context -
        /// opened by a person, rendered as an artifact, or fed to a local tool - rather than read back
        /// into the conversation later.
        ///
        /// Default true, because asking for the path form instead of the inline form is itself the
        /// request to keep it out of context; that is the purpose of the parameter. When true, those
        /// tokens count as genuinely avoided rather than merely postponed.
        ///
        /// Unlike the assumeAlternativeHasFilesystem switch this replaced, this one is a legitimate
        /// setting: it asks about how a deployment USES the tool, which no measurement of the software
        /// can answer, rather than an empirical question about what the connector returns.
        /// Configurable via <c>pathOutputConsumedOutsideContext</c> in x1mcp.config.json.
        /// </summary>
        public static bool GetPathOutputConsumedOutsideContext()
        {
            EnsureLoaded();
            return pathOutputOutsideContextCache;
        }


        /// <summary>
        /// Startup-sweep threshold for the bridge's temp preview/extract artifacts.
        /// Files under <c>%TEMP%\x1mcp_previews\</c> and orphan <c>x1mcp_extract_*.txt</c> /
        /// <c>x1mcp_content_*.txt</c> at the temp root older than this are deleted at bridge
        /// startup. Default 168 hours (7 days); <c>0</c> disables the age sweep.
        /// Configurable via <c>tempMaxAgeHours</c> in x1mcp.config.json.
        /// </summary>
        public static int GetTempMaxAgeHours()
        {
            EnsureLoaded();
            return tempMaxAgeHoursCache;
        }

        /// <summary>
        /// After the age sweep runs, if the total size of <c>%TEMP%\x1mcp_previews\</c> still
        /// exceeds this megabyte cap, the oldest files are deleted first until the total is under
        /// the cap. Default 500 MB; <c>0</c> disables the size sweep.
        /// Configurable via <c>tempMaxTotalMB</c> in x1mcp.config.json.
        /// </summary>
        public static int GetTempMaxTotalMB()
        {
            EnsureLoaded();
            return tempMaxTotalMBCache;
        }

        public static string[] GetDefaultTables()
        {
            EnsureLoaded();
            return defaultTablesCache;
        }

        /// <summary>
        /// Returns the configured display columns for the given table, or an empty array
        /// if no columns are configured (which tells the service to return bare results).
        /// </summary>
        public static string[] GetColumnsForTable(string table)
        {
            EnsureLoaded();
            if (table != null && sourceColumnsCache.TryGetValue(table, out var cols))
                return cols;
            return new string[0];
        }

        /// <summary>
        /// Returns all configured sources and their columns, for the x1_list_sources tool.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string[]>> GetAllSources()
        {
            EnsureLoaded();
            return sourceColumnsCache;
        }

        /// <summary>
        /// Root directory for <c>output="save"</c> previews that should persist across reboots.
        /// Priority: env var <c>X1_MCP_SAVED_CONTENT_DIR</c> → <c>savedContentDir</c> in config →
        /// <c>%USERPROFILE%\Documents\X1 Saved</c>.
        /// </summary>
        public static string GetSavedContentDir()
        {
            EnsureLoaded();
            return savedContentDirCache;
        }

        /// <summary>
        /// Base URL of the shared relay that ProxyMode forwards MCP JSON-RPC to. Both relay
        /// flavors serve the same contract on the same URL (see <see cref="GetRelayMode"/>).
        /// Priority: env var <c>X1_MCP_DAEMON_URL</c> → <c>daemonUrl</c> in config →
        /// <c>http://localhost:5250</c> (matches the daemon's own default Kestrel binding).
        /// </summary>
        public static string GetDaemonUrl()
        {
            EnsureLoaded();
            return daemonUrlCache;
        }

        /// <summary>
        /// Path to the X1McpGraphQL daemon executable. Priority: env var
        /// <c>X1_MCP_DAEMON_EXE_PATH</c> → <c>daemonExePath</c> in config → a sibling
        /// <c>X1McpGraphQL.exe</c> next to this bridge exe (the Full-flavor plugin/installer
        /// bundle both side by side — see build-plugin.ps1).
        ///
        /// Note this resolves a path, not an existence claim: in the Lean flavor no daemon ships,
        /// so the returned path deliberately names a file that isn't there. Callers deciding what
        /// to launch must go through <see cref="GetRelayLaunchTarget"/> instead.
        /// </summary>
        public static string GetDaemonExePath()
        {
            EnsureLoaded();
            return daemonExePathCache;
        }

        /// <summary>
        /// Which relay this install expects to be answering on <see cref="GetDaemonUrl"/>.
        /// Priority: env var <c>X1_MCP_RELAY_MODE</c> (<c>daemon</c>|<c>host</c>) →
        /// <c>relayMode</c> in config → probe: a daemon exe on disk means Full, its absence
        /// means Lean.
        ///
        /// The probe is deliberately a runtime check rather than a compile-time constant, so one
        /// X1McpBridge.exe behaves correctly in a Full install dir and a Lean one alike (a machine
        /// can legitimately have both - the standalone install and the Cowork plugin's connector\
        /// are separate roots). Two differently-compiled bridges sharing one version.props number
        /// would also destroy exactly the build identity /health and x1_version exist to report.
        /// </summary>
        public static RelayMode GetRelayMode()
        {
            EnsureLoaded();

            if (string.Equals(relayModeOverrideCache, "host", StringComparison.OrdinalIgnoreCase))
                return RelayMode.Host;
            if (string.Equals(relayModeOverrideCache, "daemon", StringComparison.OrdinalIgnoreCase))
                return RelayMode.Daemon;

            try { return File.Exists(daemonExePathCache) ? RelayMode.Daemon : RelayMode.Host; }
            catch { return RelayMode.Host; }
        }

        /// <summary>
        /// What ProxyMode should actually start when nothing usable is answering on
        /// <see cref="GetDaemonUrl"/>: the bundled net10 daemon in the Full flavor, or this very
        /// exe re-launched as <c>--host</c> in the Lean flavor.
        ///
        /// An explicitly-configured daemon path that exists always wins, even in a Lean install -
        /// that preserves a daemon-beside-a-Lean-bridge dev setup, and honours a customer who
        /// pointed daemonExePath somewhere deliberately. A configured path that no longer exists
        /// (e.g. a Full → Lean upgrade left the key behind after the installer deleted the file)
        /// falls through to <c>--host</c> rather than failing, which is what lets the installer
        /// warn about a stale key instead of having to rewrite the customer's config.
        /// </summary>
        public static RelayLaunchTarget GetRelayLaunchTarget()
        {
            if (GetRelayMode() == RelayMode.Daemon)
                return new RelayLaunchTarget(RelayMode.Daemon, daemonExePathCache, null);

            string selfExe;
            try { selfExe = typeof(BridgeConfig).Assembly.Location; }
            catch { selfExe = null; }
            if (string.IsNullOrEmpty(selfExe))
                selfExe = "X1McpBridge.exe";

            return new RelayLaunchTarget(RelayMode.Host, selfExe, "--host");
        }

        /// <summary>
        /// How long ProxyMode waits for a freshly-launched daemon to answer its health check
        /// before giving up and forwarding the request anyway (letting the resulting connection
        /// error surface as a clear tool error). Default 15 000 ms; configurable via
        /// <c>daemonStartupTimeoutMs</c> in x1mcp.config.json.
        /// </summary>
        public static int GetDaemonStartupTimeoutMs()
        {
            EnsureLoaded();
            return daemonStartupTimeoutMsCache;
        }

        internal static void ResetForTesting()
        {
            lock (Sync)
            {
                defaultTablesCache = null;
                sourceColumnsCache = null;
                autoPreviewTimeoutMsCache = -1;
                prefetchPreviewCountCache = -1;
                tempMaxAgeHoursCache = -1;
                tempMaxTotalMBCache = -1;
                savedContentDirCache = null;
                daemonUrlCache = null;
                daemonExePathCache = null;
                daemonStartupTimeoutMsCache = -1;
                relayModeOverrideCache = null;
            }
        }

        private static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (defaultTablesCache != null)
                    return;

                sourceColumnsCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                autoPreviewTimeoutMsCache = 10000;  // default
                prefetchPreviewCountCache = 3;      // default
                pathOutputOutsideContextCache = true;
                tempMaxAgeHoursCache = 168;         // default: 7 days
                tempMaxTotalMBCache = 500;          // default: 500 MB

                // Environment variable for saved-content directory (highest priority)
                var savedEnv = Environment.GetEnvironmentVariable("X1_MCP_SAVED_CONTENT_DIR");
                if (!string.IsNullOrWhiteSpace(savedEnv))
                    savedContentDirCache = savedEnv.Trim();

                var daemonUrlEnv = Environment.GetEnvironmentVariable("X1_MCP_DAEMON_URL");
                if (!string.IsNullOrWhiteSpace(daemonUrlEnv))
                    daemonUrlCache = daemonUrlEnv.Trim().TrimEnd('/');

                var daemonExeEnv = Environment.GetEnvironmentVariable("X1_MCP_DAEMON_EXE_PATH");
                if (!string.IsNullOrWhiteSpace(daemonExeEnv))
                    daemonExePathCache = daemonExeEnv.Trim();

                var relayModeEnv = Environment.GetEnvironmentVariable("X1_MCP_RELAY_MODE");
                if (!string.IsNullOrWhiteSpace(relayModeEnv))
                    relayModeOverrideCache = relayModeEnv.Trim();

                // Environment variable overrides for default tables
                var env = Environment.GetEnvironmentVariable("X1_MCP_DEFAULT_TABLES");
                if (!string.IsNullOrWhiteSpace(env))
                {
                    var parts = env.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < parts.Length; i++)
                        parts[i] = parts[i].Trim();
                    defaultTablesCache = parts.Length > 0 ? parts : new[] { "Files" };
                }

                try
                {
                    var exeDir = Path.GetDirectoryName(typeof(BridgeConfig).Assembly.Location) ?? ".";
                    var path = Path.Combine(exeDir, "x1mcp.config.json");
                    if (File.Exists(path))
                    {
                        var jo = JObject.Parse(File.ReadAllText(path));

                        if (defaultTablesCache == null)
                        {
                            var arr = jo["defaultTables"] as JArray;
                            if (arr != null && arr.Count > 0)
                            {
                                var list = new string[arr.Count];
                                for (int i = 0; i < arr.Count; i++)
                                    list[i] = arr[i].ToString();
                                defaultTablesCache = list;
                            }
                        }

                        if (jo["sources"] is JObject sources)
                        {
                            foreach (JProperty prop in sources.Properties())
                            {
                                if (prop.Value is JArray cols && cols.Count > 0)
                                {
                                    var colNames = new string[cols.Count];
                                    for (int i = 0; i < cols.Count; i++)
                                        colNames[i] = cols[i].ToString();
                                    sourceColumnsCache[prop.Name] = colNames;
                                }
                            }
                        }

                        var autoTimeout = jo.Value<int?>("autoPreviewTimeoutMs");
                        if (autoTimeout.HasValue && autoTimeout.Value > 0)
                            autoPreviewTimeoutMsCache = autoTimeout.Value;

                        var prefetchCount = jo.Value<int?>("prefetchPreviewCount");
                        if (prefetchCount.HasValue && prefetchCount.Value >= 0)
                            prefetchPreviewCountCache = prefetchCount.Value;

                        var pathOutside = jo.Value<bool?>("pathOutputConsumedOutsideContext");
                        if (pathOutside.HasValue)
                            pathOutputOutsideContextCache = pathOutside.Value;

                        var maxAge = jo.Value<int?>("tempMaxAgeHours");
                        if (maxAge.HasValue && maxAge.Value >= 0)
                            tempMaxAgeHoursCache = maxAge.Value;

                        var maxTotal = jo.Value<int?>("tempMaxTotalMB");
                        if (maxTotal.HasValue && maxTotal.Value >= 0)
                            tempMaxTotalMBCache = maxTotal.Value;

                        if (savedContentDirCache == null)
                        {
                            var savedDir = jo.Value<string>("savedContentDir");
                            if (!string.IsNullOrWhiteSpace(savedDir))
                                savedContentDirCache = savedDir.Trim();
                        }

                        if (daemonUrlCache == null)
                        {
                            var daemonUrl = jo.Value<string>("daemonUrl");
                            if (!string.IsNullOrWhiteSpace(daemonUrl))
                                daemonUrlCache = daemonUrl.Trim().TrimEnd('/');
                        }

                        if (daemonExePathCache == null)
                        {
                            var daemonExe = jo.Value<string>("daemonExePath");
                            if (!string.IsNullOrWhiteSpace(daemonExe))
                                daemonExePathCache = daemonExe.Trim();
                        }

                        var daemonStartupTimeout = jo.Value<int?>("daemonStartupTimeoutMs");
                        if (daemonStartupTimeout.HasValue && daemonStartupTimeout.Value > 0)
                            daemonStartupTimeoutMsCache = daemonStartupTimeout.Value;

                        if (relayModeOverrideCache == null)
                        {
                            var relayMode = jo.Value<string>("relayMode");
                            if (!string.IsNullOrWhiteSpace(relayMode))
                                relayModeOverrideCache = relayMode.Trim();
                        }
                    }
                }
                catch
                {
                    // ignore malformed config
                }

                if (defaultTablesCache == null)
                    defaultTablesCache = new[] { "Files" };

                if (savedContentDirCache == null)
                    savedContentDirCache = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "X1 Saved");

                if (daemonUrlCache == null)
                    daemonUrlCache = "http://localhost:5250";

                if (daemonExePathCache == null)
                {
                    try
                    {
                        var exeDir = Path.GetDirectoryName(typeof(BridgeConfig).Assembly.Location) ?? ".";
                        daemonExePathCache = Path.Combine(exeDir, "X1McpGraphQL.exe");
                    }
                    catch
                    {
                        daemonExePathCache = "X1McpGraphQL.exe";
                    }
                }

                if (daemonStartupTimeoutMsCache < 0)
                    daemonStartupTimeoutMsCache = 15000;
            }
        }
    }

    /// <summary>
    /// Which process is the shared fan-in relay for this install. Both modes serve the identical
    /// wire contract (GET /health, POST /graphql/mcp) on the identical URL, which is what lets the
    /// two installer flavors be drop-in interchangeable and keeps every registered
    /// <c>--proxy</c> MCP entry working unchanged across an upgrade in either direction.
    /// </summary>
    internal enum RelayMode
    {
        /// <summary>Full flavor: the bundled net10 X1McpGraphQL.exe, which spawns its own bridge child.</summary>
        Daemon,

        /// <summary>Lean flavor: a detached <c>X1McpBridge.exe --host</c>, which IS the bridge (no .NET 10).</summary>
        Host,
    }

    internal struct RelayLaunchTarget
    {
        public readonly RelayMode Mode;
        public readonly string FileName;
        public readonly string Arguments;   // null/empty for the daemon

        public RelayLaunchTarget(RelayMode mode, string fileName, string arguments)
        {
            Mode = mode;
            FileName = fileName;
            Arguments = arguments;
        }

        /// <summary>What the component name in /health will be once this target is up.</summary>
        public string ExpectedComponent
        {
            get { return Mode == RelayMode.Daemon ? RelayHealth.ComponentDaemon : RelayHealth.ComponentHost; }
        }
    }
}
