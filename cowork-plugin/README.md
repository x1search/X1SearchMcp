# X1 Search (Cowork plugin)

Search and preview your own content from Claude — local files, Outlook/Exchange/Gmail email
and **attachments**, cloud files (OneDrive, GDrive, Dropbox, SharePoint), and chat (Teams,
Slack) — powered by your local X1 Search index.

This single plugin bundles two things:

- **The x1-search connector** — a local MCP server (`connector/X1McpBridge.exe`) that talks to X1
  Search over WCF. Registered via `.mcp.json` using `${CLAUDE_PLUGIN_ROOT}`, so it runs from
  wherever the plugin is installed.
- **The `/x1` skill** — guidance that teaches Claude to drive the connector efficiently
  (search one table, or several merged in one call, use the inline `actions`, render previews as
  artifacts, handle attachments, and prefer the connector's path-not-bytes retrieval for large
  files). Includes a bundled `extract-office-text.ps1` helper for `.pptx`/`.xlsx` content.

## Requirements

- **Windows 10/11** with .NET Framework 4.8 (the connector is a native Windows executable).
- **X1 Desktop** installed with at least one completed index scan.
- **X1ServiceHost** running under the same Windows user account (it starts with X1 Desktop).

The plugin bundles the connector itself, but it cannot bundle X1 Desktop / X1ServiceHost —
those must be installed and running on the machine.

## What you can do

- "Find the latest deck someone at work sent me and show me a preview."
- "Search my files for the Q4 budget spreadsheet and open it."
- "What did Alice email me about the contract?"

## Tools

| Tool | Purpose |
|------|---------|
| `x1_search` | Keyword / X1-syntax search (one table, or several merged in one call); results include inline `actions`. |
| `x1_list_sources` | Discover indexed tables, columns, and `capabilities`. |
| `x1_list_actions` | Post Search Actions for a result (usually unnecessary — see `x1_search`). |
| `x1_execute_action` | Act on a result: `get_path`, `open`, `show_in_folder`, `get_url`, `open_url`. |
| `x1_get_metadata` | Field values for one item. |
| `x1_get_content` | Item content (`auto` / `content` / `preview` / `internal`). `content` is the extracted text and works for every table. |
| `x1_generate_preview` | Self-contained HTML preview to render as an artifact. |
| `x1_version` | Versions and paths of the connector's daemon and bridge, plus every X1McpBridge running on the machine — confirms which build is serving. |

For the full reference (parameters, query syntax, configuration, troubleshooting) see the
connector's [User Manual](../docs/UserManual.md).

## Upgrading

The connector runs a shared relay (`connector/X1McpBridge.exe`) that's deliberately kept running
after Claude closes, so a later session reuses it instead of starting cold. That means it can hold
its own executable locked when the plugin updates. If an update seems to not take effect (still the
old version, `x1_version` looks stale), quit Claude Desktop and Claude Code, stop the process
(Task Manager, or `Get-Process X1McpBridge | Stop-Process` in PowerShell), then update the plugin.

The relay also shuts itself down on its own after an hour of no use (configurable via the
`X1McpConnectorIdleShutdown` registry value — see the connector's
[User Manual](../docs/UserManual.md#46-logging-and-lifecycle-settings)), so an update run well
after your last search should generally find it already unloaded.

## Notes

- The connector reads `connector/x1mcp.config.json` for default tables, display columns, and
  preview settings — edit it to change which sources are searched by default.
- `connector/` and `skills/` are assembled from the build output and the canonical skill source
  (`../skill/x1`) by `build-installer.bat`; they are not checked into source control.
- The payload folder is deliberately not named `bin/`: a top-level `bin/` is auto-added to PATH
  by the CLI, and claude.ai-hosted plugins are rejected at upload for shipping one. The
  connector is declared as an entry point via `mcpServers` in `.mcp.json` instead.
