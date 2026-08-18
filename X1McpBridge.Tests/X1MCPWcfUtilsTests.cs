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
    /// XS-1609: guards the endpoint address strings for the dedicated MCP-only WCF
    /// interfaces (IX1MCPService / IX1MCPSearchManager) against regressing the
    /// prefix-typo bug pattern seen in WcfUtils.SearchManagerEndpointName's netTcpPort branch.
    /// </summary>
    [TestFixture]
    public class X1MCPWcfUtilsTests
    {
        [Test]
        public void MCPServiceEndpointName_NamedPipe_UsesX1MCPServicePrefix()
        {
            string endpoint = X1MCPWcfUtils.MCPServiceEndpointName("bob");
            Assert.That(endpoint, Is.EqualTo("net.pipe://localhost/X1MCPService_bob"));
        }

        [Test]
        public void MCPServiceEndpointName_NetTcpPort_UsesX1MCPServicePrefix()
        {
            string endpoint = X1MCPWcfUtils.MCPServiceEndpointName("bob", netTcpPort: 12345);
            Assert.That(endpoint, Is.EqualTo("net.tcp://localhost:12345/X1MCPService_bob"));
        }

        [Test]
        public void MCPServiceEndpointName_UrlEncodesServiceId()
        {
            // XS-1672: must match HttpHelper.UrlEncode's '+'-for-space encoding, not
            // Uri.EscapeDataString's '%20' - the closed-source X1ServiceHost.exe independently
            // recomputes this same name via the original HttpHelper.UrlEncode, so a mismatch here
            // would silently break any Windows username containing a space.
            string endpoint = X1MCPWcfUtils.MCPServiceEndpointName("bob smith");
            Assert.That(endpoint, Is.EqualTo("net.pipe://localhost/X1MCPService_bob+smith"));
        }

        [Test]
        public void MCPSearchManagerEndpointName_NamedPipe_UsesX1MCPSearchManagerPrefix()
        {
            string endpoint = X1MCPWcfUtils.MCPSearchManagerEndpointName("bob");
            Assert.That(endpoint, Is.EqualTo("net.pipe://localhost/X1MCPSearchManager_bob"));
        }

        [Test]
        public void MCPSearchManagerEndpointName_NetTcpPort_UsesX1MCPSearchManagerPrefix()
        {
            string endpoint = X1MCPWcfUtils.MCPSearchManagerEndpointName("bob", netTcpPort: 12345);
            Assert.That(endpoint, Is.EqualTo("net.tcp://localhost:12345/X1MCPSearchManager_bob"));
        }

        [Test]
        public void MCPSearchManagerEndpointName_RemoteHost_UsesX1MCPSearchManagerPrefix()
        {
            string endpoint = X1MCPWcfUtils.MCPSearchManagerEndpointName("bob", "otherhost");
            Assert.That(endpoint, Does.Contain("otherhost"));
            Assert.That(endpoint, Does.Contain("X1MCPSearchManager_bob"));
        }
    }
}
