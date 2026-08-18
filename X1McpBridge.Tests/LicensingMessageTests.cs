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
    /// <summary>
    /// XS-1662 acceptance criterion ("Messaging"): every user-facing licensing message must carry only
    /// the landing-page URL and never a hard-coded email. This guards all message sites in one place so
    /// a future edit that inlines an email address or drops the URL fails fast here rather than shipping.
    /// </summary>
    [TestFixture]
    public class LicensingMessageTests
    {
        private static void AssertRoutesToLandingPageOnly(string message)
        {
            Assert.That(message, Does.Contain(BridgeConstants.McpLandingPageUrl),
                "licensing message must point users to the landing page");
            Assert.That(message, Does.Not.Contain("@"),
                "licensing message must not contain a hard-coded email address");
        }

        [Test]
        public void FilesOnlyTableRejection_NamesTable_RoutesToLandingPageOnly()
        {
            var m = BridgeConstants.FilesOnlyTableRejection("Teams");
            Assert.That(m, Does.Contain("Teams"));
            AssertRoutesToLandingPageOnly(m);
        }

        [Test]
        public void ArbitraryFileFilesOnlyRejection_RoutesToLandingPageOnly()
        {
            AssertRoutesToLandingPageOnly(BridgeConstants.ArbitraryFileFilesOnlyRejection());
        }

        [Test]
        public void NotLicensedForMcp_RoutesToLandingPageOnly()
        {
            AssertRoutesToLandingPageOnly(BridgeConstants.NotLicensedForMcp);
        }

        [Test]
        public void BuildArbitraryFileLicenseError_Payload_RoutesToLandingPageOnly()
        {
            var error = SearchBridge.BuildArbitraryFileLicenseError("extract_file");
            AssertRoutesToLandingPageOnly(error.Value<string>("error"));
        }

        [Test]
        public void FilesOnlyLicenseException_Message_RoutesToLandingPageOnly()
        {
            var ex = new X1McpFilesOnlyLicenseException("Teams");
            AssertRoutesToLandingPageOnly(ex.Message);
        }

        [Test]
        public void UnlicensedException_Message_RoutesToLandingPageOnly()
        {
            var ex = new X1McpUnlicensedException();
            AssertRoutesToLandingPageOnly(ex.Message);
        }

        [Test]
        public void FirstUseFilesOnlyBanner_RoutesToLandingPageOnly()
        {
            AssertRoutesToLandingPageOnly(BridgeConstants.FirstUseFilesOnlyBanner);
        }

        [Test]
        public void FirstUseFullSuiteBanner_RoutesToLandingPageOnly()
        {
            AssertRoutesToLandingPageOnly(BridgeConstants.FirstUseFullSuiteBanner);
        }
    }
}
