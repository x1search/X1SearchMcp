// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Runtime.Serialization;
using System.ServiceModel;

namespace X1.Service
{
    // XS-1672: vendored WCF wire-contract declarations for talking to the already-installed,
    // still-closed-source X1ServiceHost.exe process. This is not business logic to replace with
    // an open-source equivalent - it IS the wire shape, copied verbatim (attributes preserved
    // character-for-character) from the proprietary X1Service\Contracts project's IX1Service.cs
    // (+ SortDirection from IQueryResult.cs, + X1FieldFlags/X1FieldType from X1Field.cs), so the
    // bridge no longer needs a ProjectReference into that closed-source project to compile.
    //
    // IX1MCPSearchManager : IX1SearchManagerBase is an empty derived interface in the original
    // source (it adds nothing beyond its base). IX1MCPService : IX1ServiceBase used to be the
    // same, but XS-1676 added IsLicensed() and XS-1685 added ReportClientInfo() directly to
    // IX1MCPService server-side (both MCP-only members, not shared with the desktop UI's own
    // IX1Service) - vendored below at exactly that real shape. XS-1698 (X1 service side) added
    // OnShutdown() to IX1MCPServiceCallbacks, fired when X1/X1ServiceHost is shutting down
    // gracefully; XS-1701 (this connector) shuts itself down in response - see
    // McpServer.HandleServiceShutdown. IX1ServiceBase and
    // IX1SearchManagerBase are vendored at exactly their real, complete member set. Neither
    // needed trimming: IX1MCPSearchManager does NOT inherit the much larger
    // IX1SearchManager (the desktop UI's own search interface, with export/brokered-search/
    // aggregate members) - only the small IX1SearchManagerBase. IX1SearchManagerCallbacks (the
    // duplex callback contract) is vendored at full fidelity regardless, since SearchManagerCallbacks
    // already implements every member as a stub and a duplex contract missing a member the server
    // tries to invoke faults the channel.

    [ServiceContract]
    public interface IX1ServiceBase
    {
        [OperationContract]
        string Connect();

        [OperationContract]
        string GetX1ServiceHostStatus();

        [OperationContract(IsOneWay = true)]
        void Disconnect(bool shutdown);

        [OperationContract(IsOneWay = true)]
        void Shutdown();

        [OperationContract(IsOneWay = true)]
        void GetDataSourcesInfo();

        [OperationContract]
        X1FieldInfo[] GetSchemaFields(string table);
    }

    [ServiceContract(CallbackContract = typeof(IX1MCPServiceCallbacks))]
    public interface IX1MCPService : IX1ServiceBase
    {
        // XS-1676/XS-1678: true if this MCP connection is entitled to the full data-source suite;
        // false means it's restricted to the Files-only tier. Distinct from Connect()'s "Unlicensed"
        // sentinel (XS-1671), which means the MCP add-on itself isn't licensed at all.
        [OperationContract]
        bool IsLicensed();

        // XS-1685: reports which MCP client is connected, for the MCP Options tab to display in
        // place of the hard-coded client label it used to show.
        [OperationContract(IsOneWay = true)]
        void ReportClientInfo(string name, string version);
    }

    public interface IX1MCPServiceCallbacks
    {
        [OperationContract(IsOneWay = true)]
        void OnGetDataSourcesInfoFinished(ConfiguredDataSourceInfo[] dataSourcesInfo);

        // XS-1698/XS-1701: fired when X1 / X1ServiceHost is shutting down gracefully. XS-1701
        // uses this to shut the connector itself down (see McpServer.HandleServiceShutdown)
        // rather than trying to keep serving with a channel to a process that's going away.
        [OperationContract(IsOneWay = true)]
        void OnShutdown();
    }

    [DataContract]
    public struct ConfiguredDataSourceInfo
    {
        [DataMember]
        public string scannerName;

        [DataMember]
        public string scannerDisplayName;

        [DataMember]
        public string accountName;

        [DataMember]
        public string[] schemas;

        [DataMember]
        public int totalCount;

        [DataMember]
        public int itemCount;

        [DataMember]
        public string lastScanTime;

        [DataMember]
        public bool isScanning;
    }

    [DataContract]
    public struct X1FieldInfo
    {
        [DataMember]
        public string Name { get; set; }

        [DataMember]
        public string DisplayName { get; set; }

        [DataMember]
        public X1FieldFlags Flags { get; set; }

        [DataMember]
        public X1FieldType FieldType { get; set; }
    }

    [Flags]
    public enum X1FieldFlags
    {
        None = 0,
        Indexed = 0x01,
        Content = 0x02,
        Private = 0x04,
        NotStored = 0x08,
        ModifyKey = 0x10,
        Big = 0x20
    }

