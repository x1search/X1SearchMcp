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

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// C1 — Verifies that exceptions thrown inside async tool methods surface their
    /// real message in the MCP error response rather than the AggregateException
    /// wrapper message "One or more errors occurred." that task.Wait() would produce.
    ///
    /// These tests use tool call paths that validate arguments before touching WCF,
    /// so they do not require X1ServiceHost to be running.
    /// </summary>
    [TestFixture]
    public class ExceptionPropagationTests
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

        private static JObject ToolCall(string toolName, string argsJson)
        {
            return McpServer.ProcessMessage(JObject.Parse($@"{{
        ""jsonrpc"": ""2.0"",
        ""id"": 1,
        ""method"": ""tools/call"",
        ""params"": {{ ""name"": ""{toolName}"", ""arguments"": {argsJson} }}
      }}"));
        }

        // ── x1_search — no query and no filters ──────────────────────────────────

        /// <summary>
        /// x1_search with neither query nor filters throws ArgumentException inside
        /// SearchAsync. With task.Wait() the error message would be
        /// "One or more errors occurred." — with GetAwaiter().GetResult() it must
        /// contain the real message.
        /// </summary>
        [Test]
        public void XSearch_NoQueryNoFilters_ErrorContainsRealMessage()
        {
            var r = ToolCall("x1_search", @"{""tables"":[""Files""]}");

            Assert.That(r["error"], Is.Not.Null, "Expected an error response");

            var message = r["error"]["message"]?.ToString() ?? "";
            // The real exception message from SearchAsync
            StringAssert.DoesNotContain("One or more errors occurred", message);
            StringAssert.Contains("search term", message);
        }

        [Test]
        public void XSearch_NoQueryNoFilters_IsInternalError()
        {
            var r = ToolCall("x1_search", @"{""tables"":[""Files""]}");

            Assert.That(r["error"], Is.Not.Null);
            Assert.That(r["error"]["code"]?.Value<int>(), Is.EqualTo(-32603));
        }

        // ── x1_get_content — missing required table/uri ──────────────────────────

        /// <summary>
        /// x1_get_content with empty table throws ArgumentException inside GetContentAsync
        /// before any WCF call is made. Verifies the real message surfaces.
        /// </summary>
        [Test]
        public void XGetContent_MissingTable_ErrorContainsRealMessage()
        {
            var r = ToolCall("x1_get_content", @"{""table"":"""",""uri"":""files://test.txt""}");

            Assert.That(r["error"], Is.Not.Null, "Expected an error response");

            var message = r["error"]["message"]?.ToString() ?? "";
            StringAssert.DoesNotContain("One or more errors occurred", message);
        }

        [Test]
        public void XGetContent_MissingUri_ErrorContainsRealMessage()
        {
            var r = ToolCall("x1_get_content", @"{""table"":""Files"",""uri"":""""}");

            Assert.That(r["error"], Is.Not.Null, "Expected an error response");

            var message = r["error"]["message"]?.ToString() ?? "";
            StringAssert.DoesNotContain("One or more errors occurred", message);
        }

        // ── x1_generate_preview — missing required table/uri ─────────────────────
        //
        // XS-1678 added a _tableResolver.ResolveOrThrowAsync call to GeneratePreviewAsync
        // (after arg validation, before the try that would otherwise swallow it into a
        // generic {"error": ...} JObject) - these confirm the pre-existing missing-table/uri
        // ArgumentExceptions still surface the same way (real message via -32603), i.e. the new
        // resolver call didn't change ordering or exception-wrapping for this method.

        [Test]
        public void XGeneratePreview_MissingTable_ErrorContainsRealMessage()
        {
            var r = ToolCall("x1_generate_preview", @"{""table"":"""",""uri"":""files://test.txt""}");

            Assert.That(r["error"], Is.Not.Null, "Expected an error response");

            var message = r["error"]["message"]?.ToString() ?? "";
            StringAssert.DoesNotContain("One or more errors occurred", message);
        }

        [Test]
        public void XGeneratePreview_MissingUri_ErrorContainsRealMessage()
        {
            var r = ToolCall("x1_generate_preview", @"{""table"":""Files"",""uri"":""""}");

            Assert.That(r["error"], Is.Not.Null, "Expected an error response");

            var message = r["error"]["message"]?.ToString() ?? "";
            StringAssert.DoesNotContain("One or more errors occurred", message);
        }

        // ── x1_get_metadata — missing required table/uri ─────────────────────────

        [Test]
        public void XGetMetadata_MissingTable_ErrorContainsRealMessage()
        {
            var r = ToolCall("x1_get_metadata", @"{""table"":"""",""uri"":""files://test.txt""}");

            Assert.That(r["error"], Is.Not.Null, "Expected an error response");

            var message = r["error"]["message"]?.ToString() ?? "";
            StringAssert.DoesNotContain("One or more errors occurred", message);
        }

        // ── Unknown tool — baseline: exception path that was always synchronous ───

        /// <summary>
        /// Sanity check: unknown tool name still returns -32603 with a useful message.
        /// </summary>
        [Test]
        public void UnknownTool_ErrorMessageIsUseful()
        {
            var r = ToolCall("x1_nonexistent", @"{}");

            Assert.That(r["error"], Is.Not.Null);
            var message = r["error"]["message"]?.ToString() ?? "";
            StringAssert.DoesNotContain("One or more errors occurred", message);
            StringAssert.Contains("Unknown tool", message);
        }
    }
}
