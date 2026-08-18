# X1 MCP Bridge

`X1McpBridge.exe` is a stdio [Model Context Protocol](https://modelcontextprotocol.io/) server that exposes X1 Search to clients such as Claude Desktop and Claude Code. It uses the same WCF `IX1SearchManager` duplex channel as the X1 desktop UI (`net.pipe://localhost/X1SearchManager_<user>` by default).

> This file is a developer-oriented quick reference. For the full guide (installation, configuration, every tool, the `/x1` skill, query syntax, and troubleshooting) see [../docs/UserManual.md](../docs/UserManual.md).

## Prerequisites

- Windows with .NET Framework 4.8.
- **X1ServiceHost.exe** running for the same Windows user as the bridge (typically start the X1 desktop app once, or ensure the service host is loaded).
- Optional: set `defaultTables` in `x1mcp.config.json` next to the executable, or set environment variable `X1_MCP_DEFAULT_TABLES` to a comma-separated list of schema names (e.g. `Files,Outlook`).

## Build

From the repository root:

```bat
nuget restore X1Mcp\X1Mcp.sln
msbuild X1Mcp\X1Mcp.sln /p:Configuration=Release
```

Output: `X1Mcp\X1McpBridge\bin\Release\X1McpBridge.exe`

## Claude Desktop

Merge the sample into your Claude Desktop config (see [Claude docs](https://docs.anthropic.com/en/docs/claude-desktop)):

- Sample: [claude_desktop_config.sample.json](claude_desktop_config.sample.json)

## Optional config file

`x1mcp.config.json` beside `X1McpBridge.exe`:

```json
{
  "defaultTables": ["Files", "Outlook"],
  "autoPreviewTimeoutMs": 10000,
  "prefetchPreviewCount": 3,
  "tempMaxAgeHours": 168,
  "tempMaxTotalMB": 500
}
```

- `autoPreviewTimeoutMs` — preview wait cap in `x1_get_content` mode `auto`.
- `prefetchPreviewCount` — top OneDrive/GDrive results that get a background preview prefetched after a search (`0` disables).
- `tempMaxAgeHours` — startup sweep deletes files under `%TEMP%\x1mcp_previews\` older than this (default 168 h / 7 days; `0` disables). Orphan `x1mcp_extract_*.txt` / `x1mcp_content_*.txt` at `%TEMP%` are always cleaned regardless of age.
- `tempMaxTotalMB` — after the age sweep, if `%TEMP%\x1mcp_previews\` is still over this cap, oldest files are evicted until it fits (default 500 MB; `0` disables).

See the [User Manual](../docs/UserManual.md#41-x1mcpconfigjson) for `sources` column mappings and [§4.5](../docs/UserManual.md#45-temp-file-cleanup) for the full temp-cleanup story.

## Remote / Virtual mode

If X1 is configured for TCP to a remote host, set `X1_MCP_SERVICE_HOST` to that hostname (same behavior as the desktop `ServiceHost` setting). Encrypted NetTcp and UPN identity follow `X1.Common.WCF.WcfUtils`.

## Registry (advanced)

If `UseNetTcpBinding` = 1 in X1 registry settings, `SearchManagerNetTcpBindingPort` must be set (matches X1UI2 `SearchSessionManager`).

## Smoke test

With X1 running:

```bat
X1McpBridge.exe --smoke-wcf
```

Prints JSON with `totalResults` or an `error` field.

## Tools

| Tool | Purpose |
|------|---------|
| `x1_search` | Keyword / X1-syntax search (one table, or several merged in one call — each searched sequentially and merged with a `byTable` breakdown) with optional `filters`, `sort`, `limit`. Results include an inline `actions` array (`includeActions`, default true) and `prefetchInitiated` on OneDrive/GDrive hits. |
| `x1_list_sources` | Configured tables, their columns, a `capabilities` object (`actions`, `preview`), and — via XS-1573 `GetDataSourcesInfo` — an `accounts[]` array of `{ accountName?, displayName?, totalCount, itemCount, lastScanTime?, isScanning }`. `totalCount` is scanner-wide (accounts sharing a schema all repeat the same number); `itemCount` is this account's own (both XS-1612). |
| `x1_list_actions` | Post Search Actions for a `table`+`uri` (usually redundant — see the inline `actions`). |
| `x1_execute_action` | Run an action: `get_path`, `open`, `show_in_folder`, `get_url`, `open_url`. |
| `x1_get_metadata` | `GetItemInternal` field values for one `table` + `uri`. |
| `x1_get_content` | Text/preview/fields for an indexed item via `mode` (`auto`/`content`/`preview`/`internal`). `content` uses XS-1575 `GetContent` — works for every table and hits the content store on repeat. `extract` accepted as a back-compat alias. |
| `x1_extract_file` | XS-1575 `ExtractTextFromFile`: extract text from an arbitrary LOCAL file (not required to be indexed). Useful for preview caches and downloads. |
| `x1_add_tags` / `x1_remove_tags` / `x1_clear_tags` | XS-1577 tagging: positional `uris[i] ↔ tags[i]` for add/remove; clear removes all tags. |
| `x1_generate_preview` | Self-contained HTML preview (`maxChars`, `previewType`) to render as a Claude artifact/widget. |
| `x1_cost_savings` / `x1_reset_stats` | Token-savings report and stats reset. |
| `x1_set_query_log` | XS-1578: enable/disable logging of actual search query text (appended to the `PERF` log line) via the `X1McpQueryLog` registry DWORD — off by default, read live so toggling needs no restart. Deliberately independent of `Verbosity=DEBUG`, which never logs query content. |
| `x1_version` | This bridge's version (stamped from `X1Mcp\version.props`) and exe path. Answered from the assembly alone, so it still works with X1ServiceHost down — which is when you most need it. The daemon's `x1_version` calls this and adds its own half. |

## Resources / prompts

- Resource URI `x1://index/stats` — JSON with `GetX1ServiceHostStatus` text.
- Prompt `x1_search_best_practices` — short usage guidance for the model.
