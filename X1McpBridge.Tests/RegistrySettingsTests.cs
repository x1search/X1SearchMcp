// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using Microsoft.Win32;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// XS-1578: covers RegistrySettings.WriteDWord, the write half added alongside the existing
    /// Read* methods so the new x1_set_query_log tool can persist X1McpQueryLog. Uses a scratch
    /// value name under the real Software\X1 Search key (not a fake/mocked registry - this
    /// codebase's other registry-backed tests avoid touching real values entirely by overriding
    /// via env var or provider seams instead; WriteDWord's only job is registry I/O, so a
    /// dedicated scratch value name with guaranteed cleanup is the narrowest way to exercise it
    /// for real without risking any value another test or a real install might rely on).
    /// </summary>
    [TestFixture]
    public class RegistrySettingsTests
    {
        private const string ProductRegistryKey = @"Software\X1 Search";
        private string _scratchValueName;

        [SetUp]
        public void SetUp()
        {
            _scratchValueName = "X1McpBridgeTests_Scratch_" + Guid.NewGuid().ToString("N");
        }

        [TearDown]
        public void TearDown()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ProductRegistryKey, writable: true))
                key?.DeleteValue(_scratchValueName, throwOnMissingValue: false);
        }

        [Test]
        public void WriteDWord_ThenReadInteger_RoundTrips()
        {
            RegistrySettings.WriteDWord(_scratchValueName, 1);
            Assert.That(RegistrySettings.ReadInteger(_scratchValueName, 0), Is.EqualTo(1));

            RegistrySettings.WriteDWord(_scratchValueName, 0);
            Assert.That(RegistrySettings.ReadInteger(_scratchValueName, 1), Is.EqualTo(0));
        }

        [Test]
        public void ReadInteger_ValueNeverWritten_ReturnsDefault()
        {
            // Never written in this test - exercises the "absent = default" path WriteDWord's
            // round-trip test above doesn't cover, which is exactly the state X1McpQueryLog is in
            // on a fresh install: absent, so QueryLogEnabledProvider must read false/off.
            Assert.That(RegistrySettings.ReadInteger(_scratchValueName, 0), Is.EqualTo(0));
        }
    }
}
