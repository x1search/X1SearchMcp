// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using X1.Service;

namespace X1.McpBridge
{
    internal static class McpServer
    {
        private static readonly ILog Log = BridgeLogger.GetLogger(typeof(McpServer));

        // XS-1583-SESSION-LIFECYCLE: per guidance from Jogy/Dominik, IX1Service.Connect() has
        // significant overhead and should be opened once at bridge startup and closed once at
        // bridge shutdown, rather than connect-per-request-disconnect. One shared, long-lived
        // connection for the life of this process — see StartBackend()/StopBackend() for the
        // open/close points, and X1MCPServiceConnection.EnsureConnectedChannel() for the
        // idempotent-connect logic.
        //
        // XS-1609: this now connects via the dedicated IX1MCPService interface, served at its
        // own endpoint address, so it no longer shares X1ServiceHost's single-client slot with
        // the desktop UI's own IX1Service connection.
        private static readonly X1MCPServiceConnection ServiceConnection =
            new X1MCPServiceConnection(HandleServiceShutdown);

        // XS-1701 test seam: overridable so a test can assert the "not in host mode" branch of
        // HandleServiceShutdown fires without actually terminating the test process. Mirrors
        // QueryLogEnabledProvider/QueryLogWriter (XS-1578) and HostMode's UtcNowProvider/
        // IdleShutdownSecondsProvider - this codebase's existing pattern for making an
        // otherwise-untestable side effect injectable.
        internal static Action ExitProcess = () => Environment.Exit(0);

        // XS-1698/XS-1701: fired when X1 announces a clean shutdown (XS-1698's OnShutdown).
        // Shuts the connector down rather than trying to keep serving a channel to a process
        // that's going away - the next Claude tool call naturally gets a fresh connector: for the
        // shared Lean relay, ProxyMode's existing "no relay running -> launch one" path spins up
        // a new one transparently; for a plain stdio session, the MCP server process for that
        // session ends and Claude needs the session restarted to reconnect (see docs/UserManual.md
        // for the user-facing note on that difference).
        internal static void HandleServiceShutdown()
        {
            Log.Info("X1 announced a clean shutdown (XS-1698 OnShutdown) - shutting the connector down.");
            if (HostMode.IsHostRunning)
                HostMode.RequestShutdown("X1 OnShutdown (XS-1698/XS-1701)");
            else
                ExitProcess();
        }

        // Replaces a hand-maintained, always-incomplete internal-name -> display-name dictionary
        // with one built from the live schema, so filters/sort/displayFields resolve correctly for
        // every field X1 reports, not just the ~20 someone thought to hardcode.
        private static readonly ColumnNameResolver ColumnResolver = new ColumnNameResolver(ServiceConnection);

        // XS-1640: resolves a caller-supplied table token (scanner name, per-account displayName,
        // or an already-correct schema) to the one schema value x1_search/x1_get_schema_fields
        // actually need, or throws a descriptive error listing valid schemas instead of letting a
        // wrong name reach the service as an opaque failure / silent empty result.
        private static readonly TableSchemaResolver TableResolver = new TableSchemaResolver(ServiceConnection);

        private static readonly SearchBridge Search = new SearchBridge(ColumnResolver, TableResolver, ServiceConnection);
        private static readonly ActionBridge Actions = new ActionBridge(Search, TableResolver);

        // XS-1578: dedicated flag for logging actual query content, deliberately separate from
        // Verbosity (which is routinely turned on for unrelated support requests - tying query
        // content to it would leak search terms into debug logs customers didn't know to expect).
        // Off (0/absent) by default: false means query content is never logged. Read live via
        // RegistrySettings on every call rather than cached, so toggling takes effect immediately
        // with no bridge/Claude restart - unlike Verbosity/X1McpConnectorLog, which BridgeLogger
        // only reads once at Configure(). The provider indirection mirrors HostMode's
        // IdleShutdownSecondsProvider test seam, so tests can flip this without touching the real
        // registry.
        private const string QueryLogRegistryValue = "X1McpQueryLog";
        internal static Func<bool> QueryLogEnabledProvider = () => RegistrySettings.ReadInteger(QueryLogRegistryValue, 0) != 0;
        internal static Action<bool> QueryLogWriter = enabled => RegistrySettings.WriteDWord(QueryLogRegistryValue, enabled ? 1 : 0);

        /// <summary>
        /// Opens the shared WCF connection and kicks off the background name caches.
        ///
        /// Shared by every transport that dispatches through ProcessMessage - stdio (RunStdio)
        /// and the in-bridge HTTP relay (HostMode) alike - rather than duplicated per transport.
        /// ServiceConnection/ColumnResolver/TableResolver are process-wide statics, so a second
        /// hand-copied warmup would silently drift the first time a fourth one is added here.
        ///
        /// Callers must not call this before they have won the right to be the single
        /// WCF-owning process: opening this connection is exactly what X1ServiceHost crashes on
        /// when two clients race it (see X1ConcurrencyWorkaround.cs).
        /// </summary>
        internal static void StartBackend()
        {
            // XS-1583-SESSION-LIFECYCLE: connect once here rather than per-request. Best-effort —
            // if the service host isn't up yet, don't fail bridge startup; individual calls fall
            // back to EnsureConnectedChannel()'s lazy connect-on-first-use.
            try { ServiceConnection.Connect(); }
            catch (Exception ex) { Log.Warn("Initial ServiceConnection.Connect() failed (will retry lazily on first use): " + ex.Message); }

            // Build the column-name cache in the background - never blocks bridge startup or the
            // first search; FilterMapper's resolver falls back gracefully if a search races ahead
            // of this finishing (see ColumnNameResolver.ResolveAsync's refresh-on-miss).
            ColumnResolver.StartBackgroundBuild();
            TableResolver.StartBackgroundBuild();
        }

        /// <summary>
        /// Closes the shared WCF connection. Safe to call unconditionally, including when
        /// StartBackend()'s own best-effort Connect() failed.
        /// </summary>
        internal static void StopBackend()
        {
            try { ServiceConnection.Disconnect(); }
            catch (Exception ex) { Log.Debug("ServiceConnection.Disconnect() on shutdown: " + ex.Message); }
        }

        /// <summary>
        /// <paramref name="mcpOut"/> must be the real stdout, captured by the caller
        /// (Program.Main) BEFORE Console.Out was redirected to null and before any
        /// logging/BridgeLogger.Configure() ran — see the comment in Program.cs for why
        /// that ordering matters (X1.Common's own logger can write straight to Console.Out
        /// the first time it's touched, independent of our log4net setup, corrupting the
        /// MCP JSON-RPC stream if it happens before the redirect).
        /// </summary>
        public static int RunStdio(TextWriter mcpOut)
        {
            BridgeLogger.Configure();
            Log.Info("X1 MCP Bridge starting (stdio transport)");

            StartBackend();

            try
            {
                // This read loop is single-threaded, and that is load-bearing rather than
                // incidental: it - not X1ConcurrencyWorkaround - is what actually guarantees this
                // process never issues two overlapping calls into IX1MCPService. The workaround's
                // gate only wraps CallTool (see CallTool below); resources/read -> ReadResource ->
                // ConnectAndGetHostStatus() sits outside it. Any transport that dispatches
                // ProcessMessage concurrently must supply its own serialization, and must not do
                // it by wrapping ProcessMessage in X1ConcurrencyWorkaround.RunSerialized - that
                // gate is a non-reentrant SemaphoreSlim(1,1) and would deadlock as soon as
                // CallTool re-entered it. See HostMode's dispatch thread.
                string line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    JObject response = null;
                    try
                    {
                        var msg = JObject.Parse(line);
                        response = ProcessMessage(msg);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("JSON parse error", ex);
                        response = McpProtocol.Err(null, -32700, "Parse error", ex.Message);
                    }

                    if (response != null)
                    {
                        mcpOut.Write(response.ToString(Formatting.None) + "\n");
                        mcpOut.Flush();
                    }
                }
            }
            finally
            {
                StopBackend();
            }

            Log.Info("X1 MCP Bridge shutting down (stdin closed)");
            return 0;
        }

