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
using System.Threading.Tasks;

namespace X1.McpBridge
{
    /// <summary>
    /// Startup cleanup for the bridge's temp artifacts.
    ///
    /// <para>Windows does not sweep <c>%TEMP%</c> on user logout / reboot, and the bridge
    /// itself has no per-request lifecycle for the two dirs it writes to:</para>
    /// <list type="bullet">
    /// <item><c>%TEMP%\x1mcp_previews\</c> — HTML fragments and image assets from
    /// <c>x1_generate_preview output=file</c> and <c>x1_export_html</c>. Stable
    /// hash-based names so repeats overwrite, but unique items accumulate over time.</item>
    /// <item><c>%TEMP%\x1mcp_extract_*.txt</c> / <c>x1mcp_content_*.txt</c> — Guid-named
    /// temp files used during <c>x1_extract_file</c> / <c>x1_get_content</c> requests.
    /// The request path deletes them in a <c>finally</c> block, so leftovers only exist
    /// when the bridge was killed mid-request.</item>
    /// </list>
    ///
    /// <para>The sweep runs at bridge startup so the cost never lands during a live
    /// tool call. Thresholds are read from <see cref="BridgeConfig.GetTempMaxAgeHours"/>
    /// and <see cref="BridgeConfig.GetTempMaxTotalMB"/> — set either to 0 to disable that
    /// half of the sweep.</para>
    /// </summary>
    internal static class TempSweep
    {
        private static readonly log4net.ILog Log = BridgeLogger.GetLogger(typeof(TempSweep));

        internal const string PreviewsDirName = "x1mcp_previews";
        internal const string ExtractOrphanGlob = "x1mcp_extract_*.txt";
        internal const string ContentOrphanGlob = "x1mcp_content_*.txt";

        /// <summary>
        /// Fires the sweep on a background thread. Never throws — logs and returns.
        /// Called from <see cref="McpServer.RunStdio"/> so a slow disk cannot delay
        /// bridge startup or block the stdio loop.
        /// </summary>
        public static void RunInBackground()
        {
            Task.Run(() =>
            {
                try { Run(Path.GetTempPath()); }
                catch (Exception ex) { Log.Warn("TempSweep failed: " + ex.Message); }
            });
        }

        /// <summary>
        /// Synchronous entry point. Exposed for unit tests — production code should use
        /// <see cref="RunInBackground"/> so a slow disk doesn't stall startup.
        /// </summary>
        internal static SweepReport Run(string tempRoot)
        {
            if (string.IsNullOrEmpty(tempRoot)) throw new ArgumentException("tempRoot required.");

            int maxAgeHours = BridgeConfig.GetTempMaxAgeHours();
            int maxTotalMB = BridgeConfig.GetTempMaxTotalMB();
            var previewsDir = Path.Combine(tempRoot, PreviewsDirName);
            var cutoff = maxAgeHours > 0
                ? DateTime.UtcNow.AddHours(-maxAgeHours)
                : (DateTime?)null;

            var report = new SweepReport();

            // 1. Orphan extract/content temps at the temp root (any age — they only exist
            //    if a request was interrupted).
            report.OrphansDeleted += DeleteMatching(tempRoot, ExtractOrphanGlob, olderThan: null, out long orphanBytes);
            report.BytesReclaimed += orphanBytes;
            report.OrphansDeleted += DeleteMatching(tempRoot, ContentOrphanGlob, olderThan: null, out orphanBytes);
            report.BytesReclaimed += orphanBytes;

            // 2. Preview dir doesn't exist yet — nothing more to do.
            if (!Directory.Exists(previewsDir))
            {
                Log.Debug("TempSweep: " + previewsDir + " does not exist; nothing to do beyond orphan cleanup.");
                LogSummary(report, maxAgeHours, maxTotalMB);
                return report;
            }

            // 3. Age sweep across everything in previews dir.
            if (cutoff.HasValue)
            {
                report.AgedOutDeleted += DeleteOlderThan(previewsDir, cutoff.Value, out long agedBytes);
                report.BytesReclaimed += agedBytes;
            }

            // 4. Size sweep: if we're still over cap after the age sweep, delete oldest first.
            if (maxTotalMB > 0)
            {
                long capBytes = (long)maxTotalMB * 1024L * 1024L;
                var files = EnumerateAllFiles(previewsDir);
                long total = 0;
                foreach (var f in files) total += f.length;
                if (total > capBytes)
                {
                    files.Sort((a, b) => a.mtimeUtc.CompareTo(b.mtimeUtc)); // oldest first
                    foreach (var f in files)
                    {
                        if (total <= capBytes) break;
                        if (TryDelete(f.path))
                        {
                            total -= f.length;
                            report.OverCapDeleted++;
                            report.BytesReclaimed += f.length;
                        }
                    }
                }
            }

            // 5. Prune empty subdirectories left behind by any of the above.
            report.EmptyDirsRemoved += PruneEmptyDirs(previewsDir);

            LogSummary(report, maxAgeHours, maxTotalMB);
            return report;
        }

