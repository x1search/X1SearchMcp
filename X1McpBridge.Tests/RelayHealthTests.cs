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
    /// Covers the relay coexistence matrix. RelayHealth.Decide is deliberately pure - no HTTP, no
    /// Process, no clock - precisely so these cases are testable here rather than only reproducible
    /// by hand on a machine that happens to be in the right state (a stale daemon from another
    /// install, two flavors mid-upgrade, two Windows sessions).
    /// </summary>
    [TestFixture]
    public class RelayHealthTests
    {
        private const string Ours = "1.0.0.9";
        private const string Me = "stewart";

        private static RelayHealth.Snapshot Running(
            string version, string component, int pid = 4242, string user = Me)
        {
            return new RelayHealth.Snapshot
            {
                Reachable = true,
                Version = version,
                Component = component,
                Pid = pid,
                User = user,
            };
        }

        // ── Nothing there ────────────────────────────────────────────────────────

        [Test]
        public void Unreachable_Launches()
        {
            var d = RelayHealth.Decide(RelayHealth.Snapshot.Unreachable(), RelayMode.Host, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.Launch));
            Assert.That(d.Reason, Is.Not.Null.And.Not.Empty);
        }

        // ── The regression this whole file exists for ────────────────────────────

        /// <summary>
        /// The defect being fixed: the old check computed
        /// "matches = version == null || ours == null || version == ours" against a version that was
        /// read from the daemon exe on disk. In a Lean install no daemon exe ships, so ours was
        /// always null, so ANY healthy relay "matched" - including an older leftover daemon that a
        /// surviving logon task keeps restarting. The customer would keep running the net10 build
        /// they had just replaced, driving the OLD bridge from the OLD directory, with no error.
        /// </summary>
        [Test]
        public void LeanProxy_OlderLeftoverDaemon_IsEvictedNotAdopted()
        {
            var d = RelayHealth.Decide(Running("1.0.0.8", RelayHealth.ComponentDaemon), RelayMode.Host, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.EvictThenLaunch));
            Assert.That(d.EvictPid, Is.EqualTo(4242), "must evict by the reported pid, not by name");
        }

        [Test]
        public void OurVersionUnknown_CrossFlavor_StillEvicts()
        {
            // Belt and braces: even if OurRelayVersion regresses to null, an unknown version must
            // never be read as "matches" across flavors.
            var d = RelayHealth.Decide(Running("1.0.0.8", RelayHealth.ComponentDaemon), RelayMode.Host, null, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.EvictThenLaunch));
        }

        // ── Version ordering outranks flavor ─────────────────────────────────────

        [Test]
        public void OlderSameFlavor_Evicts()
        {
            var d = RelayHealth.Decide(Running("1.0.0.8", RelayHealth.ComponentHost), RelayMode.Host, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.EvictThenLaunch));
        }

        [Test]
        public void NewerRelay_IsAdopted_NotKilled()
        {
            // Adopting forward converges; mutual eviction between two installs would ping-pong, and
            // every teardown races the one WCF connection.
            var d = RelayHealth.Decide(Running("1.0.1.0", RelayHealth.ComponentHost), RelayMode.Host, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.Use));
        }

        [Test]
        public void NewerRelay_AdoptedEvenAcrossFlavors()
        {
            var d = RelayHealth.Decide(Running("2.0.0.0", RelayHealth.ComponentDaemon), RelayMode.Host, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.Use));
        }

        // ── Equal version, cross flavor: the daemon wins ─────────────────────────

        [Test]
        public void SameVersionDaemon_AdoptedByLeanProxy()
        {
            // The daemon serves a strict superset of the host's contract, so this is functionally
            // correct and converges in one step instead of flapping.
            var d = RelayHealth.Decide(Running(Ours, RelayHealth.ComponentDaemon), RelayMode.Host, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.Use));
        }

        [Test]
        public void SameVersionHost_EvictedByFullProxy()
        {
            // The reverse is NOT symmetric: a Full install adopting a Lean host would silently lose
            // the GraphQL/Nitro surface it was installed for.
            var d = RelayHealth.Decide(Running(Ours, RelayHealth.ComponentHost), RelayMode.Daemon, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.EvictThenLaunch));
        }

        [Test]
        public void SameVersionSameFlavor_Uses()
        {
            var d = RelayHealth.Decide(Running(Ours, RelayHealth.ComponentHost), RelayMode.Host, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.Use));
        }

        // ── Pre-flavor relays (no component field) ───────────────────────────────

        [Test]
        public void NoComponent_TreatedAsDaemon_UsedByFullProxyAtSameVersion()
        {
            // --host did not exist before the component field did, so a relay that omits it can
            // only be a daemon. Preserves the pre-existing leniency for a Full install.
            var d = RelayHealth.Decide(Running(Ours, null, pid: 0, user: null), RelayMode.Daemon, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.Use));
        }

        [Test]
        public void NoComponentNoVersion_EvictedByLeanProxy_ByName()
        {
            // An ancient daemon reporting neither field: cross-flavor with an uncomparable version,
            // so don't guess - evict. With no pid, eviction must fall back to killing by name, and
            // the name has to be the daemon's.
            var d = RelayHealth.Decide(Running(null, null, pid: 0, user: null), RelayMode.Host, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.EvictThenLaunch));
            Assert.That(d.EvictPid, Is.EqualTo(0));
            Assert.That(d.EvictComponent, Is.EqualTo(RelayHealth.ComponentDaemon));
        }

        [Test]
        public void NoVersion_SameFlavor_IsTolerated()
        {
            var d = RelayHealth.Decide(Running(null, RelayHealth.ComponentHost), RelayMode.Host, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.Use));
        }

        // ── Cross-user ───────────────────────────────────────────────────────────

        [Test]
        public void DifferentWindowsUser_RefusedAndNeverEvicted()
        {
            // Pre-existing hole, closed here: the single-instance mutex is session-scoped but the
            // port is machine-wide, and WCF endpoints are per-user - so another user's relay answers
            // from another user's index. Never adopt it, and never kill it either.
            var d = RelayHealth.Decide(
                Running(Ours, RelayHealth.ComponentHost, user: "someone-else"), RelayMode.Host, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.RefuseWrongUser));
            Assert.That(d.EvictPid, Is.EqualTo(0), "another user's process must never be targeted");
        }

        [Test]
        public void DifferentUser_OutranksVersionMismatch()
        {
            var d = RelayHealth.Decide(
                Running("0.0.0.1", RelayHealth.ComponentDaemon, user: "someone-else"), RelayMode.Host, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.RefuseWrongUser));
        }

        [Test]
        public void UnreportedUser_DoesNotTriggerRefusal()
        {
            var d = RelayHealth.Decide(
                Running(Ours, RelayHealth.ComponentHost, user: null), RelayMode.Host, Ours, Me);
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.Use));
        }

        // ── Parsing ──────────────────────────────────────────────────────────────

        [Test]
        public void Parse_FullBody_ReadsEveryField()
        {
            var snap = RelayHealth.Parse(
                "{\"version\":\"1.0.0.9\",\"component\":\"X1McpBridge\",\"mode\":\"host\"," +
                "\"pid\":1234,\"user\":\"stewart\",\"exePath\":\"C:\\\\x\\\\X1McpBridge.exe\"}");
            Assert.That(snap.Reachable, Is.True);
            Assert.That(snap.Version, Is.EqualTo("1.0.0.9"));
            Assert.That(snap.Component, Is.EqualTo("X1McpBridge"));
            Assert.That(snap.Pid, Is.EqualTo(1234));
            Assert.That(snap.User, Is.EqualTo("stewart"));
            Assert.That(snap.ExePath, Is.EqualTo("C:\\x\\X1McpBridge.exe"));
        }

        [Test]
        public void Parse_LegacyVersionOnlyBody_IsReachableWithUnknownComponent()
        {
            var snap = RelayHealth.Parse("{\"version\":\"1.0.0.8\"}");
            Assert.That(snap.Reachable, Is.True);
            Assert.That(snap.Version, Is.EqualTo("1.0.0.8"));
            Assert.That(snap.Component, Is.Null);
            Assert.That(snap.Pid, Is.EqualTo(0));
        }

        [Test]
        public void Parse_MalformedBody_IsHealthyButUnidentified()
        {
            // A body we can't read must not be reported as unhealthy, or this check would newly
            // break against a relay that answers fine.
            var snap = RelayHealth.Parse("not json at all");
            Assert.That(snap.Reachable, Is.True);
            Assert.That(snap.Version, Is.Null);
            Assert.That(snap.Component, Is.Null);
        }

        [Test]
        public void Parse_BlankFields_BecomeNullNotEmpty()
        {
            var snap = RelayHealth.Parse("{\"version\":\"\",\"component\":\"  \",\"pid\":0}");
            Assert.That(snap.Version, Is.Null);
            Assert.That(snap.Component, Is.Null);
            Assert.That(snap.Pid, Is.EqualTo(0));
        }

        // ── Version comparison ───────────────────────────────────────────────────

        [TestCase("1.0.0.9", "1.0.0.9", 0)]
        [TestCase("1.0.0.8", "1.0.0.9", -1)]
        [TestCase("1.0.1.0", "1.0.0.9", 1)]
        [TestCase("2.0.0.0", "1.9.9.9", 1)]
        public void TryCompareVersions_ComparesNumerically(string running, string ours, int expectedSign)
        {
            int result;
            Assert.That(RelayHealth.TryCompareVersions(running, ours, out result), Is.True);
            Assert.That(System.Math.Sign(result), Is.EqualTo(expectedSign));
        }

        [TestCase(null, "1.0.0.9")]
        [TestCase("1.0.0.9", null)]
        [TestCase("", "1.0.0.9")]
        public void TryCompareVersions_MissingSide_IsIndeterminate(string running, string ours)
        {
            int result;
            Assert.That(RelayHealth.TryCompareVersions(running, ours, out result), Is.False,
                "an unknown version must be reported as unknown, never silently as equal");
        }

        [Test]
        public void TryCompareVersions_UnparseableButIdentical_ComparesEqual()
        {
            int result;
            Assert.That(RelayHealth.TryCompareVersions("dev-build", "dev-build", out result), Is.True);
            Assert.That(result, Is.EqualTo(0));
        }

        // ── The two flavors' /health bodies must not drift ───────────────────────

        [Test]
        public void BuildBody_RoundTripsThroughParse()
        {
            var snap = RelayHealth.Parse(
                RelayHealth.BuildBody("1.0.0.9", RelayHealth.ComponentHost, 99, "stewart", @"C:\x\X1McpBridge.exe"));
            Assert.That(snap.Version, Is.EqualTo("1.0.0.9"));
            Assert.That(snap.Component, Is.EqualTo(RelayHealth.ComponentHost));
            Assert.That(snap.Pid, Is.EqualTo(99));
            Assert.That(snap.User, Is.EqualTo("stewart"));
        }

        [Test]
        public void BuildBody_SatisfiesTheProxysOwnHealthCheck()
        {
            // ProxyMode.CheckHealthAsync only needs a parseable version out of this; assert that
            // directly so the host's body can't drift away from what the shim reads.
            var snap = RelayHealth.Parse(
                RelayHealth.BuildBody("1.0.0.9", RelayHealth.ComponentHost, 1, "u", "p"));
            var d = RelayHealth.Decide(snap, RelayMode.Host, "1.0.0.9", "u");
            Assert.That(d.Action, Is.EqualTo(RelayHealth.Action.Use));
        }

        [Test]
        public void BuildBody_ModeTracksComponent()
        {
            Assert.That(RelayHealth.BuildBody("1", RelayHealth.ComponentHost, 1, "u", "p"),
                Does.Contain("\"mode\":\"host\""));
            Assert.That(RelayHealth.BuildBody("1", RelayHealth.ComponentDaemon, 1, "u", "p"),
                Does.Contain("\"mode\":\"daemon\""));
        }
    }
}
