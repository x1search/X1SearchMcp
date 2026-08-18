// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.ServiceModel;
using System.ServiceModel.Channels;
using log4net;
using X1.Service;

namespace X1.McpBridge
{
    /// <summary>
    /// Duplex WCF client to IX1MCPSearchManager (same binding rules as X1UI2 SearchSessionManager).
    /// XS-1609: dedicated MCP-only WCF service, served at its own endpoint address, so this
    /// connection no longer shares X1ServiceHost's single-client slot with the desktop UI's
    /// own IX1SearchManager connection. Callbacks stay on the shared IX1SearchManagerCallbacks
    /// contract — unchanged.
    /// </summary>
    internal sealed class X1MCPSearchConnection : IDisposable
    {
        private static readonly ILog Log = BridgeLogger.GetLogger(typeof(X1MCPSearchConnection));
        private readonly SearchManagerCallbacks _callbacks;
        private DuplexChannelFactory<IX1MCPSearchManager> _factory;
        private IX1MCPSearchManager _channel;

        public X1MCPSearchConnection(SearchManagerCallbacks callbacks)
        {
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        }

        public IX1MCPSearchManager GetChannel()
        {
            // If the channel has faulted or been closed, tear it down so we reconnect cleanly.
            if (_channel is ICommunicationObject existing &&
                (existing.State == CommunicationState.Faulted ||
                 existing.State == CommunicationState.Closed))
            {
                Log.Warn("WCF search channel is in state " + existing.State + " — resetting and reconnecting");
                existing.Abort();
                _channel = null;
                try { _factory?.Abort(); } catch { }
                _factory = null;
            }

            if (_channel != null)
                return _channel;

            Binding binding;
            EndpointAddress address;
            string userName = Environment.UserName;
            string remoteHost = Environment.GetEnvironmentVariable("X1_MCP_SERVICE_HOST") ?? "";

            if (!string.IsNullOrEmpty(remoteHost))
            {
                binding = X1McpWcfBindingFactory.CreateTcpBinding(useEncryption: true, usePortSharing: false);
                string endpoint = X1MCPWcfUtils.MCPSearchManagerEndpointName(userName, remoteHost);
                address = X1McpWcfBindingFactory.CreateEndpointAddressForCurrentUser(endpoint);
            }
            else if (RegistrySettings.ReadInteger("UseNetTcpBinding", 0) == 1)
            {
                int port = RegistrySettings.ReadInteger("SearchManagerNetTcpBindingPort", 0);
                if (port == 0)
                    throw new InvalidOperationException("Registry UseNetTcpBinding=1 but SearchManagerNetTcpBindingPort is not set.");
                binding = X1McpWcfBindingFactory.CreateTcpBinding(useEncryption: true, usePortSharing: false);
                string endpoint = X1MCPWcfUtils.MCPSearchManagerEndpointName(userName, netTcpPort: port);
                address = new EndpointAddress(endpoint);
            }
            else
            {
                binding = X1McpWcfBindingFactory.CreateNamedPipeBinding(useEncryption: false);
                string endpoint = X1MCPWcfUtils.MCPSearchManagerEndpointName(userName);
                address = new EndpointAddress(endpoint);
            }

            // The X1Pro Settings.MAX_BUFFER_SIZE is only 640 KB, but OnContentReady returns
            // the full extracted text inline — a large PDF can exceed that and fault the
            // duplex channel, which silently drops all subsequent callbacks on the same
            // channel (including tiny ones like OnTagsAdded). Override to int.MaxValue here
            // so the bridge can receive arbitrarily large content responses.
            if (binding is NetNamedPipeBinding npb)
            {
                npb.MaxBufferSize = int.MaxValue;
                npb.MaxReceivedMessageSize = int.MaxValue;
                npb.ReaderQuotas.MaxStringContentLength = int.MaxValue;
                npb.ReaderQuotas.MaxArrayLength = int.MaxValue;
            }
            else if (binding is NetTcpBinding tcpb)
            {
                tcpb.MaxBufferSize = int.MaxValue;
                tcpb.MaxReceivedMessageSize = int.MaxValue;
                tcpb.ReaderQuotas.MaxStringContentLength = int.MaxValue;
                tcpb.ReaderQuotas.MaxArrayLength = int.MaxValue;
            }

            var ctx = new InstanceContext(_callbacks);
            _factory = new DuplexChannelFactory<IX1MCPSearchManager>(ctx, binding, address);
            _channel = _factory.CreateChannel();
            return _channel;
        }

        public void Dispose()
        {
            try
            {
                if (_channel is ICommunicationObject co)
                {
                    try
                    {
                        co.Close(TimeSpan.FromSeconds(2));
                    }
                    catch
                    {
                        co.Abort();
                    }
                }
            }
            catch
            {
                // ignore
            }

            _channel = null;
            try
            {
                _factory?.Close(TimeSpan.FromSeconds(2));
            }
            catch
            {
                _factory?.Abort();
            }
            _factory = null;
        }
    }
}