        internal static JObject ProcessMessage(JObject msg)
        {
            var method = msg.Value<string>("method");
            var idToken = msg["id"];
            object id = idToken != null && idToken.Type != JTokenType.Null ? idToken : null;

            if (McpProtocol.IsNotification(msg))
            {
                if (method == "notifications/initialized" || method == "initialized")
                    return null;
                if (method == "notifications/cancelled")
                    return null;
                return null;
            }

            Log.Debug("Request id=" + id + " method=" + method);
            try
            {
                var prms = msg["params"] as JObject ?? new JObject();
                JObject result;

                switch (method)
                {
                    case "initialize":
                        result = McpProtocol.Ok(id, HandleInitialize(prms));
                        break;
                    case "ping":
                        result = McpProtocol.Ok(id, new JObject());
                        break;
                    case "tools/list":
                        result = McpProtocol.Ok(id, ListTools());
                        break;
                    case "tools/call":
                        result = McpProtocol.Ok(id, CallTool(prms));
                        break;
                    case "resources/list":
                        result = McpProtocol.Ok(id, ListResources());
                        break;
                    case "resources/read":
                        result = McpProtocol.Ok(id, ReadResource(prms));
                        break;
                    case "prompts/list":
                        result = McpProtocol.Ok(id, ListPrompts());
                        break;
                    case "prompts/get":
                        result = McpProtocol.Ok(id, GetPrompt(prms));
                        break;
                    default:
                        Log.Warn("Unknown method: " + method);
                        result = McpProtocol.Err(id, -32601, "Method not found: " + method);
                        break;
                }

                Log.Debug("Response id=" + id + " method=" + method + " ok");
                return result;
            }
            catch (Exception ex)
            {
                Log.Error("Error processing id=" + id + " method=" + method, ex);
                return McpProtocol.Err(id, -32603, ex.Message, ex.ToString());
            }
        }

        private static JObject HandleInitialize(JObject prms)
        {
            ReportClientInfoBestEffort(prms["clientInfo"] as JObject);

            var result = new JObject
            {
                ["protocolVersion"] = McpProtocol.ProtocolVersion,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject(),
                    ["resources"] = new JObject(),
                    ["prompts"] = new JObject()
                },
                // Version comes from the assembly, not a literal: a hand-typed copy here silently
                // drifts from the stamped build, which makes the handshake lie about what is running.
                ["serverInfo"] = new JObject
                {
                    ["name"] = "x1-mcp-bridge",
                    ["version"] = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString()
                }
            };

            string firstUseInstructions = TryBuildFirstUseInstructions();
            if (firstUseInstructions != null)
                result["instructions"] = firstUseInstructions;

