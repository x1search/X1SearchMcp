// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    [TestFixture]
    public class McpProtocolTests
    {
        [Test]
        public void ProtocolVersion_IsExpectedSpec()
        {
            Assert.That(McpProtocol.ProtocolVersion, Is.EqualTo("2024-11-05"));
        }

        // ── Ok ───────────────────────────────────────────────────────────────────

        [Test]
        public void Ok_SetsJsonRpc2()
        {
            var r = McpProtocol.Ok(1, new JObject());
            Assert.That(r["jsonrpc"]?.ToString(), Is.EqualTo("2.0"));
        }

        [Test]
        public void Ok_SetsId_Integer()
        {
            var r = McpProtocol.Ok(42, new JObject());
            Assert.That(r["id"]?.Value<int>(), Is.EqualTo(42));
        }

        [Test]
        public void Ok_SetsId_String()
        {
            var r = McpProtocol.Ok("req-1", new JObject());
            Assert.That(r["id"]?.ToString(), Is.EqualTo("req-1"));
        }

        [Test]
        public void Ok_NullId_IsJsonNull()
        {
            var r = McpProtocol.Ok(null, new JObject());
            Assert.That(r["id"]?.Type, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void Ok_ResultIsIncluded()
        {
            var result = new JObject { ["foo"] = "bar" };
            var r = McpProtocol.Ok(1, result);
            Assert.That(r["result"]?["foo"]?.ToString(), Is.EqualTo("bar"));
        }

        [Test]
        public void Ok_NoErrorField()
        {
            var r = McpProtocol.Ok(1, new JObject());
            Assert.That(r["error"], Is.Null);
        }

        // ── Err ──────────────────────────────────────────────────────────────────

        [Test]
        public void Err_SetsJsonRpc2()
        {
            var r = McpProtocol.Err(1, -32600, "Bad request");
            Assert.That(r["jsonrpc"]?.ToString(), Is.EqualTo("2.0"));
        }

        [Test]
        public void Err_SetsId()
        {
            var r = McpProtocol.Err(7, -32600, "Bad request");
            Assert.That(r["id"]?.Value<int>(), Is.EqualTo(7));
        }

        [Test]
        public void Err_SetsCodeAndMessage()
        {
            var r = McpProtocol.Err(1, -32601, "Method not found");
            Assert.That(r["error"]?["code"]?.Value<int>(), Is.EqualTo(-32601));
            Assert.That(r["error"]?["message"]?.ToString(), Is.EqualTo("Method not found"));
        }

        [Test]
        public void Err_WithData_IncludesData()
        {
            var r = McpProtocol.Err(1, -32603, "Internal error", "stack trace here");
            Assert.That(r["error"]?["data"]?.ToString(), Is.EqualTo("stack trace here"));
        }

        [Test]
        public void Err_WithoutData_NoDataField()
        {
            var r = McpProtocol.Err(1, -32603, "Internal error");
            Assert.That(r["error"]?["data"], Is.Null);
        }

        [Test]
        public void Err_NoResultField()
        {
            var r = McpProtocol.Err(1, -32600, "Bad");
            Assert.That(r["result"], Is.Null);
        }

        // ── IsNotification ───────────────────────────────────────────────────────

        [Test]
        public void IsNotification_NoIdField_ReturnsTrue()
        {
            var msg = JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}");
            Assert.That(McpProtocol.IsNotification(msg), Is.True);
        }

        [Test]
        public void IsNotification_NullIdField_ReturnsTrue()
        {
            var msg = JObject.Parse(@"{""jsonrpc"":""2.0"",""id"":null,""method"":""ping""}");
            Assert.That(McpProtocol.IsNotification(msg), Is.True);
        }

        [Test]
        public void IsNotification_IntegerId_ReturnsFalse()
        {
            var msg = JObject.Parse(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}");
            Assert.That(McpProtocol.IsNotification(msg), Is.False);
        }

        [Test]
        public void IsNotification_StringId_ReturnsFalse()
        {
            var msg = JObject.Parse(@"{""jsonrpc"":""2.0"",""id"":""abc"",""method"":""ping""}");
            Assert.That(McpProtocol.IsNotification(msg), Is.False);
        }
    }
}
