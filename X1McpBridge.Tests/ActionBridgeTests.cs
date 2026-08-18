// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// Tests for ActionBridge that do NOT require a live X1ServiceHost.
    /// Integration tests (requiring the service) are marked [Explicit].
    /// </summary>
    [TestFixture]
    public class ActionBridgeTests
    {
        private ActionBridge _actions;

        [SetUp]
        public void SetUp()
        {
            // SearchBridge is lazy — it won't connect to X1ServiceHost unless a
            // search/metadata/content call is actually made. TableSchemaResolver(null) with an
            // unseeded/empty cache trusts every table token unchanged (see its ResolveAsync safety
            // valve) so these table names resolve without needing a live connection or seeding.
            _actions = new ActionBridge(new SearchBridge(new ColumnNameResolver(null), new TableSchemaResolver(null)), new TableSchemaResolver(null));
        }

        [TearDown]
        public void TearDown()
        {
        }

        // ── ListActions ──────────────────────────────────────────────────────────

        [Test]
        public void ListActions_FilesTable_ReturnsExpectedActions()
        {
            var result = _actions.ListActions("Files", "files://C:/test/file.docx");
            var actions = result["actions"] as JArray;
            Assert.That(actions, Is.Not.Null);
            var names = new System.Collections.Generic.HashSet<string>();
            foreach (JObject a in actions) names.Add(a["action"].ToString());
            Assert.That(names, Does.Contain("get_path"));
            Assert.That(names, Does.Contain("open"));
            Assert.That(names, Does.Contain("show_in_folder"));
        }

        [Test]
        public void ListActions_GmailTable_ReturnsGetUrlAndOpenUrl()
        {
            var result = _actions.ListActions("Gmail", "gmail://some-uri");
            var actions = result["actions"] as JArray;
            var names = new System.Collections.Generic.HashSet<string>();
            foreach (JObject a in actions) names.Add(a["action"].ToString());
            Assert.That(names, Does.Contain("get_url"));
            Assert.That(names, Does.Contain("open_url"));
        }

        [Test]
        public void ListActions_UnknownTable_ReturnsEmptyActions()
        {
            var result = _actions.ListActions("NoSuchTable", "nosuch://uri");
            var actions = result["actions"] as JArray;
            Assert.That(actions, Is.Not.Null);
            Assert.That(actions.Count, Is.EqualTo(0));
        }

        [Test]
        public void ListActions_ReturnsTableAndUri()
        {
            var result = _actions.ListActions("Files", "files://C:/foo.txt");
            Assert.That(result["table"]?.ToString(), Is.EqualTo("Files"));
            Assert.That(result["uri"]?.ToString(), Is.EqualTo("files://C:/foo.txt"));
        }

        [Test]
        public void ListActions_NullTable_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => _actions.ListActions(null, "files://x"));
        }

        [Test]
        public void ListActions_NullUri_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => _actions.ListActions("Files", null));
        }

        // ── ExecuteActionAsync argument validation ────────────────────────────────

        [Test]
        public void ExecuteActionAsync_NullTable_ThrowsArgumentException()
        {
            Assert.ThrowsAsync<ArgumentException>(
                () => _actions.ExecuteActionAsync(null, "files://x", "open", 5000));
        }

        [Test]
        public void ExecuteActionAsync_NullUri_ThrowsArgumentException()
        {
            Assert.ThrowsAsync<ArgumentException>(
                () => _actions.ExecuteActionAsync("Files", null, "open", 5000));
        }

        [Test]
        public void ExecuteActionAsync_NullAction_ThrowsArgumentException()
        {
            Assert.ThrowsAsync<ArgumentException>(
                () => _actions.ExecuteActionAsync("Files", "files://x", null, 5000));
        }

        // ── Unsupported action ────────────────────────────────────────────────────

        [Test]
        public async Task ExecuteActionAsync_UnsupportedAction_ReturnsErrorStatus()
        {
            var result = await _actions.ExecuteActionAsync("Files", "files://C:/test.txt", "delete", 5000);
            Assert.That(result.Value<string>("status"), Is.EqualTo("error"));
            Assert.That(result.Value<string>("message"), Does.Contain("delete"));
        }

        [Test]
        public async Task ExecuteActionAsync_UnknownTable_ReturnsErrorStatus()
        {
            var result = await _actions.ExecuteActionAsync("NoSuchTable", "x://uri", "open", 5000);
            Assert.That(result.Value<string>("status"), Is.EqualTo("error"));
        }

        // ── get_path ──────────────────────────────────────────────────────────────

        [Test]
        public async Task ExecuteAction_GetPath_ValidFilesUri_ReturnsStrippedPath()
        {
            var result = await _actions.ExecuteActionAsync(
                "Files", "files://C:/Users/test/report.docx", "get_path", 5000);
            Assert.That(result.Value<string>("status"), Is.EqualTo("ok"));
            var path = result.Value<string>("path");
            Assert.That(path, Is.Not.Null);
            // Forward slashes in the URI should be normalised to backslashes on Windows.
            Assert.That(path, Does.Contain("C:"));
            Assert.That(path, Does.Contain("report.docx"));
            Assert.That(path, Does.Not.Contain("files://"));
        }

        [Test]
        public async Task ExecuteAction_GetPath_FileScheme_ReturnsStrippedPath()
        {
            // X1 returns local results as file://C:\path (single 's'-less scheme, backslashes).
            var result = await _actions.ExecuteActionAsync(
                "Files", @"file://C:\Users\Stewart Robinson\Documents\report.docx", "get_path", 5000);
            Assert.That(result.Value<string>("status"), Is.EqualTo("ok"));
            var path = result.Value<string>("path");
            Assert.That(path, Is.EqualTo(@"C:\Users\Stewart Robinson\Documents\report.docx"));
        }

        [Test]
        public async Task ExecuteAction_GetPath_FileSchemeTripleSlash_ReturnsStrippedPath()
        {
            var result = await _actions.ExecuteActionAsync(
                "Files", "file:///C:/Users/test/report.docx", "get_path", 5000);
            Assert.That(result.Value<string>("status"), Is.EqualTo("ok"));
            Assert.That(result.Value<string>("path"), Is.EqualTo(@"C:\Users\test\report.docx"));
        }

        [Test]
        public async Task ExecuteAction_GetPath_NonFilesUri_ReturnsError()
        {
            var result = await _actions.ExecuteActionAsync(
                "Files", "gmail://some-other-uri", "get_path", 5000);
            Assert.That(result.Value<string>("status"), Is.EqualTo("error"));
            Assert.That(result.Value<string>("message"), Does.Contain("files://").IgnoreCase.Or.Contain("URI"));
        }

        // ── show_in_folder (logic only — does not launch Explorer in unit test) ───

        [Test]
        public async Task ExecuteAction_ShowInFolder_NonFilesUri_ReturnsError()
        {
            var result = await _actions.ExecuteActionAsync(
                "Files", "gmail://bad-uri", "show_in_folder", 5000);
            Assert.That(result.Value<string>("status"), Is.EqualTo("error"));
        }

        // ── open: non-existent file returns error ─────────────────────────────────

        [Test]
        public async Task ExecuteAction_Open_NonExistentFile_ReturnsError()
        {
            var path = Path.Combine(Path.GetTempPath(), "x1mcp_test_nonexistent_" + Guid.NewGuid() + ".txt");
            var uri = "files://" + path.Replace('\\', '/');
            var result = await _actions.ExecuteActionAsync("Files", uri, "open", 5000);
            Assert.That(result.Value<string>("status"), Is.EqualTo("error"));
            Assert.That(result.Value<string>("message"), Does.Contain("not found").IgnoreCase.Or.Contain("File"));
        }

        // ── GeneratePreviewAsync argument validation ──────────────────────────────

        [Test]
        public void GeneratePreviewAsync_NullTable_ThrowsArgumentException()
        {
            Assert.ThrowsAsync<ArgumentException>(
                () => _actions.GeneratePreviewAsync(null, "files://x", 5000));
        }

        [Test]
        public void GeneratePreviewAsync_NullUri_ThrowsArgumentException()
        {
            Assert.ThrowsAsync<ArgumentException>(
                () => _actions.GeneratePreviewAsync("Files", null, 5000));
        }

        [Test]
        public void GeneratePreviewAsync_EmptyTable_ThrowsArgumentException()
        {
            Assert.ThrowsAsync<ArgumentException>(
                () => _actions.GeneratePreviewAsync("", "files://x", 5000));
        }

        // XS-1678: GeneratePreviewAsync now resolves/validates its table before dispatching -
        // on a files-only license the server silently drops GeneratePreview for a disallowed table
        // (no callback ever fires), so an unresolved call would hang for the full timeout instead
        // of failing fast. Uses its own seeded resolver (not the shared _actions fixture, whose
        // unseeded TableSchemaResolver(null) trusts every token unchanged per its safety valve -
        // see SearchBridgeTableResolutionTests for the identical pattern) so "unknown table"
        // actually resolves to Kind.Unknown instead of passing through.
        [Test]
        public void GeneratePreviewAsync_UnknownTable_ThrowsDescriptiveArgumentExceptionBeforeTouchingChannel()
        {
            var resolver = new TableSchemaResolver(null);
            resolver.SeedForTest(new System.Collections.Generic.Dictionary<string, string[]>(),
                validSchemas: new[] { "Files" });
            var actions = new ActionBridge(new SearchBridge(new ColumnNameResolver(null), resolver), resolver);

            var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
                await actions.GeneratePreviewAsync("NotARealTable", "files://x", 5000));

            Assert.That(ex.Message, Does.Contain("unknown table 'NotARealTable'"));
            Assert.That(ex.Message, Does.Contain("Files"));
        }

        // ── Integration tests (require X1ServiceHost) ─────────────────────────────

        [Test, Explicit("Requires X1ServiceHost running and an indexed local file")]
        public async Task Integration_GetPath_RealFilesUri()
        {
            // Replace with an actual URI returned by x1_search on your machine.
            const string uri = "files://C:/Users/Public/Documents/test.txt";
            var result = await _actions.ExecuteActionAsync("Files", uri, "get_path", 10000);
            Assert.That(result.Value<string>("status"), Is.EqualTo("ok"));
            Assert.That(result.Value<string>("path"), Is.Not.Null.And.Not.Empty);
        }

        [Test, Explicit("Requires X1ServiceHost running and an indexed local file")]
        public async Task Integration_Open_RealFile()
        {
            const string uri = "files://C:/Users/Public/Documents/test.txt";
            var result = await _actions.ExecuteActionAsync("Files", uri, "open", 10000);
            Assert.That(result.Value<string>("status"), Is.EqualTo("ok"));
        }

        [Test, Explicit("Requires X1ServiceHost running and indexed Gmail items")]
        public async Task Integration_GetUrl_Gmail()
        {
            // Replace with an actual Gmail URI returned by x1_search.
            const string uri = "gmail://replace-with-real-uri";
            var result = await _actions.ExecuteActionAsync("Gmail", uri, "get_url", 15000);
            Assert.That(result.Value<string>("status"), Is.EqualTo("ok"));
            var url = result.Value<string>("url");
            Assert.That(url, Does.StartWith("https://mail.google.com/"));
        }

        [Test, Explicit("Requires X1ServiceHost running and indexed MSMail items")]
        public async Task Integration_GeneratePreview_Email_ReturnsHtml()
        {
            // Replace with an actual MSMail URI returned by x1_search.
            const string uri = "msmail://replace-with-real-uri";
            var result = await _actions.GeneratePreviewAsync("MSMail", uri, 30000);
            Assert.That(result["error"], Is.Null, result.Value<string>("error"));
            Assert.That(result.Value<string>("html"), Does.Contain("<!DOCTYPE html>"));
            Assert.That(result.Value<string>("contentType"), Is.EqualTo("text/html"));
            Assert.That(result.Value<string>("title"), Is.Not.Null.And.Not.Empty);
        }

        [Test, Explicit("Requires X1ServiceHost running and indexed Teams items")]
        public async Task Integration_GeneratePreview_Teams_ReturnsMetadataCard()
        {
            // Replace with an actual Teams URI returned by x1_search.
            const string uri = "teams://replace-with-real-uri";
            var result = await _actions.GeneratePreviewAsync("Teams", uri, 30000);
            Assert.That(result["error"], Is.Null, result.Value<string>("error"));
            Assert.That(result.Value<string>("html"), Does.Contain("<!DOCTYPE html>"));
            Assert.That(result.Value<string>("html"), Does.Contain("Teams").IgnoreCase);
        }

        [Test, Explicit("Requires X1ServiceHost running and an indexed local file")]
        public async Task Integration_GeneratePreview_Files_ReturnsHtml()
        {
            const string uri = "files://C:/Users/Public/Documents/test.txt";
            var result = await _actions.GeneratePreviewAsync("Files", uri, 30000);
            Assert.That(result["error"], Is.Null, result.Value<string>("error"));
            Assert.That(result.Value<string>("html"), Does.Contain("<!DOCTYPE html>"));
            Assert.That(result.Value<string>("previewType"), Is.Not.Null.And.Not.Empty);
        }

        // ── #4: FormatFieldValue ──────────────────────────────────────────────────

        [Test]
        public void FormatFieldValue_OaDate_ConvertedByFieldName()
        {
            // 45915.69854 is an OA date in the guarded 40000-50000 range.
            var formatted = ActionBridge.FormatFieldValue("modified", "45915.69854");
            Assert.That(formatted, Does.Match(@"^\d{4}-\d{2}-\d{2}"),
                "OA date should render as yyyy-MM-dd, got: " + formatted);
        }

        [Test]
        public void FormatFieldValue_SizeValue_NotMisreadAsDate()
        {
            // 45000 is in the OA-date range but the field name is "size" — must NOT become a date.
            var formatted = ActionBridge.FormatFieldValue("size", "45000");
            Assert.That(formatted, Does.Contain("KB"));
            Assert.That(formatted, Does.Not.Match(@"^\d{4}-\d{2}-\d{2}"));
        }

        [Test]
        public void FormatFieldValue_Size_FormatsBytesToKb()
        {
            Assert.That(ActionBridge.FormatFieldValue("size", "33568"), Is.EqualTo("32.8 KB"));
        }

        [Test]
        public void FormatFieldValue_DateFieldOutOfRange_NotConverted()
        {
            // Numeric but outside 40000-50000 — leave as-is rather than producing a nonsense date.
            Assert.That(ActionBridge.FormatFieldValue("modified", "123"), Is.EqualTo("123"));
        }

        [Test]
        public void FormatFieldValue_DrivePath_Shortened()
        {
            var formatted = ActionBridge.FormatFieldValue(
                "path", "/drives/b!abcDEF123/root:/Projects/2024/budget.xlsx");
            Assert.That(formatted, Is.EqualTo("/Projects/2024/budget.xlsx"));
        }

        [Test]
        public void FormatFieldValue_NonDrivePath_ReturnedRaw()
        {
            Assert.That(ActionBridge.FormatFieldValue("path", @"C:\Users\me\report.docx"),
                Is.EqualTo(@"C:\Users\me\report.docx"));
        }

        [Test]
        public void FormatFieldValue_EmptyOrWhitespace_ReturnsNullForSuppression()
        {
            Assert.That(ActionBridge.FormatFieldValue("modified_by", ""), Is.Null);
            Assert.That(ActionBridge.FormatFieldValue("modified_by", "   "), Is.Null);
        }

        [Test]
        public void FormatFieldValue_PlainText_ReturnedUnchanged()
        {
            Assert.That(ActionBridge.FormatFieldValue("sender", "alice@example.com"),
                Is.EqualTo("alice@example.com"));
        }

        // ── #3: TryFindCachedPreviewFile path-traversal guard ─────────────────────

        [Test]
        public void TryFindCachedPreviewFile_DotDotItemId_ReturnsNull()
        {
            Assert.That(ActionBridge.TryFindCachedPreviewFile("OneDrive", "onedrive://account/.."), Is.Null);
        }

        [Test]
        public void TryFindCachedPreviewFile_InvalidCharsItemId_ReturnsNull()
        {
            Assert.That(ActionBridge.TryFindCachedPreviewFile("OneDrive", "onedrive://account/foo<bar"), Is.Null);
        }

        [Test]
        public void TryFindCachedPreviewFile_TraversalUri_ReturnsNull()
        {
            // Plan's literal example — must never resolve to a real file outside the cache.
            Assert.That(
                ActionBridge.TryFindCachedPreviewFile("OneDrive", "onedrive://123/../../../Windows"),
                Is.Null);
        }

        // ── #5: ExtractDocxHtml maxChars truncation ───────────────────────────────

        [Test]
        public void ExtractDocxHtml_MaxChars_TruncatesAndClosesList()
        {
            var path = CreateDocxWithListItems(50);
            try
            {
                var html = ActionBridge.ExtractDocxHtml(path, 100);
                Assert.That(html, Is.Not.Null);
                Assert.That(html, Does.Contain("preview truncated at"), "expected a truncation note");
                // The list must be well-formed even though truncation happened mid-list.
                Assert.That(CountOccurrences(html, "</ul>"), Is.EqualTo(CountOccurrences(html, "<ul>")),
                    "open and close <ul> tags must balance after truncation");
                Assert.That(CountOccurrences(html, "</ul>"), Is.GreaterThanOrEqualTo(1));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void ExtractDocxHtml_NoLimit_EmitsAllItemsWithoutTruncationNote()
        {
            var path = CreateDocxWithListItems(5);
            try
            {
                var html = ActionBridge.ExtractDocxHtml(path, 0);
                Assert.That(html, Is.Not.Null);
                Assert.That(html, Does.Not.Contain("preview truncated at"));
                Assert.That(CountOccurrences(html, "<li>"), Is.EqualTo(5));
                Assert.That(CountOccurrences(html, "</ul>"), Is.EqualTo(CountOccurrences(html, "<ul>")));
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ── Gmail message-id extraction (get_url eid1 fix) ────────────────────────

        [Test]
        public void GmailEidFromUri_ExtractsLastSegment()
        {
            Assert.That(ActionBridge.GmailEidFromUri("gmail://1608121536/19d0b4f2418c01af"),
                Is.EqualTo("19d0b4f2418c01af"));
        }

        [Test]
        public void GmailEidFromUri_RootId_ReturnsNull()
        {
            // "0" is the synthetic parent/root id — not a real message.
            Assert.That(ActionBridge.GmailEidFromUri("gmail://1608121536/0"), Is.Null);
        }

        [Test]
        public void GmailEidFromUri_TrailingSlash_Ignored()
        {
            Assert.That(ActionBridge.GmailEidFromUri("gmail://1608121536/19d0b4f2418c01af/"),
                Is.EqualTo("19d0b4f2418c01af"));
        }

        [Test]
        public void GmailEidFromUri_NullOrEmpty_ReturnsNull()
        {
            Assert.That(ActionBridge.GmailEidFromUri(null), Is.Null);
            Assert.That(ActionBridge.GmailEidFromUri(""), Is.Null);
        }

        // ── #a: EmbedPreviewFileAsync type dispatch ───────────────────────────────

        [Test]
        public async Task EmbedPreviewFile_Docx_ExtractsReadableText()
        {
            var path = CreateDocxWithListItems(5);
            try
            {
                var (html, previewType) = await _actions.EmbedPreviewFileAsync(path, 0);
                Assert.That(previewType, Is.EqualTo("docx"));
                Assert.That(html, Does.Contain("<li>"));
                Assert.That(html, Does.Not.Contain("PK"), "must not leak raw zip bytes");
            }
            finally { File.Delete(path); }
        }

        [Test]
        public async Task EmbedPreviewFile_TextFile_ReturnsText()
        {
            var path = Path.Combine(Path.GetTempPath(), "x1mcp_test_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, "hello world\nsecond line", Encoding.UTF8);
            try
            {
                var (html, previewType) = await _actions.EmbedPreviewFileAsync(path, 0);
                Assert.That(previewType, Is.EqualTo("text"));
                Assert.That(html, Does.Contain("hello world"));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public async Task EmbedPreviewFile_HtmlFile_ReturnsHtml()
        {
            var path = Path.Combine(Path.GetTempPath(), "x1mcp_test_" + Guid.NewGuid().ToString("N") + ".html");
            File.WriteAllText(path, "<html><body><p>hi</p></body></html>", Encoding.UTF8);
            try
            {
                var (html, previewType) = await _actions.EmbedPreviewFileAsync(path, 0);
                Assert.That(previewType, Is.EqualTo("html"));
                Assert.That(html, Does.Contain("<p>hi</p>"));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public async Task EmbedPreviewFile_PdfBytes_ReturnsNullForCardFallback()
        {
            // %PDF magic, no extension to dispatch on — must not be embedded as text.
            var path = Path.Combine(Path.GetTempPath(), "x1mcp_test_" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(path, new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 });
            try
            {
                var (html, previewType) = await _actions.EmbedPreviewFileAsync(path, 0);
                Assert.That(html, Is.Null);
                Assert.That(previewType, Is.Null);
            }
            finally { File.Delete(path); }
        }

        [Test]
        public async Task EmbedPreviewFile_NonWordZip_ReturnsNull()
        {
            // A zip (PK..) that is NOT a Word doc, e.g. an xlsx — ExtractDocxHtml returns null,
            // so the dispatcher should report unembeddable rather than dumping bytes.
            var path = Path.Combine(Path.GetTempPath(), "x1mcp_test_" + Guid.NewGuid().ToString("N"));
            using (var fs = new FileStream(path, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("xl/workbook.xml");
                using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                    w.Write("<workbook/>");
            }
            try
            {
                var (html, previewType) = await _actions.EmbedPreviewFileAsync(path, 0);
                Assert.That(html, Is.Null);
                Assert.That(previewType, Is.Null);
            }
            finally { File.Delete(path); }
        }

        // ── #b: TryEmbedBinaryObject — cached object embedding ────────────────────

        [Test]
        public async Task EmbedPreviewFile_PngUnderCap_EmbedsAsImageDataUri()
        {
            // Minimal 1x1 PNG.
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
            var path = Path.Combine(Path.GetTempPath(), "x1mcp_test_" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(path, png);
            try
            {
                var (html, previewType) = await _actions.EmbedPreviewFileAsync(path, 0);
                Assert.That(previewType, Is.EqualTo("image"));
                Assert.That(html, Does.Contain("<img"));
                Assert.That(html, Does.Contain("data:image/png;base64,"));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void PlainTextToHtml_PdfExtractedText_RendersAsPlainTextNotEmbed()
        {
            // XS-1610: the Artifact viewer's CSP blocks <embed>/<object> plugin content, so PDFs
            // must render as extracted text, not a base64 <embed> (see EmbedPreviewFileAsync's PDF
            // branch, which feeds ExtractFileAsync's output through this same helper).
            var html = ActionBridge.PlainTextToHtml("Taekwondo chapter 1: stances and forms.", 0);
            Assert.That(html, Does.Contain("<pre class=\"plaintext\">"));
            Assert.That(html, Does.Contain("Taekwondo chapter 1"));
            Assert.That(html, Does.Not.Contain("<embed"));
            Assert.That(html, Does.Not.Contain("data:application/pdf"));
        }

        [Test]
        public void PlainTextToHtml_OverMaxChars_TruncatesWithNotice()
        {
            var html = ActionBridge.PlainTextToHtml(new string('x', 100), 10);
            Assert.That(html, Does.Contain(new string('x', 10)));
            Assert.That(html, Does.Not.Contain(new string('x', 11)));
            Assert.That(html, Does.Contain("truncated"));
        }

        [Test, Explicit("Requires X1ServiceHost running and a real local PDF file")]
        public async Task Integration_EmbedPreviewFileAsync_Pdf_ExtractsTextInsteadOfEmbedding()
        {
            // Replace with a real local PDF path indexed by X1.
            const string path = @"C:\Users\Public\Documents\test.pdf";
            var (html, previewType) = await _actions.EmbedPreviewFileAsync(path, 0);
            Assert.That(previewType, Is.EqualTo("pdf"));
            Assert.That(html, Does.Contain("<pre class=\"plaintext\">"));
            Assert.That(html, Does.Not.Contain("<embed"));
        }

        [Test]
        public async Task EmbedPreviewFile_ImageOverCap_FallsBackToCard()
        {
            // A .png larger than the embed cap must not be inlined — caller renders the metadata card.
            var path = Path.Combine(Path.GetTempPath(), "x1mcp_test_" + Guid.NewGuid().ToString("N") + ".png");
            var big = new byte[ActionBridge.MaxEmbedObjectBytes + 1];
            big[0] = 0x89; big[1] = 0x50; big[2] = 0x4E; big[3] = 0x47;   // PNG magic
            File.WriteAllBytes(path, big);
            try
            {
                var (html, previewType) = await _actions.EmbedPreviewFileAsync(path, 0);
                Assert.That(html, Is.Null);
                Assert.That(previewType, Is.Null);
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void TryEmbedBinaryObject_NonEmbeddableExtension_ReturnsNull()
        {
            var path = Path.Combine(Path.GetTempPath(), "x1mcp_test_" + Guid.NewGuid().ToString("N") + ".xlsx");
            File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
            try
            {
                var (html, previewType) = ActionBridge.TryEmbedBinaryObject(path, ".xlsx");
                Assert.That(html, Is.Null);
                Assert.That(previewType, Is.Null);
            }
            finally { File.Delete(path); }
        }

        // ── XS-1610: SanitizeUnsupportedEmbeds — general <embed>/<object> guard ────
        // SharedHtmlWrapper (every preview path's final step) runs body markup through this
        // before returning it, so it's the safety net for any raw <embed>/<object> that reaches
        // the caller — not just the PDF case EmbedPreviewFileAsync already avoids deliberately,
        // but also e.g. a cached webpage snapshot (.html/.htm passthrough) that itself embeds a
        // video/PDF plugin. The Artifact viewer's CSP blocks these outright, leaving a blank pane.

        [Test]
        public void SanitizeUnsupportedEmbeds_SelfClosingEmbedTag_ReplacedWithFallback()
        {
            var html = "<p>before</p><embed src=\"data:application/pdf;base64,AAAA\" type=\"application/pdf\" /><p>after</p>";
            var sanitized = ActionBridge.SanitizeUnsupportedEmbeds(html);

            Assert.That(sanitized, Does.Not.Contain("<embed"));
            Assert.That(sanitized, Does.Contain("embed-unsupported"));
            Assert.That(sanitized, Does.Contain("<p>before</p>"));
            Assert.That(sanitized, Does.Contain("<p>after</p>"));
        }

        [Test]
        public void SanitizeUnsupportedEmbeds_UnclosedEmbedTag_ReplacedWithFallback()
        {
            // Some generators emit <embed ...> without a trailing slash (still a void element).
            var html = "<embed src=\"data:application/pdf;base64,AAAA\" type=\"application/pdf\">";
            var sanitized = ActionBridge.SanitizeUnsupportedEmbeds(html);

            Assert.That(sanitized, Does.Not.Contain("<embed"));
            Assert.That(sanitized, Does.Contain("embed-unsupported"));
        }

        [Test]
        public void SanitizeUnsupportedEmbeds_ObjectTagWithFallbackContent_ReplacedEntirely()
        {
            var html = "<object data=\"movie.mp4\" type=\"video/mp4\"><p>fallback text</p></object>";
            var sanitized = ActionBridge.SanitizeUnsupportedEmbeds(html);

            Assert.That(sanitized, Does.Not.Contain("<object"));
            Assert.That(sanitized, Does.Not.Contain("</object>"));
            Assert.That(sanitized, Does.Contain("embed-unsupported"));
        }

        [Test]
        public void SanitizeUnsupportedEmbeds_OrdinaryHtml_ReturnsUnchanged()
        {
            var html = "<div class=\"card\"><p>Nothing to see here.</p></div>";
            Assert.That(ActionBridge.SanitizeUnsupportedEmbeds(html), Is.EqualTo(html));
        }

        [Test]
        public void SanitizeUnsupportedEmbeds_NullOrEmpty_ReturnsInputUnchanged()
        {
            Assert.That(ActionBridge.SanitizeUnsupportedEmbeds(null), Is.Null);
            Assert.That(ActionBridge.SanitizeUnsupportedEmbeds(""), Is.Empty);
        }

        // ── #c: output=file fragment writing ──────────────────────────────────────

        [Test]
        public void ExtractBodyInner_FullDocument_ReturnsInnerBodyOnly()
        {
            var html = "<!DOCTYPE html>\n<html><head><title>x</title></head>\n<body>\n<p>hello</p>\n</body>\n</html>";
            var inner = ActionBridge.ExtractBodyInner(html);
            Assert.That(inner, Is.EqualTo("<p>hello</p>"));
            Assert.That(inner, Does.Not.Contain("<body"));
            Assert.That(inner, Does.Not.Contain("<html"));
        }

        [Test]
        public void ExtractBodyInner_NoBody_ReturnsInputUnchanged()
        {
            var frag = "<p>already a fragment</p>";
            Assert.That(ActionBridge.ExtractBodyInner(frag), Is.EqualTo(frag));
        }

        [Test]
        public void WritePreviewFragmentToFile_WritesArtifactReadyFragment()
        {
            var dir = Path.Combine(Path.GetTempPath(), "x1mcp_test_prev_" + Guid.NewGuid().ToString("N"));
            var html = "<!DOCTYPE html>\n<html><head><title>ignored</title></head>\n<body>\n<p>body content</p>\n</body>\n</html>";
            try
            {
                var result = ActionBridge.WritePreviewFragmentToFile(html, "My Doc: v2", "docx", dir);

                Assert.That(result.Value<string>("mode"), Is.EqualTo("file"));
                Assert.That(result.Value<string>("previewType"), Is.EqualTo("docx"));
                Assert.That(result["html"], Is.Null, "file mode must NOT return inline html");

                var path = result.Value<string>("path");
                Assert.That(File.Exists(path), Is.True);
                Assert.That(Path.GetFileName(path), Does.Not.Contain(":"), "invalid filename chars must be sanitized");

                var written = File.ReadAllText(path);
                Assert.That(written, Does.Contain("<title>My Doc: v2</title>"));
                Assert.That(written, Does.Contain("<p>body content</p>"));
                Assert.That(written, Does.Not.Contain("<body"), "fragment must have no body wrapper for the artifact host");
                Assert.That(written, Does.Not.Contain("<html"));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        private static string CreateDocxWithListItems(int count)
        {
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
            for (int i = 0; i < count; i++)
                xml.Append("<w:p><w:pPr><w:pStyle w:val=\"ListParagraph\"/></w:pPr><w:r><w:t>")
                   .Append("This is list item number " + i + " with padding text to consume characters.")
                   .Append("</w:t></w:r></w:p>");
            xml.Append("</w:body></w:document>");

            var path = Path.Combine(Path.GetTempPath(), "x1mcp_test_" + Guid.NewGuid().ToString("N") + ".docx");
            using (var fs = new FileStream(path, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("word/document.xml");
                using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                    w.Write(xml.ToString());
            }
            return path;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
                return 0;
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }
    }
}