            return result;
        }

        /// <summary>
        /// XS-1673: one-time welcome banner delivered via MCP's "instructions" field on the initialize
        /// handshake, rather than appended to a tool result. A tool-result content block competes with
        /// the tool's own payload for the model's attention when it writes its reply, and in practice
        /// gets treated as noise and dropped; "instructions" is specifically meant to be read as
        /// operating guidance for the connecting agent, so it's a stronger bet for actually reaching
        /// the user. Returns null (omit the field) if already shown, or if the license tier can't be
        /// determined this time - retried on the next initialize rather than shown with a guessed tier
        /// or lost silently. Best-effort: must never fail the initialize handshake itself.
        /// </summary>
        private static string TryBuildFirstUseInstructions()
        {
            if (FirstUseTracker.HasBeenShown()) return null;

            try
            {
                bool fullSuite = ServiceConnection.IsFullSuiteLicensed();
                string banner = FirstUseTracker.BannerFor(fullSuite);
                FirstUseTracker.MarkShown();
                return banner;
            }
            catch (Exception ex)
            {
                Log.Debug("TryBuildFirstUseInstructions failed (non-fatal, retries next initialize): " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// XS-1685: forwards "who's connected" to X1 Search's ReportClientInfo, which the MCP
        /// Options tab displays in place of the hard-coded client label it used to show. Prefers
        /// AgentProcessScanner's locally-detected Claude/Claude Code process versions (read from the
        /// actual binary's own version resource) over the MCP-declared clientInfo, since the latter
        /// is only as accurate as whatever integration layer populated it - in the "local-agent-mode"
        /// plugin wiring this connector is often launched under, clientInfo reports a static
        /// placeholder tied to the plugin wrapper, not the real Claude Code build. Falls back to the
        /// declared clientInfo when no local Claude/Claude Code process can be identified (e.g. some
        /// other MCP client entirely), so this still degrades gracefully for non-Claude callers.
        ///
        /// XS-1684: ReportClientInfo takes a single (name, version) pair, but more than one Claude
        /// client can be running at once (e.g. the desktop app and Claude Code both open - the two
        /// x1_version reports as an array). GetConcatenatedIdentity folds them into one name and one
        /// version string (index-aligned) so every connected client reaches the service host, not
        /// just a single picked one.
        ///
        /// Best-effort and silent on failure - this must never block or fail the initialize
        /// handshake itself (X1ServiceHost may not even be up yet; StartBackend()'s own Connect()
        /// is equally best-effort for the same reason).
        /// </summary>
        private static void ReportClientInfoBestEffort(JObject clientInfo)
        {
            try
            {
                string name = null, version = null;

                var detected = AgentProcessScanner.GetConcatenatedIdentity();
                if (detected.HasValue)
                {
                    name = detected.Value.ProductName;
                    version = detected.Value.ProductVersion;
                }
                else if (clientInfo != null)
                {
                    name = clientInfo.Value<string>("name");
                    version = clientInfo.Value<string>("version");
                }

                if (!string.IsNullOrEmpty(name))
                    ServiceConnection.ReportClientInfo(name, version ?? "");
            }
            catch (Exception ex)
            {
                Log.Debug("ReportClientInfoBestEffort failed (non-fatal): " + ex.Message);
            }
        }

        // Built once and reused by both ListTools (the tools/list response) and
        // ToolSchemasByName (argument-name validation in NormalizeAndValidateArgs) - a single
        // source of truth for "what parameters does this tool declare", so validation can never
        // drift out of sync with what tools/list actually advertises to the caller.
        private static readonly JArray ToolDefinitions = BuildToolDefinitions();

        // name -> inputSchema, indexed once from ToolDefinitions for NormalizeAndValidateArgs.
        private static readonly Dictionary<string, JObject> ToolSchemasByName =
            ToolDefinitions.OfType<JObject>().ToDictionary(
                t => t.Value<string>("name"),
                t => t["inputSchema"] as JObject,
                StringComparer.Ordinal);

        private static JObject ListTools()
        {
            return new JObject { ["tools"] = ToolDefinitions };
        }

        private static JArray BuildToolDefinitions()
        {
            return new JArray
            {
                new JObject
                {
                    ["name"] = "x1_search",
                    ["annotations"] = new JObject { ["title"] = "Search X1 Index", ["readOnlyHint"] = true, ["destructiveHint"] = false },
                    ["description"] = "Search the local X1 index (keyword / X1 query syntax). Pass one table for a single-source search, or multiple tables in one call (e.g. tables:[\"Files\",\"MSMail\",\"Teams\"]) to search several source types at once — the bridge fans this out internally as one sequential search per table and merges the results, so you no longer need a separate call per source type. See the tables parameter description for the exact merged shape (byTable breakdown, jagged per-table fields) and the latency tradeoff of requesting many tables (roughly N x timeoutMs worst case). Call x1_list_sources first to discover available tables: pass any of a source's name, an account's displayName, or a schemas[] entry as tables — this bridge resolves it to the correct schema automatically (e.g. the email source's name may be \"OutlookEmail\" while its real schema is \"Email\"), or returns a descriptive error listing valid schemas if the value doesn't match anything. Use the filters parameter with column->term pairs to narrow results by path, subject, sender, date, etc. Query terms are implicitly AND-ed; use parentheses to control precedence and column:value to restrict a term to a specific field (e.g. type:cs for C# files). FILE TYPE FILTERING: always use type:ext in the query string to filter by file type (e.g. type:pptx, type:pdf, type:docx) — do NOT filter by extension via the filters parameter, as extension filters are unreliable. By default each result includes an \"actions\" array listing what you can do with it — this avoids a separate x1_list_actions call when you intend to act on results; pass includeActions:false to omit it. For OneDrive/GDrive results where prefetchInitiated is true, calling x1_generate_preview promptly (within ~30s) is likely to succeed quickly. Requires X1ServiceHost running.",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
              ""query"": { ""type"": ""string"", ""description"": ""Main search text using X1 query syntax. Key rules: (1) Terms are implicitly AND-ed — 'project status failed' means 'project AND status AND failed'. (2) Use explicit AND, OR, NOT for control — 'failed OR threatened'. (3) Use parentheses to set precedence — 'project status (failed OR threatened)' is NOT the same as 'project status failed OR threatened', which X1 parses as '(project AND status AND failed) OR threatened'. (4) Use column:value to restrict a term to a specific indexed field, e.g. type:cs (C# files), type:pdf, type:pptx, type:docx, type:xlsx, path:X1Desktop (folder filter), name:scanner (filename), subject:invoice (email subject), from:alice (email sender). Combine freely: 'scanner type:cs path:X1Service' finds C# files in X1Service containing 'scanner'. IMPORTANT: to filter by file type always use type:ext in the query (e.g. 'budget type:xlsx') — never use the filters parameter for extension matching. (5) FIELD NAMES: column:value here matches a field's DISPLAY name (case-insensitive, by prefix), NOT its internal name. Tag search is 'Tags:isocert' — 'x1tag:isocert' does NOT work. Note that an unrecognized field prefix does not error: it degrades to a literal text search and silently returns 0 results, which looks identical to 'no matches'. If unsure of a name, call x1_get_schema_fields and use its displayName here (its name goes in displayFields/sort; the filters parameter accepts either). Beware ambiguous prefixes — on Files, 'date:' silently resolves to 'Date Accessed', not Date Created/Modified."" },
              ""tables"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""One or more table names, e.g. [\""Files\""] for a single table, or [\""Files\"",\""MSMail\"",\""Teams\""] to search all three in one call. Any of x1_list_sources' name, an account's displayName, or a schemas[] value is accepted per table and resolved to the correct schema automatically; an unrecognized/ambiguous value ANYWHERE in the array fails the whole call up front with a descriptive error listing valid schemas, before any table is searched. MULTI-TABLE BEHAVIOR: each table is searched separately and sequentially (never in parallel — an X1 service-side limitation), then merged into one response: totalResults/returned are summed across tables, results from every table are concatenated (each already carries its own \""table\"" field, and rows are deliberately jagged — a result's fields shape reflects whatever displayFields resolved for THAT table, which can differ per table), and a \""byTable\"" array reports {table, totalResults, returned, error?} per table so one table's failure doesn't abort the others. LATENCY: limit and timeoutMs both apply PER TABLE (not divided/shared), so an N-table call can take up to N x timeoutMs in the worst case — prefer fewer tables per call or a shorter timeoutMs when latency matters."" },
              ""limit"": { ""type"": ""integer"", ""default"": 20, ""description"": ""Max hits (1-500)."" },
              ""include_snippets"": { ""type"": ""boolean"", ""default"": true },
              ""includeActions"": { ""type"": ""boolean"", ""default"": true, ""description"": ""When true (default), each result includes an \""actions\"" array of the Post Search Actions available for that result (from x1_list_actions). Pass false to omit action lists and shrink the payload."" },
              ""progenitorSearch"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Controls how child items (attachments) are counted. Attachments are indexed as their own items with their own URIs and a copy of the parent's subject/from/date. false (default): every item counts separately, so an email with 2 attachments yields 3 rows sharing one subject. true: results roll up to the progenitor (parent email), yielding 1 row. Pass true whenever counting, reporting 'the last N', or listing results for a user - otherwise attachments look like duplicates and can crowd out real hits. Use false only when you need to reach an attachment as its own item (preview/extract). Note x1_add_tags on a parent also tags its attachment children, so a later tag search returns more items than were tagged unless progenitorSearch is true."" },
              ""filters"": { ""description"": ""Column filters as object map column->term (e.g. {path: \""woodworking\""}) or array of {table,column,term}. Use path to limit Files results to a folder; use subject/from/to for email."" },
              ""displayFields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Result columns to return, e.g. [\""name\"",\""path\""] for Files or [\""subject\"",\""from\"",\""date\""] for email."" },
              ""sort"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""column"": { ""type"": ""string"" }, ""direction"": { ""type"": ""string"" }, ""table"": { ""type"": ""string"" } } }, ""description"": ""Sort results server-side. Array of { column, direction, table? }. direction: \""desc\""/\""descending\""/\""backwards\"" = newest/highest first; \""asc\""/\""ascending\""/\""forwards\"" (default) = oldest/lowest first. column must be a SORTABLE indexed field for the table (e.g. date for email). The bridge also enforces the order itself, so results are reliably ordered even when X1 would pin a 'current' item to the top."" },
              ""timeoutMs"": { ""type"": ""integer"", ""default"": 60000 }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_get_metadata",
                    ["annotations"] = new JObject { ["title"] = "Get Item Metadata", ["readOnlyHint"] = true, ["destructiveHint"] = false },
                    ["description"] = "Fetch field values for one indexed item (table + uri).",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""required"": [""table"", ""uri""],
            ""properties"": {
              ""table"": { ""type"": ""string"" },
              ""uri"": { ""type"": ""string"" },
              ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
              ""timeoutMs"": { ""type"": ""integer"", ""default"": 30000 }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_list_sources",
                    ["annotations"] = new JObject { ["title"] = "List Data Sources", ["readOnlyHint"] = true, ["destructiveHint"] = false },
                    ["description"] = "Lists the X1 sources (tables) configured for this bridge and their available display columns. Call this first to discover what tables exist and what fields you can request via displayFields in x1_search. Each source includes a capabilities object listing the available actions and whether preview is supported. When the X1 service host is running, each source ALSO carries an accounts[] array of { accountName?, totalCount, lastScanTime?, isScanning, schemas? } — use this to answer 'how much of my mail is indexed?' or 'when did GDrive last sync?' without an extra call. schemas (when present) lists the underlying database schema names for that account — most sources have one, but the PST scanner has several (PSTFile, PSTEmail, PSTCalendar, PSTContact, PSTNote); PSTFile (first) is the indexed .pst files themselves. Pass any schema name to x1_get_schema_fields to discover its fields.",
                    ["inputSchema"] = JObject.Parse(@"{ ""type"": ""object"", ""properties"": {} }")
                },
                new JObject
                {
                    ["name"] = "x1_get_schema_fields",
                    ["annotations"] = new JObject { ["title"] = "Get Schema Fields", ["readOnlyHint"] = true, ["destructiveHint"] = false },
                    ["description"] = "List the fields/columns available on a table (or PST sub-schema, e.g. PSTEmail). Use this to discover valid names for x1_search's displayFields/filters/sort parameters instead of guessing — especially for tables not covered by hardcoded skill examples (JIRA, Skype, PST sub-schemas surfaced via x1_list_sources' schemas array). Returns { fields: [{ name, displayName, fieldType, isIndexed, isContent, isStored }] }.",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""required"": [""table""],
            ""properties"": {
              ""table"": { ""type"": ""string"", ""description"": ""Table or schema name, e.g. \""Files\"", \""PSTEmail\"". Any of x1_list_sources' name, displayName, or schemas[] value is accepted and resolved automatically; an unrecognized or ambiguous value returns a descriptive error listing valid schemas instead of an empty field list.""},
              ""timeoutMs"": { ""type"": ""integer"", ""default"": 15000 }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_list_actions",
                    ["annotations"] = new JObject { ["title"] = "List Available Actions", ["readOnlyHint"] = true, ["destructiveHint"] = false },
                    ["description"] = "Returns the available Post Search Actions for a specific result (table + uri). Prefer includeActions:true on x1_search (the default) to get this data inline on every result — use x1_list_actions only when you need to confirm actions for a result fetched with includeActions:false.",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""required"": [""table"", ""uri""],
            ""properties"": {
              ""table"": { ""type"": ""string"", ""description"": ""Table name from the search result (e.g. Files, Gmail, GDrive)."" },
              ""uri"":   { ""type"": ""string"", ""description"": ""URI from the search result."" }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_execute_action",
                    ["annotations"] = new JObject { ["title"] = "Execute Item Action", ["readOnlyHint"] = false, ["destructiveHint"] = true },
                    ["description"] = "Execute a Post Search Action on an indexed item. Call x1_list_actions first to discover which actions are available for a given table/uri. Actions that launch the item (open, show_in_folder, open_url) return status. Actions that return data (get_path, get_url) return the value as a string.",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""required"": [""table"", ""uri"", ""action""],
            ""properties"": {
              ""table"":     { ""type"": ""string"", ""description"": ""Table name from the search result."" },
              ""uri"":       { ""type"": ""string"", ""description"": ""URI from the search result."" },
              ""action"":    { ""type"": ""string"", ""enum"": [""get_path"", ""open"", ""show_in_folder"", ""get_url"", ""open_url""], ""description"": ""Action to perform. Use x1_list_actions (or the actions array on a search result) to discover which are valid. The open action works for local files AND cloud files (OneDrive, GDrive, Dropbox, SharePoint) — it opens the local cached copy if available, falling back to a preview callback."" },
              ""timeoutMs"": { ""type"": ""integer"", ""default"": 30000 }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_get_content",
                    ["annotations"] = new JObject { ["title"] = "Get Item Content", ["readOnlyHint"] = true, ["destructiveHint"] = false },
                    ["description"] = "Get extracted text for an indexed item across ANY table (Files, MSMail, Gmail, Exchange, OneDrive, SP365, Teams, Slack, …). mode=auto (recommended): try content → preview → internal. mode=content: extracted text via X1's content store — works for every table and is essentially free on repeat calls once the content store is warm. mode=preview: HTML/text preview (docx-to-HTML, image embed, extracted PDF text, mail card) — only for connectors with a registered preview provider. mode=internal: raw index fields, works for all connectors. mode=extract is accepted as a back-compat alias for content.",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""required"": [""table"", ""uri""],
            ""properties"": {
              ""table"": { ""type"": ""string"" },
              ""uri"": { ""type"": ""string"" },
              ""mode"": { ""type"": ""string"", ""enum"": [""auto"", ""content"", ""preview"", ""internal""], ""default"": ""auto"" },
              ""timeoutMs"": { ""type"": ""integer"", ""default"": 120000 }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_extract_file",
                    ["annotations"] = new JObject { ["title"] = "Extract Text From File", ["readOnlyHint"] = true, ["destructiveHint"] = false },
                    ["description"] = "Extract plain text from an arbitrary LOCAL FILE (does not have to be indexed). Useful for temp preview caches, downloads that haven't hit the index yet, or ad-hoc extraction. Uses the same text-extraction pipeline X1 uses at index time. Requires the MCP-full license entitlement — on a Files-only license this returns { error }; use x1_get_content on an indexed item instead. Returns { text, path, truncated? } on success or { error }.",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""required"": [""file""],
            ""properties"": {
              ""file"": { ""type"": ""string"", ""description"": ""Absolute path to the local file to extract text from."" },
              ""timeoutMs"": { ""type"": ""integer"", ""default"": 120000 }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_export_html",
                    ["annotations"] = new JObject { ["title"] = "Export Item to HTML", ["readOnlyHint"] = true, ["destructiveHint"] = false },
                    ["description"] = "Export an indexed item (table+uri) OR an arbitrary local file to formatted HTML using X1's native HTML export — preserves tables/formatting and may emit sibling image files alongside the returned html. Prefer this over x1_get_content when the user wants a faithful visual rendering of a complex document (tables, embedded images) rather than flattened plain text. Returns { html, path, assetFolder } on success or { error } on failure. Pass either (table AND uri) for an indexed item, or file for an arbitrary local path — not both. The file (arbitrary local path) form requires the MCP-full license entitlement; on a Files-only license it returns { error } — the table+uri form works normally for Files.",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
              ""table"":     { ""type"": ""string"", ""description"": ""Table name from x1_search result. Use with uri; omit if using file."" },
              ""uri"":       { ""type"": ""string"", ""description"": ""URI from x1_search result. Use with table; omit if using file."" },
              ""file"":      { ""type"": ""string"", ""description"": ""Absolute local path to export (arbitrary file, not required to be indexed). Omit if using table+uri."" },
              ""timeoutMs"": { ""type"": ""integer"", ""default"": 120000 }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_add_tags",
                    ["annotations"] = new JObject { ["title"] = "Add Tags", ["readOnlyHint"] = false, ["destructiveHint"] = true },
                    ["description"] = "Add tags to one or more indexed items. IMPORTANT: uris and tags are positional arrays and MUST have the same length — uris[i] gets tags[i]. To apply the same tag to N items, pass N copies of the tag: uris:[\"u1\",\"u2\",\"u3\"], tags:[\"label\",\"label\",\"label\"]. Never pass a single-element tags array for multiple URIs. Returns { op, requested, affected }. Existing tags are preserved; the same tag is not added twice.",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""required"": [""table"", ""uris"", ""tags""],
            ""properties"": {
              ""table"": { ""type"": ""string"", ""description"": ""Source table (e.g. Files, Gmail) - any of x1_list_sources' name, displayName, or schemas[] value is accepted and resolved automatically."" },
              ""uris"":  { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""URIs from x1_search results. Must have the same length as tags."" },
              ""tags"":  { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Tag strings, one per URI in uris (positional)."" },
              ""timeoutMs"": { ""type"": ""integer"", ""default"": 30000 }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_remove_tags",
                    ["annotations"] = new JObject { ["title"] = "Remove Tags", ["readOnlyHint"] = false, ["destructiveHint"] = true },
                    ["description"] = "Remove specific tags from indexed items. uris and tags are positional arrays and MUST have the same length — uris[i] loses tags[i]. To remove the same tag from N items, pass N copies of the tag. Absent tags are silently skipped. Returns { op, requested, affected }.",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""required"": [""table"", ""uris"", ""tags""],
            ""properties"": {
              ""table"": { ""type"": ""string"" },
              ""uris"":  { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
              ""tags"":  { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
              ""timeoutMs"": { ""type"": ""integer"", ""default"": 30000 }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_clear_tags",
                    ["annotations"] = new JObject { ["title"] = "Clear All Tags", ["readOnlyHint"] = false, ["destructiveHint"] = true },
                    ["description"] = "Remove ALL tags from the given indexed items. Returns { op, requested, affected }.",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""required"": [""table"", ""uris""],
            ""properties"": {
              ""table"": { ""type"": ""string"" },
              ""uris"":  { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
              ""timeoutMs"": { ""type"": ""integer"", ""default"": 30000 }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_generate_preview",
                    ["annotations"] = new JObject { ["title"] = "Generate Item Preview", ["readOnlyHint"] = true, ["destructiveHint"] = false },
                    ["description"] = "Generate a self-contained HTML preview of an indexed item for display inside Claude. IMPORTANT — RENDER THE RESULT: when the user asks to preview, view, open, or look at an item, take the returned \"html\" and render it as an HTML artifact (use the returned \"title\" as the artifact title) so the user sees a formatted visual preview, NOT the raw markup. The html is a complete, self-contained document (own <style>, no external scripts) and is safe to render directly. Works for all source types: Files (embedded text/HTML; docx is extracted to readable HTML), email (MSMail, Gmail, Exchange — headers + body), cloud files (OneDrive, GDrive, Dropbox — metadata card with link), and messages (Teams, Slack — formatted message card). Returns { html, contentType, previewType, title } on success or { error } on failure. previewType is \"docx\", \"html\", \"text\", \"image\", \"pdf\", or \"metadata_card\" — \"metadata_card\" means a fallback card (e.g. a cloud file not yet cached locally or a binary format that can't be extracted). OUTPUT MODE: pass output=\"file\" to write the preview to a temp artifact-ready HTML fragment and return { mode:\"file\", path, ... } — markup never enters context (ephemeral, cleaned on reboot). Pass output=\"save\" to write to the configured saved-content directory (persistent, survives reboots) and return { mode:\"save\", path, ... } — use when the user says \"save this\", \"keep a copy\", or \"archive\". Use output=\"inline\" when you need to read/reason over the content yourself.",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""required"": [""table"", ""uri""],
            ""properties"": {
              ""table"":     { ""type"": ""string"", ""description"": ""Table name from x1_search result."" },
              ""uri"":       { ""type"": ""string"", ""description"": ""URI from x1_search result."" },
              ""maxChars"":  { ""type"": ""integer"", ""default"": 0, ""description"": ""Limit docx preview output to approximately this many characters. Pass 4000-8000 for a readable summary (~2-3 pages). Default 0 = full document."" },
              ""output"":    { ""type"": ""string"", ""enum"": [""inline"", ""file"", ""save""], ""default"": ""inline"", ""description"": ""inline (default) returns the html in the result; file writes the preview to a temp .html fragment and returns its path (zero-token display, ephemeral); save writes to the configured saved-content directory (persistent, survives reboots) and returns the same path structure — use save when the user wants to keep a copy or archive content."" },
              ""timeoutMs"": { ""type"": ""integer"", ""default"": 120000 }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_cost_savings",
                    ["annotations"] = new JObject { ["title"] = "Get Cost Savings Report", ["readOnlyHint"] = true, ["destructiveHint"] = false },
                    ["description"] = "Report the estimated token-cost savings accumulated since the bridge was first used. Compares actual x1 token usage against what the curated Microsoft Graph / Gmail / OneDrive connector already installed on this machine would have cost for the same retrieval — classified per item into metadata/search, text-native content, structured/tabular content, or image/OCR-required content, each with its own measured or provisional coefficient (see coefficientsVersion in the response). Returns a JSON summary with total tokens saved, a savings-fraction range (not a single hero percentage), estimated USD saving at Claude Sonnet pricing, a capabilityGainCount for items with no curated-connector counterfactual at all, and a per-category breakdown sorted by savings. Use this when the user asks about cost savings, token efficiency, or ROI of the x1 connector.",
                    ["inputSchema"] = JObject.Parse(@"{ ""type"": ""object"", ""properties"": {} }")
                },
                new JObject
                {
                    ["name"] = "x1_reset_stats",
                    ["annotations"] = new JObject { ["title"] = "Reset Cost Savings Stats", ["readOnlyHint"] = false, ["destructiveHint"] = true },
                    ["description"] = "Reset all accumulated token-cost statistics. Deletes the persisted stats file so the next x1_cost_savings report starts fresh from zero. Use when the user wants to begin a new tracking period or clear historical data.",
                    ["inputSchema"] = JObject.Parse(@"{ ""type"": ""object"", ""properties"": {} }")
                },
                new JObject
                {
                    ["name"] = "x1_set_query_log",
                    ["annotations"] = new JObject { ["title"] = "Set Query Logging", ["readOnlyHint"] = false, ["destructiveHint"] = false },
                    ["description"] = "Enable or disable diagnostic logging of the actual search query text/filters sent to X1 (for troubleshooting slow or timed-out searches). Off by default — no query content is ever logged unless this is explicitly turned on. Call with no 'enabled' argument to report the current state without changing it.",
                    ["inputSchema"] = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
              ""enabled"": { ""type"": ""boolean"", ""description"": ""true to start logging query content, false to stop. Omit to just report the current state."" }
            }
          }")
                },
                new JObject
                {
                    ["name"] = "x1_version",
                    ["annotations"] = new JObject { ["title"] = "Get Connector Version", ["readOnlyHint"] = true, ["destructiveHint"] = false },
                    ["description"] = "Reports which connector build is actually answering: this binary's version and path, the install flavor, and every X1McpBridge/X1McpGraphQL process running on the machine. Use it to confirm a deployment took effect - the shared relay listens on a fixed port regardless of which install started it, so the binary serving a session is not always the one you just installed, and a leftover one answers normally rather than erroring.",
                    ["inputSchema"] = JObject.Parse(@"{ ""type"": ""object"", ""properties"": {} }")
                }
            };
        }

        /// <summary>
        /// This bridge's own identity: the version stamped from X1Mcp\version.props at build time,
        /// and the exe it was loaded from. The path matters as much as the version - a relay launched
        /// from another install answers on the same port and looks identical otherwise.
        ///
        /// Also reports the machine-wide picture (flavor, role, and every running bridge/daemon),
        /// because in the Lean flavor there is no daemon above this process to assemble it. Before
        /// that, this returned only {component,version,path} and the nested
        /// {daemon,bridge,mismatch,runningBridges} shape came from the daemon's own gateway - so a
        /// Lean install silently answered a strictly smaller payload, in the one tool whose entire
        /// purpose is to stop "which build is answering?" from being a silent question. The daemon's
        /// projection was widened to pass these fields through (see X1McpGateway.Stats.cs), so both
        /// flavors report the same field names.
        /// </summary>
        internal static JObject BuildVersionInfo()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string path;
            try { path = asm.Location; }
            catch { path = null; }

            var info = new JObject
            {
                ["component"] = "X1McpBridge",
                ["version"] = asm.GetName().Version.ToString(),
                ["path"] = string.IsNullOrEmpty(path) ? null : path
            };

            // Which flavor this install is, and what this particular process is doing in it. Three
            // roles are possible: the Lean relay ("host"), the Full daemon's spawned WCF owner
            // ("stdio-child"), or a plain stdio bridge a client launched directly.
            try
            {
                var mode = BridgeConfig.GetRelayMode();
                info["flavor"] = mode == RelayMode.Host ? "lean" : "full";
                info["relayComponent"] = mode == RelayMode.Host
                    ? RelayHealth.ComponentHost
                    : RelayHealth.ComponentDaemon;
            }
            catch
            {
                // Config unreadable - leave the flavor unstated rather than guessing wrong.
            }

            // Only meaningful, and only reported, when THIS process is actually running the --host
            // loop: a --proxy shim or plain stdio bridge never runs this timer, so reporting a
            // configured-but-inert value here would answer a question nobody asked with a number
            // that describes nothing this process does. XS-1692: this is the diagnostic surface that
            // was missing when an idle-shutdown registry change was hard to verify from outside the
            // process - ask the running relay what it's actually using instead of inspecting the
            // registry from a possibly-different vantage point.
            if (HostMode.IsHostRunning)
            {
                try
                {
                    info["idleShutdownSeconds"] = HostMode.IdleShutdownSecondsProvider();
                }
                catch
                {
                    // best-effort diagnostic field; never fail x1_version over it
                }
            }

            // XS-1578: best-effort so QueryLogEnabledProvider's registry read can never fail
            // x1_version - lets the current state of the flag be confirmed with zero log noise,
            // the same rationale idleShutdownSeconds above was added under XS-1692 for.
            try
            {
                info["queryLogEnabled"] = QueryLogEnabledProvider();
            }
            catch
            {
                // best-effort diagnostic field; never fail x1_version over it
            }

            try
            {
                var bridges = RelayProcessScanner.ScanBridges();
                var daemons = RelayProcessScanner.ScanDaemons();
                info["runningBridges"] = bridges;
                info["runningBridgesDisagree"] = RelayProcessScanner.VersionsDisagree(bridges);
                // On a Lean machine any daemon found here is a leftover: either a plugin still ships
                // one, or a surviving logon task keeps restarting it. Either way it can win the port
                // and serve every session from the old install, so it is worth surfacing loudly.
                info["runningDaemons"] = daemons;
            }
            catch (Exception ex)
            {
                info["scanError"] = ex.Message;
            }

            // Which Claude application is actually driving this session, detected by inspecting
            // other processes on the machine rather than trusting the MCP clientInfo self-report
            // (see AgentProcessScanner's doc comment for why that's unreliable in practice).
            try
            {
                info["detectedClaudeProcesses"] = AgentProcessScanner.ScanClaudeProcesses();
            }
            catch (Exception ex)
            {
                info["detectedClaudeProcessesError"] = ex.Message;
            }

            return info;
        }

        // XS-1583-CONCURRENCY-WORKAROUND: see X1ConcurrencyWorkaround.cs for the full writeup.
        // Serializes tool dispatch so this bridge never issues overlapping X1 service calls.
        // TO BACK OUT: delete this method, rename CallToolInner back to CallTool.
        private static JObject CallTool(JObject prms) =>
            X1ConcurrencyWorkaround.RunSerialized(() => CallToolInner(prms));

        private static JObject CallToolInner(JObject prms)
        {
            string name = prms.Value<string>("name");
            var args = prms["arguments"] as JObject ?? new JObject();
            NormalizeAndValidateArgs(name, args);

            // Answered from the assembly alone - no X1ServiceHost round-trip. That's deliberate:
            // "which build is this?" must still be answerable when the service is down, since
            // that's exactly when someone is trying to work out what they're running.
            if (name == "x1_version")
                return ToolTextResult(BuildVersionInfo().ToString(Formatting.None));

            // XS-1671: X1 Search MCP access is gated on licensing (X1MCPService.Connect() returns
            // "Unlicensed" when LicenseManager.Instance.IsPaidPluginEnabled(Plugin.MCP) is false).
            // Without this check, every other tool call below would independently discover the
            // same unlicensed session - most by timing out waiting for a callback that will never
            // fire - and each report it differently. Short-circuit once, here, with a single clear
            // message instead of a scattering of timeouts/empty results.
            if (ServiceConnection.IsUnlicensed)
                return ToolTextResult(new JObject
                {
                    ["status"] = "error",
                    ["message"] = BridgeConstants.NotLicensedForMcp
                }.ToString(Formatting.None));

            if (name == "x1_search")
            {
                int timeout = args.Value<int?>("timeoutMs") ?? 60000;
                int limit = args.Value<int?>("limit") ?? 20;
                bool snippets = args.Value<bool?>("include_snippets") ?? true;
                bool includeActions = args.Value<bool?>("includeActions") ?? true;
                bool progenitor = args.Value<bool?>("progenitorSearch") ?? false;
                string query = args.Value<string>("query") ?? "";

                var task = Search.SearchAsync(query, args["tables"], progenitor, limit, snippets, includeActions,
                    args["filters"], args["displayFields"], args["sort"], timeout);
                var t0 = DateTime.UtcNow;
                var searchResult = task.GetAwaiter().GetResult();
                long elapsedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
                int x1Tokens = CostTracker.RecordSearch(searchResult, elapsedMs);
                LogPerf(name, elapsedMs, searchResult, x1Tokens,
                    QueryLogEnabledProvider() ? "query=" + query : null);
                return ToolTextResult(searchResult.ToString(Formatting.None));
            }

            if (name == "x1_get_metadata")
            {
                int timeout = args.Value<int?>("timeoutMs") ?? 30000;
                var task = Search.GetMetadataAsync(args.Value<string>("table"), args.Value<string>("uri"), args["fields"], timeout);
                var t0 = DateTime.UtcNow;
                var metaResult = task.GetAwaiter().GetResult();
                long elapsedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
                int x1Tokens = CostTracker.RecordMetadata(metaResult, elapsedMs);
                LogPerf(name, elapsedMs, metaResult, x1Tokens,
                    QueryLogEnabledProvider() ? "fields=" + (args["fields"]?.ToString(Formatting.None) ?? "") : null);
                return ToolTextResult(metaResult.ToString(Formatting.None));
            }

            if (name == "x1_list_sources")
            {
                var t0 = DateTime.UtcNow;
                // Only report sources X1ServiceHost's own GetDataSourcesInfo() actually
                // confirmed - previously this was padded with every table BridgeConfig knew
                // how to configure, whether or not it was actually present on this machine.
                // XS-1583-SESSION-LIFECYCLE: reuse the shared, already-connected ServiceConnection
                // instead of opening/closing a fresh one per call.
                ConfiguredDataSourceInfo[] info;
                try
                {
                    info = ServiceConnection.GetDataSourcesInfoAsync(timeoutMs: 5000).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log.Debug("x1_list_sources: GetDataSourcesInfo failed, returning no sources: " + ex.Message);
                    info = new ConfiguredDataSourceInfo[0];
                }
                var sources = DataSourceInfoMapper.BuildSources(info);

                var sourcesResult = new JObject { ["sources"] = sources };
                long elapsedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
                int x1Tokens = CostTracker.RecordSources(sourcesResult, elapsedMs);
                LogPerf(name, elapsedMs, sourcesResult, x1Tokens);
                return ToolTextResult(sourcesResult.ToString(Formatting.None));
            }

            if (name == "x1_get_schema_fields")
            {
                int timeout = args.Value<int?>("timeoutMs") ?? 15000;
                // XS-1640: resolve/validate the table BEFORE calling GetSchemaFieldsAsync - an
                // unrecognized table previously reached the service, which silently returned null
                // (logged, never surfaced) and this handler turned into an uninformative {"fields":[]}
                // with no indication the table name was the cause. Now it throws a descriptive error
                // naming the bad/ambiguous value and listing valid schemas instead.
                string table = TableResolver.ResolveOrThrowAsync(args.Value<string>("table")).GetAwaiter().GetResult();
                // XS-1583-SESSION-LIFECYCLE: reuse the shared, already-connected ServiceConnection
                // instead of opening/closing a fresh one per call.
                var t0 = DateTime.UtcNow;
                var fields = ServiceConnection.GetSchemaFieldsAsync(table, timeout)
                    .GetAwaiter().GetResult();
                long elapsedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
                var arr = new JArray();
                foreach (var f in fields)
                    arr.Add(new JObject
                    {
                        ["name"] = f.Name,
                        ["displayName"] = f.DisplayName,
                        ["fieldType"] = f.FieldType.ToString(),
                        // X1FieldInfo (a plain serializable POCO, unlike X1Field) only carries the raw
                        // Flags bitmask — derive the same booleans X1Field used to expose.
                        ["isIndexed"] = (f.Flags & X1FieldFlags.Indexed) == X1FieldFlags.Indexed,
                        ["isContent"] = (f.Flags & X1FieldFlags.Content) == X1FieldFlags.Content,
                        ["isStored"] = (f.Flags & X1FieldFlags.NotStored) != X1FieldFlags.NotStored
                    });
                var schemaFieldsResult = new JObject { ["fields"] = arr };
                LogPerf(name, elapsedMs, schemaFieldsResult, x1Tokens: null);
                return ToolTextResult(schemaFieldsResult.ToString(Formatting.None));
            }

            if (name == "x1_list_actions")
            {
                return ToolTextResult(Actions.ListActions(
                    args.Value<string>("table"), args.Value<string>("uri"))
                    .ToString(Formatting.None));
            }

            if (name == "x1_execute_action")
            {
                int timeout = args.Value<int?>("timeoutMs") ?? 30000;
                var task = Actions.ExecuteActionAsync(
                    args.Value<string>("table"), args.Value<string>("uri"),
                    args.Value<string>("action"), timeout);
                var t0 = DateTime.UtcNow;
                var actionResult = task.GetAwaiter().GetResult();
                long elapsedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
                int x1Tokens = CostTracker.RecordAction(actionResult, elapsedMs);
                LogPerf(name, elapsedMs, actionResult, x1Tokens);
                return ToolTextResult(actionResult.ToString(Formatting.None));
            }

            if (name == "x1_get_content")
            {
                int timeout = args.Value<int?>("timeoutMs") ?? 120000;
                string table = args.Value<string>("table");
                string uri = args.Value<string>("uri");
                var task = Search.GetContentAsync(table, uri, args.Value<string>("mode"), timeout);
                var t0 = DateTime.UtcNow;
                var contentResult = task.GetAwaiter().GetResult();
                long elapsedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
                int x1Tokens = CostTracker.RecordContent(contentResult, table, uri, elapsedMs);
                LogPerf(name, elapsedMs, contentResult, x1Tokens);
                return ToolTextResult(contentResult.ToString(Formatting.None));
            }

            if (name == "x1_generate_preview")
            {
                int timeout = args.Value<int?>("timeoutMs") ?? 120000;
                int maxChars = args.Value<int?>("maxChars") ?? 0;
                string output = args.Value<string>("output") ?? "inline";
                string table = args.Value<string>("table");
                string uri = args.Value<string>("uri");
                var task = Actions.GeneratePreviewAsync(
                    table, uri, timeout, maxChars, output);
                var t0 = DateTime.UtcNow;
                var previewResult = task.GetAwaiter().GetResult();
                long elapsedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
                int x1Tokens = CostTracker.RecordPreview(previewResult, output, elapsedMs, table, uri);
                LogPerf(name, elapsedMs, previewResult, x1Tokens);
                return ToolTextResult(previewResult.ToString(Formatting.None));
            }

            if (name == "x1_extract_file")
            {
                int timeout = args.Value<int?>("timeoutMs") ?? 120000;
                var task = Search.ExtractFileAsync(args.Value<string>("file"), timeout);
                var t0 = DateTime.UtcNow;
                var extractResult = task.GetAwaiter().GetResult();
                long elapsedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
                int x1Tokens = CostTracker.RecordExtractFile(extractResult, elapsedMs);
                LogPerf(name, elapsedMs, extractResult, x1Tokens);
                return ToolTextResult(extractResult.ToString(Formatting.None));
            }

            if (name == "x1_export_html")
            {
                int timeout = args.Value<int?>("timeoutMs") ?? 120000;
                string file = args.Value<string>("file");
                Task<JObject> task = string.IsNullOrEmpty(file)
                    ? Search.ExportHtmlAsync(args.Value<string>("table"), args.Value<string>("uri"), timeout)
                    : Search.ExportHtmlFromFileAsync(file, timeout);
                var t0 = DateTime.UtcNow;
                var exportResult = task.GetAwaiter().GetResult();
                long elapsedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
                LogPerf(name, elapsedMs, exportResult, x1Tokens: null);
                return ToolTextResult(exportResult.ToString(Formatting.None));
            }

            if (name == "x1_add_tags" || name == "x1_remove_tags" || name == "x1_clear_tags")
            {
                int timeout = args.Value<int?>("timeoutMs") ?? 30000;
                string table = args.Value<string>("table");
                string[] uris = ToStringArray(args["uris"]);
                Task<JObject> task;
                if (name == "x1_add_tags")
                {
                    string[] tags = ToStringArray(args["tags"]);
                    task = Search.AddTagsAsync(table, uris, tags, timeout);
                }
                else if (name == "x1_remove_tags")
                {
                    string[] tags = ToStringArray(args["tags"]);
                    task = Search.RemoveTagsAsync(table, uris, tags, timeout);
                }
                else
                {
                    task = Search.ClearTagsAsync(table, uris, timeout);
                }
                var t0 = DateTime.UtcNow;
                var tagResult = task.GetAwaiter().GetResult();
                long elapsedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
                int x1Tokens = CostTracker.RecordTagOp(tagResult, elapsedMs);
                LogPerf(name, elapsedMs, tagResult, x1Tokens);
                return ToolTextResult(tagResult.ToString(Formatting.None));
            }

            if (name == "x1_cost_savings")
            {
                // Serialize first so the recorded cost is this report's real size rather than a
                // guess. The returned report therefore excludes its own call, which is the same
                // ordering every other tool has: you see the state as of before your request.
                string reportJson = CostTracker.GetReport().ToString(Formatting.None);
                CostTracker.RecordCostSavingsQuery(reportJson.Length / 4);
                return ToolTextResult(reportJson);
            }

            if (name == "x1_reset_stats")
            {
                CostTracker.Reset();
                return ToolTextResult(@"{""status"":""ok"",""message"":""Token cost statistics have been reset.""}");
            }

            if (name == "x1_set_query_log")
            {
                bool? enabled = args.Value<bool?>("enabled");
                if (enabled.HasValue)
                    QueryLogWriter(enabled.Value);
                return ToolTextResult(new JObject
                {
                    ["status"] = "ok",
                    ["queryLogEnabled"] = QueryLogEnabledProvider()
                }.ToString(Formatting.None));
            }

            throw new InvalidOperationException("Unknown tool: " + name);
        }

        /// <summary>
        /// XS-1642 follow-up: an LLM caller (or any MCP client) that misnames a top-level argument -
        /// e.g. "table" where a tool's schema requires the plural "tables" array - previously failed
        /// silently. JSON-RPC tool arguments are just a JObject; an unrecognized key is simply never
        /// read by the code below, so the call proceeded with whatever default the downstream code
        /// used instead of the caller's actual intent (x1_search's "table" mistake fell through to
        /// SearchBridge.ResolveTablesAsync's BridgeConfig.GetDefaultTables() and searched the wrong
        /// table with a normal-looking, wrong, response - the exact failure mode that motivated this).
        ///
        /// This checks every top-level key in <paramref name="args"/> against the tool's declared
        /// inputSchema properties (ToolSchemasByName, built from the same JSON tools/list advertises,
        /// so the two can never drift apart). A key that doesn't match a declared property is checked
        /// against the single most common shape of this mistake - singular/plural confusion (table vs
        /// tables, uri vs uris) - and auto-corrected in place: the key is renamed, and its value is
        /// wrapped into a one-element array or unwrapped from one as the target property's declared
        /// type requires. Anything left over is a genuine unknown parameter this method can't
        /// confidently reinterpret: it fails loudly with an ArgumentException naming exactly which
        /// key(s) are wrong and what the valid names are (mirroring TableSchemaResolver.ResolveOrThrowAsync's
        /// "name the bad token, don't just return nothing" convention), rather than adding a fourth
        /// kind of silent default to the ones this method exists to stop.
        /// </summary>
        // Internal (not private) so McpServerProtocolTests can exercise it directly without a
        // live WCF connection - x1_search's own CallToolInner path needs X1ServiceHost reachable
        // to test end-to-end, but this method's normalization/validation logic doesn't.
        internal static void NormalizeAndValidateArgs(string toolName, JObject args)
        {
            if (!ToolSchemasByName.TryGetValue(toolName, out var schema) || schema == null)
                return; // unknown tool name - CallToolInner's final `throw` reports this

            if (!(schema["properties"] is JObject props) || props.Count == 0)
                return; // tool declares no parameters - nothing to validate against

            var validNames = new HashSet<string>(props.Properties().Select(p => p.Name), StringComparer.Ordinal);
            var unknown = new List<string>();

            foreach (var prop in args.Properties().ToList())
            {
                string key = prop.Name;
                if (validNames.Contains(key))
                    continue;

                string aliasTarget =
                    (!key.EndsWith("s", StringComparison.Ordinal) && validNames.Contains(key + "s")) ? key + "s"
                    : (key.EndsWith("s", StringComparison.Ordinal) && key.Length > 1 && validNames.Contains(key.Substring(0, key.Length - 1))) ? key.Substring(0, key.Length - 1)
                    : null;

                // Only auto-correct when the caller didn't ALSO supply the correctly-named
                // parameter - if both are present, something odder than a typo is going on, and
                // silently picking one would hide that rather than surface it.
                if (aliasTarget != null && args[aliasTarget] == null)
                {
                    JToken value = prop.Value;
                    bool wantsArray = (props[aliasTarget] as JObject)?.Value<string>("type") == "array";
                    if (wantsArray && value.Type != JTokenType.Array)
                        value = new JArray(value);
                    else if (!wantsArray && value is JArray singleElement && singleElement.Count == 1)
                        value = singleElement[0];

                    Log.Warn("McpServer: tool '" + toolName + "' called with unrecognized parameter '" + key +
                              "' - auto-corrected to '" + aliasTarget + "'. The caller should be fixed to use '" +
                              aliasTarget + "' directly.");
                    args[aliasTarget] = value;
                    args.Remove(key);
                    continue;
                }

                unknown.Add(key);
            }

            if (unknown.Count > 0)
            {
                string validList = string.Join(", ", validNames.OrderBy(n => n, StringComparer.Ordinal));
                throw new ArgumentException(
                    "Unknown parameter(s) for tool '" + toolName + "': " + string.Join(", ", unknown) +
                    ". Valid parameters are: " + validList + ".");
            }
        }

        private static string[] ToStringArray(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return new string[0];
            if (token is JArray arr)
            {
                var list = new System.Collections.Generic.List<string>(arr.Count);
                foreach (var t in arr) list.Add(t == null ? "" : t.ToString());
                return list.ToArray();
            }
            throw new ArgumentException("Expected a JSON array of strings.");
        }

        // XS-1594: one structured log line per tool call — the bridge's own elapsed time
        // (X1 WCF round-trip + JSON formatting), result size, and an estimated token count.
        // This is the "X1-side timing" half of attributing time between X1 and Claude; the
        // Claude-side half is inferred externally by comparing this against the client's own
        // end-to-end latency for the same call. Always logged (Log.Info) since it carries no
        // query content — no privacy gating needed.
        //
        // XS-1578: traceDetail is the query-content field, appended to this same line (never a
        // second log line) so the same grep that finds a slow call also shows what it was for.
        // Callers pass non-null only when QueryLogEnabledProvider() is true - null (the default)
        // leaves this line byte-identical to before XS-1578.
        private static void LogPerf(string toolName, long elapsedMs, JObject result, int? x1Tokens, string traceDetail = null)
        {
            try
            {
                string resultJson = result?.ToString(Formatting.None) ?? "";
                int tokens = x1Tokens ?? (resultJson.Length / 4);
                string line = "PERF tool=" + toolName + " elapsedMs=" + elapsedMs +
                    " bytes=" + resultJson.Length + " estTokens=" + tokens;
                if (traceDetail != null)
                    line += " " + traceDetail;
                Log.Info(line);
            }
            catch (Exception ex)
            {
                Log.Debug("LogPerf failed for tool=" + toolName + ": " + ex.Message);
            }
        }

        private static JObject ToolTextResult(string text)
        {
            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = text }
                },
                ["isError"] = false
            };
        }

        private static JObject ListResources()
        {
            var resources = new JArray
            {
                new JObject
                {
                    ["uri"] = "x1://index/stats",
                    ["name"] = "x1_index_stats",
                    ["description"] = "X1 service host status string from GetX1ServiceHostStatus()",
                    ["mimeType"] = "application/json"
                }
            };
            return new JObject { ["resources"] = resources };
        }

        private static JObject ReadResource(JObject prms)
        {
            string uri = prms.Value<string>("uri");
            if (uri != "x1://index/stats")
                throw new InvalidOperationException("Unknown resource URI: " + uri);

            // XS-1583-SESSION-LIFECYCLE: reuse the shared, already-connected ServiceConnection
            // instead of opening/closing a fresh one per call.
            string status;
            try
            {
                status = ServiceConnection.ConnectAndGetHostStatus();
            }
            catch (Exception ex)
            {
                status = "error: " + ex.Message;
            }

            var body = new JObject
            {
                ["getX1ServiceHostStatus"] = status,
                ["user"] = Environment.UserName,
                ["machine"] = Environment.MachineName,
                ["note"] = "Rich per-scanner stats require future service API exposure; this is the same status string the desktop UI uses."
            };

            return new JObject
            {
                ["contents"] = new JArray
                {
                    new JObject
                    {
                        ["uri"] = uri,
                        ["mimeType"] = "application/json",
                        ["text"] = body.ToString(Formatting.None)
                    }
                }
            };
        }

        private static JObject ListPrompts()
        {
            var prompts = new JArray
            {
                new JObject
                {
                    ["name"] = "x1_search_best_practices",
                    ["description"] = "How to query X1 effectively from an MCP client."
                }
            };
            return new JObject { ["prompts"] = prompts };
        }

        private static JObject GetPrompt(JObject prms)
        {
            string name = prms.Value<string>("name");
            if (name != "x1_search_best_practices")
                throw new InvalidOperationException("Unknown prompt: " + name);

            var text = new StringBuilder();
            text.AppendLine("X1 MCP bridge — search tips:");
            text.AppendLine("- x1_search accepts one OR multiple tables per call: tables=[\"Files\",\"MSMail\",\"Teams\"] searches all three and merges the results into one response (with a per-table \"byTable\" breakdown) - no need for a separate call per table. Tables are searched sequentially internally (never in parallel), so an N-table call can take up to N x timeoutMs in the worst case; prefer fewer tables or a shorter timeoutMs when latency matters.");
            text.AppendLine("- For local files: tables=[\"Files\"]. For Gmail: tables=[\"Gmail\"]. For Dropbox: tables=[\"Dropbox\"]. Other mail sources vary by install (e.g. Outlook may be \"MSMail\", \"Email\", or \"Exchange\") - call x1_list_sources and pass any of its name/displayName/schemas value; the bridge resolves it to the correct schema or returns a descriptive error listing valid schemas.");
            text.AppendLine("- If the user's request spans multiple source types (e.g. 'search my files and emails'), pass all the relevant tables in ONE x1_search call (tables=[\"Files\",\"MSMail\"]) rather than making a separate call per table.");
            text.AppendLine("- Call x1_list_sources first to discover all available tables and their columns.");
            text.AppendLine("- Use the filters parameter to narrow results: {\"path\": \"woodworking\"} limits Files to a folder; {\"from\": \"alice\"} filters email by sender.");
            text.AppendLine("- Query syntax: terms are implicitly AND-ed ('project status' = 'project AND status'). Use AND, OR, NOT explicitly for control.");
            text.AppendLine("- PRECEDENCE WARNING: 'project status failed OR threatened' is parsed as '(project AND status AND failed) OR threatened' — probably not what you want.");
            text.AppendLine("  Use parentheses to fix it: 'project status (failed OR threatened)' means 'project AND status AND (failed OR threatened)'.");
            text.AppendLine("- Column syntax: use column:value in the query to restrict a term to a specific indexed field.");
            text.AppendLine("  Examples: type:cs (C# files), type:pdf, path:X1Desktop (folder), name:scanner (filename), subject:invoice (email), from:alice (sender).");
            text.AppendLine("  Combine with free text: 'scanner type:cs path:X1Service' finds C# files in X1Service containing 'scanner'.");
            text.AppendLine("- Results include \"uri\" and \"table\"; pass those to x1_get_metadata and x1_get_content.");
            text.AppendLine("- Use x1_get_content mode \"content\" (default via \"auto\") for extracted text — works for ANY table (Files, Gmail, OneDrive, Teams…) and hits the content store on repeat calls. \"preview\" gives HTML/mail-card previews; \"internal\" returns raw index fields.");
            text.AppendLine("- To read a .pptx/.xlsx/.pdf attachment, x1_get_content mode \"content\" on the attachment URI is now the one-call answer — no local extraction script needed.");
            text.AppendLine("- Use x1_extract_file to pull text from an arbitrary local file that isn't (yet) indexed, e.g. a cached preview path or a fresh download.");
            text.AppendLine("- Tagging: x1_add_tags / x1_remove_tags take positional arrays (uris[i] ↔ tags[i]); x1_clear_tags wipes all tags on the given URIs. Combine with tag: query filters on x1_search.");
            text.AppendLine("- Use x1_generate_preview to show an item to the user, and RENDER its \"html\" result as an HTML artifact (title = the returned \"title\") so the user sees a formatted preview rather than raw markup. The html is self-contained and safe to render directly.");
            text.AppendLine("- X1ServiceHost must be running under the same Windows user as this bridge.");
            text.AppendLine("- Each x1_search result includes an \"actions\" array by default (includeActions defaults to true) — read it instead of calling x1_list_actions. Pass includeActions:false to omit it for a smaller payload.");
            text.AppendLine("- Use x1_execute_action to act on a result: action=get_path returns the local file path; action=open opens the file, message, or cloud file's local cached copy; action=show_in_folder opens Explorer to the file's location; action=get_url returns a web URL (Gmail/GDrive); action=open_url opens the item in the default browser.");
            text.AppendLine("- Not all actions are available for all tables — the result's actions array (or x1_list_actions) tells you exactly which ones apply.");
            text.AppendLine("- For OneDrive/GDrive results, prefetchInitiated:true means a preview download was kicked off — calling x1_generate_preview promptly (within ~30s) is likely to be fast.");

            return new JObject
            {
                ["description"] = "X1 search best practices",
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JObject
                        {
                            ["type"] = "text",
                            ["text"] = text.ToString()
                        }
                    }
                }
            };
        }
    }
}
