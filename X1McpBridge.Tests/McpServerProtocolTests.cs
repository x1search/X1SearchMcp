// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// Tests MCP JSON-RPC routing in McpServer.ProcessMessage for all paths that
    /// do NOT require a live WCF connection (i.e. no x1_search / x1_get_metadata /
    /// x1_get_content calls).  The SearchBridge instance inside McpServer is lazy —
    /// it won't try to connect to X1ServiceHost unless an actual search is invoked.
    /// </summary>
    [TestFixture]
    public class McpServerProtocolTests
    {
        private static string AssemblyDir =>
            Path.GetDirectoryName(typeof(BridgeConfig).Assembly.Location);

        private static string ConfigPath =>
            Path.Combine(AssemblyDir, "x1mcp.config.json");

        private string _savedConfig;

        // XS-1673: seeded "already shown" so the ambient first-use banner never nondeterministically
        // appends to the content arrays these tests assert on (most of which index content[0] only,
        // some of which - like ToolsCall_ListSources_ReturnsWellFormedSourcesWithoutError - already
        // tolerate ServiceConnection being reachable or not depending on the machine; we don't want to
        // ALSO make "did the banner show" a source of that same variability). Tests that exercise the
        // banner itself override to a fresh path and restore this one in a finally block.
        private string _firstUseMarkerPath;

        [SetUp]
        public void SetUp()
        {
            _savedConfig = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
            BridgeConfig.ResetForTesting();

            _firstUseMarkerPath = Path.Combine(Path.GetTempPath(), "x1mcp_first_use_test_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(_firstUseMarkerPath, @"{""version"":1,""shown"":true}");
            FirstUseTracker.OverrideMarkerPath(_firstUseMarkerPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (_savedConfig != null)
                File.WriteAllText(ConfigPath, _savedConfig);
            else if (File.Exists(ConfigPath))
                File.Delete(ConfigPath);
            BridgeConfig.ResetForTesting();

            FirstUseTracker.OverrideMarkerPath(null);
            if (File.Exists(_firstUseMarkerPath)) File.Delete(_firstUseMarkerPath);
        }

        private static JObject Msg(int id, string method, string paramsJson = null)
        {
            var o = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method
            };
            if (paramsJson != null)
                o["params"] = JObject.Parse(paramsJson);
            return o;
        }

        // ── initialize ───────────────────────────────────────────────────────────

        [Test]
        public void Initialize_ReturnsProtocolVersion()
        {
            var r = McpServer.ProcessMessage(Msg(1, "initialize"));
            Assert.That(r["result"]?["protocolVersion"]?.ToString(), Is.EqualTo(McpProtocol.ProtocolVersion));
        }

        [Test]
        public void Initialize_ReturnsServerInfo()
        {
            var r = McpServer.ProcessMessage(Msg(1, "initialize"));
            Assert.That(r["result"]?["serverInfo"]?["name"]?.ToString(), Is.EqualTo("x1-mcp-bridge"));
        }

        [Test]
        public void Initialize_ReturnsCapabilities()
        {
            var r = McpServer.ProcessMessage(Msg(1, "initialize"));
            var caps = r["result"]?["capabilities"];
            Assert.That(caps?["tools"], Is.Not.Null);
            Assert.That(caps?["resources"], Is.Not.Null);
            Assert.That(caps?["prompts"], Is.Not.Null);
        }

        // ── ping ─────────────────────────────────────────────────────────────────

        [Test]
        public void Ping_ReturnsEmptyResult()
        {
            var r = McpServer.ProcessMessage(Msg(2, "ping"));
            Assert.That(r["result"], Is.Not.Null);
            Assert.That(r["error"], Is.Null);
        }

        [Test]
        public void Ping_EchoesId()
        {
            var r = McpServer.ProcessMessage(Msg(99, "ping"));
            Assert.That(r["id"]?.Value<int>(), Is.EqualTo(99));
        }

        // ── tools/list ───────────────────────────────────────────────────────────

        [Test]
        public void ToolsList_ReturnsSeventeenTools()
        {
            var r = McpServer.ProcessMessage(Msg(3, "tools/list"));
            var tools = r["result"]?["tools"] as JArray;
            Assert.That(tools, Is.Not.Null);
            Assert.That(tools.Count, Is.EqualTo(17));
        }

        [Test]
        public void ToolsList_ContainsExpectedToolNames()
        {
            var r = McpServer.ProcessMessage(Msg(3, "tools/list"));
            var tools = r["result"]?["tools"] as JArray;
            var nameSet = new System.Collections.Generic.HashSet<string>();
            foreach (JObject t in tools)
                nameSet.Add(t["name"].ToString());

            Assert.That(nameSet, Does.Contain("x1_search"));
            Assert.That(nameSet, Does.Contain("x1_get_metadata"));
            Assert.That(nameSet, Does.Contain("x1_get_content"));
            Assert.That(nameSet, Does.Contain("x1_extract_file"));
            Assert.That(nameSet, Does.Contain("x1_export_html"));
            Assert.That(nameSet, Does.Contain("x1_list_sources"));
            Assert.That(nameSet, Does.Contain("x1_get_schema_fields"));
            Assert.That(nameSet, Does.Contain("x1_list_actions"));
            Assert.That(nameSet, Does.Contain("x1_execute_action"));
            Assert.That(nameSet, Does.Contain("x1_generate_preview"));
            Assert.That(nameSet, Does.Contain("x1_add_tags"));
            Assert.That(nameSet, Does.Contain("x1_remove_tags"));
            Assert.That(nameSet, Does.Contain("x1_clear_tags"));
            Assert.That(nameSet, Does.Contain("x1_cost_savings"));
            Assert.That(nameSet, Does.Contain("x1_reset_stats"));
            Assert.That(nameSet, Does.Contain("x1_set_query_log"));
            Assert.That(nameSet, Does.Contain("x1_version"));
        }

        // ── x1_version (XS-1620) ─────────────────────────────────────────────────

        [Test]
        public void Version_ReportsThisAssemblysVersionAndPath()
        {
            var r = McpServer.ProcessMessage(Msg(4, "tools/call",
                @"{ ""name"": ""x1_version"", ""arguments"": {} }"));

            var text = r["result"]?["content"]?[0]?["text"]?.ToString();
            Assert.That(text, Is.Not.Null);
            var info = JObject.Parse(text);

            Assert.That(info["component"]?.ToString(), Is.EqualTo("X1McpBridge"));
            // Stamped from version.props at build time, so assert the shape rather than a literal -
            // hard-coding the number here would mean bump.bat breaks the test suite every time.
            Assert.That(info["version"]?.ToString(), Does.Match(@"^\d+\.\d+\.\d+\.\d+$"));
            Assert.That(info["version"]?.ToString(), Is.Not.EqualTo("0.0.0.0"));
            Assert.That(info["path"]?.ToString(), Does.EndWith(".dll").Or.EndWith(".exe"));
        }

        [Test]
        public void Version_ReportsTheInstallFlavorAndTheMachineWidePicture()
        {
            // These fields exist because the Lean flavor has no daemon above this process to
            // assemble them. Before the split, x1_version returned only component/version/path here
            // and the nested daemon/bridge/runningBridges shape came from the daemon's gateway - so a
            // Lean install silently answered a strictly smaller payload in the one tool whose entire
            // job is to make "which build is answering?" a non-silent question. Anything reading
            // .runningBridges would have got undefined.
            var r = McpServer.ProcessMessage(Msg(6, "tools/call",
                @"{ ""name"": ""x1_version"", ""arguments"": {} }"));
            var info = JObject.Parse(r["result"]["content"][0]["text"].ToString());

            Assert.That(info["flavor"]?.ToString(), Is.EqualTo("lean").Or.EqualTo("full"));
            Assert.That(info["relayComponent"]?.ToString(),
                Is.EqualTo("X1McpBridge").Or.EqualTo("X1McpGraphQL"));

            // Arrays, not null: a caller iterating them must never have to null-check first.
            Assert.That(info["runningBridges"], Is.TypeOf<JArray>());
            Assert.That(info["runningDaemons"], Is.TypeOf<JArray>(),
                "a leftover net10 daemon is the most likely coexisting install on a Lean machine");
            Assert.That(info["runningBridgesDisagree"], Is.Not.Null);

            // This test process is itself an X1McpBridge-hosted assembly, so the scan must at minimum
            // not blow up; rows carry an error field rather than being dropped.
            foreach (var row in (JArray)info["runningBridges"])
                Assert.That(row["processId"], Is.Not.Null);
        }

        [Test]
        public void Version_ScanDoesNotThrowOnUnreadableProcesses()
        {
            // The scanner's contract is "never throw": it feeds a diagnostic call, so a process it
            // cannot read must be reported as a row carrying an error rather than dropped or fatal.
            Assert.DoesNotThrow(() => RelayProcessScanner.ScanBridges());
            Assert.DoesNotThrow(() => RelayProcessScanner.ScanDaemons());
        }

        [Test]
        public void Version_VersionsDisagree_OnlyWhenTwoIdentifiedVersionsDiffer()
        {
            var agree = JArray.Parse(@"[{""version"":""1.0.0.9""},{""version"":""1.0.0.9""}]");
            var differ = JArray.Parse(@"[{""version"":""1.0.0.8""},{""version"":""1.0.0.9""}]");
            // A row with no readable version must not be mistaken for a disagreement - that would
            // report a false alarm on the very machines where the scan is least able to see clearly.
            var unknown = JArray.Parse(@"[{""version"":""1.0.0.9""},{""version"":null}]");

            Assert.That(RelayProcessScanner.VersionsDisagree(agree), Is.False);
            Assert.That(RelayProcessScanner.VersionsDisagree(differ), Is.True);
            Assert.That(RelayProcessScanner.VersionsDisagree(unknown), Is.False);
        }

        [Test]
        public void Version_DetectedClaudeProcessesIsAlwaysAnArray()
        {
            // XS-1685: AgentProcessScanner feeds this, and its own contract is "never throw" - on a
            // machine with no "claude"-named process running (e.g. CI), this must still come back an
            // empty array, never null/missing, so a caller iterating it never has to null-check first.
            var r = McpServer.ProcessMessage(Msg(7, "tools/call",
                @"{ ""name"": ""x1_version"", ""arguments"": {} }"));
            var info = JObject.Parse(r["result"]["content"][0]["text"].ToString());

            Assert.That(info["detectedClaudeProcesses"], Is.TypeOf<JArray>());
        }

        [Test]
        public void Version_DoesNotRequireX1ServiceHost()
        {
            // The whole point: "which build is this?" must stay answerable when the service is
            // down, because that is exactly when someone is trying to work out what they're
            // running. This passes trivially in-process; it exists to stop anyone routing
            // x1_version through a service call later.
            var r = McpServer.ProcessMessage(Msg(5, "tools/call",
                @"{ ""name"": ""x1_version"", ""arguments"": {} }"));

            Assert.That(r["error"], Is.Null);
            Assert.That(r["result"]?["isError"]?.Value<bool>(), Is.False);
        }

        // ── x1_set_query_log (XS-1578) ──────────────────────────────────────────
        //
        // Overrides McpServer's QueryLogEnabledProvider/QueryLogWriter test seams instead of
        // touching the real registry - mirrors HostMode's IdleShutdownSecondsProvider seam and
        // keeps this fixture consistent with the rest of the suite, which avoids mutating real
        // global state in favor of injectable providers (see HostModeTests' env-var overrides).
        // RegistrySettingsTests covers the real RegistrySettings.WriteDWord/ReadInteger round trip.

        private Func<bool> _savedQueryLogEnabledProvider;
        private Action<bool> _savedQueryLogWriter;

        [SetUp]
        public void SetUpQueryLogSeams()
        {
            _savedQueryLogEnabledProvider = McpServer.QueryLogEnabledProvider;
            _savedQueryLogWriter = McpServer.QueryLogWriter;
        }

        [TearDown]
        public void TearDownQueryLogSeams()
        {
            McpServer.QueryLogEnabledProvider = _savedQueryLogEnabledProvider;
            McpServer.QueryLogWriter = _savedQueryLogWriter;
        }

        [Test]
        public void SetQueryLog_NoArguments_ReportsCurrentStateWithoutWriting()
        {
            bool wroteAnything = false;
            McpServer.QueryLogEnabledProvider = () => false;
            McpServer.QueryLogWriter = _ => wroteAnything = true;

            var r = McpServer.ProcessMessage(Msg(20, "tools/call",
                @"{ ""name"": ""x1_set_query_log"", ""arguments"": {} }"));

            Assert.That(r["error"], Is.Null);
            var info = JObject.Parse(r["result"]["content"][0]["text"].ToString());
            Assert.That(info.Value<bool>("queryLogEnabled"), Is.False,
                "false must mean query content is not logged - this is the off-by-default state.");
            Assert.That(wroteAnything, Is.False, "omitting 'enabled' must be a pure read, never a write.");
        }

        [Test]
        public void SetQueryLog_EnabledTrue_WritesAndReportsTrue()
        {
            bool? written = null;
            McpServer.QueryLogWriter = v => written = v;
            McpServer.QueryLogEnabledProvider = () => written ?? false;

            var r = McpServer.ProcessMessage(Msg(21, "tools/call",
                @"{ ""name"": ""x1_set_query_log"", ""arguments"": { ""enabled"": true } }"));

            Assert.That(r["error"], Is.Null);
            Assert.That(written, Is.True);
            var info = JObject.Parse(r["result"]["content"][0]["text"].ToString());
            Assert.That(info.Value<bool>("queryLogEnabled"), Is.True);
        }

        [Test]
        public void SetQueryLog_EnabledFalse_WritesAndReportsFalse()
        {
            bool? written = null;
            McpServer.QueryLogWriter = v => written = v;
            McpServer.QueryLogEnabledProvider = () => written ?? true;

            var r = McpServer.ProcessMessage(Msg(22, "tools/call",
                @"{ ""name"": ""x1_set_query_log"", ""arguments"": { ""enabled"": false } }"));

            Assert.That(r["error"], Is.Null);
            Assert.That(written, Is.False);
            var info = JObject.Parse(r["result"]["content"][0]["text"].ToString());
            Assert.That(info.Value<bool>("queryLogEnabled"), Is.False);
        }

        // ── HandleServiceShutdown (XS-1698/XS-1701) ─────────────────────────────
        //
        // McpServer.HandleServiceShutdown is what X1MCPServiceConnection's OnShutdown callback
        // invokes when X1 announces a clean shutdown. It has two branches (Host mode vs. plain
        // stdio) - both are exercised here without touching a live relay or actually terminating
        // the test process, via the ExitProcess seam and HostMode's own StopRequestedForTest seam.

        [TearDown]
        public void TearDownServiceShutdownSeams()
        {
            HostMode.IsHostRunning = false;
            HostMode.ResetStopRequestedForTest();
            McpServer.ExitProcess = () => Environment.Exit(0);
        }

        [Test]
        public void HandleServiceShutdown_WhenHostRunning_RequestsHostShutdownInsteadOfExiting()
        {
            HostMode.IsHostRunning = true;
            bool exited = false;
            McpServer.ExitProcess = () => exited = true;

            McpServer.HandleServiceShutdown();

            Assert.That(HostMode.StopRequestedForTest, Is.True,
                "Host mode must request HostMode's own graceful shutdown");
            Assert.That(exited, Is.False,
                "Host mode must not also exit the process directly - HostMode.Run()'s own Shutdown() handles that");
        }

        [Test]
        public void HandleServiceShutdown_WhenNotHostRunning_CallsExitProcessSeam()
        {
            HostMode.IsHostRunning = false;
            bool exited = false;
            McpServer.ExitProcess = () => exited = true;

            McpServer.HandleServiceShutdown();

            Assert.That(exited, Is.True,
                "Plain stdio (not host mode) must exit the process - there is no relay to relaunch");
        }

        [Test]
        public void Initialize_WithClientInfo_StillSucceedsWithoutX1ServiceHost()
        {
            // XS-1685: initialize now best-effort reports clientInfo to X1 Search via
            // ReportClientInfoBestEffort. That must never block or fail the handshake itself - the
            // service host may not even be up yet (same reasoning as StartBackend()'s own best-effort
            // Connect()). This test process has no live X1ServiceHost, so if the try/catch in
            // ReportClientInfoBestEffort didn't work, this would throw instead of returning normally.
            var r = McpServer.ProcessMessage(Msg(2, "initialize",
                @"{ ""clientInfo"": { ""name"": ""test-client"", ""version"": ""9.9.9"" } }"));

            Assert.That(r["error"], Is.Null);
            Assert.That(r["result"]?["protocolVersion"], Is.Not.Null);
        }

        [Test]
        public void Initialize_WithoutClientInfo_StillSucceeds()
        {
            // Some transports/callers may omit clientInfo entirely - must not NRE on the missing key.
            var r = McpServer.ProcessMessage(Msg(2, "initialize"));

            Assert.That(r["error"], Is.Null);
            Assert.That(r["result"]?["protocolVersion"], Is.Not.Null);
        }

        [Test]
        public void Initialize_ReportsTheStampedVersionNotAHardcodedOne()
        {
            // Regression: serverInfo.version used to be the literal "1.0.0", which drifted from
            // the stamped build and made the handshake misreport what was running.
            var r = McpServer.ProcessMessage(Msg(1, "initialize"));
            var version = r["result"]?["serverInfo"]?["version"]?.ToString();

            Assert.That(version, Does.Match(@"^\d+\.\d+\.\d+\.\d+$"));
            Assert.That(version, Is.EqualTo(
                System.Reflection.Assembly.GetAssembly(typeof(McpServer)).GetName().Version.ToString()));
        }

        [Test]
        public void ToolsList_EachToolHasInputSchema()
        {
            var r = McpServer.ProcessMessage(Msg(3, "tools/list"));
            var tools = r["result"]?["tools"] as JArray;
            foreach (JObject t in tools)
                Assert.That(t["inputSchema"], Is.Not.Null, $"Tool '{t["name"]}' missing inputSchema");
        }

        private static JObject ToolSchema(string toolName)
        {
            var r = McpServer.ProcessMessage(Msg(3, "tools/list"));
            var tools = r["result"]?["tools"] as JArray;
            foreach (JObject t in tools)
                if (t["name"]?.ToString() == toolName)
                    return t["inputSchema"] as JObject;
            return null;
        }

        // ── #1: x1_search includeActions schema ───────────────────────────────────

        [Test]
        public void ToolsList_Search_IncludesIncludeActionsDefaultingTrue()
        {
            var schema = ToolSchema("x1_search");
            var prop = schema?["properties"]?["includeActions"];
            Assert.That(prop, Is.Not.Null, "x1_search should expose includeActions");
            Assert.That(prop["default"]?.Value<bool>(), Is.True, "includeActions should default to true");
        }

        // ── Multi-table fan-out schema wording (XS-1642 follow-up) ────────────────

        [Test]
        public void ToolsList_Search_TablesDescriptionMentionsMultiTableSupport()
        {
            // Regression guard: the old wording actively told the caller NOT to pass multiple
            // tables. If a future edit reverts to that, this should fail loudly rather than
            // silently reintroducing stale, contradictory guidance now that multi-table fan-out
            // is supported.
            var schema = ToolSchema("x1_search");
            var description = schema?["properties"]?["tables"]?["description"]?.ToString() ?? "";

            Assert.That(description, Does.Contain("byTable"));
            Assert.That(description, Does.Not.Contain("Do NOT pass multiple tables"));
        }

        // ── #5: x1_generate_preview maxChars schema ───────────────────────────────

        [Test]
        public void ToolsList_GeneratePreview_IncludesMaxChars()
        {
            var schema = ToolSchema("x1_generate_preview");
            var prop = schema?["properties"]?["maxChars"];
            Assert.That(prop, Is.Not.Null, "x1_generate_preview should expose maxChars");
            Assert.That(prop["default"]?.Value<int>(), Is.EqualTo(0), "maxChars should default to 0 (unlimited)");
        }

        // ── resources/list ───────────────────────────────────────────────────────

        [Test]
        public void ResourcesList_ReturnsIndexStatsResource()
        {
            var r = McpServer.ProcessMessage(Msg(4, "resources/list"));
            var resources = r["result"]?["resources"] as JArray;
            Assert.That(resources, Is.Not.Null);
            Assert.That(resources.Count, Is.EqualTo(1));
            Assert.That(resources[0]["uri"]?.ToString(), Is.EqualTo("x1://index/stats"));
        }

        // ── prompts/list ─────────────────────────────────────────────────────────

        [Test]
        public void PromptsList_ReturnsOnePrompt()
        {
            var r = McpServer.ProcessMessage(Msg(5, "prompts/list"));
            var prompts = r["result"]?["prompts"] as JArray;
            Assert.That(prompts, Is.Not.Null);
            Assert.That(prompts.Count, Is.EqualTo(1));
            Assert.That(prompts[0]["name"]?.ToString(), Is.EqualTo("x1_search_best_practices"));
        }

        // ── prompts/get ──────────────────────────────────────────────────────────

        [Test]
        public void PromptsGet_BestPractices_ReturnsContent()
        {
            var r = McpServer.ProcessMessage(
                Msg(6, "prompts/get", @"{""name"":""x1_search_best_practices""}"));
            var messages = r["result"]?["messages"] as JArray;
            Assert.That(messages, Is.Not.Null);
            Assert.That(messages.Count, Is.GreaterThan(0));
            Assert.That(messages[0]["role"]?.ToString(), Is.EqualTo("user"));
            var text = messages[0]["content"]?["text"]?.ToString();
            Assert.That(text, Does.Contain("X1"));
        }

        [Test]
        public void PromptsGet_UnknownPrompt_ReturnsError()
        {
            var r = McpServer.ProcessMessage(
                Msg(6, "prompts/get", @"{""name"":""no_such_prompt""}"));
            Assert.That(r["error"], Is.Not.Null);
        }

        // ── tools/call x1_list_sources ────────────────────────────────────────────
        //
        // x1_list_sources' actual grouping/columns/capabilities/XS-1605-unmangling logic
        // is exercised directly (no WCF needed) in DataSourceInfoMapperTests. Unlike the
        // old static-catalog-backed tool, this one's *content* now genuinely depends on
        // whether a live X1ServiceHost is reachable — and McpServer.ServiceConnection
        // isn't mockable, so whether that's true varies by machine (a dev box with X1
        // actually running will get real data back, not an empty array). This test only
        // covers McpServer's routing: it must never error, and whatever it returns must
        // be well-formed, regardless of whether a host happened to be reachable.

        [Test]
        public void ToolsCall_ListSources_ReturnsWellFormedSourcesWithoutError()
        {
            var r = McpServer.ProcessMessage(Msg(7, "tools/call",
                @"{""name"":""x1_list_sources"",""arguments"":{}}"));

            Assert.That(r["error"], Is.Null);
            var content = r["result"]?["content"] as JArray;
            Assert.That(content, Is.Not.Null);
            var text = content[0]["text"]?.ToString();
            var sources = JObject.Parse(text)["sources"] as JArray;
            Assert.That(sources, Is.Not.Null);
            foreach (JObject s in sources)
            {
                Assert.That(s["name"], Is.Not.Null);
                Assert.That(s["columns"], Is.InstanceOf<JArray>());
                Assert.That(s["capabilities"], Is.InstanceOf<JObject>());
                Assert.That(s["accounts"], Is.InstanceOf<JArray>());
            }
        }

        // ── first-use banner (XS-1673) ──────────────────────────────────────────
        //
        // Delivered via initialize's "instructions" field (read by the connecting agent as
        // operating guidance) rather than appended to a tool result - a tool-result content block
        // competes with the tool's own payload for the model's attention and in practice gets
        // dropped when the model writes its reply.

        [Test]
        public void Initialize_FirstEverCall_IncludesInstructionsWithLandingPage()
        {
            var freshPath = Path.Combine(Path.GetTempPath(), "x1mcp_first_use_test_" + Guid.NewGuid().ToString("N") + ".json");
            FirstUseTracker.OverrideMarkerPath(freshPath);
            try
            {
                var r = McpServer.ProcessMessage(Msg(10, "initialize"));

                Assert.That(r["result"]?["instructions"]?.ToString(), Does.Contain(BridgeConstants.McpLandingPageUrl));
                Assert.That(FirstUseTracker.HasBeenShown(), Is.True);
            }
            finally
            {
                FirstUseTracker.OverrideMarkerPath(_firstUseMarkerPath);
                if (File.Exists(freshPath)) File.Delete(freshPath);
            }
        }

        [Test]
        public void Initialize_SecondCallAfterFirst_OmitsInstructions()
        {
            var freshPath = Path.Combine(Path.GetTempPath(), "x1mcp_first_use_test_" + Guid.NewGuid().ToString("N") + ".json");
            FirstUseTracker.OverrideMarkerPath(freshPath);
            try
            {
                McpServer.ProcessMessage(Msg(11, "initialize"));
                var r2 = McpServer.ProcessMessage(Msg(12, "initialize"));

                Assert.That(r2["result"]?["instructions"], Is.Null);
            }
            finally
            {
                FirstUseTracker.OverrideMarkerPath(_firstUseMarkerPath);
                if (File.Exists(freshPath)) File.Delete(freshPath);
            }
        }

        // ── unknown method ───────────────────────────────────────────────────────

        [Test]
        public void UnknownMethod_ReturnsMethodNotFoundError()
        {
            var r = McpServer.ProcessMessage(Msg(8, "something/unknown"));
            Assert.That(r["error"], Is.Not.Null);
            Assert.That(r["error"]["code"]?.Value<int>(), Is.EqualTo(-32601));
        }

        // ── notifications ────────────────────────────────────────────────────────

        [Test]
        public void Notification_Initialized_ReturnsNull()
        {
            var msg = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}");
            var r = McpServer.ProcessMessage(msg);
            Assert.That(r, Is.Null);
        }

        [Test]
        public void Notification_Cancelled_ReturnsNull()
        {
            var msg = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""notifications/cancelled""}");
            var r = McpServer.ProcessMessage(msg);
            Assert.That(r, Is.Null);
        }

        // ── JSON-RPC structure ───────────────────────────────────────────────────

        [Test]
        public void AnyResponse_ContainsJsonrpc2()
        {
            var r = McpServer.ProcessMessage(Msg(1, "ping"));
            Assert.That(r["jsonrpc"]?.ToString(), Is.EqualTo("2.0"));
        }

        [Test]
        public void ToolsCall_UnknownTool_ReturnsError()
        {
            var r = McpServer.ProcessMessage(Msg(9, "tools/call",
                @"{""name"":""x1_does_not_exist"",""arguments"":{}}"));
            Assert.That(r["error"], Is.Not.Null);
        }

        // ── tools/call x1_list_actions (no WCF) ─────────────────────────────────

        [Test]
        public void ToolsCall_ListActions_FilesTable_ReturnsActionsArray()
        {
            var r = McpServer.ProcessMessage(Msg(12, "tools/call",
                @"{""name"":""x1_list_actions"",""arguments"":{""table"":""Files"",""uri"":""files://C:/test.txt""}}"));

            Assert.That(r["error"], Is.Null);
            var text = (r["result"]?["content"] as JArray)?[0]["text"]?.ToString();
            Assert.That(text, Is.Not.Null);
            var result = JObject.Parse(text);
            Assert.That(result["actions"], Is.Not.Null);
            Assert.That((result["actions"] as JArray)?.Count, Is.GreaterThan(0));
        }

        [Test]
        public void ToolsCall_ListActions_UnknownTable_ErrorsIfSchemaKnown_OtherwiseEmptyActions()
        {
            // XS-1640: TableSchemaResolver now validates every table-taking tool's table argument,
            // including x1_list_actions. Its behavior legitimately depends on whether THIS process
            // can reach a live X1ServiceHost (this test suite is deliberately WCF-free otherwise,
            // but McpServer's shared TableResolver/ServiceConnection are process-wide singletons,
            // so a host that happens to be running under the current Windows user is reachable
            // here too): once the resolver has learned the real set of valid schemas, an
            // unrecognized table now fails fast with a descriptive error naming it - the same
            // opaque/silent-failure pattern XS-1640 flagged for x1_search and x1_get_schema_fields
            // also applied here before this fix. With no schema knowledge at all (no reachable
            // host), the resolver's safety valve trusts the caller's token unchanged, preserving
            // the old empty-actions-array behavior instead of inventing a validation failure.
            var r = McpServer.ProcessMessage(Msg(13, "tools/call",
                @"{""name"":""x1_list_actions"",""arguments"":{""table"":""NoSuchTable"",""uri"":""x://uri""}}"));

            if (r["error"] != null)
            {
                var message = r["error"]["message"]?.ToString() ?? "";
                StringAssert.Contains("NoSuchTable", message);
            }
            else
            {
                var text = (r["result"]?["content"] as JArray)?[0]["text"]?.ToString();
                var result = JObject.Parse(text);
                Assert.That((result["actions"] as JArray)?.Count, Is.EqualTo(0));
            }
        }

        // ── NormalizeAndValidateArgs (XS-1642 follow-up) ─────────────────────────
        //
        // Regression coverage for the mistake that motivated this: an agent called x1_search
        // with "table": "MSMail" (singular) instead of the schema's "tables" (plural array).
        // That key was silently ignored - args["tables"] was null, so SearchBridge.ResolveTablesAsync
        // fell back to BridgeConfig.GetDefaultTables() and searched the wrong table with a
        // normal-looking, wrong, response. These tests exercise the fix directly (no WCF needed)
        // rather than through x1_search's full CallToolInner path, which does need a live host.

        [Test]
        public void NormalizeArgs_Search_SingularTable_RenamedToTablesAsArray()
        {
            var args = JObject.Parse(@"{""query"":""hello"",""table"":""MSMail""}");
            McpServer.NormalizeAndValidateArgs("x1_search", args);

            Assert.That(args["table"], Is.Null, "the misnamed key should be removed");
            var tables = args["tables"] as JArray;
            Assert.That(tables, Is.Not.Null);
            Assert.That(tables.Count, Is.EqualTo(1));
            Assert.That(tables[0].ToString(), Is.EqualTo("MSMail"));
        }

        [Test]
        public void NormalizeArgs_Search_TablesAlreadyCorrect_LeftUntouched()
        {
            var args = JObject.Parse(@"{""query"":""hello"",""tables"":[""MSMail""]}");
            McpServer.NormalizeAndValidateArgs("x1_search", args);

            var tables = args["tables"] as JArray;
            Assert.That(tables?.Count, Is.EqualTo(1));
            Assert.That(tables[0].ToString(), Is.EqualTo("MSMail"));
        }

        [Test]
        public void NormalizeArgs_Search_BothTableAndTablesGiven_ThrowsRatherThanGuessing()
        {
            // Ambiguous - don't silently prefer one over the other.
            var args = JObject.Parse(@"{""query"":""hello"",""table"":""MSMail"",""tables"":[""Files""]}");
            var ex = Assert.Throws<ArgumentException>(() => McpServer.NormalizeAndValidateArgs("x1_search", args));
            Assert.That(ex.Message, Does.Contain("table"));
        }

        [Test]
        public void NormalizeArgs_UnrecognizedParameter_ThrowsNamingItAndListingValidOnes()
        {
            var args = JObject.Parse(@"{""query"":""hello"",""tabel"":[""MSMail""]}"); // typo, not an alias
            var ex = Assert.Throws<ArgumentException>(() => McpServer.NormalizeAndValidateArgs("x1_search", args));

            Assert.That(ex.Message, Does.Contain("tabel"));
            Assert.That(ex.Message, Does.Contain("tables"), "should list the valid parameter names");
        }

        [Test]
        public void NormalizeArgs_GetMetadata_SingularTableIsAlreadyCorrect_NoChange()
        {
            // x1_get_metadata's schema genuinely declares "table" (singular, not an array) - proves
            // the alias logic doesn't rename a parameter that's already correct for this tool's shape.
            var args = JObject.Parse(@"{""table"":""Files"",""uri"":""files://C:/x.txt""}");
            Assert.DoesNotThrow(() => McpServer.NormalizeAndValidateArgs("x1_get_metadata", args));
            Assert.That(args["table"]?.ToString(), Is.EqualTo("Files"));
        }

        [Test]
        public void NormalizeArgs_UnknownToolName_DoesNotThrow()
        {
            // CallToolInner's own final `throw new InvalidOperationException("Unknown tool: ...")`
            // is the right place to report this - normalization should just no-op.
            var args = JObject.Parse(@"{""anything"":""goes""}");
            Assert.DoesNotThrow(() => McpServer.NormalizeAndValidateArgs("x1_does_not_exist", args));
        }

        // ── tools/call x1_execute_action (no WCF) ────────────────────────────────

        [Test]
        public void ToolsCall_ExecuteAction_UnsupportedAction_ReturnsErrorJson()
        {
            var r = McpServer.ProcessMessage(Msg(14, "tools/call",
                @"{""name"":""x1_execute_action"",""arguments"":{""table"":""Files"",""uri"":""files://C:/x.txt"",""action"":""delete""}}"));

            // The bridge wraps action errors as JSON text content, NOT a JSON-RPC error.
            Assert.That(r["error"], Is.Null, "Unsupported action should return JSON text, not JSON-RPC error");
            var text = (r["result"]?["content"] as JArray)?[0]["text"]?.ToString();
            Assert.That(text, Is.Not.Null);
            var result = JObject.Parse(text);
            Assert.That(result.Value<string>("status"), Is.EqualTo("error"));
        }

        [Test]
        public void ToolsCall_ExecuteAction_GetPath_NonExistentFile_ReturnsErrorJson()
        {
            var r = McpServer.ProcessMessage(Msg(15, "tools/call",
                @"{""name"":""x1_execute_action"",""arguments"":{""table"":""Files"",""uri"":""files://C:/nonexistent_x1mcp_test.txt"",""action"":""open""}}"));

            Assert.That(r["error"], Is.Null);
            var text = (r["result"]?["content"] as JArray)?[0]["text"]?.ToString();
            var result = JObject.Parse(text);
            Assert.That(result.Value<string>("status"), Is.EqualTo("error"));
        }
    }
}
