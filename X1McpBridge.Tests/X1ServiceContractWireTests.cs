// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using X1.Service;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// XS-1672: --smoke-wcf only exercises CreateSearchSession/SetSearchTerms/the duplex
    /// results-changed callback/DestroySearchSession against the real X1ServiceHost.exe. These
    /// tests cover the remaining call paths that were previously untested by anything: GetContent,
    /// ExportHtml, AddTags/RemoveTags/ClearTags, GeneratePreview, GetDataSourcesInfo, GetSchemaFields.
    /// Each drives a real duplex WCF channel — built the same way X1MCPSearchConnection/
    /// X1MCPServiceConnection do (NetNamedPipeBinding + DuplexChannelFactory) — against a
    /// FakeSearchManager/FakeService self-hosted in this test process, proving the vendored
    /// contract/DTO shapes in X1ServiceContracts.cs actually round-trip over a real WCF channel.
    /// </summary>
    [TestFixture]
    public class X1ServiceContractWireTests
    {
        private static IX1MCPSearchManager ConnectSearchManager(string address, SearchManagerCallbacks callbacks)
        {
            var binding = new NetNamedPipeBinding(NetNamedPipeSecurityMode.None);
            var factory = new DuplexChannelFactory<IX1MCPSearchManager>(new InstanceContext(callbacks), binding, new EndpointAddress(address));
            return factory.CreateChannel();
        }

        private static IX1MCPService ConnectService(string address, X1MCPServiceCallbacks callbacks)
        {
            var binding = new NetNamedPipeBinding(NetNamedPipeSecurityMode.None);
            var factory = new DuplexChannelFactory<IX1MCPService>(new InstanceContext(callbacks), binding, new EndpointAddress(address));
            return factory.CreateChannel();
        }

        [Test]
        public async Task GetContent_RoundTripsThroughRealWcfChannel()
        {
            var fake = new FakeSearchManager();
            fake.OnGetContent = (table, uri, outputFile) => FakeSearchManager.FireContentReady(outputFile, "ok");

            using (var host = new FakeServiceHost<IX1MCPSearchManager>(fake))
            {
                var callbacks = new SearchManagerCallbacks();
                var ch = ConnectSearchManager(host.Address, callbacks);

                var tcs = callbacks.ExpectContent("C:\\fake\\output.txt");
                ch.GetContent("Files", "files://some/uri", "C:\\fake\\output.txt");
                var result = await callbacks.WaitContentAsync(tcs, 5000);

                Assert.That(result.Success, Is.True);
                Assert.That(result.OutputFile, Is.EqualTo("C:\\fake\\output.txt"));
            }
        }

        [Test]
        public async Task ExportHtml_RoundTripsThroughRealWcfChannel()
        {
            var fake = new FakeSearchManager();
            fake.OnExportHtml = (table, uri, outputFile) => FakeSearchManager.FireExportHtmlReady(outputFile, "ok");

            using (var host = new FakeServiceHost<IX1MCPSearchManager>(fake))
            {
                var callbacks = new SearchManagerCallbacks();
                var ch = ConnectSearchManager(host.Address, callbacks);

                var tcs = callbacks.ExpectExportHtml("C:\\fake\\export.html");
                ch.ExportHtml("Files", "files://some/uri", "C:\\fake\\export.html");
                var result = await callbacks.WaitExportHtmlAsync(tcs, 5000);

                Assert.That(result.Success, Is.True);
                Assert.That(result.OutputFile, Is.EqualTo("C:\\fake\\export.html"));
            }
        }

        [Test]
        public async Task AddTags_RoundTripsThroughRealWcfChannel()
        {
            var fake = new FakeSearchManager();
            fake.OnAddTags = (table, uris, tags) => FakeSearchManager.FireTagsAdded(uris.Length);

            using (var host = new FakeServiceHost<IX1MCPSearchManager>(fake))
            {
                var callbacks = new SearchManagerCallbacks();
                var ch = ConnectSearchManager(host.Address, callbacks);

                var tcs = callbacks.ExpectTagsAdded();
                ch.AddTags("Files", new[] { "files://a", "files://b" }, new[] { "important" });
                int count = await callbacks.WaitTagOpAsync(tcs, 5000);

                Assert.That(count, Is.EqualTo(2));
            }
        }

        [Test]
        public async Task RemoveTags_RoundTripsThroughRealWcfChannel()
        {
            var fake = new FakeSearchManager();
            fake.OnRemoveTags = (table, uris, tags) => FakeSearchManager.FireTagsRemoved(uris.Length);

            using (var host = new FakeServiceHost<IX1MCPSearchManager>(fake))
            {
                var callbacks = new SearchManagerCallbacks();
                var ch = ConnectSearchManager(host.Address, callbacks);

                var tcs = callbacks.ExpectTagsRemoved();
                ch.RemoveTags("Files", new[] { "files://a" }, new[] { "important" });
                int count = await callbacks.WaitTagOpAsync(tcs, 5000);

                Assert.That(count, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task ClearTags_RoundTripsThroughRealWcfChannel()
        {
            var fake = new FakeSearchManager();
            fake.OnClearTags = (table, uris) => FakeSearchManager.FireTagsCleared(uris.Length);

            using (var host = new FakeServiceHost<IX1MCPSearchManager>(fake))
            {
                var callbacks = new SearchManagerCallbacks();
                var ch = ConnectSearchManager(host.Address, callbacks);

                var tcs = callbacks.ExpectTagsCleared();
                ch.ClearTags("Files", new[] { "files://a", "files://b", "files://c" });
                int count = await callbacks.WaitTagOpAsync(tcs, 5000);

                Assert.That(count, Is.EqualTo(3));
            }
        }

        [Test]
        public async Task GeneratePreview_RoundTripsThroughRealWcfChannel()
        {
            var fake = new FakeSearchManager();
            fake.OnGeneratePreview = (table, uri, isForOpen, additionalData) =>
                FakeSearchManager.FirePreviewReady(uri, "<html>preview</html>", isForOpen, null, additionalData);

            using (var host = new FakeServiceHost<IX1MCPSearchManager>(fake))
            {
                var callbacks = new SearchManagerCallbacks();
                var ch = ConnectSearchManager(host.Address, callbacks);

                string key = callbacks.ExpectPreview("files://some/uri");
                ch.GeneratePreview("Files", "files://some/uri", false, null);
                var result = await callbacks.WaitPreviewAsync(key, 5000);

                Assert.That(result.Error, Is.Null.Or.Empty);
                Assert.That(result.Preview, Is.EqualTo("<html>preview</html>"));
            }
        }

        [Test]
        public async Task GetDataSourcesInfo_RoundTripsThroughRealWcfChannel()
        {
            var fake = new FakeService
            {
                DataSourcesToReturn = new[]
                {
                    new ConfiguredDataSourceInfo { scannerName = "Files", totalCount = 42, isScanning = false }
                }
            };

            using (var host = new FakeServiceHost<IX1MCPService>(fake))
            {
                var callbacks = new X1MCPServiceCallbacks();
                var ch = ConnectService(host.Address, callbacks);

                var tcs = callbacks.ExpectDataSourcesInfo();
                ch.GetDataSourcesInfo();
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));

                Assert.That(completed, Is.SameAs(tcs.Task), "GetDataSourcesInfo callback did not arrive within 5s");
                Assert.That(tcs.Task.Result.Length, Is.EqualTo(1));
                Assert.That(tcs.Task.Result[0].scannerName, Is.EqualTo("Files"));
                Assert.That(tcs.Task.Result[0].totalCount, Is.EqualTo(42));
            }
        }

        [Test]
        public void GetSchemaFields_RoundTripsThroughRealWcfChannel()
        {
            var fake = new FakeService
            {
                SchemaFieldsToReturn = new[]
                {
                    new X1FieldInfo { Name = "subject", DisplayName = "Subject", FieldType = X1FieldType.String, Flags = X1FieldFlags.Indexed }
                }
            };

            using (var host = new FakeServiceHost<IX1MCPService>(fake))
            {
                var callbacks = new X1MCPServiceCallbacks();
                var ch = ConnectService(host.Address, callbacks);

                X1FieldInfo[] fields = ch.GetSchemaFields("MSMail");

                Assert.That(fields.Length, Is.EqualTo(1));
                Assert.That(fields[0].Name, Is.EqualTo("subject"));
                Assert.That(fields[0].FieldType, Is.EqualTo(X1FieldType.String));
                Assert.That(fields[0].Flags, Is.EqualTo(X1FieldFlags.Indexed));
            }
        }

        // XS-1676/XS-1685: proves the newer IX1MCPService members actually (de)serialize over a
        // real WCF channel (not just that they compile against the vendored interface) - the
        // full-suite-entitled and files-only-tier IsLicensed() responses, and ReportClientInfo.

        [Test]
        public void IsLicensed_RoundTripsThroughRealWcfChannel()
        {
            var fake = new FakeService { IsLicensedToReturn = true };

            using (var host = new FakeServiceHost<IX1MCPService>(fake))
            {
                var callbacks = new X1MCPServiceCallbacks();
                var ch = ConnectService(host.Address, callbacks);

                Assert.That(ch.IsLicensed(), Is.True);
            }
        }

        [Test]
        public void IsLicensed_FilesOnlyTier_RoundTripsFalse()
        {
            var fake = new FakeService { IsLicensedToReturn = false };

            using (var host = new FakeServiceHost<IX1MCPService>(fake))
            {
                var callbacks = new X1MCPServiceCallbacks();
                var ch = ConnectService(host.Address, callbacks);

                Assert.That(ch.IsLicensed(), Is.False);
            }
        }

        [Test]
        public void ReportClientInfo_RoundTripsThroughRealWcfChannel()
        {
            var fake = new FakeService();

            using (var host = new FakeServiceHost<IX1MCPService>(fake))
            {
                var callbacks = new X1MCPServiceCallbacks();
                var ch = ConnectService(host.Address, callbacks);

                ch.ReportClientInfo("Claude Code", "2.1.222.0");

                // One-way call - give it a moment to land before asserting.
                Thread.Sleep(200);
                Assert.That(fake.ReportedClientName, Is.EqualTo("Claude Code"));
                Assert.That(fake.ReportedClientVersion, Is.EqualTo("2.1.222.0"));
            }
        }

        // XS-1698/XS-1701: proves OnShutdown - the new callback member wired up for XS-1701 -
        // actually (de)serializes over a real WCF channel, not just that it compiles against the
        // vendored interface.
        [Test]
        public void OnShutdown_RoundTripsThroughRealWcfChannel()
        {
            var fake = new FakeService();

            using (var host = new FakeServiceHost<IX1MCPService>(fake))
            {
                bool received = false;
                var callbacks = new X1MCPServiceCallbacks(() => received = true);
                var ch = ConnectService(host.Address, callbacks);

                ch.Shutdown();

                // One-way call - give it a moment to land before asserting.
                Thread.Sleep(200);
                Assert.That(received, Is.True, "OnShutdown callback did not arrive within 200ms");
            }
        }
    }
}
