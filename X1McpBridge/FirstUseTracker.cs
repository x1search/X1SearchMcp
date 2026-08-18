// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace X1.McpBridge
{
    /// <summary>
    /// XS-1673: tracks whether the one-time first-use welcome banner has already been shown for this
    /// install, persisted as its own JSON file beside the exe (survives bridge restarts, same pattern
    /// as <see cref="CostTracker"/>). Deliberately a separate file from x1mcp_stats.json rather than a
    /// field on it - CostTracker's file is discarded wholesale whenever its cost-methodology schema
    /// version bumps, which would make this banner unexpectedly reappear if it shared that file.
    ///
    /// Accepted race: two bridge processes for the same user starting at the same instant (e.g. Claude
    /// Desktop and Claude Code cold-starting together) could both read "not shown" before either writes
    /// - the banner could in theory appear twice. Not worth cross-process file locking for a cosmetic,
    /// non-blocking message.
    /// </summary>
    internal static class FirstUseTracker
    {
        // Bumped only if the marker file's shape changes; older files are treated as "not yet shown"
        // rather than merged, so a schema change simply re-shows the banner once, harmlessly.
        private const int CurrentSchemaVersion = 1;

        private static readonly object Sync = new object();
        private static string _markerPath;

        internal static bool HasBeenShown()
        {
            lock (Sync)
            {
                return Load().Value<bool?>("shown") == true;
            }
        }

        internal static void MarkShown()
        {
            lock (Sync)
            {
                JObject state = Load();
                state["version"] = CurrentSchemaVersion;
                state["shown"] = true;
                state["shownAt"] = DateTime.UtcNow.ToString("o");
                Save(state);
            }
        }

        internal static string BannerFor(bool fullSuiteLicensed) =>
            fullSuiteLicensed ? BridgeConstants.FirstUseFullSuiteBanner : BridgeConstants.FirstUseFilesOnlyBanner;

        private static JObject Load()
        {
            string path = MarkerPath();
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

        private static void Save(JObject state)
        {
            File.WriteAllText(MarkerPath(), state.ToString(Formatting.Indented));
        }

        internal static string MarkerPath()
        {
            if (_markerPath != null) return _markerPath;
            var exeDir = Path.GetDirectoryName(typeof(FirstUseTracker).Assembly.Location) ?? ".";
            _markerPath = Path.Combine(exeDir, "x1mcp_first_use.json");
            return _markerPath;
        }

        internal static void OverrideMarkerPath(string path)
        {
            lock (Sync) { _markerPath = path; }
        }
    }
}
