// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using X1.Service;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// DataSourceInfoMapper builds x1_list_sources' output directly from
    /// ConfiguredDataSourceInfo[] - the array X1ServiceHost's own GetDataSourcesInfo()
    /// call returns. Only scanners/accounts actually present in that array should ever
    /// appear; BridgeConfig only supplies columns/capabilities for a name already
    /// confirmed live, it never contributes source names of its own.
    /// </summary>
    [TestFixture]
    public class DataSourceInfoMapperTests
    {
        private static string AssemblyDir =>
            Path.GetDirectoryName(typeof(BridgeConfig).Assembly.Location);

        private static string ConfigPath =>
            Path.Combine(AssemblyDir, "x1mcp.config.json");

        private string _savedConfig;

        [SetUp]
        public void SetUp()
        {
            _savedConfig = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
            BridgeConfig.ResetForTesting();
        }

        [TearDown]
        public void TearDown()
        {
            if (_savedConfig != null)
                File.WriteAllText(ConfigPath, _savedConfig);
            else if (File.Exists(ConfigPath))
                File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();
        }

        private static ConfiguredDataSourceInfo Info(string scannerName, string accountName, int totalCount,
            bool isScanning = false, string lastScanTime = null, string[] schemas = null,
            string scannerDisplayName = null, int itemCount = 0)
        {
            return new ConfiguredDataSourceInfo
            {
                scannerName = scannerName,
                scannerDisplayName = scannerDisplayName,
                accountName = accountName,
                schemas = schemas,
                totalCount = totalCount,
                itemCount = itemCount,
                lastScanTime = lastScanTime,
                isScanning = isScanning
            };
        }

        // ── BuildSources: only reports what the service host confirmed ──────────────

        [Test]
        public void BuildSources_NullInfo_ReturnsEmptyArray()
        {
            var sources = DataSourceInfoMapper.BuildSources(null);
            Assert.That(sources, Is.Not.Null);
            Assert.That(sources.Count, Is.EqualTo(0));
        }

        [Test]
        public void BuildSources_EmptyInfo_ReturnsEmptyArray()
        {
            var sources = DataSourceInfoMapper.BuildSources(new ConfiguredDataSourceInfo[0]);
            Assert.That(sources.Count, Is.EqualTo(0));
        }

        [Test]
        public void BuildSources_OnlyIncludesScannersPresentInInfo()
        {
            // BridgeConfig may know how to configure many more tables than this -
            // none of them should appear unless GetDataSourcesInfo actually reported them.
            File.WriteAllText(ConfigPath,
                @"{""sources"":{""Files"":[""name""],""Gmail"":[""subject""],""JIRA"":[""summary""]}}");
            BridgeConfig.ResetForTesting();

            var sources = DataSourceInfoMapper.BuildSources(new[] { Info("MSMail", "srobinson@x1.com", 16509) });

            Assert.That(sources.Count, Is.EqualTo(1));
            Assert.That(sources[0]["name"]?.ToString(), Is.EqualTo("MSMail"));
        }

        [Test]
        public void BuildSources_GroupsMultipleAccountsUnderOneScanner()
        {
            var info = new[]
            {
                Info("Teams", "srobinson@x1.com", 267284, schemas: new[] { "Teams", "TeamsChannel" }),
                Info("Teams", "other@x1.com", 100)
            };

            var sources = DataSourceInfoMapper.BuildSources(info);

            Assert.That(sources.Count, Is.EqualTo(1));
            var accounts = sources[0]["accounts"] as JArray;
            Assert.That(accounts.Count, Is.EqualTo(2));
        }

        // ── XS-1605 account-suffixed scanner name correction ─────────────────────────

        [Test]
        public void BuildSources_AccountSuffixedScannerName_IsUnmangled()
        {
            var info = new[] { Info("Dropbox-sfrobins@nucleus.com", "sfrobins@nucleus.com", -1, isScanning: true) };

            var sources = DataSourceInfoMapper.BuildSources(info);

            Assert.That(sources.Count, Is.EqualTo(1));
            Assert.That(sources[0]["name"]?.ToString(), Is.EqualTo("Dropbox"));
            var acc = (sources[0]["accounts"] as JArray)[0];
            Assert.That(acc["accountName"]?.ToString(), Is.EqualTo("sfrobins@nucleus.com"));
        }

        [Test]
        public void CleanScannerName_StripsKnownAccountSuffix()
        {
            Assert.That(DataSourceInfoMapper.CleanScannerName("Dropbox-user@x.com", "user@x.com"),
                Is.EqualTo("Dropbox"));
        }

        [Test]
        public void CleanScannerName_NoAccountName_ReturnsScannerNameUnchanged()
        {
            Assert.That(DataSourceInfoMapper.CleanScannerName("Files", ""), Is.EqualTo("Files"));
            Assert.That(DataSourceInfoMapper.CleanScannerName("Files", null), Is.EqualTo("Files"));
        }

        [Test]
        public void CleanScannerName_ScannerNameDoesNotEndWithAccountSuffix_ReturnsUnchanged()
        {
            Assert.That(DataSourceInfoMapper.CleanScannerName("MSMail", "srobinson@x1.com"), Is.EqualTo("MSMail"));
        }

        // ── columns / capabilities enrichment for confirmed-live sources ────────────

        [Test]
        public void BuildSources_IncludesColumnsFromBridgeConfig()
        {
            File.WriteAllText(ConfigPath, @"{""sources"":{""Files"":[""name"",""path"",""size""]}}");
            BridgeConfig.ResetForTesting();

            var sources = DataSourceInfoMapper.BuildSources(new[] { Info("Files", "", 10) });

            var cols = sources[0]["columns"] as JArray;
            Assert.That(cols.Count, Is.EqualTo(3));
        }

        [Test]
        public void BuildSources_NoConfiguredColumns_ReturnsEmptyColumnsArray()
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();

            var sources = DataSourceInfoMapper.BuildSources(new[] { Info("SomeUnconfiguredTable", "", 5) });

            var cols = sources[0]["columns"] as JArray;
            Assert.That(cols, Is.Not.Null);
            Assert.That(cols.Count, Is.EqualTo(0));
        }

        [Test]
        public void BuildSources_IncludesCapabilities()
        {
            var sources = DataSourceInfoMapper.BuildSources(new[] { Info("OneDrive", "srobinson@x1.com", 3393) });

            var caps = sources[0]["capabilities"] as JObject;
            Assert.That(caps, Is.Not.Null);
            Assert.That(caps["actions"], Is.Not.Null);
            Assert.That(caps["preview"], Is.Not.Null);
        }

        [Test]
        public void BuildSources_TotalCountAndIsScanning_PassThrough()
        {
            var sources = DataSourceInfoMapper.BuildSources(
                new[] { Info("SP365", "srobinson@x1.com", 52869, isScanning: true) });

            var acc = (sources[0]["accounts"] as JArray)[0];
            Assert.That(acc["totalCount"]?.Value<int>(), Is.EqualTo(52869));
            Assert.That(acc["isScanning"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void BuildSources_EmptyAccountName_OmitsAccountNameField()
        {
            // Local, non-account-based sources like Files report an empty accountName.
            var sources = DataSourceInfoMapper.BuildSources(new[] { Info("Files", "", 8767) });

            var acc = (sources[0]["accounts"] as JArray)[0];
            Assert.That(acc["accountName"], Is.Null);
        }

        // ── XS-1612: itemCount / scannerDisplayName ─────────────────────────────────

        [Test]
        public void BuildSources_ItemCount_IsDistinctFromTotalCount()
        {
            // totalCount is scanner-wide (can be shared across sibling accounts); itemCount
            // is this account's own count. A test where they differ catches any accidental
            // collapsing of the two.
            var sources = DataSourceInfoMapper.BuildSources(
                new[] { Info("MSMail", "srobinson@x1.com", totalCount: 20000, itemCount: 8000) });

            var acc = (sources[0]["accounts"] as JArray)[0];
            Assert.That(acc["totalCount"]?.Value<int>(), Is.EqualTo(20000));
            Assert.That(acc["itemCount"]?.Value<int>(), Is.EqualTo(8000));
        }

        [Test]
        public void BuildSources_IncludesDisplayNameWhenPresent()
        {
            // e.g. Exchange's scannerDisplayName is "MS Online Archive", distinct from its
            // internal scannerName ("Exchange") which x1_search's tables param still needs.
            var sources = DataSourceInfoMapper.BuildSources(
                new[] { Info("Exchange", "srobinson@x1.com", 100, scannerDisplayName: "MS Online Archive") });

            Assert.That(sources[0]["name"]?.ToString(), Is.EqualTo("Exchange"));
            var acc = (sources[0]["accounts"] as JArray)[0];
            Assert.That(acc["displayName"]?.ToString(), Is.EqualTo("MS Online Archive"));
        }

        [Test]
        public void BuildSources_NoDisplayName_OmitsDisplayNameField()
        {
            var sources = DataSourceInfoMapper.BuildSources(new[] { Info("Files", "", 8767) });

            var acc = (sources[0]["accounts"] as JArray)[0];
            Assert.That(acc["displayName"], Is.Null);
        }

        [Test]
        public void BuildSources_DisplayNameCanDifferPerAccountUnderSameScanner()
        {
            // IMAP's displayName depends on that specific account's AccountType, so two
            // accounts grouped under the same "IMAP" scanner can legitimately show different
            // values - displayName must live per-account, not be hoisted to the source level.
            var info = new[]
            {
                Info("IMAP", "user1@gmail.com", 100, scannerDisplayName: "IMAP - Gmail"),
                Info("IMAP", "user2@yahoo.com", 50, scannerDisplayName: "IMAP - Yahoo")
            };

            var sources = DataSourceInfoMapper.BuildSources(info);

            Assert.That(sources.Count, Is.EqualTo(1));
            var accounts = sources[0]["accounts"] as JArray;
            Assert.That(accounts[0]["displayName"]?.ToString(), Is.EqualTo("IMAP - Gmail"));
            Assert.That(accounts[1]["displayName"]?.ToString(), Is.EqualTo("IMAP - Yahoo"));
        }
    }
}