        private static void LogSummary(SweepReport report, int maxAgeHours, int maxTotalMB)
        {
            if (report.IsNoop)
            {
                Log.Debug(string.Format(
                    "TempSweep complete: nothing to reclaim. (maxAgeHours={0}, maxTotalMB={1})",
                    maxAgeHours, maxTotalMB));
            }
            else
            {
                Log.Info(string.Format(
                    "TempSweep reclaimed {0:N0} bytes: {1} orphans, {2} aged, {3} over-cap, {4} empty dirs. (maxAgeHours={5}, maxTotalMB={6})",
                    report.BytesReclaimed, report.OrphansDeleted, report.AgedOutDeleted,
                    report.OverCapDeleted, report.EmptyDirsRemoved, maxAgeHours, maxTotalMB));
            }
        }

        private static int DeleteMatching(string dir, string pattern, DateTime? olderThan, out long bytesReclaimed)
        {
            bytesReclaimed = 0;
            if (!Directory.Exists(dir)) return 0;

            int deleted = 0;
            foreach (var path in EnumerateFilesSafe(dir, pattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (olderThan.HasValue && fi.LastWriteTimeUtc >= olderThan.Value) continue;
                    long len = fi.Length;
                    if (TryDelete(path)) { deleted++; bytesReclaimed += len; }
                }
                catch { /* ignore */ }
            }
            return deleted;
        }

        private static int DeleteOlderThan(string dir, DateTime cutoff, out long bytesReclaimed)
        {
            bytesReclaimed = 0;
            int deleted = 0;
            foreach (var path in EnumerateFilesSafe(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.LastWriteTimeUtc >= cutoff) continue;
                    long len = fi.Length;
                    if (TryDelete(path)) { deleted++; bytesReclaimed += len; }
                }
                catch { /* ignore */ }
            }
            return deleted;
        }

        private struct FileRec { public string path; public long length; public DateTime mtimeUtc; }

        private static List<FileRec> EnumerateAllFiles(string dir)
        {
            var list = new List<FileRec>();
            foreach (var path in EnumerateFilesSafe(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(path);
                    list.Add(new FileRec { path = path, length = fi.Length, mtimeUtc = fi.LastWriteTimeUtc });
                }
                catch { /* skip unreadable */ }
            }
            return list;
        }

        private static int PruneEmptyDirs(string root)
        {
            int removed = 0;
            // Post-order: process children before their parents so newly-emptied dirs also get removed.
            foreach (var dir in EnumerateDirsPostOrder(root))
            {
                try
                {
                    if (dir.Equals(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Directory.GetFileSystemEntries(dir).Length == 0)
                    {
                        Directory.Delete(dir);
                        removed++;
                    }
                }
                catch { /* ignore */ }
            }
            return removed;
        }

        private static IEnumerable<string> EnumerateDirsPostOrder(string root)
        {
            var stack = new Stack<string>();
            var out_ = new List<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                out_.Add(cur);
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(cur))
                        stack.Push(sub);
                }
                catch { /* ignore */ }
            }
            out_.Reverse(); // deepest first
            return out_;
        }

        private static IEnumerable<string> EnumerateFilesSafe(string dir, string pattern, SearchOption opt)
        {
            try { return Directory.EnumerateFiles(dir, pattern, opt); }
            catch { return System.Linq.Enumerable.Empty<string>(); }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.IsReadOnly) fi.IsReadOnly = false;
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        internal sealed class SweepReport
        {
            public int OrphansDeleted;
            public int AgedOutDeleted;
            public int OverCapDeleted;
            public int EmptyDirsRemoved;
            public long BytesReclaimed;
            public bool IsNoop
            {
                get
                {
                    return OrphansDeleted == 0 && AgedOutDeleted == 0 &&
                           OverCapDeleted == 0 && EmptyDirsRemoved == 0;
                }
            }
        }
    }
}
