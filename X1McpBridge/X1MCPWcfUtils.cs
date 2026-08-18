// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;

namespace X1.McpBridge
{
    /// <summary>
    /// XS-1609: endpoint-naming helpers for the dedicated MCP-only WCF interfaces
    /// (IX1MCPService / IX1MCPSearchManager). Mirrors X1.Common.WCF.WcfUtils'
    /// ServiceHostEndpointName/SearchManagerEndpointName exactly (same host/port/pipe
    /// branching), but building the "X1MCPService_"/"X1MCPSearchManager_" prefixes that
    /// distinguish these from the desktop UI's own IX1Service/IX1SearchManager endpoints.
    /// Kept local to X1McpBridge rather than added to X1.Common/WcfUtils.cs, since that file
    /// is owned by the X1Service team — see XS-1609 plan.
    ///
    /// XS-1672: no longer references X1.Common (proprietary, closed-source) at all. The
    /// UrlEncode call below is vendored inline (see UrlEncodeServiceId), and the
    /// Settings.PRODUCT_LINE == X1ProductLine.X1Virtual branch that used to gate the net.tcp
    /// fallback has been deleted outright: this connector's csproj has no build configuration
    /// that produces anything but the X1Pro flavor of X1.Common.dll, and a repo-wide grep found
    /// no other reference to a Citrix/X1Virtual MCP connector build.
    /// </summary>
    internal static class X1MCPWcfUtils
    {
        public static string MCPServiceEndpointName(string serviceId, string host = "", int? netTcpPort = null)
        {
            serviceId = UrlEncodeServiceId(serviceId);

            if (netTcpPort.HasValue)
                return string.Format("net.tcp://{0}:{2}/X1MCPService_{1}", string.IsNullOrEmpty(host) ? "localhost" : host, serviceId, netTcpPort);
            if (!string.IsNullOrEmpty(host))
                return string.Format("net.tcp://{0}/X1MCPService_{1}", string.IsNullOrEmpty(host) ? "localhost" : host, serviceId);
            return "net.pipe://localhost/X1MCPService_" + serviceId;
        }

        public static string MCPSearchManagerEndpointName(string serviceId, string host = "", int? netTcpPort = null)
        {
            serviceId = UrlEncodeServiceId(serviceId);

            if (netTcpPort.HasValue)
                return string.Format("net.tcp://{0}:{2}/X1MCPSearchManager_{1}", string.IsNullOrEmpty(host) ? "localhost" : host, serviceId, netTcpPort);
            if (!string.IsNullOrEmpty(host))
                return string.Format("net.tcp://{0}/X1MCPSearchManager_{1}", string.IsNullOrEmpty(host) ? "localhost" : host, serviceId);
            return "net.pipe://localhost/X1MCPSearchManager_" + serviceId;
        }

        /// <summary>
        /// XS-1672: vendored replacement for X1.Common.Utils.HttpHelper.UrlEncode. Not a naive
        /// Uri.EscapeDataString swap - HttpHelper.UrlEncode calls System.Web.HttpUtility.UrlEncode,
        /// which encodes a space as '+', not '%20'. This string becomes part of the WCF endpoint
        /// address, and the closed-source X1ServiceHost.exe independently recomputes the same
        /// name using the original HttpHelper.UrlEncode - an ordinary Windows username containing
        /// a space (e.g. "Stewart Robinson") would otherwise produce a mismatched endpoint name.
        /// </summary>
        private static string UrlEncodeServiceId(string serviceId)
        {
            return Uri.EscapeDataString(serviceId).Replace("%20", "+");
        }
    }
}
