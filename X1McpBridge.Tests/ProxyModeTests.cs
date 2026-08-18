// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// Pure-logic tests for ProxyMode's SSE parsing — the rest of ProxyMode (health-check,
    /// detached launch, HTTP relay) requires a live daemon and is covered by manual end-to-end
    /// verification instead (see the fan-in plan's Phase 4 verification step).
    /// </summary>
    [TestFixture]
    public class ProxyModeTests
    {
        [Test]
        public void ParseSseLastMessage_SingleEvent_ReturnsIt()
        {
            const string sse = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n";
            var result = ProxyMode.ParseSseLastMessage(sse);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Value<int>("id"), Is.EqualTo(1));
        }

        [Test]
        public void ParseSseLastMessage_MultipleEvents_ReturnsLastOne()
        {
            const string sse =
                "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\n" +
                "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":5,\"result\":{\"done\":true}}\n\n";
            var result = ProxyMode.ParseSseLastMessage(sse);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Value<int>("id"), Is.EqualTo(5));
        }

        [Test]
        public void ParseSseLastMessage_NoDataLines_ReturnsNull()
        {
            const string sse = "event: message\n\n";
            var result = ProxyMode.ParseSseLastMessage(sse);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void ParseSseLastMessage_MalformedJsonInOneEvent_SkipsItAndReturnsLastValidOne()
        {
            const string sse =
                "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n" +
                "event: message\ndata: not valid json\n\n";
            var result = ProxyMode.ParseSseLastMessage(sse);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Value<int>("id"), Is.EqualTo(1));
        }

        [Test]
        public void ParseSseLastMessage_EmptyBody_ReturnsNull()
        {
            var result = ProxyMode.ParseSseLastMessage("");
            Assert.That(result, Is.Null);
        }

        [Test]
        public void ParseSseLastMessage_CrLfLineEndings_ParsesCorrectly()
        {
            const string sse = "event: message\r\ndata: {\"jsonrpc\":\"2.0\",\"id\":42,\"result\":{}}\r\n\r\n";
            var result = ProxyMode.ParseSseLastMessage(sse);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Value<int>("id"), Is.EqualTo(42));
        }
    }
}
