# X1 Search (Desktop Extension / MCPB)

Search and preview your own content from Claude — local files, Outlook/Exchange/Gmail email
and **attachments**, cloud files (OneDrive, GDrive, Dropbox, SharePoint), and chat (Teams,
Slack) — powered by your local X1 Search index.

This is the Anthropic MCPB ("Desktop Extension") packaging of the x1-search connector, for
one-click install into Claude Desktop. It bundles the same `X1McpBridge.exe` the Cowork plugin
and `install.ps1` distribution paths use, wrapped via the MCPB manifest's `binary` server type —
no new server code, since the connector already speaks stdio MCP JSON-RPC directly.

## Layout

```
mcpb-package/
  manifest.json   - checked in; version kept in sync with version.props by bump.ps1
  icon.png        - checked in; rendered from the official X1 logo mark, see tools\make-icon.ps1
  tools/
    make-icon.ps1 - regenerates icon.png from X1UI2.Base\Icons\X1.svg; rerun if that source changes
  server/         - NOT checked in; staged fresh by build-mcpb.ps1 from installer\ on each build
```

## Requirements

- **Windows 10/11** with .NET Framework 4.8 (the connector is a native Windows executable — this
  extension does not support macOS/Linux).
- **X1 Desktop** installed with at least one completed index scan.
- **X1ServiceHost** running under the same Windows user account (it starts with X1 Desktop).

This extension bundles the connector itself, but it cannot bundle X1 Desktop / X1ServiceHost —
those must already be installed and running on the machine.

## Why `--proxy`

`manifest.json`'s `mcp_config.args` is `["--proxy"]`, not empty. X1ServiceHost cannot tolerate
two concurrent WCF clients, so every MCP registration on a machine (this extension, the Cowork
plugin, an `install.ps1` install) must proxy through one shared relay instead of each opening its
own WCF connection — see `X1McpBridge\ProxyMode.cs`. This package ships Lean-only (no
`X1McpGraphQL.exe`), so `--proxy` self-elects `X1McpBridge.exe --host` as that shared relay.

## Building

Run `build-installer.bat` from the repo root — it builds the solution, stages `installer\`, and
(if the `mcpb` CLI is on PATH — `npm install -g @anthropic-ai/mcpb`) invokes `build-mcpb.ps1`,
which stages this package's `server\` folder and runs `mcpb pack` to produce
`installer\x1-search.mcpb`.

## Known gaps

- `manifest.json`'s `compatibility.claude_desktop` version floor (`>=0.10.0`) is an unverified
  guess — confirm against Anthropic's current MCPB docs before submitting.

## Privacy Policy

See [Privacy Policy](https://www.x1.com/privacy-and-terms/). This extension does not send your
indexed content (files, email, chat) to X1 or to Anthropic — it reads from your local X1 Search
index and returns results directly to Claude.
