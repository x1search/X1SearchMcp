// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using log4net;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace X1.McpBridge
{
    /// <summary>
    /// Executes Post Search Actions on X1 search results.
    /// </summary>
    internal sealed class ActionBridge
    {
        private static readonly ILog Log = BridgeLogger.GetLogger(typeof(ActionBridge));

        // Only needed for preview-based open (Tier 3: MSMail, GDrive).
        private readonly SearchBridge _search;
        private readonly TableSchemaResolver _tableResolver;

        // Gmail labels that can be used to build a direct web URL.
        private static readonly string[] GmailFactoryLabels = { "INBOX", "SENT", "TRASH" };

        public ActionBridge(SearchBridge search, TableSchemaResolver tableResolver)
        {
            _search = search ?? throw new ArgumentNullException("search");
            _tableResolver = tableResolver ?? throw new ArgumentNullException("tableResolver");
        }

        public JObject ListActions(string table, string uri)
        {
            if (string.IsNullOrEmpty(table))
                throw new ArgumentException("table is required.");
            if (string.IsNullOrEmpty(uri))
                throw new ArgumentException("uri is required.");

            // ListActions is otherwise synchronous; blocking on the resolver here matches this
            // codebase's existing style of blocking on short-lived async calls (e.g. McpServer's
            // task.GetAwaiter().GetResult() at each tool dispatch site).
            table = _tableResolver.ResolveOrThrowAsync(table).GetAwaiter().GetResult();

            var actionsArray = new JArray();
            foreach (var (action, description) in ActionRegistry.GetActions(table))
            {
                actionsArray.Add(new JObject
                {
                    ["action"] = action,
                    ["description"] = description
                });
            }

            return new JObject
            {
                ["table"] = table,
                ["uri"] = uri,
                ["actions"] = actionsArray
            };
        }

        public async Task<JObject> ExecuteActionAsync(string table, string uri, string action, int timeoutMs)
        {
            if (string.IsNullOrEmpty(table))
                throw new ArgumentException("table is required.");
            if (string.IsNullOrEmpty(uri))
                throw new ArgumentException("uri is required.");
            if (string.IsNullOrEmpty(action))
                throw new ArgumentException("action is required.");

            table = await _tableResolver.ResolveOrThrowAsync(table).ConfigureAwait(false);
            Log.Debug("ExecuteActionAsync table=" + table + " uri=" + uri + " action=" + action);

            if (!ActionRegistry.IsActionSupported(table, action))
            {
                return new JObject
                {
                    ["action"] = action,
                    ["status"] = "error",
                    ["message"] = "Action '" + action + "' is not supported for table '" + table + "'. " +
                                  "Call x1_list_actions to see available actions."
                };
            }

            var a = action.ToLowerInvariant();

            if (a == "get_path")
                return ActionGetPath(table, uri);

            if (a == "open")
                return await ActionOpenAsync(table, uri, timeoutMs).ConfigureAwait(false);

            if (a == "show_in_folder")
                return ActionShowInFolder(uri);

            if (a == "get_url")
                return await ActionGetUrlAsync(table, uri, timeoutMs).ConfigureAwait(false);

            if (a == "open_url")
                return await ActionOpenUrlAsync(table, uri, timeoutMs).ConfigureAwait(false);

            // Should not reach here — IsActionSupported guards above.
            return new JObject
            {
                ["action"] = action,
                ["status"] = "error",
                ["message"] = "Action '" + action + "' is registered but has no handler."
            };
        }

        // ── Tier 1: Files ────────────────────────────────────────────────────────

        private static JObject ActionGetPath(string table, string uri)
        {
            var path = StripFilesUriPrefix(uri);
            if (path == null)
            {
                return new JObject
                {
                    ["action"] = "get_path",
                    ["status"] = "error",
                    ["message"] = "URI does not appear to be a local file path: " + uri
                };
            }

            return new JObject
            {
                ["action"] = "get_path",
                ["status"] = "ok",
                ["path"] = path
            };
        }

        private async Task<JObject> ActionOpenAsync(string table, string uri, int timeoutMs)
        {
            // For Files: open via local path.
            if (string.Equals(table, "Files", StringComparison.OrdinalIgnoreCase))
            {
                var path = StripFilesUriPrefix(uri);
                if (path == null)
                    return ErrorResult("open", "URI does not appear to be a local file path: " + uri);

                if (!File.Exists(path))
                    return ErrorResult("open", "File not found: " + path);

                try
                {
                    var psi = new ProcessStartInfo { FileName = path, UseShellExecute = true };
                    Process.Start(psi);
                    Log.Info("Opened file: " + path);
                    return new JObject
                    {
                        ["action"] = "open",
                        ["status"] = "ok",
                        ["message"] = "Opened " + Path.GetFileName(path)
                    };
                }
                catch (Exception ex)
                {
                    Log.Warn("ActionOpen failed for " + path + ": " + ex.Message);
                    return ErrorResult("open", ex.Message);
                }
            }

            // For cloud file tables (OneDrive, GDrive, Dropbox, SharePoint, SP365): prefer a live
            // locally-synced copy, then fall back to the X1 cache, then to the preview callback.
            if (IsCloudFileTable(table))
            {
                // Tier 1: For OneDrive, look for the file in the OneDrive local sync folders
                // (e.g. C:\Users\…\OneDrive - Company\…) so edits sync back automatically.
                if (string.Equals(table, "OneDrive", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var meta = await _search.GetMetadataAsync(
                            table, uri, new JArray("name"), timeoutMs).ConfigureAwait(false);
                        var fileName = (meta?["fields"] as JObject)?.Value<string>("name");
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            var synced = TryFindOneDriveSyncedFile(fileName);
                            if (synced != null)
                            {
                                try
                                {
                                    Process.Start(new ProcessStartInfo { FileName = synced, UseShellExecute = true });
                                    Log.Info("Opened OneDrive synced file: " + synced);
                                    return new JObject
                                    {
                                        ["action"] = "open",
                                        ["status"] = "ok",
                                        ["message"] = "Opened live synced copy: " + Path.GetFileName(synced)
                                    };
                                }
                                catch (Exception ex)
                                {
                                    Log.Warn("ActionOpen (OneDrive synced) failed for " + synced + ": " + ex.Message);
                                    // Fall through to X1 cache.
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("ActionOpen (OneDrive sync lookup) failed: " + ex.Message);
                        // Fall through to X1 cache.
                    }
                }

                // Tier 2: X1 local cache (instant, no callback race).
                var cached = TryFindCachedPreviewFile(table, uri);
                if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = cached, UseShellExecute = true });
                        Log.Info("Opened cached cloud file: " + cached);
                        return new JObject
                        {
                            ["action"] = "open",
                            ["status"] = "ok",
                            ["message"] = "Opened local cached copy: " + Path.GetFileName(cached)
                        };
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("ActionOpen (cloud cached) failed for " + cached + ": " + ex.Message);
                        // Fall through to the preview-based open below.
                    }
                }
            }

            // For MSMail and cloud items with no cached copy: open via X1 preview (local temp file or URL).
            return await ActionOpenWithPreviewAsync(table, uri, timeoutMs).ConfigureAwait(false);
        }

        private static bool IsCloudFileTable(string table)
        {
            switch ((table ?? "").ToLowerInvariant())
            {
                case "onedrive":
                case "gdrive":
                case "dropbox":
                case "sharepoint":
                case "sp365":
                    return true;
                default:
                    return false;
            }
        }

        private static JObject ActionShowInFolder(string uri)
        {
            var path = StripFilesUriPrefix(uri);
            if (path == null)
                return ErrorResult("show_in_folder", "URI does not appear to be a local file path: " + uri);

            try
            {
                // /select highlights the item; falls back gracefully if file no longer exists.
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
                Log.Info("Explorer opened to folder for: " + path);
                return new JObject
                {
                    ["action"] = "show_in_folder",
                    ["status"] = "ok",
                    ["message"] = "Opened Explorer to " + Path.GetDirectoryName(path)
                };
            }
            catch (Exception ex)
            {
                Log.Warn("ActionShowInFolder failed for " + path + ": " + ex.Message);
                return ErrorResult("show_in_folder", ex.Message);
            }
        }

        // ── Tier 2: URL-based cloud tables ───────────────────────────────────────

        private async Task<JObject> ActionGetUrlAsync(string table, string uri, int timeoutMs)
        {
            if (string.Equals(table, "Gmail", StringComparison.OrdinalIgnoreCase))
                return await GetGmailUrlAsync(uri, timeoutMs).ConfigureAwait(false);

            if (string.Equals(table, "GDrive", StringComparison.OrdinalIgnoreCase))
                return await GetGDriveUrlAsync(uri, timeoutMs).ConfigureAwait(false);

            return ErrorResult("get_url", "get_url is not implemented for table '" + table + "'.");
        }

        private async Task<JObject> ActionOpenUrlAsync(string table, string uri, int timeoutMs)
        {
            // For Gmail: build URL from metadata then open it.
            if (string.Equals(table, "Gmail", StringComparison.OrdinalIgnoreCase))
            {
                var urlResult = await GetGmailUrlAsync(uri, timeoutMs).ConfigureAwait(false);
                if (urlResult.Value<string>("status") != "ok")
                    return new JObject { ["action"] = "open_url", ["status"] = urlResult["status"], ["message"] = urlResult["message"] };
                return OpenUrl("open_url", urlResult.Value<string>("url"));
            }

            // For GDrive: build URL from metadata then open it.
            if (string.Equals(table, "GDrive", StringComparison.OrdinalIgnoreCase))
            {
                var urlResult = await GetGDriveUrlAsync(uri, timeoutMs).ConfigureAwait(false);
                if (urlResult.Value<string>("status") != "ok")
                    return new JObject { ["action"] = "open_url", ["status"] = urlResult["status"], ["message"] = urlResult["message"] };
                return OpenUrl("open_url", urlResult.Value<string>("url"));
            }

            // For Dropbox, OneDrive, SharePoint, Teams, Slack: use X1 preview to get the URL.
            return await ActionOpenWithPreviewUrlAsync(table, uri, timeoutMs).ConfigureAwait(false);
        }

        private async Task<JObject> GetGmailUrlAsync(string uri, int timeoutMs)
        {
            // Fetch the message id and labels via metadata. The id field is "eid1" in the index
            // (older code looked for "EID", which never matched); labels is "labels". As a final
            // fallback the message id is the last segment of the gmail:// URI itself.
            var meta = await _search.GetMetadataAsync(
                "Gmail", uri,
                new JArray("eid1", "EID", "labels", "Labels"),
                timeoutMs).ConfigureAwait(false);

            var fields = meta["fields"] as JObject;
            var eid = FirstNonEmpty(fields, "eid1", "EID") ?? GmailEidFromUri(uri);
            var labels = FirstNonEmpty(fields, "labels", "Labels");

            if (string.IsNullOrEmpty(eid))
                return ErrorResult("get_url", "Could not determine the Gmail message id for this item.");

            // Mirror GmailOpenPSA: pick the first factory label, default to inbox.
            var label = "inbox";
            if (!string.IsNullOrEmpty(labels))
            {
                var parts = labels.Split(',').Select(l => l.Trim()).ToArray();
                var factoryMatch = parts.FirstOrDefault(
                    x => GmailFactoryLabels.Contains(x, StringComparer.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(factoryMatch))
                    label = factoryMatch.ToLowerInvariant();
            }

            var url = "https://mail.google.com/mail/#" + label + "/" + eid;
            Log.Debug("Gmail URL: " + url);
            return new JObject { ["action"] = "get_url", ["status"] = "ok", ["url"] = url };
        }

        private async Task<JObject> GetGDriveUrlAsync(string uri, int timeoutMs)
        {
            // Fetch webViewLink (or alternateLink) from GDrive metadata.
            var meta = await _search.GetMetadataAsync(
                "GDrive", uri,
                new JArray("webViewLink", "alternateLink"),
                timeoutMs).ConfigureAwait(false);

            var fields = meta["fields"] as JObject;
            var url = fields?.Value<string>("webViewLink")
                   ?? fields?.Value<string>("alternateLink");

            if (string.IsNullOrEmpty(url))
                return ErrorResult("get_url", "Could not retrieve a web URL for this Google Drive item. The item may not have a web link field in the index.");

            Log.Debug("GDrive URL: " + url);
            return new JObject { ["action"] = "get_url", ["status"] = "ok", ["url"] = url };
        }

        // ── Tier 3: Preview-based open ───────────────────────────────────────────

        private async Task<JObject> ActionOpenWithPreviewAsync(string table, string uri, int timeoutMs)
        {
            var content = await _search.GetContentAsync(table, uri, "preview", timeoutMs)
                .ConfigureAwait(false);

            var previewPath = content.Value<string>("preview");
            if (string.IsNullOrEmpty(previewPath))
            {
                var err = content.Value<string>("error");
                return ErrorResult("open", err ?? "Preview not available for this item.");
            }

            // Preview may return either a local file path or an HTTP(S) URL.
            if (previewPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                previewPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return OpenUrl("open", previewPath);
            }

            if (!File.Exists(previewPath))
                return ErrorResult("open", "Preview file not found: " + previewPath);

            try
            {
                var psi = new ProcessStartInfo { FileName = previewPath, UseShellExecute = true };
                Process.Start(psi);
                Log.Info("Opened preview file: " + previewPath);
                return new JObject
                {
                    ["action"] = "open",
                    ["status"] = "ok",
                    ["message"] = "Opened " + Path.GetFileName(previewPath)
                };
            }
            catch (Exception ex)
            {
                Log.Warn("ActionOpenWithPreview failed for " + previewPath + ": " + ex.Message);
                return ErrorResult("open", ex.Message);
            }
        }

        private async Task<JObject> ActionOpenWithPreviewUrlAsync(string table, string uri, int timeoutMs)
        {
            int previewTimeoutMs = Math.Min(timeoutMs, 30000);
            JObject content = null;
            try
            {
                content = await _search.GetContentAsync(table, uri, "preview", previewTimeoutMs)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("ActionOpenWithPreviewUrlAsync preview attempt failed: " + ex.Message);
            }

            var previewPath = content?.Value<string>("preview");
            var additionalData = content?.Value<string>("additionalData");

            // Prefer additionalData (often contains a direct web URL for cloud connectors).
            var url = additionalData ?? previewPath;

            if (!string.IsNullOrEmpty(url) &&
                (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                return OpenUrl("open_url", url);
            }

            // Preview callback timed out or returned no URL — fall back to the local cached file.
            var cachedPath = TryFindCachedPreviewFile(table, uri);
            if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = cachedPath, UseShellExecute = true });
                    Log.Info("Opened cached preview file: " + cachedPath);
                    return new JObject
                    {
                        ["action"] = "open_url",
                        ["status"] = "ok",
                        ["message"] = "Opened local cached copy: " + Path.GetFileName(cachedPath)
                    };
                }
                catch (Exception ex)
                {
                    Log.Warn("ActionOpenWithPreviewUrlAsync: could not open cached file: " + ex.Message);
                }
            }

            var err = content?.Value<string>("error");
            return ErrorResult("open_url", err ?? "Preview timed out and no cached copy was found.");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static string StripFilesUriPrefix(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return null;

            // X1 returns local-file results as "file://C:\path" (and historically "files://").
            // Accept both schemes, plus the RFC "file:///C:/path" form with a leading slash.
            string rest;
            if (uri.StartsWith("files://", StringComparison.OrdinalIgnoreCase))
                rest = uri.Substring("files://".Length);
            else if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                rest = uri.Substring("file://".Length);
            else
                return null;

            // Drop a leading slash before a drive letter: "/C:/..." -> "C:/...".
            if (rest.Length >= 3 && rest[0] == '/' && char.IsLetter(rest[1]) && rest[2] == ':')
                rest = rest.Substring(1);

            return rest.Replace('/', Path.DirectorySeparatorChar);
        }

        private static JObject OpenUrl(string actionName, string url)
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return ErrorResult(actionName, "Invalid URL (must be http or https): " + url);
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                Log.Info("Opened URL: " + url);
                return new JObject
                {
                    ["action"] = actionName,
                    ["status"] = "ok",
                    ["url"] = url,
                    ["message"] = "Opened in default browser"
                };
            }
            catch (Exception ex)
            {
                Log.Warn("OpenUrl failed for " + url + ": " + ex.Message);
                return ErrorResult(actionName, ex.Message);
            }
        }

        private static string FirstNonEmpty(JObject fields, params string[] names)
        {
            if (fields == null)
                return null;
            foreach (var n in names)
            {
                var v = fields.Value<string>(n);
                if (!string.IsNullOrEmpty(v))
                    return v;
            }
            return null;
        }

        // The Gmail message id is the last path segment of a gmail://account/<id> URI.
        internal static string GmailEidFromUri(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return null;
            var s = uri.TrimEnd('/');
            var slash = s.LastIndexOf('/');
            var seg = slash >= 0 ? s.Substring(slash + 1) : s;
            return seg == "0" ? null : seg;   // "0" is the synthetic parent/root id
        }

        private static JObject ErrorResult(string action, string message)
        {
            return new JObject
            {
                ["action"] = action,
                ["status"] = "error",
                ["message"] = message
            };
        }

        // ── x1_generate_preview ──────────────────────────────────────────────────

        public async Task<JObject> GeneratePreviewAsync(string table, string uri, int timeoutMs, int maxChars = 0,
            string output = "inline")
        {
            if (string.IsNullOrEmpty(table))
                throw new ArgumentException("table is required.");
            if (string.IsNullOrEmpty(uri))
                throw new ArgumentException("uri is required.");

            // XS-1678: resolve/validate before dispatching - on a files-only license the server
            // silently drops GeneratePreview for a disallowed table (no callback ever fires), which
            // would otherwise hang this call for the full timeout. Deliberately outside the try
            // below so the resolver's ArgumentException propagates uncaught rather than being
            // swallowed into a generic {"error": ...} result.
            table = await _tableResolver.ResolveOrThrowAsync(table).ConfigureAwait(false);

            Log.Debug("GeneratePreviewAsync table=" + table + " uri=" + uri + " maxChars=" + maxChars +
                      " output=" + (output ?? "inline"));

            JObject result;
            try
            {
                if (IsEmailTable(table))
                    result = await GenerateEmailPreviewAsync(table, uri, timeoutMs).ConfigureAwait(false);
                else if (IsLocalFileTable(table))
                    result = await GenerateFilePreviewAsync(table, uri, timeoutMs, maxChars).ConfigureAwait(false);
                else
                    result = await GenerateMetadataCardPreviewAsync(table, uri, timeoutMs, maxChars).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("GeneratePreviewAsync failed for table=" + table + " uri=" + uri, ex);
                return new JObject { ["error"] = ex.Message };
            }

            // output=file: write the generated HTML to an artifact-ready fragment file and return its
            // path instead of the inline markup. The bytes never travel back through the caller's
            // context — the caller renders the file directly (e.g. as an artifact) for a zero-token
            // view, or reads it only when it needs the content. Inline remains the default so callers
            // that want to reason over the preview still get the HTML directly.
            bool isFileLike = string.Equals(output, "file", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(output, "save", StringComparison.OrdinalIgnoreCase);
            if (isFileLike && result != null && result["html"] != null && result["error"] == null)
            {
                bool isSave = string.Equals(output, "save", StringComparison.OrdinalIgnoreCase);
                string dir = isSave
                    ? Path.Combine(SavedContentDir, DateTime.Today.ToString("yyyy-MM-dd"))
                    : PreviewFileDir;
                try
                {
                    var fileResult = WritePreviewFragmentToFile(
                        result.Value<string>("html"),
                        result.Value<string>("title"),
                        result.Value<string>("previewType"),
                        dir);

                    if (isSave)
                    {
                        // Change mode to "save" so callers and CostTracker can distinguish it.
                        fileResult["mode"] = "save";
                        // Append a one-line record to the manifest for audit / later indexing.
                        AppendToManifest(SavedContentDir, table, uri, fileResult);
                    }

                    return fileResult;
                }
                catch (Exception ex)
                {
                    Log.Warn("GeneratePreviewAsync output=" + output + " write failed; returning inline. " + ex.Message);
                }
            }
            return result;
        }

        // Directory for output=file preview fragments and x1_export_html output. Under the OS
        // temp dir so it is writable without extra permissions. Windows does NOT auto-clean %TEMP%
        // — TempSweep runs at bridge startup to enforce tempMaxAgeHours + tempMaxTotalMB limits.
        internal static string PreviewFileDir =>
            Path.Combine(Path.GetTempPath(), "x1mcp_previews");

        // Persistent directory root for output=save. Configured via BridgeConfig (env var or config file).
        private static string SavedContentDir =>
            BridgeConfig.GetSavedContentDir();

        private static readonly object ManifestSync = new object();

        /// <summary>
        /// Appends a single-line JSON record to <c>{dir}/manifest.json</c> so saved items are
        /// enumerable without scanning the directory.  Errors are swallowed — the manifest is
        /// advisory; losing an entry is better than surfacing an IO failure to the caller.
        /// </summary>
        private static void AppendToManifest(string dir, string table, string uri, JObject fileResult)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var manifestPath = Path.Combine(dir, "manifest.json");
                var relativePath = fileResult.Value<string>("path");
                // Store relative path within savedContentDir for portability.
                if (relativePath != null && relativePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar);

                var entry = new JObject
                {
                    ["savedAt"]     = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["path"]        = relativePath,
                    ["title"]       = fileResult.Value<string>("title"),
                    ["previewType"] = fileResult.Value<string>("previewType"),
                    ["table"]       = table,
                    ["uri"]         = uri
                };
                var line = entry.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine;

                lock (ManifestSync)
                    File.AppendAllText(manifestPath, line, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Warn("AppendToManifest failed (non-fatal): " + ex.Message);
            }
        }

        /// <summary>
        /// Extracts the inner body markup from a full SharedHtmlWrapper document — everything between
        /// the opening &lt;body&gt; tag and the closing &lt;/body&gt;. Returns the input unchanged when
        /// no body element is present (already a fragment).
        /// </summary>
        internal static string ExtractBodyInner(string html)
        {
            if (string.IsNullOrEmpty(html))
                return html;
            int bodyStart = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (bodyStart < 0)
                return html;
            int tagClose = html.IndexOf('>', bodyStart);
            int bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (tagClose < 0 || bodyEnd <= tagClose)
                return html;
            return html.Substring(tagClose + 1, bodyEnd - tagClose - 1).Trim();
        }

        /// <summary>
        /// Writes an artifact-ready HTML fragment (a &lt;title&gt;, the base styles, then the body
        /// markup with no &lt;html&gt;/&lt;head&gt;/&lt;body&gt; wrapper) to a file in <paramref name="dir"/>
        /// and returns a path-only result. Pure given <paramref name="dir"/> so it is unit-testable
        /// without a live X1 service.
        /// </summary>
        internal static JObject WritePreviewFragmentToFile(string html, string title, string previewType, string dir)
        {
            title = string.IsNullOrEmpty(title) ? "preview" : title;
            var fragment = ExtractBodyInner(html);
            // Inline the base styles so the fragment is fully self-styled once the artifact host wraps
            // it in its own head/body skeleton.
            var doc = "<title>" + HtmlEncode(title) + "</title>\n<style>\n" + BaseStyles() + "\n</style>\n" + fragment;

            Directory.CreateDirectory(dir);
            var safe = SanitizeFileName(title);
            if (safe.Length > 60) safe = safe.Substring(0, 60);
            var path = Path.Combine(dir, safe + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".html");
            File.WriteAllText(path, doc, new UTF8Encoding(false));

            return new JObject
            {
                ["mode"] = "file",
                ["path"] = path,
                ["title"] = title,
                ["previewType"] = previewType,
                ["contentType"] = "text/html",
                ["bytes"] = new FileInfo(path).Length
            };
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "preview";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            var result = sb.ToString().Trim();
            return result.Length == 0 ? "preview" : result;
        }

        // internal: also used by CostTracker to classify calls for the cost-savings taxonomy.
        internal static bool IsEmailTable(string table)
        {
            switch (table.ToLowerInvariant())
            {
                case "msmail":
                case "gmail":
                case "exchange":
                case "outlook":
                case "email":
                case "pstemail":
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsLocalFileTable(string table)
        {
            switch (table.ToLowerInvariant())
            {
                case "files":
                case "note":
                case "pstnote":
                case "psttask":
                case "pstcontact":
                case "pstcalendar":
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsMessageTable(string table)
        {
            switch (table.ToLowerInvariant())
            {
                case "teams":
                case "slack":
                case "skype":
                    return true;
                default:
                    return false;
            }
        }

        // ── Tier A: Email ────────────────────────────────────────────────────────

        private async Task<JObject> GenerateEmailPreviewAsync(string table, string uri, int timeoutMs)
        {
            // Always use the WCF preview callback — do NOT scan the local MSMailPreview cache by
            // item-ID prefix. All Exchange item IDs in a given account share a long common prefix
            // (e.g. "AAMkADYyN2E1MjA0LTJl…" for 20+ chars), so a StartsWith prefix match returns
            // an arbitrary cached file rather than the one for this specific message.
            // Use "preview" mode (not "auto") so the callback respects the caller's full timeout.
            // "auto" mode caps at BridgeConfig.GetAutoPreviewTimeoutMs() (10s default) which is
            // too short for MSMail's first-call render from the local mail store (10-30s).
            string previewPath = null;
            string previewAdditionalData = null;
            // Cap the email preview attempt at the auto-preview timeout (default 10 s). Email
            // connectors that support preview (MSMail on first call) can take 10-30 s; connectors
            // that never fire OnPreviewReady (Gmail) would otherwise consume the entire MCP timeout
            // (120 s) before the internal-fields fallback below runs.
            int previewBudgetMs = Math.Min(BridgeConfig.GetAutoPreviewTimeoutMs(), Math.Max(5000, timeoutMs - 5000));
            try
            {
                var pr = await _search.GetContentAsync(table, uri, "preview", previewBudgetMs).ConfigureAwait(false);
                var callbackPath = pr?.Value<string>("preview");
                if (!string.IsNullOrEmpty(callbackPath)) previewPath = callbackPath;
                previewAdditionalData = pr?.Value<string>("additionalData");
            }
            catch (Exception ex)
            {
                Log.Debug("Email preview callback failed for " + uri + ": " + ex.Message);
            }

            if (!string.IsNullOrEmpty(previewPath) && File.Exists(previewPath))
            {
                var embed = await EmbedPreviewFileAsync(previewPath, 0).ConfigureAwait(false);
                // Reject X1 error strings stored in the preview file (e.g. Gmail caches
                // "Error opening gmail://…: File preview not available" on first-call failure).
                if (embed.html != null && !IsX1PreviewError(embed.html))
                {
                    // Fetch header fields for the styled header card above the body.
                    JObject fields2 = new JObject();
                    try
                    {
                        var meta = await _search.GetContentAsync(table, uri, "internal", 15000).ConfigureAwait(false);
                        fields2 = RowsToFields(meta["rows"] as JArray);
                    }
                    catch { }

                    var subject2 = fields2.Value<string>("subject") ?? "(no subject)";
                    var from2    = HtmlEncode(fields2.Value<string>("from") ?? "");
                    var to2      = HtmlEncode(fields2.Value<string>("to") ?? "");
                    var date2    = HtmlEncode(FormatFieldValue("date",
                        fields2.Value<string>("date") ?? fields2.Value<string>("date_sent")) ?? "");

                    var hdr = new StringBuilder();
                    hdr.Append("<div class=\"email-header\">");
                    hdr.Append("<table class=\"meta\"><tbody>");
                    if (!string.IsNullOrEmpty(from2))
                        hdr.Append("<tr><th>From</th><td>").Append(from2).Append("</td></tr>");
                    if (!string.IsNullOrEmpty(to2))
                        hdr.Append("<tr><th>To</th><td>").Append(to2).Append("</td></tr>");
                    if (!string.IsNullOrEmpty(date2))
                        hdr.Append("<tr><th>Date</th><td>").Append(date2).Append("</td></tr>");
                    hdr.Append("<tr><th>Subject</th><td><strong>")
                       .Append(HtmlEncode(subject2)).Append("</strong></td></tr>");
                    hdr.Append("</tbody></table></div>");
                    hdr.Append("<div class=\"email-body\">").Append(embed.html).Append("</div>");

                    return new JObject
                    {
                        ["html"] = SharedHtmlWrapper(subject2, EmailCss() + hdr.ToString()),
                        ["contentType"] = "text/html",
                        ["previewType"] = embed.previewType,
                        ["title"] = subject2
                    };
                }
            }

            // Fallback: metadata card from internal fields (preview callback timed out or binary type).
            var internalContent = await _search.GetContentAsync(table, uri, "internal", 15000).ConfigureAwait(false);
            var fields = RowsToFields(internalContent["rows"] as JArray);

            var subject = HtmlEncode(fields.Value<string>("subject") ?? "(no subject)");
            var from    = HtmlEncode(fields.Value<string>("from") ?? "");
            var to      = HtmlEncode(fields.Value<string>("to") ?? "");
            var date    = HtmlEncode(FormatFieldValue("date", fields.Value<string>("date") ?? fields.Value<string>("date_sent")) ?? "");
            var snippet = fields.Value<string>("snippet") ?? "";

            var body = new StringBuilder();
            body.Append("<div class=\"email-header\">");
            body.Append("<table class=\"meta\"><tbody>");
            if (!string.IsNullOrEmpty(from))
                body.Append("<tr><th>From</th><td>").Append(from).Append("</td></tr>");
            if (!string.IsNullOrEmpty(to))
                body.Append("<tr><th>To</th><td>").Append(to).Append("</td></tr>");
            if (!string.IsNullOrEmpty(date))
                body.Append("<tr><th>Date</th><td>").Append(date).Append("</td></tr>");
            body.Append("<tr><th>Subject</th><td><strong>").Append(subject).Append("</strong></td></tr>");
            body.Append("</tbody></table></div>");

            if (!string.IsNullOrEmpty(snippet))
            {
                body.Append("<div class=\"email-body\">")
                    .Append(HtmlEncode(snippet))
                    .Append("</div>");
            }
            else
            {
                body.Append("<p class=\"no-body\"><em>Email body not available — use the open action to view in Outlook.</em></p>");
            }

            // OWA button: try to resolve a web URL so the user can navigate to the full email.
            string owaUrl = null;
            try
            {
                owaUrl = await TryGetEmailWebUrlAsync(table, uri, previewAdditionalData, 8000).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("TryGetEmailWebUrlAsync failed: " + ex.Message);
            }
            if (!string.IsNullOrEmpty(owaUrl))
            {
                var btnLabel = string.Equals(table, "Gmail", StringComparison.OrdinalIgnoreCase)
                    ? "Open in Gmail &#8599;"
                    : "Open in Outlook Web &#8599;";
                body.Append("<div class=\"open-link\"><a class=\"open-btn\" href=\"")
                    .Append(HtmlEncode(owaUrl))
                    .Append("\" target=\"_blank\" rel=\"noopener\">")
                    .Append(btnLabel)
                    .Append("</a></div>");
            }

            return new JObject
            {
                ["html"] = SharedHtmlWrapper(
                    fields.Value<string>("subject") ?? "(no subject)",
                    EmailCss() + body.ToString()),
                ["contentType"] = "text/html",
                ["previewType"] = "metadata_card",
                ["title"] = fields.Value<string>("subject") ?? "(no subject)"
            };
        }

        // Resolves a navigable web URL for an email item — used to add an OWA/Gmail button to
        // the metadata card fallback. Returns null when no URL can be determined.
        private async Task<string> TryGetEmailWebUrlAsync(string table, string uri, string previewAdditionalData, int timeoutMs)
        {
            // Gmail: reuse the existing URL-builder (message id + labels → mail.google.com link).
            if (string.Equals(table, "Gmail", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var urlResult = await GetGmailUrlAsync(uri, timeoutMs).ConfigureAwait(false);
                    if (urlResult.Value<string>("status") == "ok")
                        return urlResult.Value<string>("url");
                }
                catch (Exception ex)
                {
                    Log.Debug("TryGetEmailWebUrlAsync Gmail URL lookup failed: " + ex.Message);
                }
                return null;
            }

            // Other email tables (Exchange, MSMail): prefer additionalData from the preview callback.
            if (!string.IsNullOrEmpty(previewAdditionalData) &&
                (previewAdditionalData.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 previewAdditionalData.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                return previewAdditionalData;

            // Check metadata fields — Exchange connectors may expose an OWA link directly.
            try
            {
                var meta = await _search.GetMetadataAsync(table, uri,
                    new JArray("url", "OwaLink", "OWALink", "webViewLink", "ItemURI", "webUrl"),
                    Math.Min(timeoutMs, 8000)).ConfigureAwait(false);
                var url = FirstNonEmpty(meta["fields"] as JObject,
                    "url", "OwaLink", "OWALink", "webViewLink", "ItemURI", "webUrl");
                if (!string.IsNullOrEmpty(url) &&
                    (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    return url;
            }
            catch (Exception ex)
            {
                Log.Debug("TryGetEmailWebUrlAsync metadata lookup failed for " + table + "/" + uri + ": " + ex.Message);
            }

            // Last resort for MSMail / Exchange: construct an Exchange Online OWA deep link from
            // the EWS item ID that is embedded in the msmail:// URI. MSMail items don't expose a
            // URL field in the index, but the EWS ID in the URI is sufficient for OWA to resolve
            // the message directly.
            var ewsItemId = ExtractMsMailItemId(uri);
            if (!string.IsNullOrEmpty(ewsItemId))
            {
                Log.Debug("TryGetEmailWebUrlAsync: constructing OWA URL from EWS item ID for " + table + "/" + uri);
                return "https://outlook.office.com/mail/inbox/id/" + Uri.EscapeDataString(ewsItemId);
            }

            return null;
        }

        // Extracts the EWS item ID segment from a msmail:// URI.
        // Format: msmail://<accountId>/<EWSItemId>[/<attachmentId>]
        private static string ExtractMsMailItemId(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            var s = uri;
            var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd >= 0) s = s.Substring(schemeEnd + 3);
            var slash = s.IndexOf('/');
            if (slash < 0) return null;
            s = s.Substring(slash + 1);   // skip accountId segment
            var nextSlash = s.IndexOf('/');
            var itemId = nextSlash >= 0 ? s.Substring(0, nextSlash) : s;
            return string.IsNullOrEmpty(itemId) ? null : itemId;
        }

        // ── Tier B: Local files ──────────────────────────────────────────────────

        private async Task<JObject> GenerateFilePreviewAsync(string table, string uri, int timeoutMs, int maxChars)
        {
            var content = await _search.GetContentAsync(table, uri, "auto", timeoutMs).ConfigureAwait(false);

            // If auto succeeded with a preview file, embed it. EmbedPreviewFile extracts docx to
            // readable HTML and returns null for binary formats (PDF, spreadsheets, images) so we
            // fall through to the metadata card rather than dumping raw bytes.
            if (content.Value<string>("mode") == "preview")
            {
                var previewPath = content.Value<string>("preview");
                if (!string.IsNullOrEmpty(previewPath) && File.Exists(previewPath))
                {
                    var embed = await EmbedPreviewFileAsync(previewPath, maxChars).ConfigureAwait(false);
                    if (embed.html != null)
                    {
                        var fileName = Path.GetFileName(StripFilesUriPrefix(uri) ?? uri);
                        return new JObject
                        {
                            ["html"] = SharedHtmlWrapper(fileName, embed.html),
                            ["contentType"] = "text/html",
                            ["previewType"] = embed.previewType,
                            ["title"] = fileName
                        };
                    }
                }
            }

            // X1's preview callback is unreliable for some local docs (it can time out even for a
            // local file). For local Files the URI *is* the on-disk path, so read and extract the
            // file directly — this makes docx/text previews work regardless of the callback.
            var diskPath = StripFilesUriPrefix(uri);
            if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
            {
                var embed = await EmbedPreviewFileAsync(diskPath, maxChars).ConfigureAwait(false);
                if (embed.html != null)
                {
                    var fileName = Path.GetFileName(diskPath);
                    return new JObject
                    {
                        ["html"] = SharedHtmlWrapper(fileName, embed.html),
                        ["contentType"] = "text/html",
                        ["previewType"] = embed.previewType,
                        ["title"] = fileName
                    };
                }
            }

            // Fallback: render metadata card. Fetch internal fields explicitly — when "auto" mode
            // returns preview mode the rows in that response are empty (binary formats like pdf/xlsx/pptx).
            JObject fields;
            try
            {
                var ic = await _search.GetContentAsync(table, uri, "internal", 15000).ConfigureAwait(false);
                fields = RowsToFields(ic["rows"] as JArray);
            }
            catch
            {
                fields = RowsToFields(content["rows"] as JArray);
            }
            var name = HtmlEncode(fields.Value<string>("name") ?? Path.GetFileName(StripFilesUriPrefix(uri) ?? uri));
            var path = HtmlEncode(FormatFieldValue("path", fields.Value<string>("path")) ?? "");
            var size = HtmlEncode(FormatFieldValue("size", fields.Value<string>("size")) ?? "");
            var modified = HtmlEncode(FormatFieldValue("modified", fields.Value<string>("modified")) ?? "");
            var snippet = HtmlEncode(fields.Value<string>("snippet") ?? "");

            var body = new StringBuilder();
            body.Append("<div class=\"card\">");
            body.Append("<div class=\"card-title\">").Append(name).Append("</div>");
            body.Append("<table class=\"meta\"><tbody>");
            if (!string.IsNullOrEmpty(path))
                body.Append("<tr><th>Path</th><td>").Append(path).Append("</td></tr>");
            if (!string.IsNullOrEmpty(size))
                body.Append("<tr><th>Size</th><td>").Append(size).Append("</td></tr>");
            if (!string.IsNullOrEmpty(modified))
                body.Append("<tr><th>Modified</th><td>").Append(modified).Append("</td></tr>");
            body.Append("</tbody></table>");
            if (!string.IsNullOrEmpty(snippet))
                body.Append("<div class=\"snippet\">").Append(snippet).Append("</div>");
            body.Append("</div>");

            return new JObject
            {
                ["html"] = SharedHtmlWrapper(fields.Value<string>("name") ?? "File", CardCss() + body.ToString()),
                ["contentType"] = "text/html",
                ["previewType"] = "metadata_card",
                ["title"] = fields.Value<string>("name") ?? "File"
            };
        }

        // ── Tier C: Cloud files and messages ─────────────────────────────────────

        private async Task<JObject> GenerateMetadataCardPreviewAsync(string table, string uri, int timeoutMs, int maxChars)
        {
            // Try preview mode with a capped timeout so the MCP call has room to fall back.
            // Some connectors (e.g. OneDrive) have a preview provider that downloads the
            // actual file. Cap at 30s so there is still time for the internal fallback.
            int previewTimeoutMs = Math.Min(timeoutMs / 2, 30000);
            JObject previewContent = null;
            try
            {
                previewContent = await _search.GetContentAsync(table, uri, "preview", previewTimeoutMs)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("Preview attempt failed for " + table + "/" + uri + ": " + ex.Message);
            }

            // Resolve a preview file path — either from the callback or from a local cache.
            string resolvedPreviewPath = previewContent?.Value<string>("preview");
            if (string.IsNullOrEmpty(resolvedPreviewPath))
                resolvedPreviewPath = TryFindCachedPreviewFile(table, uri);

            if (!string.IsNullOrEmpty(resolvedPreviewPath) && File.Exists(resolvedPreviewPath))
            {
                var embed = await EmbedPreviewFileAsync(resolvedPreviewPath, maxChars).ConfigureAwait(false);
                if (embed.html != null)
                {
                    // For Teams/Slack the preview file is an AdaptiveCard JSON blob. Parse it and
                    // render a readable message card; derive the title from index metadata rather
                    // than the GUID cache filename.
                    if (IsMessageTable(table) &&
                        string.Equals(Path.GetExtension(resolvedPreviewPath), ".json",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        JObject msgFields = new JObject();
                        try
                        {
                            var msgMeta = await _search.GetContentAsync(table, uri, "internal", 15000)
                                .ConfigureAwait(false);
                            msgFields = RowsToFields(msgMeta["rows"] as JArray);
                        }
                        catch { }
                        var cardHtml = TryBuildMessageCard(table, msgFields, resolvedPreviewPath);
                        if (cardHtml != null)
                        {
                            var cardTitle = DeriveTitle(table, msgFields);
                            return new JObject
                            {
                                ["html"] = SharedHtmlWrapper(cardTitle, CardCss() + cardHtml),
                                ["contentType"] = "text/html",
                                ["previewType"] = "html",
                                ["title"] = cardTitle
                            };
                        }
                    }

                    var fileName = Path.GetFileName(resolvedPreviewPath);
                    return new JObject
                    {
                        ["html"] = SharedHtmlWrapper(fileName, embed.html),
                        ["contentType"] = "text/html",
                        ["previewType"] = embed.previewType,
                        ["title"] = fileName
                    };
                }
            }

            // Fall back to metadata card from internal fields.
            var internalContent = await _search.GetContentAsync(table, uri, "internal", 15000)
                .ConfigureAwait(false);
            var fields = RowsToFields(internalContent["rows"] as JArray);

            // Derive a title from the most relevant field for this table type.
            var title = DeriveTitle(table, fields);

            // Prefer a web URL from additionalData or known metadata fields.
            var webUrl = previewContent?.Value<string>("additionalData")
                ?? fields.Value<string>("webViewLink")
                ?? fields.Value<string>("alternateLink");

            var body = new StringBuilder();
            body.Append("<div class=\"card\">");
            body.Append("<div class=\"card-source\">").Append(HtmlEncode(table)).Append("</div>");
            body.Append("<div class=\"card-title\">").Append(HtmlEncode(title)).Append("</div>");

            // Render fields relevant to each table type.
            body.Append("<table class=\"meta\"><tbody>");
            AppendCardFields(body, table, fields);
            body.Append("</tbody></table>");

            // Snippet / message body.
            var snippet = fields.Value<string>("snippet")
                ?? fields.Value<string>("message_body")
                ?? fields.Value<string>("body");
            if (!string.IsNullOrEmpty(snippet))
                body.Append("<div class=\"snippet\">").Append(HtmlEncode(snippet)).Append("</div>");

            // Button to open in browser if a web URL is available.
            if (!string.IsNullOrEmpty(webUrl))
                body.Append("<div class=\"open-link\"><a class=\"open-btn\" href=\"").Append(HtmlEncode(webUrl))
                    .Append("\" target=\"_blank\" rel=\"noopener\">Open in browser &#8599;</a></div>");

            body.Append("</div>");

            return new JObject
            {
                ["html"] = SharedHtmlWrapper(title, CardCss() + body.ToString()),
                ["contentType"] = "text/html",
                ["previewType"] = "metadata_card",
                ["title"] = title
            };
        }

        private static string DeriveTitle(string table, JObject fields)
        {
            switch (table.ToLowerInvariant())
            {
                case "teams":
                case "slack":
                case "skype":
                    return fields.Value<string>("subject")
                        ?? fields.Value<string>("chat_topic")
                        ?? fields.Value<string>("conversation_name")
                        ?? table + " message";
                case "jira":
                    return fields.Value<string>("summary")
                        ?? fields.Value<string>("key")
                        ?? "JIRA issue";
                default:
                    return fields.Value<string>("name")
                        ?? fields.Value<string>("title")
                        ?? fields.Value<string>("subject")
                        ?? table + " item";
            }
        }

        private static void AppendCardFields(StringBuilder sb, string table, JObject fields)
        {
            // Emit the most meaningful fields for each table type.
            switch (table.ToLowerInvariant())
            {
                case "teams":
                    AppendField(sb, "Team",    fields.Value<string>("team_display_name"));
                    AppendField(sb, "Channel", fields.Value<string>("channel_display_name"));
                    AppendField(sb, "Chat",    fields.Value<string>("chat_topic"));
                    AppendField(sb, "Sender",  fields.Value<string>("sender"));
                    AppendField(sb, "Created", fields.Value<string>("created"));
                    break;
                case "slack":
                    AppendField(sb, "Conversation", fields.Value<string>("conversation_name"));
                    AppendField(sb, "Sender",        fields.Value<string>("sender"));
                    AppendField(sb, "Created",       fields.Value<string>("created"));
                    break;
                case "onedrive":
                case "gdrive":
                case "dropbox":
                case "sharepoint":
                case "sp365":
                case "box":
                    AppendField(sb, "Name",         fields.Value<string>("name"));
                    AppendField(sb, "Path",         fields.Value<string>("path"));
                    AppendField(sb, "Modified by",  fields.Value<string>("modified_by"));
                    AppendField(sb, "Modified",     fields.Value<string>("modified"));
                    AppendField(sb, "Size",         fields.Value<string>("size"));
                    break;
                case "jira":
                    AppendField(sb, "Key",      fields.Value<string>("key"));
                    AppendField(sb, "Project",  fields.Value<string>("project_name"));
                    AppendField(sb, "Type",     fields.Value<string>("issue_type"));
                    AppendField(sb, "Status",   fields.Value<string>("status"));
                    AppendField(sb, "Assignee", fields.Value<string>("assignee"));
                    AppendField(sb, "Created",  fields.Value<string>("created"));
                    break;
                case "calendar":
                case "mscalendar":
                case "pstcalendar":
                    AppendField(sb, "Organizer", fields.Value<string>("organizer"));
                    AppendField(sb, "Start",     fields.Value<string>("startdate") ?? fields.Value<string>("starttime"));
                    AppendField(sb, "End",       fields.Value<string>("enddate") ?? fields.Value<string>("endtime"));
                    AppendField(sb, "Location",  fields.Value<string>("location"));
                    break;
                default:
                    // Generic: emit whatever fields are present.
                    foreach (var prop in fields.Properties())
                    {
                        if (prop.Name == "snippet" || prop.Name == "message_body" || prop.Name == "body")
                            continue;
                        var val = prop.Value?.ToString();
                        if (!string.IsNullOrWhiteSpace(val))
                            AppendField(sb, prop.Name, val);
                    }
                    break;
            }
        }

        private static void AppendField(StringBuilder sb, string label, string value)
        {
            var formatted = FormatFieldValue(label, value);
            if (string.IsNullOrWhiteSpace(formatted))
                return; // suppress empty / whitespace-only fields entirely
            sb.Append("<tr><th>").Append(HtmlEncode(label)).Append("</th><td>")
              .Append(HtmlEncode(formatted)).Append("</td></tr>");
        }

        // ── Field value formatting ───────────────────────────────────────────────
        //
        // Metadata cards otherwise surface raw internal values that are unreadable to Claude and
        // users: OA-format dates (45915.69854), byte counts, and OneDrive/GDrive drive paths
        // (/drives/b!.../root:/...). FormatFieldValue cleans these up and returns null/empty to
        // signal that a field should be omitted from the card entirely.

        /// <summary>
        /// Returns a human-readable rendering of a metadata field, or null/empty when the field has
        /// no meaningful value and should be suppressed. The <paramref name="fieldName"/> (the card
        /// label or raw index field name) gates type-specific formatting so that, e.g., a 45000-byte
        /// file size is never misread as an OA date.
        /// </summary>
        internal static string FormatFieldValue(string fieldName, string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                return null;

            var name = (fieldName ?? "").Trim().ToLowerInvariant();
            var value = rawValue.Trim();

            if (IsDateField(name))
            {
                // OA-format date: a decimal day-count. Guarded by BOTH the field name and a sane
                // range so non-date numeric fields are never converted.
                if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out double oa) &&
                    oa >= 40000 && oa <= 50000)
                {
                    try
                    {
                        return DateTime.FromOADate(oa).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        return value;
                    }
                }
                return value;
            }

            if (name == "size")
            {
                if (long.TryParse(value, NumberStyles.Integer | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out long bytes))
                    return FormatBytes(bytes);
                return value;
            }

            if (name == "path")
                return ShortenDrivePath(value);

            return value;
        }

        private static bool IsDateField(string name)
        {
            switch (name)
            {
                case "date":
                case "modified":
                case "created":
                case "date_sent":
                case "datesent":
                case "sent":
                case "received":
                case "start":
                case "end":
                case "startdate":
                case "enddate":
                case "starttime":
                case "endtime":
                    return true;
                default:
                    return false;
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0)
                return bytes.ToString(CultureInfo.InvariantCulture);
            if (bytes < 1024)
                return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024)
                return kb.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024)
                return mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB";
            double gb = mb / 1024.0;
            return gb.ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        }

        private static string ShortenDrivePath(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            // OneDrive / GDrive internal paths look like "/drives/b!<id>/root:/Folder/Sub/file.docx".
            // Strip the noisy "/drives/<id>/root:" prefix, keeping the human-meaningful tail. Return
            // the raw value untouched when no recognized prefix is present.
            bool looksInternal = value.IndexOf("/drives/", StringComparison.OrdinalIgnoreCase) >= 0
                || value.StartsWith("b!", StringComparison.OrdinalIgnoreCase)
                || value.IndexOf("root:", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!looksInternal)
                return value;

            var rootIdx = value.IndexOf("root:", StringComparison.OrdinalIgnoreCase);
            var tail = rootIdx >= 0 ? value.Substring(rootIdx + "root:".Length) : value;
            tail = tail.Trim('/');
            if (string.IsNullOrEmpty(tail))
            {
                var segs = value.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                return segs.Length > 0 ? segs[segs.Length - 1] : value;
            }
            return "/" + tail;
        }

        // ── Preview cache lookup ─────────────────────────────────────────────────

        // Some connectors (OneDrive, GDrive) download the preview file to a local cache
        // directory before firing OnPreviewReady. If WaitPreviewAsync times out (e.g. due
        // to an OAuth token check) but the file is already in cache from a prior call, we
        // can find it directly. Each connector stores files under a predictable subdirectory
        // named after the item's ID (the last path segment of the URI).
        /// <summary>
        /// Returns local OneDrive sync folder roots from the registry
        /// (HKCU\SOFTWARE\Microsoft\OneDrive\Accounts\*\UserFolder).
        /// </summary>
        private static IEnumerable<string> GetOneDriveSyncRoots()
        {
            var roots = new List<string>();
            try
            {
                using (var accountsKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\OneDrive\Accounts"))
                {
                    if (accountsKey == null) return roots;
                    foreach (var name in accountsKey.GetSubKeyNames())
                    {
                        using (var sub = accountsKey.OpenSubKey(name))
                        {
                            var folder = sub?.GetValue("UserFolder") as string;
                            if (!string.IsNullOrEmpty(folder))
                                roots.Add(folder);
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Debug("GetOneDriveSyncRoots: " + ex.Message); }
            return roots;
        }

        /// <summary>
        /// Searches all OneDrive local sync folders for a file with the given name.
        /// Returns the first match found, or null.
        /// </summary>
        private static string TryFindOneDriveSyncedFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            foreach (var root in GetOneDriveSyncRoots())
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    var matches = Directory.GetFiles(root, fileName, SearchOption.AllDirectories);
                    if (matches.Length > 0)
                        return matches[0];
                }
                catch (Exception ex) { Log.Debug("TryFindOneDriveSyncedFile in " + root + ": " + ex.Message); }
            }
            return null;
        }

        internal static string TryFindCachedPreviewFile(string table, string uri)
        {
            try
            {
                // Extract the item ID — the last non-empty segment of the URI path.
                var itemId = uri.TrimEnd('/');
                var slash = itemId.LastIndexOf('/');
                if (slash >= 0)
                    itemId = itemId.Substring(slash + 1);
                if (string.IsNullOrEmpty(itemId))
                    return null;

                // Path-traversal guard: itemId is concatenated into a cache path and the result is
                // handed to ShellExecute, so reject anything that isn't a plain file-name segment.
                if (itemId == "." || itemId == ".." ||
                    itemId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    Log.Warn("TryFindCachedPreviewFile: rejecting suspicious itemId '" + itemId + "' from uri " + uri);
                    return null;
                }

                string cacheRoot = null;
                switch (table.ToLowerInvariant())
                {
                    case "onedrive":
                        cacheRoot = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "X1 Search", "OneDrivePreview");
                        break;
                    case "gdrive":
                        cacheRoot = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "X1 Search", "GDrivePreview");
                        break;
                }

                if (cacheRoot == null || !Directory.Exists(cacheRoot))
                    return null;

                // Walk one level of account subdirectories, then look for itemId dir.
                foreach (var accountDir in Directory.GetDirectories(cacheRoot))
                {
                    var itemDir = Path.Combine(accountDir, itemId);
                    if (!Directory.Exists(itemDir))
                        continue;

                    // Return the most recently written file in the item directory.
                    var files = Directory.GetFiles(itemDir);
                    if (files.Length == 0)
                        continue;

                    string newest = null;
                    DateTime newestTime = DateTime.MinValue;
                    foreach (var f in files)
                    {
                        var wt = File.GetLastWriteTimeUtc(f);
                        if (wt > newestTime) { newestTime = wt; newest = f; }
                    }
                    if (newest != null)
                    {
                        Log.Debug("TryFindCachedPreviewFile: found cached file " + newest + " for " + table + "/" + itemId);
                        return newest;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("TryFindCachedPreviewFile failed: " + ex.Message);
            }
            return null;
        }

        // MSMail stores rendered email HTML under a path whose directory name uses only the
        // first ~20 chars of the item ID followed by a numeric hash, so we can't do an exact
        // match. Scan account subdirectories and look for a dir whose name starts with the
        // item ID prefix.
        private static string TryFindCachedEmailPreviewFile(string uri)
        {
            try
            {
                // Extract item ID — last non-empty segment of the msmail:// URI.
                var itemId = uri.TrimEnd('/');
                var slash = itemId.LastIndexOf('/');
                if (slash >= 0) itemId = itemId.Substring(slash + 1);
                if (itemId.Length < 8) return null;

                if (itemId == "." || itemId == ".." ||
                    itemId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return null;

                var cacheRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "X1 Search", "MSMailPreview");
                if (!Directory.Exists(cacheRoot)) return null;

                // X1 truncates the item ID to ~20 chars for the directory name.
                var prefix = itemId.Substring(0, Math.Min(16, itemId.Length));

                foreach (var accountDir in Directory.GetDirectories(cacheRoot))
                {
                    foreach (var itemDir in Directory.GetDirectories(accountDir))
                    {
                        if (!Path.GetFileName(itemDir).StartsWith(prefix, StringComparison.Ordinal))
                            continue;
                        var msgFile = Path.Combine(itemDir, "message.html");
                        if (File.Exists(msgFile))
                        {
                            Log.Debug("TryFindCachedEmailPreviewFile: found " + msgFile);
                            return msgFile;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("TryFindCachedEmailPreviewFile failed: " + ex.Message);
            }
            return null;
        }

        // ── HTML helpers ─────────────────────────────────────────────────────────

        internal static string ExtractDocxHtml(string path, int maxChars = 0)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    var entry = zip.Entries.FirstOrDefault(
                        e => string.Equals(e.FullName, "word/document.xml", StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                        return null;

                    string xmlText;
                    using (var stream = entry.Open())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        xmlText = reader.ReadToEnd();

                    var doc = new XmlDocument();
                    doc.LoadXml(xmlText);

                    var nsm = new XmlNamespaceManager(doc.NameTable);
                    nsm.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");

                    var sb = new StringBuilder();
                    bool inList = false;
                    int textChars = 0;     // running count of visible text characters appended
                    bool truncated = false;

                    foreach (XmlNode para in doc.SelectNodes("//w:p", nsm))
                    {
                        // Character-budget check BEFORE appending the next paragraph: char count is a
                        // better proxy for token size than paragraph count for arbitrary documents.
                        if (maxChars > 0 && textChars >= maxChars)
                        {
                            truncated = true;
                            break;
                        }

                        var styleNode = para.SelectSingleNode("w:pPr/w:pStyle/@w:val", nsm);
                        var style = styleNode?.Value ?? "";

                        var textNodes = para.SelectNodes(".//w:t", nsm);
                        var text = new StringBuilder();
                        foreach (XmlNode t in textNodes)
                            text.Append(t.InnerText);
                        var line = text.ToString();

                        bool isList = style == "ListParagraph";

                        if (!isList && inList) { sb.Append("</ul>"); inList = false; }

                        if (isList)
                        {
                            if (!inList) { sb.Append("<ul>"); inList = true; }
                            sb.Append("<li>").Append(HtmlEncode(line)).Append("</li>");
                        }
                        else if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ||
                                 style.StartsWith("Title", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.Append("<h1>").Append(HtmlEncode(line)).Append("</h1>");
                        }
                        else if (!string.IsNullOrWhiteSpace(line))
                        {
                            sb.Append("<p>").Append(HtmlEncode(line)).Append("</p>");
                        }
                        else
                        {
                            sb.Append("<p>&nbsp;</p>");
                        }

                        textChars += line.Length;
                    }
                    if (inList) sb.Append("</ul>");   // always close the list, even when truncated

                    if (truncated)
                        sb.Append("<p class=\"preview-truncated\"><em>… preview truncated at ~")
                          .Append(maxChars.ToString(CultureInfo.InvariantCulture))
                          .Append(" characters. Full document available via the open action.</em></p>");

                    return "<style>h1{font-size:18px;font-weight:700;margin:0 0 14px}p{margin:0 0 10px}" +
                           "ul{margin:0 0 10px 20px}li{margin:2px 0}" +
                           ".preview-truncated{color:#888;margin-top:14px}</style>" + sb.ToString();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("ExtractDocxHtml failed for " + path + ": " + ex.Message);
                return null;
            }
        }

        // Resolves a preview file to embeddable HTML plus a previewType tag. Returns (null, null)
        // for binary formats we can't render inline (spreadsheets, images over the size cap) so
        // callers fall back to a metadata card instead of dumping raw bytes into a <pre> block.
        internal async Task<(string html, string previewType)> EmbedPreviewFileAsync(string path, int maxChars)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".html" || ext == ".htm")
                return (EmbedLocalFile(path, maxChars), "html");

            if (ext == ".docx")
            {
                var docx = ExtractDocxHtml(path, maxChars);
                return docx != null ? (docx, "docx") : (null, null);
            }

            // PDF: X1 has no native PDF-to-HTML preview provider, so the "preview" callback just
            // hands back the original PDF file. Embedding it as a base64 <embed>/<object> used to
            // "work" in the Claude Desktop app's own preview pane, but the Artifact viewer's CSP
            // blocks plugin content entirely, leaving a blank pane. Extract the text instead (the
            // same ExtractTextFromFile pipeline x1_extract_file uses) and reflow it into headed
            // sections/paragraphs (FormatExtractedDocumentHtml) rather than one giant text blob.
            // Falls through to the metadata card if extraction fails.
            if (ext == ".pdf")
            {
                var extracted = await _search.ExtractFileAsync(path, 60000).ConfigureAwait(false);
                var text = extracted.Value<string>("text");
                if (!string.IsNullOrEmpty(text))
                    return (BookCss() + FormatExtractedDocumentHtml(text, maxChars), "pdf");
                Log.Debug("PDF text extraction failed for " + path + ": " +
                    (extracted.Value<string>("error") ?? "no text returned"));
                return (null, null);
            }

            // Images: when we hold the actual cached object, embed it as a self-contained data:
            // URI so the user sees the real file rather than a metadata card. Size-capped so a
            // large file can't flood the caller's context with base64 (see TryEmbedBinaryObject).
            var embedded = TryEmbedBinaryObject(path, ext);
            if (embedded.html != null)
                return embedded;

            // Unknown / no extension: sniff the leading bytes.
            var kind = SniffFileType(path);
            if (kind == FileKind.Zip)
            {
                // Might be an extension-less Word doc; ExtractDocxHtml returns null for
                // non-Word OOXML (xlsx/pptx), in which case we fall back to the card.
                var docx = ExtractDocxHtml(path, maxChars);
                return docx != null ? (docx, "docx") : (null, null);
            }
            if (kind == FileKind.Binary)
                return (null, null);

            return (EmbedLocalFile(path, maxChars), "text");
        }

        private enum FileKind { Text, Binary, Zip }

        // Classifies a file by its first bytes: a NUL byte or a known binary magic number means
        // binary; the ZIP magic (PK\x03\x04) is called out separately because OOXML docx are zips.
        private static FileKind SniffFileType(string path)
        {
            try
            {
                byte[] buf = new byte[512];
                int n;
                using (var fs = File.OpenRead(path))
                    n = fs.Read(buf, 0, buf.Length);
                if (n <= 0)
                    return FileKind.Text;

                if (n >= 4 && buf[0] == 0x50 && buf[1] == 0x4B && buf[2] == 0x03 && buf[3] == 0x04)
                    return FileKind.Zip;                                  // PK.. ZIP / OOXML
                if (n >= 4 && buf[0] == 0x25 && buf[1] == 0x50 && buf[2] == 0x44 && buf[3] == 0x46)
                    return FileKind.Binary;                               // %PDF
                if (n >= 4 && buf[0] == 0xD0 && buf[1] == 0xCF && buf[2] == 0x11 && buf[3] == 0xE0)
                    return FileKind.Binary;                               // legacy OLE (.doc/.xls/.ppt)
                if (n >= 4 && buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47)
                    return FileKind.Binary;                               // PNG
                if (n >= 3 && buf[0] == 0xFF && buf[1] == 0xD8 && buf[2] == 0xFF)
                    return FileKind.Binary;                               // JPEG
                if (n >= 3 && buf[0] == 0x47 && buf[1] == 0x49 && buf[2] == 0x46)
                    return FileKind.Binary;                               // GIF

                for (int i = 0; i < n; i++)
                    if (buf[i] == 0)
                        return FileKind.Binary;                           // NUL byte ⇒ binary

                return FileKind.Text;
            }
            catch (Exception ex)
            {
                Log.Debug("SniffFileType failed for " + path + ": " + ex.Message);
                return FileKind.Binary;   // err on the side of not dumping bytes
            }
        }

        // Largest cached object we will inline as a base64 data: URI. The encoded bytes travel back
        // through the MCP response and into the caller's context, so this bounds the token cost — a
        // 1 MB file is ~1.37 MB of base64. Files above the cap fall back to the metadata card.
        internal const long MaxEmbedObjectBytes = 1024 * 1024;   // 1 MB

        // Map of embeddable image extensions to their MIME type. Anything not listed is left to
        // the caller's text/card fallback. PDF is deliberately absent — see EmbedPreviewFileAsync,
        // which extracts PDF text instead of embedding the raw file (the Artifact viewer's CSP
        // blocks <embed>/<object> plugin content, unlike Claude Desktop's own preview pane).
        private static readonly Dictionary<string, string> EmbeddableImageMime =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".png"] = "image/png",
                [".jpg"] = "image/jpeg",
                [".jpeg"] = "image/jpeg",
                [".gif"] = "image/gif",
                [".webp"] = "image/webp",
                [".bmp"] = "image/bmp",
                [".svg"] = "image/svg+xml",
            };

        /// <summary>
        /// Embeds an image the bridge physically holds (a local Files path, or a connector's cached
        /// copy) as a self-contained data: URI so the preview shows the real object instead of a
        /// metadata card. Returns (null, null) when the extension isn't an embeddable image, the
        /// file is missing/empty, or it exceeds <see cref="MaxEmbedObjectBytes"/> — in which case
        /// the caller falls back to the metadata card.
        /// </summary>
        internal static (string html, string previewType) TryEmbedBinaryObject(string path, string ext)
        {
            if (!EmbeddableImageMime.TryGetValue(ext, out string mime))
                return (null, null);

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0 || info.Length > MaxEmbedObjectBytes)
                    return (null, null);   // missing, empty, or over the cap — caller renders the card

                var dataUri = "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(path));
                var fileName = HtmlEncode(Path.GetFileName(path));

                return ("<img src=\"" + dataUri + "\" alt=\"" + fileName +
                        "\" style=\"max-width:100%;height:auto;border-radius:4px\" />", "image");
            }
            catch (Exception ex)
            {
                Log.Debug("TryEmbedBinaryObject failed for " + path + ": " + ex.Message);
                return (null, null);
            }
        }

        // Wraps extracted plain text (e.g. from PDF text extraction) in the same <pre> block /
        // truncation convention EmbedLocalFile uses for plain-text files, so both paths render
        // identically in the shared HTML shell.
        internal static string PlainTextToHtml(string text, int maxChars)
        {
            bool truncated = false;
            if (maxChars > 0 && text.Length > maxChars)
            {
                text = text.Substring(0, maxChars);
                truncated = true;
            }
            var pre = "<pre class=\"plaintext\">" + HtmlEncode(text) + "</pre>";
            if (truncated)
                pre += "<p class=\"preview-truncated\" style=\"color:#888;margin-top:14px\"><em>… preview truncated at ~"
                     + maxChars.ToString(CultureInfo.InvariantCulture)
                     + " characters. Full document available via the open action.</em></p>";
            return pre;
        }

        // XS-1610: PDF text extraction (X1's ExtractTextFromFile) returns one continuous run of
        // text with no paragraph or heading breaks — readable as a wall of text, but not as a
        // document. A large share of this corpus is Wikipedia "print to PDF" exports, which have a
        // very regular shape we can exploit: a "Contents" block listing numbered headings (bulleted
        // with ◾ or •), immediately followed by the body, which repeats each heading's title
        // verbatim as a section marker. Detect that shape and split the flat text into a
        // proper heading/paragraph structure with a linked table of contents; fall back to a
        // generic sentence-grouped paragraph reflow (still far more readable than one blob) when
        // the shape isn't present. Never throws — worst case, degrades to the paragraph fallback.
        private static readonly Regex PageFurnitureRegex = new Regex(
            @"\s*Page\s+\d+\s+of\s+\d+\s+.{0,120}?\s+\d{1,2}/\d{1,2}/\d{4}\s+https?://\S+",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex TocNumberRegex = new Regex(
            @"^\s*((?:\d+\.)*\d+)\s+(.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

        internal static string FormatExtractedDocumentHtml(string rawText, int maxChars)
        {
            try
            {
                var structured = TryFormatStructuredDocument(rawText, maxChars);
                if (structured != null)
                    return structured;
            }
            catch (Exception ex)
            {
                Log.Debug("FormatExtractedDocumentHtml: structured parse failed, falling back: " + ex.Message);
            }
            return FormatReflowedParagraphs(rawText, maxChars);
        }

        private static string TryFormatStructuredDocument(string rawText, int maxChars)
        {
            var text = Regex.Replace(rawText, @"\s+", " ").Trim();
            text = PageFurnitureRegex.Replace(text, " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            int contentsIdx = text.IndexOf("Contents", StringComparison.Ordinal);
            if (contentsIdx < 0)
                return null;

            var afterContents = text.Substring(contentsIdx + "Contents".Length);

            // Walk bullet positions directly (rather than String.Split) so we keep absolute
            // offsets into `afterContents` — the document body legitimately uses the same bullet
            // characters for its own lists further down, so we can't assume every bullet in the
            // remaining text belongs to the TOC. We stop consuming entries the moment a segment's
            // title reappears (meaning the TOC has run into the body) or stops matching the
            // "N Title" shape at all (meaning we've drifted into an unrelated body list).
            var bulletMatches = Regex.Matches(afterContents, "[◾•]");
            if (bulletMatches.Count < 3)
                return null;

            var entries = new List<(string number, int level, string title)>();
            string firstTitle = null;
            string bodyText = null;

            for (int i = 0; i < bulletMatches.Count; i++)
            {
                int segStart = bulletMatches[i].Index + 1;
                int segEnd = i + 1 < bulletMatches.Count ? bulletMatches[i + 1].Index : afterContents.Length;
                if (segEnd <= segStart) continue;
                string seg = afterContents.Substring(segStart, segEnd - segStart);

                var m = TocNumberRegex.Match(seg);
                if (!m.Success)
                    break;   // drifted past the TOC into an unrelated body list — stop here

                string number = m.Groups[1].Value;
                string restRaw = m.Groups[2].Value;   // no leading whitespace: consumed by \s+ above
                int level = number.Count(c => c == '.') + 2;   // "1" -> h2, "2.1" -> h3, ...

                if (firstTitle == null)
                {
                    firstTitle = restRaw.Trim();
                    entries.Add((number, level, firstTitle));
                    continue;
                }

                int bodyIdx = firstTitle.Length >= 3
                    ? restRaw.IndexOf(firstTitle, StringComparison.Ordinal)
                    : -1;
                if (bodyIdx < 0)
                {
                    entries.Add((number, level, restRaw.Trim()));
                    continue;
                }

                // This entry's title reappears here — that's the real body start.
                entries.Add((number, level, restRaw.Substring(0, bodyIdx).Trim()));
                int absoluteBodyIdx = segStart + m.Groups[2].Index + bodyIdx;
                bodyText = afterContents.Substring(absoluteBodyIdx);
                break;
            }

            if (entries.Count < 3 || string.IsNullOrEmpty(bodyText))
                return null;

            // Strip any stray bullet markers the body itself uses for its own lists — we're
            // reflowing everything into prose paragraphs, not preserving nested list structure.
            bodyText = Regex.Replace(bodyText, "[◾•]", " ");

            // Locate each heading's title within the body, in order, to slice out section text.
            var starts = new int[entries.Count];
            int cursor = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                var title = entries[i].title;
                int idx = string.IsNullOrEmpty(title) ? -1 : bodyText.IndexOf(title, cursor, StringComparison.Ordinal);
                starts[i] = idx >= 0 ? idx : cursor;   // not found — merge into the previous section
                cursor = starts[i] + Math.Max(title?.Length ?? 0, 0);
            }

            var sb = new StringBuilder();
            sb.Append("<nav class=\"book-toc\"><div class=\"book-toc-label\">Contents</div><ul>");
            for (int i = 0; i < entries.Count; i++)
            {
                var (number, level, title) = entries[i];
                if (string.IsNullOrEmpty(title)) continue;
                sb.Append("<li class=\"toc-l").Append(Math.Min(level, 4)).Append("\"><a href=\"#sec-")
                  .Append(i).Append("\">").Append(HtmlEncode(title)).Append("</a></li>");
            }
            sb.Append("</ul></nav>");

            int budget = maxChars > 0 ? maxChars : int.MaxValue;
            int emitted = 0;
            bool truncated = false;
            sb.Append("<article class=\"book\">");
            for (int i = 0; i < entries.Count && !truncated; i++)
            {
                var (number, level, title) = entries[i];
                if (string.IsNullOrEmpty(title)) continue;

                int sectionStart = starts[i] + title.Length;
                int sectionEnd = i + 1 < entries.Count ? starts[i + 1] : bodyText.Length;
                if (sectionEnd <= sectionStart) continue;
                var sectionText = bodyText.Substring(sectionStart, sectionEnd - sectionStart).Trim();

                int tag = Math.Min(Math.Max(level, 2), 4);
                sb.Append("<h").Append(tag).Append(" id=\"sec-").Append(i).Append("\">")
                  .Append(HtmlEncode(title)).Append("</h").Append(tag).Append(">");

                foreach (var para in GroupIntoParagraphs(sectionText))
                {
                    if (emitted + para.Length > budget)
                    {
                        int remaining = Math.Max(budget - emitted, 0);
                        sb.Append("<p>").Append(HtmlEncode(para.Substring(0, remaining))).Append("</p>");
                        truncated = true;
                        break;
                    }
                    sb.Append("<p>").Append(HtmlEncode(para)).Append("</p>");
                    emitted += para.Length;
                }
            }
            sb.Append("</article>");

            if (truncated)
                sb.Append("<p class=\"preview-truncated\"><em>… preview truncated at ~")
                  .Append(maxChars.ToString(CultureInfo.InvariantCulture))
                  .Append(" characters. Full document available via the open action.</em></p>");

            return sb.ToString();
        }

        // Generic fallback when the Wikipedia-print TOC shape isn't detected: still break the flat
        // text into readable paragraphs (grouped sentences) rather than one continuous blob.
        private static string FormatReflowedParagraphs(string rawText, int maxChars)
        {
            var text = Regex.Replace(rawText, @"\s+", " ").Trim();
            text = Regex.Replace(PageFurnitureRegex.Replace(text, " "), @"\s+", " ").Trim();

            var sb = new StringBuilder("<article class=\"book\">");
            int budget = maxChars > 0 ? maxChars : int.MaxValue;
            int emitted = 0;
            bool truncated = false;
            foreach (var para in GroupIntoParagraphs(text))
            {
                if (emitted + para.Length > budget)
                {
                    int remaining = Math.Max(budget - emitted, 0);
                    sb.Append("<p>").Append(HtmlEncode(para.Substring(0, remaining))).Append("</p>");
                    truncated = true;
                    break;
                }
                sb.Append("<p>").Append(HtmlEncode(para)).Append("</p>");
                emitted += para.Length;
            }
            sb.Append("</article>");
            if (truncated)
                sb.Append("<p class=\"preview-truncated\"><em>… preview truncated at ~")
                  .Append(maxChars.ToString(CultureInfo.InvariantCulture))
                  .Append(" characters. Full document available via the open action.</em></p>");
            return sb.ToString();
        }

        private static readonly Regex SentenceSplitRegex = new Regex(
            @"(?<=[.!?])\s+(?=[A-Z0-9""“(])", RegexOptions.Compiled);

        // Groups sentences into ~500-700 char paragraphs. Extracted PDF/plain text has no real
        // paragraph markers, so this is a readability heuristic, not a reconstruction of the
        // author's original paragraph breaks.
        private static IEnumerable<string> GroupIntoParagraphs(string text)
        {
            text = text.Trim();
            if (text.Length == 0) yield break;

            const int targetLen = 550;
            var sentences = SentenceSplitRegex.Split(text);
            var current = new StringBuilder();
            foreach (var sentence in sentences)
            {
                if (current.Length > 0 && current.Length + sentence.Length > targetLen)
                {
                    yield return current.ToString().Trim();
                    current.Clear();
                }
                if (current.Length > 0) current.Append(' ');
                current.Append(sentence);
            }
            if (current.Length > 0)
                yield return current.ToString().Trim();
        }

        // Book-styled CSS for FormatExtractedDocumentHtml's output: serif reading type, a linked
        // table of contents, and heading hierarchy — distinct from the monospace-leaning
        // plaintext/card styles used elsewhere, since this content is meant to read like a book
        // chapter rather than a code/log dump.
        private static string BookCss()
        {
            return
@"<style>
.book-toc { border: 1px solid #e3ddc9; background: #faf8f0; border-radius: 8px;
  padding: 14px 18px; margin-bottom: 22px; }
.book-toc-label { font-size: 11px; letter-spacing: .08em; text-transform: uppercase;
  color: #8a7c4a; font-weight: 600; margin-bottom: 8px; }
.book-toc ul { list-style: none; columns: 2; column-gap: 24px; }
.book-toc li { break-inside: avoid; margin: 2px 0; font-size: 13px; }
.book-toc li a { color: #4a3a1e; }
.book-toc li.toc-l3 { padding-left: 14px; }
.book-toc li.toc-l4 { padding-left: 28px; font-size: 12px; color: #8a7c4a; }
article.book { font-family: Georgia, 'Iowan Old Style', 'Palatino Linotype', serif;
  font-size: 16px; line-height: 1.7; max-width: 680px; color: #262117; }
article.book h2 { font-size: 22px; font-weight: 600; margin: 30px 0 12px; letter-spacing: -0.01em; }
article.book h3 { font-size: 18px; font-weight: 600; margin: 24px 0 10px; color: #3a3120; }
article.book h4 { font-size: 15px; font-weight: 600; margin: 18px 0 8px; color: #5a4e30;
  text-transform: uppercase; letter-spacing: .04em; }
article.book h2:first-child, article.book h3:first-child { margin-top: 4px; }
article.book p { margin: 0 0 14px; text-wrap: pretty; }
.preview-truncated { color: #8a7c4a; font-size: 13px; margin-top: 10px; }
@media (prefers-color-scheme: dark) {
  .book-toc { border-color: #3a341f; background: #201c12; }
  .book-toc-label { color: #c9a75c; }
  .book-toc li a { color: #e2d9b8; }
  .book-toc li.toc-l4 { color: #a3945f; }
  article.book { color: #e7e2d2; }
  article.book h3 { color: #d8cfaf; }
  article.book h4 { color: #c9bd8e; }
  .preview-truncated { color: #a3945f; }
}
</style>";
        }

        private static string EmbedLocalFile(string path, int maxChars = 0)
        {
            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var text = File.ReadAllText(path, Encoding.UTF8);

                if (ext == ".html" || ext == ".htm")
                {
                    // Strip outer html/head/body wrappers — we embed inside our own shell.
                    var bodyStart = text.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
                    var bodyEnd   = text.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (bodyStart >= 0)
                    {
                        var tagClose = text.IndexOf('>', bodyStart);
                        if (tagClose >= 0 && bodyEnd > tagClose)
                            return text.Substring(tagClose + 1, bodyEnd - tagClose - 1);
                    }
                    return text;
                }

                // Plain text — wrap in a <pre> block, truncating to the character budget if set.
                return PlainTextToHtml(text, maxChars);
            }
            catch (Exception ex)
            {
                Log.Warn("EmbedLocalFile failed for " + path + ": " + ex.Message);
                return "<p><em>Could not read preview file.</em></p>";
            }
        }

        private static string SharedHtmlWrapper(string title, string bodyContent)
        {
            return "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n" +
                   "<meta charset=\"UTF-8\">\n" +
                   "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n" +
                   "<title>" + HtmlEncode(title) + "</title>\n" +
                   "<style>\n" + BaseStyles() + "\n</style>\n" +
                   "</head>\n<body>\n" + SanitizeUnsupportedEmbeds(bodyContent) + "\n</body>\n</html>";
        }

        // Every preview path (email, file, metadata card) funnels its body markup through
        // SharedHtmlWrapper before it's returned as "html" — so this is the single choke point
        // that catches ANY raw <embed>/<object> reaching the caller, not just the PDF case
        // EmbedPreviewFileAsync already avoids deliberately (see its ".pdf" branch). It exists as
        // a safety net for paths that pass third-party markup through mostly unchanged — e.g.
        // EmbedLocalFile's .html/.htm branch, which strips only the outer html/body wrapper from
        // a cached webpage snapshot and could still contain a video/PDF plugin embed of its own —
        // and for any future preview type that reintroduces one. The Claude Artifact viewer's CSP
        // blocks <embed>/<object> plugin content outright, so left alone these render as a
        // silent blank pane instead of a visible failure (XS-1610).
        private static readonly System.Text.RegularExpressions.Regex UnsupportedEmbedPattern =
            new System.Text.RegularExpressions.Regex(
                @"<object\b[^>]*>[\s\S]*?</object>|<embed\b[^>]*/?>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);

        internal static string SanitizeUnsupportedEmbeds(string bodyContent)
        {
            if (string.IsNullOrEmpty(bodyContent))
                return bodyContent;
            return UnsupportedEmbedPattern.Replace(bodyContent,
                "<div class=\"embed-unsupported\"><em>This preview contained embedded plugin " +
                "content (e.g. a PDF or video object) that can't render in this viewer. Use " +
                "<code>x1_get_content</code> or <code>x1_extract_file</code> to read it as text, " +
                "or open the original file directly.</em></div>");
        }

        private static string BaseStyles()
        {
            return
@"*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
body {
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
  font-size: 14px; line-height: 1.5;
  color: #1a1a1a; background: #fff; padding: 16px;
}
@media (prefers-color-scheme: dark) {
  body { color: #e8e8e8; background: #1e1e1e; }
  a { color: #70b5f9; }
  .meta th { color: #aaa; }
  .card { border-color: #333; background: #252525; }
  .card-source { background: #333; color: #bbb; }
  .snippet { background: #2a2a2a; border-color: #444; color: #ccc; }
  pre.plaintext { background: #252525; border-color: #444; }
}
a { color: #0066cc; text-decoration: none; }
a:hover { text-decoration: underline; }
.embed-unsupported { padding: 12px; border: 1px dashed #c9a227; border-radius: 6px; background: #fdf6e3; color: #6b5a1e; }
@media (prefers-color-scheme: dark) {
  .embed-unsupported { border-color: #8a7c4a; background: #2a2410; color: #d8c98a; }
}";
        }

        private static string EmailCss()
        {
            return
@"<style>
.email-header { border: 1px solid #ddd; border-radius: 6px; padding: 12px; margin-bottom: 16px; background: #fafafa; }
.email-body { padding: 8px 0; word-break: break-word; }
.no-body { padding: 8px 0; color: #888; font-style: italic; }
.meta { border-collapse: collapse; width: 100%; }
.meta th { text-align: right; padding: 3px 8px 3px 0; color: #666; font-weight: 500; width: 60px; white-space: nowrap; vertical-align: top; }
.meta td { padding: 3px 0; word-break: break-word; }
.open-link { padding: 12px 0 4px; }
.open-btn { display: inline-block; padding: 8px 16px; background: #0066cc; color: #fff !important;
  border-radius: 6px; font-size: 13px; font-weight: 500; text-decoration: none !important; }
.open-btn:hover { background: #0052a3; }
@media (prefers-color-scheme: dark) {
  .email-header { border-color: #444; background: #252525; }
  .no-body { color: #666; }
}
</style>";
        }

        private static string CardCss()
        {
            return
@"<style>
.card { border: 1px solid #e0e0e0; border-radius: 8px; overflow: hidden; }
.card-source { font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: .5px;
  color: #666; background: #f5f5f5; padding: 4px 12px; }
.card-title { font-size: 16px; font-weight: 600; padding: 12px 12px 8px; word-break: break-word; }
.meta { border-collapse: collapse; width: 100%; padding: 0 12px 8px; }
.meta th { text-align: right; padding: 3px 8px 3px 12px; color: #666; font-weight: 500; width: 90px;
  white-space: nowrap; vertical-align: top; font-size: 13px; }
.meta td { padding: 3px 12px 3px 0; word-break: break-word; font-size: 13px; }
.snippet { margin: 8px 12px; padding: 10px; background: #f9f9f9; border-left: 3px solid #ccc;
  font-size: 13px; color: #444; white-space: pre-wrap; word-break: break-word; }
.link { padding: 10px 12px 12px; font-size: 13px; }
.open-link { padding: 10px 12px 12px; }
.open-btn { display: inline-block; padding: 8px 16px; background: #0066cc; color: #fff !important;
  border-radius: 6px; font-size: 13px; font-weight: 500; text-decoration: none !important; }
.open-btn:hover { background: #0052a3; }
pre.plaintext { background: #f8f8f8; border: 1px solid #e0e0e0; border-radius: 4px;
  padding: 12px; overflow-x: auto; font-size: 13px; white-space: pre-wrap; word-break: break-word; }
</style>";
        }

        // ── Utilities ────────────────────────────────────────────────────────────

        private static JObject RowsToFields(JArray rows)
        {
            var obj = new JObject();
            if (rows == null) return obj;
            for (int i = 0; i + 1 < rows.Count; i += 2)
            {
                var key = rows[i]?.ToString();
                var val = rows[i + 1]?.ToString();
                if (!string.IsNullOrEmpty(key))
                    obj[key] = val;
            }
            return obj;
        }

        // Returns true when the HTML content is an X1 connector error string rather than usable
        // preview content. X1 caches these strings (e.g. for Gmail on first-call failure):
        //   "Error opening gmail://…: File preview not available"
        private static bool IsX1PreviewError(string html)
        {
            var t = html == null ? "" : html.TrimStart();
            return t.StartsWith("Error ", StringComparison.Ordinal) ||
                   t.StartsWith("Error:", StringComparison.Ordinal);
        }

        // Renders an AdaptiveCard JSON preview file as a readable message card with metadata
        // fields (sender, team, channel, created) and extracted TextBlock paragraphs.
        // Returns null if the file is not a valid AdaptiveCard, allowing fallback to the
        // generic metadata card.
        private static string TryBuildMessageCard(string table, JObject fields, string jsonPath)
        {
            try
            {
                var json = File.ReadAllText(jsonPath, Encoding.UTF8);
                var card = JObject.Parse(json);
                if (!string.Equals(card.Value<string>("type"), "AdaptiveCard",
                        StringComparison.OrdinalIgnoreCase))
                    return null;

                var bodyArray = card["body"] as JArray;
                if (bodyArray == null) return null;

                var sb = new StringBuilder();
                sb.Append("<div class=\"card\">");
                sb.Append("<div class=\"card-source\">").Append(HtmlEncode(table)).Append("</div>");

                sb.Append("<table class=\"meta\"><tbody>");
                AppendField(sb, "Sender",  fields.Value<string>("sender"));
                AppendField(sb, "Team",    fields.Value<string>("team_display_name"));
                AppendField(sb, "Channel", fields.Value<string>("channel_display_name"));
                AppendField(sb, "Chat",    fields.Value<string>("chat_topic"));
                AppendField(sb, "Created", fields.Value<string>("created"));
                sb.Append("</tbody></table>");

                bool firstBlock = true;
                foreach (var item in bodyArray)
                {
                    if (!string.Equals(item.Value<string>("type"), "TextBlock",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    var text = StripMarkdownLinks(item.Value<string>("text") ?? "");
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var weight = item.Value<string>("weight") ?? "";
                    var size   = item.Value<string>("size") ?? "";
                    if (firstBlock &&
                        string.Equals(weight, "bolder", StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(size, "large", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(size, "extraLarge", StringComparison.OrdinalIgnoreCase)))
                    {
                        sb.Append("<div class=\"card-title\">").Append(HtmlEncode(text)).Append("</div>");
                    }
                    else
                    {
                        sb.Append("<div class=\"snippet\">").Append(HtmlEncode(text)).Append("</div>");
                    }
                    firstBlock = false;
                }
                sb.Append("</div>");
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        // Strips Markdown hyperlinks [label](url) → label without using Regex.
        private static string StripMarkdownLinks(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('[') < 0)
                return text;
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '[')
                {
                    int close = text.IndexOf(']', i + 1);
                    if (close > i && close + 1 < text.Length && text[close + 1] == '(')
                    {
                        int urlClose = text.IndexOf(')', close + 2);
                        if (urlClose > close + 1)
                        {
                            sb.Append(text, i + 1, close - i - 1);   // label only
                            i = urlClose + 1;
                            continue;
                        }
                    }
                }
                sb.Append(text[i++]);
            }
            return sb.ToString();
        }

        private static string HtmlEncode(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : WebUtility.HtmlEncode(value);
        }
    }
}