    public enum X1FieldType
    {
        String,
        Date,
        Int64,
        Double,
        Boolean,
        EditableInteger,
        ItemNum
    }

    public enum SortDirection
    {
        Forwards,
        Backwards
    }

    [DataContract]
    public struct SortColumn
    {
        [DataMember]
        public string table;

        [DataMember]
        public string name;

        [DataMember]
        public SortDirection direction;

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(name))
                return name + ":" + direction.ToString();
            return "";
        }
    }

    [DataContract]
    public struct Column
    {
        [DataMember]
        public string table;

        [DataMember]
        public string name;

        public Column(string table, string name)
        {
            this.table = table;
            this.name = name;
        }
    }

    [DataContract]
    public struct MergeColumn
    {
        [DataMember]
        public string name;

        [DataMember]
        public Column[] columns;
    }

    [DataContract]
    public struct SearchTerm
    {
        public SearchTerm(string table)
        {
            this.table = table;
            columnName = "";
            term = "";
        }

        public SearchTerm(string table, string columnName, string term)
        {
            this.table = table;
            this.columnName = columnName;
            this.term = term;
        }

        public override bool Equals(object obj)
        {
            if (!(obj is SearchTerm))
                return false;
            SearchTerm t = (SearchTerm)obj;
            return (t.table == table) && (t.columnName == columnName) && (t.term == term);
        }

        public override int GetHashCode()
        {
            return (table + " " + columnName + " " + term).GetHashCode();
        }

        [DataMember]
        public string table;

        [DataMember]
        public string columnName;

        [DataMember]
        public string term;

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(columnName) && !string.IsNullOrEmpty(term))
            {
                var col = columnName.Replace(" ", "");
                return col + ":" + term;
            }

            return "";
        }
    };

    [DataContract]
    public struct SearchResult
    {
        [DataMember]
        public string uri;

        [DataMember]
        public string table;

        [DataMember]
        public bool selected;

        [DataMember]
        public string[] fields;

        [DataMember]
        public string keywords;
    }

    [DataContract]
    [Serializable]
    public struct HighlightTerm
    {
        [DataMember]
        public int findType;

        [DataMember]
        public string term;

        [DataMember]
        public int color;

        [DataMember]
        public string column;
    }

    public struct BrokeredSearchResult
    {
        public BrokeredSearchResult(int requestID, string table, string accountName, int start, int count, int[] receivedCounts, int[] totalCounts, HighlightTerm[] highlightTerms, string translatedSearchterms = null, string nextPageToken = null)
        {
            this.requestID = requestID;
            this.table = table;
            this.accountName = accountName;
            this.start = start;
            this.count = count;
            this.receivedCounts = receivedCounts;
            this.totalCounts = totalCounts;
            this.highlightTerms = highlightTerms;
            this.translatedSearchterms = translatedSearchterms;
            this.nextPageToken = nextPageToken;
        }

        [DataMember]
        public string table;

        [DataMember]
        public string accountName;

        [DataMember]
        public int start;

        [DataMember]
        public int count;

        [DataMember]
        public int[] receivedCounts;

        [DataMember]
        public int[] totalCounts;

        [DataMember]
        public HighlightTerm[] highlightTerms;

        [DataMember]
        public string translatedSearchterms;

        [DataMember]
        public int requestID;

        [DataMember]
        public string nextPageToken;
    }

    public struct BrokeredSearchError
    {
        public BrokeredSearchError(int requestID, string table, string accountName, string error)
        {
            this.requestID = requestID;
            this.table = table;
            this.accountName = accountName;
            this.error = error;
        }

        [DataMember]
        public string table;

        [DataMember]
        public string accountName;

        [DataMember]
        public string error;

        [DataMember]
        public int requestID;
    }

    [DataContract]
    public struct EnterpriseSearchExportProgress
    {
        [DataMember]
        public string CurrentSearchName { get; set; }

        [DataMember]
        public int ExportedSearchesCount { get; set; }

        [DataMember]
        public int TotalSearchesCount { get; set; }

        [DataMember]
        public int ExportedItemsCount { get; set; }

        [DataMember]
        public int TotalItemsCount { get; set; }

        [DataMember]
        public int TotalFilesCount { get; set; }

        [DataMember]
        public int UploadedFilesCount { get; set; }

        [DataMember]
        public int TotalItemsToDeleteCount { get; set; }

        [DataMember]
        public int DeletedItemsCount { get; set; }

        [DataMember]
        public int TotalItemsToMigrateCount { get; set; }

        [DataMember]
        public int MigratedItemsCount { get; set; }
    }

    [DataContract]
    public struct AggregateColumnResult
    {
        [DataMember]
        public string table;

        [DataMember]
        public string name;

        [DataMember]
        public long result;
    }

    [DataContract]
    public struct AggregateColumnResults
    {
        [DataMember]
        public int processedCount;

        [DataMember]
        public int totalCount;

        [DataMember]
        public AggregateColumnResult[] result;
    }

    [ServiceContract]
    public interface IX1SearchManagerBase
    {
        [OperationContract]
        int CreateSearchSession(string[] tables, bool progenitorSearch, bool getKeywordStats);

        [OperationContract(IsOneWay = true)]
        void SetTables(int sessionID, string[] tables);

        [OperationContract(IsOneWay = true)]
        void SetSearchTerms(int sessionID, SearchTerm[] searchTerms, SortColumn[] sortColumns,
          Column[] displayColumns, MergeColumn[] mergeColumns, int pageSize);

        [OperationContract(IsOneWay = true)]
        void GetSearchResults(int sessionID, int requestID, int startRow, int numRows);

        [OperationContract(IsOneWay = true)]
        void ResultChangesOutdated(int sessionID, int uiSequence, int serviceSequence);

        [OperationContract(IsOneWay = true)]
        void DestroySearchSession(int sessionID);

        [OperationContract(IsOneWay = true)]
        void GeneratePreview(string table, string uri, bool isForOpen, string addtionalData);

        [OperationContract(IsOneWay = true)]
        void CancelPreview(string uri, string additionalData = null);

        [OperationContract]
        string[] GetItemInternal(string table, string uri);

        [OperationContract(IsOneWay = true)]
        void Serialize(string table, string uri, string fileName);

        [OperationContract(IsOneWay = true)]
        void AddTags(string table, string[] uris, string[] tags);

        [OperationContract(IsOneWay = true)]
        void RemoveTags(string table, string[] uris, string[] tags);

        [OperationContract(IsOneWay = true)]
        void ClearTags(string table, string[] uris);

        [OperationContract(IsOneWay = true)]
        void GetContent(string table, string uri, string outputFile);

        [OperationContract(IsOneWay = true)]
        void ExtractTextFromFile(string file, string outputFile);

        [OperationContract(IsOneWay = true)]
        void ExportHtml(string table, string uri, string outputFile);

        [OperationContract(IsOneWay = true)]
        void ExportHtmlFromFile(string file, string outputFile);
    }

    [ServiceContract(CallbackContract = typeof(IX1SearchManagerCallbacks))]
    public interface IX1MCPSearchManager : IX1SearchManagerBase
    {
    }

    public interface IX1SearchManagerCallbacks
    {
        [OperationContract(IsOneWay = true)]
        void OnSearchResultsChanged(int sessionID, int totalResults, int selectedItemsCount, HighlightTerm[] highlightTerms,
          int firstRow, SearchResult[] firstPage, string[] trackURIs, int[] trackIndices, int lastSelectionSequence, int elapsedTime);

        [OperationContract(IsOneWay = true)]
        void OnSearchResultsChangedMMF(int sessionID, int totalResults, int selectedItemsCount, HighlightTerm[] highlightTerms,
          int firstRow, string mmfName, string[] trackURIs, int[] trackIndices, int lastSelectionSequence, int elapsedTime);

        [OperationContract(IsOneWay = true)]
        void OnSearchResultsReady(int sessionID, int requestID, SearchResult[] searchResults, int elapsedTime);

        [OperationContract(IsOneWay = true)]
        void OnSearchResultsReadyMMF(int sessionID, int requestID, string mmfName, int elapsedTime);

        [OperationContract(IsOneWay = true)]
        void OnPreviewReady(string uri, string preview, bool isForOpen, string error, string additionalData, int elapsedTime);

        [OperationContract(IsOneWay = true)]
        void OnDownloadProgress(string uri, string progress);

        [OperationContract(IsOneWay = true)]
        void OnDownloadFinished(string uri);

        [OperationContract(IsOneWay = true)]
        void OnDownloadError(string uri, string error);

        [OperationContract(IsOneWay = true)]
        void OnFieldStringsReady(int requestID, string[] uris, string[][] fieldStrings);

        [OperationContract(IsOneWay = true)]
        void OnPSACompleted(int sessionID, int psaRequestID, bool success, string resultDescription);

        [OperationContract(IsOneWay = true)]
        void OnSelectionCountChanged(int sessionID, int sequence, int count);

        [OperationContract(IsOneWay = true)]
        void OnSelectionIterationReady(int sessionID, int requestID, int selectionCount);

        [OperationContract(IsOneWay = true)]
        void OnNextSelectedItems(int sessionID, int requestID, SearchResult[] fieldStrings);

        [OperationContract(IsOneWay = true)]
        void OnSerializationComplete(string uri, bool success);

        [OperationContract(IsOneWay = true)]
        void OnExtractTextComplete(string uri, string fileName, bool success);

        [OperationContract(IsOneWay = true)]
        void OnExtractPiiComplete(string uri, string fileName, bool success, string error);

        [OperationContract(IsOneWay = true)]
        void OnExtractPiiProgress(string uri, string progress);

        [OperationContract(IsOneWay = true)]
        void OnSelectionPartitionResult(int requestID, int totalSelected, int[] itemCounts);

        [OperationContract(IsOneWay = true)]
        void OnExportResultsProgress(int exportedCount, int totalCount);
        [OperationContract(IsOneWay = true)]
        void OnExportResultsFinished(int exportedCount, int totalCount);
        [OperationContract(IsOneWay = true)]
        void OnExportResultsError(string error);

        [OperationContract(IsOneWay = true)]
        void OnExportTagsProgress(int exportedCount, int processedCount, int totalCount);
        [OperationContract(IsOneWay = true)]
        void OnExportTagsFinished(int exportedCount, int processedCount, int totalCount);
        [OperationContract(IsOneWay = true)]
        void OnExportTagsError(string error);

        [OperationContract(IsOneWay = true)]
        void OnImportTagsProgress(int importedCount, int processedCount, int totalCount);
        [OperationContract(IsOneWay = true)]
        void OnImportTagsFinished(int importedCount, int processedCount, int totalCount, string reportFile);
        [OperationContract(IsOneWay = true)]
        void OnImportTagsError(string error);

        [OperationContract(IsOneWay = true)]
        void OnBrokeredSearchFinished(string scannerName, BrokeredSearchResult result);

        [OperationContract(IsOneWay = true)]
        void OnBrokeredSearchError(string scannerName, BrokeredSearchError error);

        [OperationContract(IsOneWay = true)]
        void OnBrokeredSearchExportProgress(string scannerName, string table, string accountName, int[] receivedCounts, int[] totalCounts);
        [OperationContract(IsOneWay = true)]
        void OnBrokeredSearchExportFinished(string scannerName, string table, string accountName, int[] totalCounts);

        [OperationContract(IsOneWay = true)]
        void OnBrokeredSearchUpdateMetadataFinished(string scannerName, string table, string accountName);

        [OperationContract(IsOneWay = true)]
        void OnBrokeredSearchQueryCanUpdateMetadataFinished(string scannerName, string table, string accountName, bool canUpdateMetadata);

        [OperationContract(IsOneWay = true)]
        void OnEnterpriseSearchExportResultsProgress(EnterpriseSearchExportProgress progress);
        [OperationContract(IsOneWay = true)]
        void OnEnterpriseSearchExportResultsFinished(EnterpriseSearchExportProgress progress);
        [OperationContract(IsOneWay = true)]
        void OnEnterpriseSearchExportResultsError(string error, bool isFatal);

        [OperationContract(IsOneWay = true)]
        void OnEnterpriseSearchUpgradeRequired();

        [OperationContract(IsOneWay = true)]
        void OnFindDuplicateURIsProgress(double percent, string progress);

        [OperationContract(IsOneWay = true)]
        void OnFindDuplicateURIsComplete(string[] uris);

        [OperationContract(IsOneWay = true)]
        void OnCheckURISortProgress(double percent, string progress);

        [OperationContract(IsOneWay = true)]
        void OnCheckURISortComplete(string[] uris);

        [OperationContract(IsOneWay = true)]
        void OnAggregateResultsProgress(AggregateColumnResults result);
        [OperationContract(IsOneWay = true)]
        void OnAggregateResultsFinished(AggregateColumnResults result);
        [OperationContract(IsOneWay = true)]
        void OnAggregateResultsError(string error);

        [OperationContract(IsOneWay = true)]
        void OnGroupAggregateProgress(int processedCount, int totalCount);
        [OperationContract(IsOneWay = true)]
        void OnGroupAggregateFinished(int processedCount, int totalCount, string fileName);
        [OperationContract(IsOneWay = true)]
        void OnGroupAggregateError(string error);

        [OperationContract(IsOneWay = true)]
        void OnTagsAdded(int count);

        [OperationContract(IsOneWay = true)]
        void OnTagsRemoved(int count);

        [OperationContract(IsOneWay = true)]
        void OnTagsCleared(int count);

        [OperationContract(IsOneWay = true)]
        void OnContentReady(string outputFile, string extractionResult);

        [OperationContract(IsOneWay = true)]
        void OnTextExtracted(string outputFile, string extractionResult);

        [OperationContract(IsOneWay = true)]
        void OnExportHtmlReady(string outputFile, string extractionResult);
    }
}
