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
using System.Security.Principal;

namespace X1.McpBridge
{
    /// <summary>
    /// XS-1672: vendored replacement for X1.Common.WCF.WcfUtils' binding/endpoint-address
    /// helpers, since the bridge no longer references X1.Common (proprietary, closed-source).
    /// Kept local to X1McpBridge, mirroring X1MCPWcfUtils.cs's existing "kept local... since
    /// that file is owned by the X1Service team" precedent (see XS-1609 plan).
    ///
    /// Only the two parameters ever passed at any call site in this project are ported
    /// (useEncryption/usePortSharing) - WcfUtils' other optional knobs (custom timeouts,
    /// maxConnections, listenBacklog, etc.) are never exercised here.
    ///
    /// Buffer/message-size limits are hardcoded to 640 * 1024 - the X1Pro-flavor values of
    /// X1.Common.Settings.MAX_BUFFER_SIZE / MAX_WCF_STRING_SIZE. This connector's csproj has no
    /// build configuration that produces anything but the X1Pro flavor of X1.Common.dll, so
    /// these are correct as constants rather than as a ported Settings lookup. (Note:
    /// X1MCPSearchConnection overrides these limits to int.MaxValue immediately after binding
    /// creation regardless, so this default only actually matters for X1MCPServiceConnection's
    /// binding, which does not override it.)
    /// </summary>
    internal static class X1McpWcfBindingFactory
    {
        private const int MaxBufferAndStringSize = 640 * 1024;

        public static Binding CreateNamedPipeBinding(bool useEncryption)
        {
            var binding = new NetNamedPipeBinding(NetNamedPipeSecurityMode.Transport)
            {
                MaxBufferSize = MaxBufferAndStringSize,
                MaxReceivedMessageSize = MaxBufferAndStringSize,
                ReceiveTimeout = TimeSpan.MaxValue,
                SendTimeout = TimeSpan.MaxValue
            };
            binding.ReaderQuotas.MaxStringContentLength = MaxBufferAndStringSize;

            if (!useEncryption)
            {
                binding.Security.Transport.ProtectionLevel = System.Net.Security.ProtectionLevel.None;
                binding.Security.Mode = NetNamedPipeSecurityMode.None;
            }

            return binding;
        }

        public static Binding CreateTcpBinding(bool useEncryption, bool usePortSharing)
        {
            var binding = new NetTcpBinding
            {
                MaxBufferSize = MaxBufferAndStringSize,
                MaxReceivedMessageSize = MaxBufferAndStringSize,
                OpenTimeout = TimeSpan.FromMinutes(1),
                SendTimeout = TimeSpan.MaxValue,
                CloseTimeout = TimeSpan.FromMinutes(1),
                ReceiveTimeout = TimeSpan.MaxValue,
                PortSharingEnabled = usePortSharing,
                TransferMode = TransferMode.Buffered
            };
            binding.ReaderQuotas.MaxStringContentLength = MaxBufferAndStringSize;

            if (!useEncryption)
            {
                binding.Security.Transport.ProtectionLevel = System.Net.Security.ProtectionLevel.None;
                binding.Security.Mode = SecurityMode.None;
            }

            return binding;
        }

        /// <summary>
        /// XS-1672: vendored replacement for WcfUtils.CreateEndpointAddressForCurrentUser, which
        /// internally called X1.Common.Utils.UserNameUtils.GetCurrentUserName()/
        /// SplitUserAndDomain() - both ported inline below rather than as a separate helper,
        /// since neither is used anywhere else in this project.
        /// </summary>
        public static EndpointAddress CreateEndpointAddressForCurrentUser(string endpoint)
        {
            string currentUser = WindowsIdentity.GetCurrent().Name;

            // Matches the original's exact (inconsistent) casing behavior: the split
            // user/domain are lowercased, but the no-backslash fallback uses the original,
            // non-lowercased currentUser - not "fixed" here, just reproduced.
            string[] parts = currentUser.ToLowerInvariant().Split('\\');
            EndpointIdentity identity = parts.Length == 2
                ? EndpointIdentity.CreateUpnIdentity(string.Format("{0}@{1}", parts[1], parts[0]))
                : EndpointIdentity.CreateUpnIdentity(currentUser);

            return new EndpointAddress(new Uri(endpoint), identity, new AddressHeader[0]);
        }
    }
}
