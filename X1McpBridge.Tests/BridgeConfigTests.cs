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
    /// BridgeConfig reads x1mcp.config.json from beside the main assembly (X1McpBridge.exe).
    /// When the test project builds, the exe is copied to the test output directory, so we
    /// can write/delete config files there.  ResetForTesting() clears the cache so each test
    /// gets a fresh load.
    /// </summary>
    [TestFixture]
    public class BridgeConfigTests
    {
        private static string AssemblyDir =>
            Path.GetDirectoryName(typeof(BridgeConfig).Assembly.Location);

        private static string ConfigPath =>
            Path.Combine(AssemblyDir, "x1mcp.config.json");

        private string _savedConfig;

        [SetUp]
        public void SetUp()
        {
            // Preserve any existing config so TearDown can restore it.
            _savedConfig = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
            BridgeConfig.ResetForTesting();
        }

        [TearDown]
        public void TearDown()
        {
            // Restore original state.
            if (_savedConfig != null)
                File.WriteAllText(ConfigPath, _savedConfig);
            else if (File.Exists(ConfigPath))
                File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();
        }

        private void WriteConfig(string json)
        {
            File.WriteAllText(ConfigPath, json);
            BridgeConfig.ResetForTesting();
        }

        // ── GetDefaultTables ─────────────────────────────────────────────────────

        [Test]
        public void GetDefaultTables_NoConfigFile_ReturnsFiles()
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();

            var tables = BridgeConfig.GetDefaultTables();
            Assert.That(tables, Is.EqualTo(new[] { "Files" }));
        }

        [Test]
        public void GetDefaultTables_ConfigWithDefaults_ReturnsConfigured()
        {
            WriteConfig(@"{""defaultTables"":[""Files"",""MSMail"",""Gmail""],""sources"":{}}");
            var tables = BridgeConfig.GetDefaultTables();
            Assert.That(tables, Is.EqualTo(new[] { "Files", "MSMail", "Gmail" }));
        }

        [Test]
        public void GetDefaultTables_ConfigWithNoDefaultTables_ReturnsFiles()
        {
            WriteConfig(@"{""sources"":{}}");
            var tables = BridgeConfig.GetDefaultTables();
            Assert.That(tables, Is.EqualTo(new[] { "Files" }));
        }

        [Test]
        public void GetDefaultTables_EmptyDefaultTablesArray_ReturnsFiles()
        {
            WriteConfig(@"{""defaultTables"":[],""sources"":{}}");
            var tables = BridgeConfig.GetDefaultTables();
            Assert.That(tables, Is.EqualTo(new[] { "Files" }));
        }

        [Test]
        public void GetDefaultTables_MalformedJson_ReturnsFiles()
        {
            File.WriteAllText(ConfigPath, "NOT VALID JSON{{{{");
            BridgeConfig.ResetForTesting();
            var tables = BridgeConfig.GetDefaultTables();
            Assert.That(tables, Is.EqualTo(new[] { "Files" }));
        }

        [Test]
        public void GetDefaultTables_EnvVarOverridesConfig()
        {
            WriteConfig(@"{""defaultTables"":[""Files""],""sources"":{}}");
            Environment.SetEnvironmentVariable("X1_MCP_DEFAULT_TABLES", "MSMail;Gmail");
            BridgeConfig.ResetForTesting();
            try
            {
                var tables = BridgeConfig.GetDefaultTables();
                Assert.That(tables, Is.EqualTo(new[] { "MSMail", "Gmail" }));
            }
            finally
            {
                Environment.SetEnvironmentVariable("X1_MCP_DEFAULT_TABLES", null);
                BridgeConfig.ResetForTesting();
            }
        }

        [Test]
        public void GetDefaultTables_EnvVarCommaSeparated_Parsed()
        {
            Environment.SetEnvironmentVariable("X1_MCP_DEFAULT_TABLES", "Files,Dropbox");
            BridgeConfig.ResetForTesting();
            try
            {
                var tables = BridgeConfig.GetDefaultTables();
                Assert.That(tables, Is.EqualTo(new[] { "Files", "Dropbox" }));
            }
            finally
            {
                Environment.SetEnvironmentVariable("X1_MCP_DEFAULT_TABLES", null);
                BridgeConfig.ResetForTesting();
            }
        }

        // ── GetColumnsForTable ───────────────────────────────────────────────────

        [Test]
        public void GetColumnsForTable_KnownTable_ReturnsColumns()
        {
            WriteConfig(@"{""defaultTables"":[""Files""],""sources"":{""Files"":[""name"",""path"",""size""]}}");
            var cols = BridgeConfig.GetColumnsForTable("Files");
            Assert.That(cols, Is.EqualTo(new[] { "name", "path", "size" }));
        }

        [Test]
        public void GetColumnsForTable_UnknownTable_ReturnsEmpty()
        {
            WriteConfig(@"{""defaultTables"":[""Files""],""sources"":{""Files"":[""name""]}}");
            var cols = BridgeConfig.GetColumnsForTable("Nonexistent");
            Assert.That(cols, Is.Empty);
        }

        [Test]
        public void GetColumnsForTable_NullTable_ReturnsEmpty()
        {
            WriteConfig(@"{""defaultTables"":[""Files""],""sources"":{}}");
            var cols = BridgeConfig.GetColumnsForTable(null);
            Assert.That(cols, Is.Empty);
        }

        [Test]
        public void GetColumnsForTable_CaseInsensitive()
        {
            WriteConfig(@"{""defaultTables"":[""Files""],""sources"":{""Gmail"":[""subject"",""from""]}}");
            var cols = BridgeConfig.GetColumnsForTable("gmail");
            Assert.That(cols, Is.EqualTo(new[] { "subject", "from" }));
        }

        // ── GetAllSources ────────────────────────────────────────────────────────

        [Test]
        public void GetAllSources_ReturnsAllConfiguredSources()
        {
            WriteConfig(@"{
        ""defaultTables"":[""Files""],
        ""sources"":{
          ""Files"":[""name"",""path""],
          ""MSMail"":[""subject"",""from""]
        }
      }");
            var sources = BridgeConfig.GetAllSources().ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.That(sources.ContainsKey("Files"), Is.True);
            Assert.That(sources.ContainsKey("MSMail"), Is.True);
            Assert.That(sources["Files"], Is.EqualTo(new[] { "name", "path" }));
        }

        [Test]
        public void GetAllSources_NoConfig_ReturnsEmptyDictionary()
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();
            var sources = BridgeConfig.GetAllSources().ToList();
            Assert.That(sources, Is.Empty);
        }

        // ── Caching ──────────────────────────────────────────────────────────────

        [Test]
        public void GetDefaultTables_CalledTwice_ReturnsSameReference()
        {
            WriteConfig(@"{""defaultTables"":[""Files""],""sources"":{}}");
            var first = BridgeConfig.GetDefaultTables();
            var second = BridgeConfig.GetDefaultTables();
            Assert.That(ReferenceEquals(first, second), Is.True);
        }

        // ── GetAutoPreviewTimeoutMs (H4) ─────────────────────────────────────────

        [Test]
        public void GetAutoPreviewTimeoutMs_NoConfig_Returns10000()
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();
            Assert.That(BridgeConfig.GetAutoPreviewTimeoutMs(), Is.EqualTo(10000));
        }

        [Test]
        public void GetAutoPreviewTimeoutMs_ConfiguredValue_ReturnsIt()
        {
            WriteConfig(@"{""defaultTables"":[""Files""],""sources"":{},""autoPreviewTimeoutMs"":5000}");
            Assert.That(BridgeConfig.GetAutoPreviewTimeoutMs(), Is.EqualTo(5000));
        }

        [Test]
        public void GetAutoPreviewTimeoutMs_ZeroOrNegativeInConfig_ReturnsDefault()
        {
            // Zero/negative values are invalid; the default (10000) must be preserved.
            WriteConfig(@"{""defaultTables"":[""Files""],""sources"":{},""autoPreviewTimeoutMs"":0}");
            Assert.That(BridgeConfig.GetAutoPreviewTimeoutMs(), Is.EqualTo(10000));
        }

        [Test]
        public void GetAutoPreviewTimeoutMs_LargeValue_ReturnsIt()
        {
            WriteConfig(@"{""defaultTables"":[""Files""],""sources"":{},""autoPreviewTimeoutMs"":30000}");
            Assert.That(BridgeConfig.GetAutoPreviewTimeoutMs(), Is.EqualTo(30000));
        }

        // ── GetTempMaxAgeHours ────────────────────────────────────────────────────

        [Test]
        public void GetTempMaxAgeHours_NoConfig_Returns168()
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();
            Assert.That(BridgeConfig.GetTempMaxAgeHours(), Is.EqualTo(168));
        }

        [Test]
        public void GetTempMaxAgeHours_ConfiguredValue_ReturnsIt()
        {
            WriteConfig(@"{""sources"":{},""tempMaxAgeHours"":24}");
            Assert.That(BridgeConfig.GetTempMaxAgeHours(), Is.EqualTo(24));
        }

        [Test]
        public void GetTempMaxAgeHours_Zero_ReturnsZeroAsDisableSignal()
        {
            // Zero means "disable age sweep" — must be honored, not replaced with the default.
            WriteConfig(@"{""sources"":{},""tempMaxAgeHours"":0}");
            Assert.That(BridgeConfig.GetTempMaxAgeHours(), Is.EqualTo(0));
        }

        [Test]
        public void GetTempMaxAgeHours_NegativeInConfig_ReturnsDefault()
        {
            WriteConfig(@"{""sources"":{},""tempMaxAgeHours"":-5}");
            Assert.That(BridgeConfig.GetTempMaxAgeHours(), Is.EqualTo(168));
        }

        // ── GetTempMaxTotalMB ─────────────────────────────────────────────────────

        [Test]
        public void GetTempMaxTotalMB_NoConfig_Returns500()
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();
            Assert.That(BridgeConfig.GetTempMaxTotalMB(), Is.EqualTo(500));
        }

        [Test]
        public void GetTempMaxTotalMB_ConfiguredValue_ReturnsIt()
        {
            WriteConfig(@"{""sources"":{},""tempMaxTotalMB"":2048}");
            Assert.That(BridgeConfig.GetTempMaxTotalMB(), Is.EqualTo(2048));
        }

        [Test]
        public void GetTempMaxTotalMB_Zero_ReturnsZeroAsDisableSignal()
        {
            WriteConfig(@"{""sources"":{},""tempMaxTotalMB"":0}");
            Assert.That(BridgeConfig.GetTempMaxTotalMB(), Is.EqualTo(0));
        }

        [Test]
        public void GetTempMaxTotalMB_NegativeInConfig_ReturnsDefault()
        {
            WriteConfig(@"{""sources"":{},""tempMaxTotalMB"":-1}");
            Assert.That(BridgeConfig.GetTempMaxTotalMB(), Is.EqualTo(500));
        }

        // ── GetDaemonUrl / GetDaemonExePath / GetDaemonStartupTimeoutMs (ProxyMode) ──

        [Test]
        public void GetDaemonUrl_NoConfig_ReturnsDefaultLocalhost5250()
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();
            Assert.That(BridgeConfig.GetDaemonUrl(), Is.EqualTo("http://localhost:5250"));
        }

        [Test]
        public void GetDaemonUrl_ConfiguredValue_TrimsTrailingSlash()
        {
            WriteConfig(@"{""sources"":{},""daemonUrl"":""http://localhost:9999/""}");
            Assert.That(BridgeConfig.GetDaemonUrl(), Is.EqualTo("http://localhost:9999"));
        }

        [Test]
        public void GetDaemonUrl_EnvVarOverridesConfig()
        {
            WriteConfig(@"{""sources"":{},""daemonUrl"":""http://localhost:9999""}");
            Environment.SetEnvironmentVariable("X1_MCP_DAEMON_URL", "http://localhost:1234/");
            BridgeConfig.ResetForTesting();
            try
            {
                Assert.That(BridgeConfig.GetDaemonUrl(), Is.EqualTo("http://localhost:1234"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("X1_MCP_DAEMON_URL", null);
                BridgeConfig.ResetForTesting();
            }
        }

        [Test]
        public void GetDaemonExePath_NoConfig_ReturnsSiblingX1McpGraphQLExe()
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();
            var expected = Path.Combine(AssemblyDir, "X1McpGraphQL.exe");
            Assert.That(BridgeConfig.GetDaemonExePath(), Is.EqualTo(expected));
        }

        [Test]
        public void GetDaemonExePath_ConfiguredValue_ReturnsIt()
        {
            WriteConfig(@"{""sources"":{},""daemonExePath"":""C:\\custom\\X1McpGraphQL.exe""}");
            Assert.That(BridgeConfig.GetDaemonExePath(), Is.EqualTo(@"C:\custom\X1McpGraphQL.exe"));
        }

        [Test]
        public void GetDaemonStartupTimeoutMs_NoConfig_Returns15000()
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();
            Assert.That(BridgeConfig.GetDaemonStartupTimeoutMs(), Is.EqualTo(15000));
        }

        [Test]
        public void GetDaemonStartupTimeoutMs_ConfiguredValue_ReturnsIt()
        {
            WriteConfig(@"{""sources"":{},""daemonStartupTimeoutMs"":30000}");
            Assert.That(BridgeConfig.GetDaemonStartupTimeoutMs(), Is.EqualTo(30000));
        }

        [Test]
        public void GetDaemonStartupTimeoutMs_ZeroOrNegativeInConfig_ReturnsDefault()
        {
            WriteConfig(@"{""sources"":{},""daemonStartupTimeoutMs"":0}");
            Assert.That(BridgeConfig.GetDaemonStartupTimeoutMs(), Is.EqualTo(15000));
        }

        // ── GetRelayMode / GetRelayLaunchTarget (flavor selection) ───────────────
        //
        // These need no fixture: the test output directory has no X1McpGraphQL.exe in it, so the
        // filesystem probe naturally resolves to the Lean answer. GetDaemonExePath's own default
        // (a sibling daemon exe) is deliberately left alone above - it still resolves a path; it is
        // only the *launch* decision that consults whether that path exists.

        [Test]
        public void GetRelayMode_NoDaemonExePresent_ProbesToHost()
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();
            Assert.That(File.Exists(Path.Combine(AssemblyDir, "X1McpGraphQL.exe")), Is.False,
                "precondition: this test relies on the Lean shape of the test output dir");
            Assert.That(BridgeConfig.GetRelayMode(), Is.EqualTo(RelayMode.Host));
        }

        [Test]
        public void GetRelayMode_ConfiguredDaemon_OverridesTheProbe()
        {
            WriteConfig(@"{""sources"":{},""relayMode"":""daemon""}");
            Assert.That(BridgeConfig.GetRelayMode(), Is.EqualTo(RelayMode.Daemon));
        }

        [Test]
        public void GetRelayMode_ConfiguredHost_IsHonoured()
        {
            WriteConfig(@"{""sources"":{},""relayMode"":""Host""}");
            Assert.That(BridgeConfig.GetRelayMode(), Is.EqualTo(RelayMode.Host));
        }

        [Test]
        public void GetRelayMode_UnrecognisedValue_FallsBackToTheProbe()
        {
            WriteConfig(@"{""sources"":{},""relayMode"":""banana""}");
            Assert.That(BridgeConfig.GetRelayMode(), Is.EqualTo(RelayMode.Host));
        }

        [Test]
        public void GetRelayMode_EnvVarOverridesConfig()
        {
            WriteConfig(@"{""sources"":{},""relayMode"":""host""}");
            Environment.SetEnvironmentVariable("X1_MCP_RELAY_MODE", "daemon");
            BridgeConfig.ResetForTesting();
            try
            {
                Assert.That(BridgeConfig.GetRelayMode(), Is.EqualTo(RelayMode.Daemon));
            }
            finally
            {
                Environment.SetEnvironmentVariable("X1_MCP_RELAY_MODE", null);
                BridgeConfig.ResetForTesting();
            }
        }

        [Test]
        public void GetRelayLaunchTarget_LeanInstall_LaunchesSelfWithHostFlag()
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();

            var target = BridgeConfig.GetRelayLaunchTarget();
            Assert.That(target.Mode, Is.EqualTo(RelayMode.Host));
            Assert.That(target.Arguments, Is.EqualTo("--host"));
            Assert.That(Path.GetFileName(target.FileName), Is.EqualTo("X1McpBridge.exe"));
            Assert.That(target.ExpectedComponent, Is.EqualTo(RelayHealth.ComponentHost));
        }

        [Test]
        public void GetRelayLaunchTarget_DaemonMode_LaunchesTheDaemonWithNoArgs()
        {
            WriteConfig(@"{""sources"":{},""relayMode"":""daemon"",""daemonExePath"":""C:\\custom\\X1McpGraphQL.exe""}");

            var target = BridgeConfig.GetRelayLaunchTarget();
            Assert.That(target.Mode, Is.EqualTo(RelayMode.Daemon));
            Assert.That(target.FileName, Is.EqualTo(@"C:\custom\X1McpGraphQL.exe"));
            Assert.That(target.Arguments, Is.Null.Or.Empty);
            Assert.That(target.ExpectedComponent, Is.EqualTo(RelayHealth.ComponentDaemon));
        }

        [Test]
        public void GetRelayLaunchTarget_StaleConfiguredDaemonPath_FallsBackToHost()
        {
            // A Full -> Lean upgrade deletes the daemon exe but deliberately does not rewrite the
            // customer's config, so a leftover daemonExePath naming a now-missing file must degrade
            // to --host rather than leaving the session with nothing to launch.
            WriteConfig(@"{""sources"":{},""daemonExePath"":""C:\\gone\\X1McpGraphQL.exe""}");

            var target = BridgeConfig.GetRelayLaunchTarget();
            Assert.That(target.Mode, Is.EqualTo(RelayMode.Host));
            Assert.That(target.Arguments, Is.EqualTo("--host"));
        }
    }
}
