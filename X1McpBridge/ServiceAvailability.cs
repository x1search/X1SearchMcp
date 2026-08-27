// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.ServiceModel;

namespace X1.McpBridge
{
    /// <summary>
    /// XS-1719: turns a WCF transport failure into the connector's one friendly "X1 isn't running"
    /// message, instead of letting the raw exception text reach the calling agent.
    ///
    /// The leak this exists to close: <see cref="X1MCPSearchConnection.GetChannel"/> hands back a
    /// *lazy* proxy that never throws on its own (see WcfChannelFaultTests) - the connection is only
    /// attempted when the first operation is invoked on it, deep inside SearchBridge/ActionBridge.
    /// With X1ServiceHost down that throws <c>EndpointNotFoundException</c> ("There was no endpoint
    /// listening at net.pipe://localhost/X1MCPSearchManager_&lt;user&gt; ... See InnerException, if
    /// present, for more details"), which nothing between there and the JSON-RPC error response
    /// translated. An agent shown that text has no idea the fix is "start X1 Search", and the advice
    /// it does contain ("check the address or SOAP action", "see InnerException") is addressed to a
    /// WCF developer, not to the caller.
    ///
    /// Deliberately applied at the few places an exception becomes *caller-visible text* rather than
    /// at each throw site: the bridge reaches the service from nine tools over two connections, and a
    /// per-call-site translation would have to be remembered for the tenth. See the call sites in
    /// McpServer.ProcessMessage / McpServer.ReadResource / SearchBridge.SearchMultiTableAsync /
    /// ActionBridge.GeneratePreviewAsync - between them they cover every tool.
    ///
    /// The raw exception is never discarded, only demoted: every call site above logs the full
    /// exception, and ProcessMessage additionally keeps <c>ex.ToString()</c> in the JSON-RPC error's
    /// <c>data</c> member. Support still gets the transport detail; the agent gets the actionable line.
    /// </summary>
    internal static class ServiceAvailability
    {
        /// <summary>
        /// Cap on how far <see cref="IsTransportFailure(Exception)"/> will walk an exception chain.
        /// Bounded rather than unbounded because this runs on the error path, where a self-referencing
        /// or pathologically deep chain must not turn a failed tool call into a hang.
        /// </summary>
        private const int MaxChainDepth = 16;

        /// <summary>
        /// True when <paramref name="ex"/> - or anything it wraps - is WCF reporting that it could not
        /// reach or stay connected to X1ServiceHost. Pure (no WCF, no channel, no IO), so the
        /// classification is directly unit-testable without a live service host, in the same spirit as
        /// <see cref="SearchBridge.ThrowIfSessionCreationFailed"/> - see ServiceAvailabilityTests.
        ///
        /// Matches on the <see cref="CommunicationException"/> base rather than an allow-list of
        /// subtypes: the host being down surfaces as <c>EndpointNotFoundException</c> (named pipe not
        /// listening / TCP actively refused), while a host that dies mid-call surfaces as a bare
        /// <c>CommunicationException</c> ("the pipe has been ended") or
        /// <c>CommunicationObjectFaultedException</c>. All of them mean the same thing to the caller,
        /// and an allow-list would silently miss whichever subtype WCF picks next.
        ///
        /// Two subtypes are excluded because they are NOT "X1 isn't running":
        /// <list type="bullet">
        /// <item><see cref="FaultException"/> (and <c>FaultException&lt;T&gt;</c>) - the service was
        /// reached and answered with a server-authored fault. That message describes the actual
        /// problem and must not be replaced with a guess about the host being down.</item>
        /// <item><see cref="ProtocolException"/> - the endpoint is there but the two sides disagree on
        /// the message version/binding. Telling someone to restart X1ServiceHost would send them
        /// chasing the wrong thing; a version/binding mismatch needs its own diagnosis.</item>
        /// </list>
        /// </summary>
        internal static bool IsTransportFailure(Exception ex) => IsTransportFailure(ex, MaxChainDepth);

        private static bool IsTransportFailure(Exception ex, int budget)
        {
            while (ex != null && budget-- > 0)
            {
                // Task-based call sites (SearchAsync/GeneratePreviewAsync and friends) can surface a
                // faulted task's exception wrapped this way; the transport fault is then one of the
                // inner exceptions rather than the head of a single .InnerException chain.
                if (ex is AggregateException aggregate)
                {
                    foreach (Exception inner in aggregate.InnerExceptions)
                    {
                        if (IsTransportFailure(inner, budget))
                            return true;
                    }
                    return false;
                }

                if (ex is CommunicationException && !(ex is FaultException) && !(ex is ProtocolException))
                    return true;

                ex = ex.InnerException;
            }

            return false;
        }

        /// <summary>
        /// The text to show the caller for <paramref name="ex"/>: the shared down-state message when
        /// this is a transport failure, otherwise the exception's own message unchanged. Every
        /// non-transport error - bad table name, files-only license rejection, timeout, argument
        /// validation - keeps the specific wording it already had.
        /// </summary>
        internal static string DescribeForCaller(Exception ex)
        {
            if (ex == null)
                return null;

            return IsTransportFailure(ex)
                ? BridgeConstants.ServiceUnavailable
                : ex.Message;
        }
    }
}
