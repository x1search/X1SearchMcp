// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.ServiceModel;
using System.Threading.Tasks;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// XS-1719: with X1ServiceHost down, x1_search surfaced the raw WCF transport exception to the
    /// calling agent while x1_list_sources degraded gracefully. <see cref="ServiceAvailability"/> is
    /// the shared classifier that closes that gap.
    ///
    /// Tested directly rather than through a live WCF round-trip for the same reason
    /// SearchBridgeSessionGateTests gives: X1MCPSearchConnection's endpoint address is derived from
    /// the current Windows username, with no seam to redirect it at a FakeServiceHost, so
    /// "is X1ServiceHost down?" isn't a state a unit test can reliably arrange either way. Extracting
    /// the classification as a pure function is what makes it testable at all - and the exceptions
    /// below are the genuine WCF types, constructed directly, so the shapes under test are real.
    /// </summary>
    [TestFixture]
    public class ServiceAvailabilityTests
    {
        /// <summary>
        /// The verbatim message Anusha reported on XS-1719 (build 1.0.0.34), with the username elided.
        /// Pinned as a literal so this test still describes the original defect years from now.
        /// </summary>
        private const string ReportedNetPipeMessage =
            "There was no endpoint listening at net.pipe://localhost/X1MCPSearchManager_auser that " +
            "could accept the message. This is often caused by an incorrect address or SOAP action. " +
            "See InnerException, if present, for more details.";

        // ── The reported defect ──────────────────────────────────────────────────

        [Test]
        public void ReportedNetPipeException_IsTranslatedToTheFriendlyMessage()
        {
            var ex = new EndpointNotFoundException(ReportedNetPipeMessage);

            Assert.That(ServiceAvailability.DescribeForCaller(ex),
                Is.EqualTo(BridgeConstants.ServiceUnavailable));
        }

        [Test]
        public void ReportedNetPipeException_FriendlyMessageLeaksNoTransportDetail()
        {
            string described = ServiceAvailability.DescribeForCaller(
                new EndpointNotFoundException(ReportedNetPipeMessage));

            Assert.That(described, Does.Not.Contain("net.pipe"));
            Assert.That(described, Does.Not.Contain("SOAP"));
            Assert.That(described, Does.Not.Contain("InnerException"));
            Assert.That(described, Does.Contain("X1ServiceHost"),
                "the message must name the thing the user has to start");
        }

        // ── Transport failures: every shape a down/dying host produces ───────────

        [Test]
        public void EndpointNotFound_IsTransportFailure()
        {
            Assert.That(ServiceAvailability.IsTransportFailure(
                new EndpointNotFoundException("no endpoint listening")), Is.True);
        }

        [Test]
        public void BareCommunicationException_IsTransportFailure()
        {
            // What a host that dies mid-call produces: "The pipe has been ended", "The socket
            // connection was aborted".
            Assert.That(ServiceAvailability.IsTransportFailure(
                new CommunicationException("The pipe has been ended. (109, 0x6d)")), Is.True);
        }

        [Test]
        public void CommunicationObjectFaulted_IsTransportFailure()
        {
            Assert.That(ServiceAvailability.IsTransportFailure(
                new CommunicationObjectFaultedException("channel faulted")), Is.True);
        }

        [Test]
        public void CommunicationObjectAborted_IsTransportFailure()
        {
            Assert.That(ServiceAvailability.IsTransportFailure(
                new CommunicationObjectAbortedException("channel aborted")), Is.True);
        }

        [Test]
        public void ServerTooBusy_IsTransportFailure()
        {
            Assert.That(ServiceAvailability.IsTransportFailure(
                new ServerTooBusyException("too busy")), Is.True);
        }

        // ── Not transport failures: these must keep their own message ────────────

        /// <summary>
        /// A FaultException means the service WAS reached and answered. Its message is server-authored
        /// and describes the real problem; replacing it with "X1 may be unavailable" would send the
        /// caller after a service that is demonstrably running.
        /// </summary>
        [Test]
        public void FaultException_IsNotTransportFailure_AndKeepsItsOwnMessage()
        {
            var ex = new FaultException("Scanner 'Teams' is mid-rebuild.");

            Assert.That(ServiceAvailability.IsTransportFailure(ex), Is.False);
            Assert.That(ServiceAvailability.DescribeForCaller(ex), Is.EqualTo(ex.Message));
        }

        /// <summary>
        /// A binding/message-version mismatch: the endpoint is there. "Restart X1ServiceHost" would be
        /// wrong advice, so this keeps its own (admittedly technical) message.
        /// </summary>
        [Test]
        public void ProtocolException_IsNotTransportFailure_AndKeepsItsOwnMessage()
        {
            var ex = new ProtocolException("content type mismatch");

            Assert.That(ServiceAvailability.IsTransportFailure(ex), Is.False);
            Assert.That(ServiceAvailability.DescribeForCaller(ex), Is.EqualTo(ex.Message));
        }

        /// <summary>
        /// A search that times out against a healthy host is a different condition with different
        /// advice, and TimeoutException is not a CommunicationException — it must pass through.
        /// </summary>
        [Test]
        public void Timeout_IsNotTransportFailure_AndKeepsItsOwnMessage()
        {
            var ex = new TimeoutException("Search timed out after 60000ms.");

            Assert.That(ServiceAvailability.IsTransportFailure(ex), Is.False);
            Assert.That(ServiceAvailability.DescribeForCaller(ex), Is.EqualTo(ex.Message));
        }

        [Test]
        public void ArgumentException_KeepsItsOwnMessage()
        {
            var ex = new ArgumentException("query or filters must supply at least one search term.");

            Assert.That(ServiceAvailability.DescribeForCaller(ex), Is.EqualTo(ex.Message));
        }

        /// <summary>
        /// The files-only licensing rejection (XS-1678) must survive untouched — it routes the user to
        /// the landing page, and masking it as a down-state would lose that.
        /// </summary>
        [Test]
        public void FilesOnlyLicenseException_KeepsItsOwnMessage()
        {
            var ex = new X1McpFilesOnlyLicenseException("Teams");

            Assert.That(ServiceAvailability.IsTransportFailure(ex), Is.False);
            Assert.That(ServiceAvailability.DescribeForCaller(ex), Is.EqualTo(ex.Message));
            Assert.That(ServiceAvailability.DescribeForCaller(ex),
                Does.Contain(BridgeConstants.McpLandingPageUrl));
        }

        // ── Wrapped chains ───────────────────────────────────────────────────────

        /// <summary>
        /// The bridge's own layers re-wrap: the transport fault is often not the head of the chain.
        /// </summary>
        [Test]
        public void TransportFailureNestedInWrapper_IsStillDetected()
        {
            var ex = new InvalidOperationException("Search failed for table 'Files'.",
                new EndpointNotFoundException(ReportedNetPipeMessage));

            Assert.That(ServiceAvailability.IsTransportFailure(ex), Is.True);
            Assert.That(ServiceAvailability.DescribeForCaller(ex),
                Is.EqualTo(BridgeConstants.ServiceUnavailable));
        }

        [Test]
        public void TransportFailureInsideAggregateException_IsStillDetected()
        {
            var ex = new AggregateException(
                new InvalidOperationException("unrelated"),
                new EndpointNotFoundException(ReportedNetPipeMessage));

            Assert.That(ServiceAvailability.IsTransportFailure(ex), Is.True);
        }

        [Test]
        public void AggregateExceptionWithNoTransportFailure_IsNotDetected()
        {
            var ex = new AggregateException(
                new ArgumentException("bad table"),
                new TimeoutException("too slow"));

            Assert.That(ServiceAvailability.IsTransportFailure(ex), Is.False);
        }

        /// <summary>
        /// A faulted Task observed via .Exception hands back an AggregateException — the shape several
        /// of the bridge's async call sites would see.
        /// </summary>
        [Test]
        public void FaultedTaskException_IsDetected()
        {
            var tcs = new TaskCompletionSource<int>();
            tcs.SetException(new EndpointNotFoundException(ReportedNetPipeMessage));

            Assert.That(ServiceAvailability.IsTransportFailure(tcs.Task.Exception), Is.True);
        }

        // ── Guards ───────────────────────────────────────────────────────────────

        [Test]
        public void Null_IsNotTransportFailure_AndDescribesAsNull()
        {
            Assert.That(ServiceAvailability.IsTransportFailure(null), Is.False);
            Assert.That(ServiceAvailability.DescribeForCaller(null), Is.Null);
        }

        /// <summary>
        /// The depth cap exists so a pathologically deep chain can't turn a failed tool call into a
        /// long walk on the error path. Nested AggregateExceptions are the worst case, since each one
        /// fans out rather than stepping linearly.
        /// </summary>
        [Test]
        public void DeeplyNestedChain_TerminatesInsteadOfHanging()
        {
            Exception deep = new InvalidOperationException("leaf");
            for (int i = 0; i < 200; i++)
                deep = new AggregateException("layer " + i, new InvalidOperationException("side", deep));

            Assert.That(() => ServiceAvailability.IsTransportFailure(deep), Throws.Nothing);
            Assert.That(ServiceAvailability.IsTransportFailure(deep), Is.False);
        }

        /// <summary>
        /// A transport fault buried deeper than the walk budget is reported as a non-transport error
        /// rather than being chased indefinitely. Documents the deliberate trade-off: bounded work on
        /// the error path beats exhaustive classification of a chain no real call site produces.
        /// </summary>
        [Test]
        public void TransportFailureBeyondDepthCap_IsNotChased()
        {
            Exception deep = new EndpointNotFoundException(ReportedNetPipeMessage);
            for (int i = 0; i < 50; i++)
                deep = new InvalidOperationException("layer " + i, deep);

            Assert.That(ServiceAvailability.IsTransportFailure(deep), Is.False);
        }

        // ── Message consistency ──────────────────────────────────────────────────

        /// <summary>
        /// The two independent paths to "X1 isn't answering" must read identically: this classifier
        /// (host unreachable) and SearchBridge's session gate (host reachable, returned the 0
        /// "unavailable" sentinel). They are different failures on the wire and the same failure to
        /// the caller — the shared constant is what keeps them from drifting apart.
        /// </summary>
        [Test]
        public void SessionGateAndClassifier_ShareTheSameDownStateSentence()
        {
            var gateException = Assert.Throws<InvalidOperationException>(
                () => SearchBridge.ThrowIfSessionCreationFailed(0, "Files"));

            Assert.That(gateException.Message, Does.Contain(BridgeConstants.ServiceUnavailable));
            Assert.That(ServiceAvailability.DescribeForCaller(new CommunicationException("dead")),
                Is.EqualTo(BridgeConstants.ServiceUnavailable));
        }

        /// <summary>
        /// XS-1662 routes every *licensing* message to the landing page. This is not a licensing
        /// message, and pointing a user whose service is stopped at a marketing page instead of at
        /// "start X1ServiceHost" would be actively unhelpful — asserted so a future tidy-up that
        /// blanket-appends the URL has to make that choice deliberately.
        /// </summary>
        [Test]
        public void ServiceUnavailable_IsActionable_AndNotRoutedToTheLandingPage()
        {
            Assert.That(BridgeConstants.ServiceUnavailable, Does.Contain("X1ServiceHost"));
            Assert.That(BridgeConstants.ServiceUnavailable, Does.Contain("retry"));
            Assert.That(BridgeConstants.ServiceUnavailable,
                Does.Not.Contain(BridgeConstants.McpLandingPageUrl));
        }
    }
}
