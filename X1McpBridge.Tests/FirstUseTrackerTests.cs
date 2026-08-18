// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.IO;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// XS-1673: pins the "shown exactly once, ever" contract for the first-use welcome banner, and
    /// that an old/foreign schema version is treated as "not yet shown" rather than merged.
    /// </summary>
    [TestFixture]
    public class FirstUseTrackerTests
    {
        private string _tmpFile;

        [SetUp]
        public void SetUp()
        {
            _tmpFile = Path.Combine(Path.GetTempPath(), "x1mcp_first_use_test_" + Guid.NewGuid().ToString("N") + ".json");
            FirstUseTracker.OverrideMarkerPath(_tmpFile);
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_tmpFile)) File.Delete(_tmpFile);
            FirstUseTracker.OverrideMarkerPath(null);
        }

        [Test]
        public void HasBeenShown_FalseInitially_TrueAfterMarkShown()
        {
            Assert.That(FirstUseTracker.HasBeenShown(), Is.False);

            FirstUseTracker.MarkShown();

            Assert.That(FirstUseTracker.HasBeenShown(), Is.True);
        }

        [Test]
        public void MarkShown_PersistsAcrossFreshLoad()
        {
            FirstUseTracker.MarkShown();

            FirstUseTracker.OverrideMarkerPath(_tmpFile); // simulate a fresh bridge reading the same file

            Assert.That(FirstUseTracker.HasBeenShown(), Is.True);
        }

        [Test]
        public void Load_DiscardsOldSchemaVersion_TreatsAsNotYetShown()
        {
            File.WriteAllText(_tmpFile, @"{""version"":0,""shown"":true}");

            Assert.That(FirstUseTracker.HasBeenShown(), Is.False);
        }

        [Test]
        public void BannerFor_FilesOnly_ReturnsFilesOnlyText()
        {
            Assert.That(FirstUseTracker.BannerFor(fullSuiteLicensed: false),
                Is.EqualTo(BridgeConstants.FirstUseFilesOnlyBanner));
        }

        [Test]
        public void BannerFor_FullSuite_ReturnsFullSuiteText()
        {
            Assert.That(FirstUseTracker.BannerFor(fullSuiteLicensed: true),
                Is.EqualTo(BridgeConstants.FirstUseFullSuiteBanner));
        }
    }
}
