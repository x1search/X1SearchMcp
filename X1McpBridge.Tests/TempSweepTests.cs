// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// TempSweep runs at bridge startup to enforce tempMaxAgeHours + tempMaxTotalMB limits on
    /// %TEMP%\x1mcp_previews\ plus any orphaned x1mcp_extract_*.txt / x1mcp_content_*.txt at
    /// the temp root. Tests build a fake temp root under Path.GetTempPath() and drive the sweep
    /// synchronously via the internal Run overload.
    /// </summary>
    [TestFixture]
    public class TempSweepTests
    {
        private string _fakeTempRoot;
        private string _previewsDir;
        private string _savedConfig;
        private static string ConfigPath =>
            Path.Combine(Path.GetDirectoryName(typeof(BridgeConfig).Assembly.Location),
                "x1mcp.config.json");

        [SetUp]
        public void SetUp()
        {
            _fakeTempRoot = Path.Combine(Path.GetTempPath(),
                "x1mcp_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_fakeTempRoot);
            _previewsDir = Path.Combine(_fakeTempRoot, TempSweep.PreviewsDirName);

            _savedConfig = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
            BridgeConfig.ResetForTesting();
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_fakeTempRoot, recursive: true); } catch { }

            if (_savedConfig != null) File.WriteAllText(ConfigPath, _savedConfig);
            else if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();
        }

        private void SetConfig(int maxAgeHours, int maxTotalMB)
        {
            File.WriteAllText(ConfigPath,
                $"{{\"sources\":{{}},\"tempMaxAgeHours\":{maxAgeHours},\"tempMaxTotalMB\":{maxTotalMB}}}");
            BridgeConfig.ResetForTesting();
        }

        private string WriteFile(string dir, string name, int bytes, DateTime mtimeUtc)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, mtimeUtc);
            return path;
        }

        // ── Missing previews dir ─────────────────────────────────────────────────

        [Test]
        public void Run_PreviewsDirMissing_IsNoop()
        {
            SetConfig(maxAgeHours: 24, maxTotalMB: 100);
            Assert.That(Directory.Exists(_previewsDir), Is.False);

            var report = TempSweep.Run(_fakeTempRoot);

            Assert.That(report.IsNoop, Is.True);
        }

        // ── Age sweep ────────────────────────────────────────────────────────────

        [Test]
        public void Run_AgeSweep_DeletesOldFilesKeepsFresh()
        {
            SetConfig(maxAgeHours: 24, maxTotalMB: 500);
            var old = WriteFile(_previewsDir, "old.html", 100, DateTime.UtcNow.AddDays(-3));
            var fresh = WriteFile(_previewsDir, "fresh.html", 100, DateTime.UtcNow.AddHours(-1));

            var report = TempSweep.Run(_fakeTempRoot);

            Assert.That(File.Exists(old), Is.False, "old file should be swept");
            Assert.That(File.Exists(fresh), Is.True, "fresh file must survive");
            Assert.That(report.AgedOutDeleted, Is.EqualTo(1));
            Assert.That(report.BytesReclaimed, Is.EqualTo(100));
        }

        [Test]
        public void Run_AgeSweep_Zero_DisablesAgeSweep()
        {
            SetConfig(maxAgeHours: 0, maxTotalMB: 0);
            var old = WriteFile(_previewsDir, "ancient.html", 100, DateTime.UtcNow.AddDays(-365));

            var report = TempSweep.Run(_fakeTempRoot);

            Assert.That(File.Exists(old), Is.True, "age sweep is disabled — file must survive");
            Assert.That(report.AgedOutDeleted, Is.EqualTo(0));
        }

        [Test]
        public void Run_AgeSweep_RecursesIntoAssetSubdirs()
        {
            // x1_export_html writes an export.html plus sibling JPEGs in a subdir.
            SetConfig(maxAgeHours: 24, maxTotalMB: 500);
            var subdir = Path.Combine(_previewsDir, "export_abc123");
            var oldMtime = DateTime.UtcNow.AddDays(-5);
            WriteFile(subdir, "export.html", 100, oldMtime);
            WriteFile(subdir, "export0001.jpg", 1000, oldMtime);
            WriteFile(subdir, "export0002.jpg", 1000, oldMtime);

            var report = TempSweep.Run(_fakeTempRoot);

            Assert.That(report.AgedOutDeleted, Is.EqualTo(3));
            Assert.That(report.BytesReclaimed, Is.EqualTo(2100));
        }

        // ── Size sweep ───────────────────────────────────────────────────────────

        [Test]
        public void Run_SizeSweep_DeletesOldestFirstUntilUnderCap()
        {
            // 1 MB cap, three fresh 500 KB files -> total 1.5 MB, one must go (oldest).
            SetConfig(maxAgeHours: 0, maxTotalMB: 1);
            var now = DateTime.UtcNow;
            var oldest = WriteFile(_previewsDir, "a.html", 500 * 1024, now.AddMinutes(-30));
            var middle = WriteFile(_previewsDir, "b.html", 500 * 1024, now.AddMinutes(-20));
            var newest = WriteFile(_previewsDir, "c.html", 500 * 1024, now.AddMinutes(-10));

            var report = TempSweep.Run(_fakeTempRoot);

            Assert.That(File.Exists(oldest), Is.False, "oldest file should be evicted first");
            Assert.That(File.Exists(middle), Is.True);
            Assert.That(File.Exists(newest), Is.True);
            Assert.That(report.OverCapDeleted, Is.EqualTo(1));
            Assert.That(report.BytesReclaimed, Is.EqualTo(500 * 1024));
        }

        [Test]
        public void Run_SizeSweep_Zero_DisablesSizeSweep()
        {
            SetConfig(maxAgeHours: 0, maxTotalMB: 0);
            var big = WriteFile(_previewsDir, "big.html", 2 * 1024 * 1024, DateTime.UtcNow);

            var report = TempSweep.Run(_fakeTempRoot);

            Assert.That(File.Exists(big), Is.True, "size sweep is disabled — file must survive");
            Assert.That(report.OverCapDeleted, Is.EqualTo(0));
        }

        [Test]
        public void Run_SizeSweep_UnderCap_DeletesNothing()
        {
            SetConfig(maxAgeHours: 0, maxTotalMB: 10);
            var small = WriteFile(_previewsDir, "small.html", 1024, DateTime.UtcNow);

            var report = TempSweep.Run(_fakeTempRoot);

            Assert.That(File.Exists(small), Is.True);
            Assert.That(report.OverCapDeleted, Is.EqualTo(0));
        }

        [Test]
        public void Run_AgeAndSizeCombined_AgeRunsFirstThenSize()
        {
            // 1 MB cap. Two old (aged out) and two fresh 700 KB files.
            // After age sweep only the fresh survive (1.4 MB), then size sweep must evict
            // the older of the two fresh ones.
            SetConfig(maxAgeHours: 24, maxTotalMB: 1);
            var now = DateTime.UtcNow;
            var agedA = WriteFile(_previewsDir, "aged-a.html", 700 * 1024, now.AddDays(-2));
            var agedB = WriteFile(_previewsDir, "aged-b.html", 700 * 1024, now.AddDays(-3));
            var freshOld = WriteFile(_previewsDir, "fresh-old.html", 700 * 1024, now.AddMinutes(-30));
            var freshNew = WriteFile(_previewsDir, "fresh-new.html", 700 * 1024, now.AddMinutes(-5));

            var report = TempSweep.Run(_fakeTempRoot);

            Assert.That(File.Exists(agedA), Is.False);
            Assert.That(File.Exists(agedB), Is.False);
            Assert.That(File.Exists(freshOld), Is.False, "oldest-surviving fresh file must be size-evicted");
            Assert.That(File.Exists(freshNew), Is.True, "newest fresh file must remain");
            Assert.That(report.AgedOutDeleted, Is.EqualTo(2));
            Assert.That(report.OverCapDeleted, Is.EqualTo(1));
        }

        // ── Orphan extract / content sweep ───────────────────────────────────────

        [Test]
        public void Run_OrphanExtractAndContent_AtTempRootAreCleaned()
        {
            SetConfig(maxAgeHours: 24, maxTotalMB: 500);
            var orphanExtract = WriteFile(_fakeTempRoot, "x1mcp_extract_abc.txt", 500, DateTime.UtcNow);
            var orphanContent = WriteFile(_fakeTempRoot, "x1mcp_content_def.txt", 700, DateTime.UtcNow);
            var unrelated = WriteFile(_fakeTempRoot, "keep-me.log", 100, DateTime.UtcNow);

            var report = TempSweep.Run(_fakeTempRoot);

            Assert.That(File.Exists(orphanExtract), Is.False);
            Assert.That(File.Exists(orphanContent), Is.False);
            Assert.That(File.Exists(unrelated), Is.True, "unrelated files at temp root must be untouched");
            Assert.That(report.OrphansDeleted, Is.EqualTo(2));
            Assert.That(report.BytesReclaimed, Is.GreaterThanOrEqualTo(1200));
        }

        // ── Empty subdir pruning ─────────────────────────────────────────────────

        [Test]
        public void Run_PrunesEmptySubdirectoriesLeftBehindByAgeSweep()
        {
            SetConfig(maxAgeHours: 24, maxTotalMB: 500);
            var exportDir = Path.Combine(_previewsDir, "export_ffd80277");
            WriteFile(exportDir, "export.html", 100, DateTime.UtcNow.AddDays(-5));

            var report = TempSweep.Run(_fakeTempRoot);

            Assert.That(Directory.Exists(exportDir), Is.False,
                "empty subdir left after age sweep should be pruned");
            Assert.That(report.EmptyDirsRemoved, Is.EqualTo(1));
            Assert.That(Directory.Exists(_previewsDir), Is.True,
                "root previews dir must never be pruned");
        }

        [Test]
        public void Run_NonEmptySubdirectory_IsNotPruned()
        {
            SetConfig(maxAgeHours: 24, maxTotalMB: 500);
            var exportDir = Path.Combine(_previewsDir, "export_keep");
            WriteFile(exportDir, "fresh.html", 100, DateTime.UtcNow.AddHours(-1));

            var report = TempSweep.Run(_fakeTempRoot);

            Assert.That(Directory.Exists(exportDir), Is.True,
                "non-empty subdir must survive");
            Assert.That(report.EmptyDirsRemoved, Is.EqualTo(0));
        }

        // ── Robustness ───────────────────────────────────────────────────────────

        [Test]
        public void Run_NullTempRoot_Throws()
        {
            Assert.Throws<ArgumentException>(() => TempSweep.Run(null));
        }
    }
}
