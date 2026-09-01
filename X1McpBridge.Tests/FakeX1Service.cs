// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.ServiceModel;
using X1.Service;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// XS-1672: lightweight in-proc implementations of IX1MCPSearchManager / IX1MCPService,
    /// self-hosted over a NetNamedPipeBinding so X1ServiceContractWireTests can drive the real
    /// client-side connection classes (X1MCPSearchConnection / SearchManagerCallbacks style
    /// duplex channel) against something other than the real, closed-source X1ServiceHost.exe.
    /// This proves the vendored contract/DTO shapes in X1ServiceContracts.cs actually
    /// (de)serialize correctly over the wire for the call paths --smoke-wcf doesn't reach
    /// (GetContent, ExportHtml, AddTags/RemoveTags/ClearTags, GeneratePreview, GetDataSourcesInfo,
    /// GetSchemaFields) — not just that the code compiles against them.
    /// </summary>
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Multiple)]
    internal sealed class FakeSearchManager : IX1MCPSearchManager
    {
        public Action<string, string, string> OnGetContent;
        public Action<string, string, string> OnExportHtml;
        public Action<string, string[], string[]> OnAddTags;
        public Action<string, string[], string[]> OnRemoveTags;
        public Action<string, string[]> OnClearTags;
        public Action<string, string, bool, string> OnGeneratePreview;

        // XS-1678: settable so tests can simulate the server's licensing-gate sentinels - -1
        // (table not allowed on this license tier) and 0 (service unavailable) - alongside the
        // default "always succeeds" behavior every other existing test relies on.
        public Func<string[], bool, bool, int> CreateSearchSessionResult = (tables, progenitorSearch, getKeywordStats) => 1;

        private static IX1SearchManagerCallbacks Callback =>
            OperationContext.Current.GetCallbackChannel<IX1SearchManagerCallbacks>();

        public int CreateSearchSession(string[] tables, bool progenitorSearch, bool getKeywordStats) =>
            CreateSearchSessionResult(tables, progenitorSearch, getKeywordStats);
        public void SetTables(int sessionID, string[] tables) { }
        public void SetSearchTerms(int sessionID, SearchTerm[] searchTerms, SortColumn[] sortColumns, Column[] displayColumns, MergeColumn[] mergeColumns, int pageSize) { }
        public void GetSearchResults(int sessionID, int requestID, int startRow, int numRows) { }
        public void ResultChangesOutdated(int sessionID, int uiSequence, int serviceSequence) { }
        public void DestroySearchSession(int sessionID) { }
        public void CancelPreview(string uri, string additionalData = null) { }
        // XS-1746: settable so a test can hand back a realistic flat field array (in particular an
        // "istatus" pair), which is what the connector reads to explain why an item has no text. The
        // default keeps the previous "returns nothing" behavior every existing test relies on.
        public Func<string, string, string[]> GetItemInternalResult = (table, uri) => new string[0];

        public string[] GetItemInternal(string table, string uri) => GetItemInternalResult(table, uri);
        public void Serialize(string table, string uri, string fileName) { }
        public void ExtractTextFromFile(string file, string outputFile) { }
        public void ExportHtmlFromFile(string file, string outputFile) { }

        public void GeneratePreview(string table, string uri, bool isForOpen, string addtionalData)
        {
            OnGeneratePreview?.Invoke(table, uri, isForOpen, addtionalData);
        }

        public void GetContent(string table, string uri, string outputFile)
        {
            OnGetContent?.Invoke(table, uri, outputFile);
        }

        public void ExportHtml(string table, string uri, string outputFile)
        {
            OnExportHtml?.Invoke(table, uri, outputFile);
        }

        public void AddTags(string table, string[] uris, string[] tags)
        {
            OnAddTags?.Invoke(table, uris, tags);
        }

        public void RemoveTags(string table, string[] uris, string[] tags)
        {
            OnRemoveTags?.Invoke(table, uris, tags);
        }

        public void ClearTags(string table, string[] uris)
        {
            OnClearTags?.Invoke(table, uris);
        }

        /// <summary>Fires OnContentReady/OnExportHtmlReady from a test's OnGetContent/OnExportHtml hook.</summary>
        public static void FireContentReady(string outputFile, string extractionResult) =>
            Callback.OnContentReady(outputFile, extractionResult);

        public static void FireExportHtmlReady(string outputFile, string extractionResult) =>
            Callback.OnExportHtmlReady(outputFile, extractionResult);

        public static void FireTagsAdded(int count) => Callback.OnTagsAdded(count);
        public static void FireTagsRemoved(int count) => Callback.OnTagsRemoved(count);
        public static void FireTagsCleared(int count) => Callback.OnTagsCleared(count);

        public static void FirePreviewReady(string uri, string preview, bool isForOpen, string error, string additionalData) =>
            Callback.OnPreviewReady(uri, preview, isForOpen, error, additionalData, elapsedTime: 0);
    }

    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Multiple)]
    internal sealed class FakeService : IX1MCPService
    {
        public ConfiguredDataSourceInfo[] DataSourcesToReturn = new ConfiguredDataSourceInfo[0];
        public X1FieldInfo[] SchemaFieldsToReturn = new X1FieldInfo[0];
        public bool IsLicensedToReturn = true;
        public string ReportedClientName;
        public string ReportedClientVersion;

        private static IX1MCPServiceCallbacks Callback =>
            OperationContext.Current.GetCallbackChannel<IX1MCPServiceCallbacks>();

        public string Connect() => "1.0.0.0";
        public string GetX1ServiceHostStatus() => "OK";
        public void Disconnect(bool shutdown) { }

        // XS-1701: firing OnShutdown from the fake's own Shutdown() handler mirrors what a real
        // graceful-shutdown sequence looks like - a request to shut down followed by the server
        // notifying connected callback channels before it goes away.
        public void Shutdown() => FireShutdown();

        public void GetDataSourcesInfo()
        {
            Callback.OnGetDataSourcesInfoFinished(DataSourcesToReturn);
        }

        public X1FieldInfo[] GetSchemaFields(string table) => SchemaFieldsToReturn;

        // XS-1676/XS-1678: full-suite-vs-files-only entitlement check.
        public bool IsLicensed() => IsLicensedToReturn;

        // XS-1685: records what was reported, so a test can assert on it.
        public void ReportClientInfo(string name, string version)
        {
            ReportedClientName = name;
            ReportedClientVersion = version;
        }

        // XS-1698/XS-1701: fires OnShutdown, simulating X1ServiceHost notifying a connected
        // client that it is shutting down gracefully.
        public static void FireShutdown() => Callback?.OnShutdown();
    }

    /// <summary>Self-hosts a fake duplex WCF service on a uniquely-named pipe for one test.</summary>
    internal sealed class FakeServiceHost<TContract> : IDisposable
    {
        public string Address { get; }
        private readonly ServiceHost _host;

        public FakeServiceHost(object singletonInstance)
        {
            Address = "net.pipe://localhost/XS1672FakeHost_" + Guid.NewGuid().ToString("N");
            _host = new ServiceHost(singletonInstance, new Uri(Address));
            _host.AddServiceEndpoint(typeof(TContract), new NetNamedPipeBinding(NetNamedPipeSecurityMode.None), "");
            _host.Open();
        }

        public void Dispose()
        {
            try { _host.Close(TimeSpan.FromSeconds(2)); }
            catch { _host.Abort(); }
        }
    }
}
