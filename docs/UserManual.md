# X1 Search MCP Bridge — User Manual

## Table of Contents

1. [Overview](#1-overview)
   - [1.1 Token Economy](#11-token-economy)
2. [Prerequisites](#2-prerequisites)
3. [Installation](#3-installation)
4. [Configuration](#4-configuration)
   - [x1mcp.config.json](#41-x1mcpconfigjson)
   - [Default Tables Environment Variable](#42-default-tables-environment-variable)
   - [Client Registration](#43-client-registration)
   - [Temp file cleanup](#45-temp-file-cleanup)
   - [Logging and lifecycle settings](#46-logging-and-lifecycle-settings)
5. [Tools Reference](#5-tools-reference)
   - [x1_search](#51-x1_search)
   - [x1_get_metadata](#52-x1_get_metadata)
   - [x1_get_content](#53-x1_get_content)
   - [x1_list_sources](#54-x1_list_sources)
   - [x1_list_actions](#55-x1_list_actions)
   - [x1_execute_action](#56-x1_execute_action)
   - [x1_generate_preview](#57-x1_generate_preview)
   - [x1_cost_savings](#58-x1_cost_savings)
   - [x1_reset_stats](#59-x1_reset_stats)
   - [x1_set_query_log](#510-x1_set_query_log)
6. [Resources Reference](#6-resources-reference)
7. [Prompts Reference](#7-prompts-reference)
8. [Query Syntax](#8-query-syntax)
9. [Usage Examples](#9-usage-examples)
10. [User Stories](#10-user-stories)
11. [The /x1 Skill](#11-the-x1-skill)
12. [Diagnostic Tool](#12-diagnostic-tool)
13. [Troubleshooting](#13-troubleshooting)

---

## 1. Overview

The **X1 Search MCP Bridge** (`X1McpBridge.exe`) is a Model Context Protocol (MCP) server that connects Claude to your local X1 Search index. Once installed, Claude can search your files, email and email **attachments**, calendar, contacts, cloud files (OneDrive, GDrive, Dropbox, SharePoint), and chat (Teams, Slack) directly using natural language — powered by the same index that the X1 desktop application uses. Beyond searching, Claude can **preview** an item (rendered inline), **act** on it (open, reveal in Explorer, get a web URL), and pull readable **content** from documents and attachments.

It works with **Claude Desktop**, **Claude Code** (CLI / IDE extensions), and **claude.ai** (web, via the Desktop relay). Communication uses the MCP stdio transport: the Claude client launches `X1McpBridge.exe` as a subprocess and exchanges JSON-RPC 2.0 messages over stdin/stdout. The bridge in turn communicates with the X1 service layer via WCF (Windows Communication Foundation).

```
Claude (Desktop / Code / web)  ←── JSON-RPC (stdio) ──→  X1McpBridge.exe  ←── WCF ──→  X1ServiceHost
```

For Claude Code and GitHub Copilot, the installer also deploys a **`/x1` skill** that teaches the agent how to drive the connector efficiently — see [Section 11](#11-the-x1-skill).

### 1.1 Token Economy

The most consequential difference between X1 Search and the curated Microsoft Graph / Gmail / OneDrive connector already installed on this machine is **what enters the conversation context**. Every character of text the model sees consumes tokens. Token use directly affects cost, latency, and whether a task fits within the context window at all.

**The comparison baseline is the curated connector, not a raw API.** An earlier version of this section (and of `x1_cost_savings`) compared every x1 call against a hypothetical direct Graph/Gmail/OneDrive API call returning files as base64 binary (`file_bytes ÷ 3` tokens). That over-states savings on text content (the connector actually installed returns readable HTML/text, not a base64 dump), gives zero credit to metadata/search (field-selection is a real, measurable win), and under-states savings on scanned/image content (the realistic alternative needs OCR/vision, which doesn't scale with file size the way a base64 divisor implies). The methodology below fixes that by classifying every retrieval into one of four categories, each measured against its own realistic counterfactual.

#### How tokens are counted

As a rough rule of thumb, **1 token ≈ 4 characters** of English text.

#### Three kinds of number, never mixed

The report is built so that its strongest claims are the ones that cannot be wrong:

1. **Observed** — tokens x1 actually put in context, latency, item counts. No model is involved.
2. **Bounded comparison** — a low/high interval for what the connector would have cost. It is *not* a confidence interval; it is the span between the most and the least the available evidence supports. **The interval widens when the evidence is thin**, so a wide range signals weak calibration, not a big win. Cite the low end.
3. **Neither** — deferred tokens and capability gains. These are not token savings and never enter the savings figures.

#### The four retrieval categories, and what each is allowed to claim

| Category | Realistic counterfactual | Evidence | Claims |
|---|---|---|---|
| Metadata / search (`x1_search`, `x1_get_metadata`) | The connector's own search record (duplicated ids, tracking `webLink`, `internetMessageId`) | Measured, n=2: 570–615 chars vs 1,220–1,310 → **2.13×** | floor 1.62×, ceiling 2.13× |
| Text-native (email, `.docx`, `.txt`, `.md`, `.html`) | The connector's `read_resource` rich text/HTML | Measured, n=1: 6,989 chars vs 15,306 → **2.17×** | floor 1.47×, ceiling 2.17× |
| Structured/tabular (`.xlsx`, `.csv`) | The connector's workbook download/parse | **Not measured** | **nothing** |
| Image/OCR-required (scanned `.pdf`, images) | Per-page vision/OCR tokens to reach text parity | Cited, not measured: ~1,500 tokens/page | ceiling only, and only where sparse extraction proves no text layer existed |

Two rules do most of the work here, and both were learned by getting them wrong:

- **No claim without evidence.** A category we never measured claims nothing rather than borrowing a neighbour's coefficient. A *cited* figure we didn't verify ourselves can raise the ceiling but never establish a floor.
- **A path is not a payload.** When x1 returns a file path, the content never entered context. That is a different outcome from an alternative that delivered it, so those calls claim **zero** on the low bound and their tokens are reported as **deferred** — reading the file later costs them then.

The floor is derived from the measured multiple discounted by how many paired samples back it (n=1 → 40% of the observed gap, n=2 → 55%, rising to 90% at n≥10). That is the mechanism that turns thin evidence into a visibly wide range instead of a confident wrong number.

#### What the alternative actually does with a binary — measured, not assumed

An earlier revision made this a configurable assumption about the *client*: does the fallback
environment have a local filesystem and parsing tooling, so it could download a binary and parse it
without spending context? Measurement on 2026-07-31 showed that was the wrong question, and that
both settings of the switch were indefensible.

`read_resource` on a OneDrive `.xlsx` and on a 75 KB OneDrive `.pdf` both returned **extracted text
inlined in the response** — no path option, no base64, and long content truncated rather than paged.
So the connector never hands over bytes for you to put on disk, which means:

- **The client is not the deciding variable.** Claude Code and Claude Desktop behave identically:
  reading a file through the connector puts its text in context.
- **Base64 was the wrong ceiling too.** For that 75 KB PDF, base64 would have implied ~25,000 tokens;
  the connector actually spent ~1,000. Roughly 25× over.

The switch was therefore deleted rather than re-tuned — a measured fact doesn't need a config knob.
For a path-returning x1 call the floor is **zero** (nothing entered context, and a later read pays
the tokens then) and the ceiling is **the extracted text the connector would have inlined**, sized
from x1's own fragment since both are extracted text of the same document.

One case stays deliberately silent: `x1_get_content mode=preview` returns a bare path with no
fragment to size a ceiling from, and extracted text is a small, highly variable fraction of a
binary's bytes — so it claims nothing at either end rather than guessing.

**Capability gain** is counted separately: items no curated connector could reach at all — local
files **not** mirrored to any cloud it can call. Files inside a synced OneDrive/Dropbox/Google Drive
folder do *not* count, because the connector reaches those by another route.


#### How x1 avoids the problem

The bridge calls X1ServiceHost locally and returns one of:

| What the bridge returns | Tokens entering context |
|---|---|
| Indexed metadata + keyword snippet per result | 300–500 per item |
| Extracted-text preview (capped by `maxChars`) | ~1 token per 4 chars of text |
| A local file path (from `x1_get_content mode=preview`) | ~50 tokens |
| Path to an on-disk HTML fragment (`output="file"`) | **~80 tokens** |
| Inline HTML with embedded `data:` URI image/PDF < 1 MB (`output="inline"`) | varies by file size |

Text content is extracted locally by X1ServiceHost or the bundled `extract-office-text.ps1`; binary objects embed as `data:` URIs only when the file fits inside the 1 MB cap and the caller explicitly wants inline HTML. Note that inline pdf/image previews embed the same base64 data the connector would — x1 hasn't extracted anything in that mode, so there's no realistic saving to claim there (see the `x1_cost_savings` breakdown, which reports that case as zero saved rather than inflating it).

#### Cost reference — x1 operations

| Operation | Approximate tokens |
|---|---|
| `x1_search` — one result (fields + snippet + actions) | 300–500 |
| `x1_search` — 10-result page | 3,000–5,000 |
| `x1_generate_preview` — email metadata fallback card | 400–600 |
| `x1_generate_preview` — email with body, `output="inline"` | 1,500–4,000 |
| `x1_generate_preview` — DOCX full document, `output="inline"` | 8,000–15,000 |
| `x1_generate_preview` — DOCX with `maxChars=6000`, `output="inline"` | 6,500–8,000 |
| `x1_generate_preview` — any type, `output="file"` | **~80** (path only) |
| `x1_get_content` — path returned (`mode=preview`) | ~50 |
| `extract-office-text.ps1` text output, `maxChars=6000` | ~1,800 |

#### Cost reference — equivalent curated-connector fetch

| Content | x1 cost | Connector (realistic) cost | Notes |
|---|---|---|---|
| Email search result, 1 item | ~570–615 chars | ~1,220–1,310 chars | Measured 2026-07-30; connector row carries duplicated id/uri, tracking `webLink`, `internetMessageId` |
| Email body, one real thread | 6,989 chars (extracted text) | 15,306 chars (HTML `body.content`, connector wrapper fields not even counted) | Measured 2026-07-30 |
| Scanned PDF, 1 page | x1: pre-extracted text, a few hundred tokens | ~1,500 tokens (vision/OCR parity cost) | Per-page, not per-byte — a large multi-page file scales differently than a base64 divisor implies |
| Local (non-cloud) file with no connector counterfactual | x1: extracted text or path | **N/A — capability gain**, not a token ratio | Reported separately in `x1_cost_savings` |

For pure email body text, x1 and the connector both return text, so the gap is the HTML-stripping ratio above (~46%), not the enormous multiple a base64 assumption would suggest. The gap remains real and worth claiming — just proportionate to what the connector actually returns, not to a hypothetical raw API.

---

## 2. Prerequisites

| Requirement | Notes |
|---|---|
| **Windows 10 / 11** | The bridge is a .NET Framework 4.8 Windows executable |
| **.NET Framework 4.8** | Included in Windows 10 1903+ and all Windows 11 builds |
| **X1 Desktop** | Must be installed and have completed at least one index scan |
| **X1ServiceHost running** | Must be running under the same Windows user account |
| **A supported client** | Claude Desktop, Claude Code, or GitHub Copilot (desktop app or CLI). The bridge is registered in that client's own MCP server config |
| **A paid Copilot license** (Copilot only) | Copilot Free is not entitled to the CLI/app agent surface the connector plugs into. An org-provided seat also needs the org's Copilot CLI policy enabled — see [Section 13](#13-troubleshooting) |

> **Note:** X1ServiceHost typically starts automatically with Windows when X1 Desktop is installed. If it is not running, the bridge will fail to connect and return an error on the first tool call.

---

## 3. Installation

### Build the package (from source)

If you are building from the repository rather than a prebuilt release, run the staging script first — it restores packages, builds in Release, and assembles the `installer\` folder (binaries, `install.ps1`, and the `/x1` skill):

```bat
build-installer.bat
```

### Automated installer (recommended)

The package contains `install.ps1` alongside the bridge binaries and the `/x1` skill. Run it from a PowerShell prompt:

```powershell
powershell -ExecutionPolicy Bypass -File installer\install.ps1
```

By default it installs for **every** supported client. Choose a target with `-Target`:

| `-Target` value | Configures |
|---|---|
| `All` (default) | Claude Desktop, Claude Code **and** the GitHub Copilot app |
| `Desktop` | Claude Desktop only (also enables claude.ai web via the Desktop relay) |
| `Code` | Claude Code CLI / IDE extensions only |
| `ClaudeAi` | Alias for `Desktop` |
| `Copilot` | The GitHub Copilot desktop app and the Copilot CLI (they share `~/.copilot`) |

`All` writes the Copilot registration whether or not Copilot is installed, so a machine that
installs it later is already configured. The installer says which case it found; it never fails on
a missing client.

```powershell
powershell -ExecutionPolicy Bypass -File installer\install.ps1 -Target Code
```

The installer will:

1. Copy all binaries to `%LOCALAPPDATA%\X1 Discovery\McpBridge\`.
2. Deploy a default `x1mcp.config.json` (an existing file is never overwritten).
3. **Claude Desktop** (when targeted): merge the `x1-search` entry into `%APPDATA%\Claude\claude_desktop_config.json` and offer to restart Claude Desktop.
4. **Claude Code** (when targeted): merge the `x1-search` entry into `%USERPROFILE%\.claude\settings.json`; **install the `/x1` skill** to `%USERPROFILE%\.claude\skills\x1\`; and **pre-approve the read-only/preview tools** (`x1_search`, `x1_list_sources`, `x1_list_actions`, `x1_get_metadata`, `x1_get_content`, `x1_generate_preview`) in `permissions.allow` so they stop prompting. `x1_execute_action` and `x1_reset_stats` are intentionally left to prompt — execute_action opens files and launches the browser; reset_stats permanently deletes the stats file. `x1_cost_savings` is also read-only and safe to add by hand if you want it silent too.
5. **GitHub Copilot** (when targeted): merge the `x1-search` entry into `%USERPROFILE%\.copilot\mcp-config.json` and **install the `/x1` skill** to `%USERPROFILE%\.copilot\skills\x1\`. Nothing is pre-approved: Copilot stores tool approvals in `permissions-config.json`, keyed by absolute **project directory**, so there is no machine-wide allowlist for an installer to seed — choose Copilot's "always allow" at the first prompt, or start it with `--allow-tool 'x1-search(*)'`. Copilot is **not** restarted for you, unlike Claude Desktop: it runs long-lived agent sessions in their own git worktrees, and killing it mid-session discards work.

**Custom install directory:**

```powershell
powershell -ExecutionPolicy Bypass -File installer\install.ps1 -InstallDir "D:\Tools\X1McpBridge"
```

**Uninstall** (removes the config entries, the `/x1` skill, and optionally the install directory):

```powershell
powershell -ExecutionPolicy Bypass -File installer\install.ps1 -Uninstall          # every client
powershell -ExecutionPolicy Bypass -File installer\install.ps1 -Uninstall -Target Code
powershell -ExecutionPolicy Bypass -File installer\install.ps1 -Uninstall -Target Copilot
```

> **Note — the standalone installer handles this for you.** The shared relay is deliberately kept
> running after every Claude session closes (see [§4.6](#46-logging-and-lifecycle-settings) on
> `X1McpConnectorIdleShutdown`), so it holds `X1McpBridge.exe` and its DLLs locked indefinitely.
> `install.ps1` stops it, verifies the port is actually free, copies the new binaries, and restarts
> the relay from them automatically — you don't need to quit Claude or kill anything by hand for a
> re-install with this script.
>
> **This does not apply to the Cowork/marketplace plugin.** Its update mechanism has no equivalent
> stop-before-replace step, so if you installed x1-search as a plugin rather than through
> `install.ps1`, quit Claude Desktop and Claude Code and stop `X1McpBridge.exe` yourself (Task
> Manager, or `Get-Process X1McpBridge | Stop-Process`) before updating the plugin — or simply wait
> past the idle-shutdown threshold (1 hour by default) since the relay's last use.

### GitHub Copilot as a plugin instead

`build-installer.bat` also produces `installer\copilot-plugin\`, a self-contained Copilot plugin carrying both the connector and the `/x1` skill. Use it **instead of** `-Target Copilot`, not as well: each registers a server named `x1-search`, so a machine with both sees the server and the skill twice (harmless — both run `--proxy` and share the one relay — but confusing). `install.ps1` warns when it finds the plugin installed.

```powershell
# Must be an ABSOLUTE path. A relative one - with or without .\ - is rejected.
copilot plugin install C:\X1SearchMcp\copilot-plugin
```

The argument must be an **absolute path to the directory**. A relative path (`.\copilot-plugin`) is rejected as an invalid plugin spec, and so is the `x1-search-copilot.plugin` zip — Copilot reads an archive as a repository and reports `No plugin.json found in repository`, so unzip it first. Installing this way also enables the plugin, which matters: a plugin's MCP servers only start when it is enabled, so the `copilot --plugin-dir` alternative needs an `enabledPlugins` entry or its tools silently never appear.

Copilot warns that direct installs (local paths, repos, URLs) are deprecated in favour of `plugin@marketplace`. The directory route works today; a published marketplace entry is the durable one.

See [copilot-plugin/README.md](../copilot-plugin/README.md) for the full matrix and for which path placeholders Copilot does and does not expand in a server's `command`.

### Manual installation

1. Copy all files from the release package to a folder of your choice (e.g. `%LOCALAPPDATA%\X1 Discovery\McpBridge\`).
2. Copy or create `x1mcp.config.json` beside `X1McpBridge.exe` (see [Section 4.1](#41-x1mcpconfigjson)).
3. Register the server in the client config(s), adding the following inside the `mcpServers` object and adjusting the path:
   - **Claude Desktop:** `%APPDATA%\Claude\claude_desktop_config.json`
   - **Claude Code:** `%USERPROFILE%\.claude\settings.json`
   - **GitHub Copilot:** `%USERPROFILE%\.copilot\mcp-config.json`

```json
{
  "mcpServers": {
    "x1-search": {
      "command": "C:\\Users\\YourName\\AppData\\Local\\X1 Discovery\\McpBridge\\X1McpBridge.exe",
      "args": ["--proxy"]
    }
  }
}
```

`args` **must** be `["--proxy"]`. Without it the client spawns a bridge that opens its own WCF
connection to X1ServiceHost instead of proxying to the one shared relay — the concurrent
`Connect()`/teardown race that crashes X1ServiceHost outright.

GitHub Copilot needs two extra keys on the entry — `"type": "local"` to declare the transport, and
a `"tools"` filter:

```json
{
  "mcpServers": {
    "x1-search": {
      "type": "local",
      "command": "C:\\Users\\YourName\\AppData\\Local\\X1 Discovery\\McpBridge\\X1McpBridge.exe",
      "args": ["--proxy"],
      "tools": ["*"]
    }
  }
}
```

4. (Claude Code, optional) To stop the read-only tools from prompting, add them to `permissions.allow` in `%USERPROFILE%\.claude\settings.json`:

```json
{
  "permissions": {
    "allow": [
      "mcp__x1-search__x1_search",
      "mcp__x1-search__x1_list_sources",
      "mcp__x1-search__x1_list_actions",
      "mcp__x1-search__x1_get_metadata",
      "mcp__x1-search__x1_get_content",
      "mcp__x1-search__x1_generate_preview",
      "mcp__x1-search__x1_cost_savings"
    ]
  }
}
```

`x1_cost_savings` is read-only and safe to pre-approve. `x1_reset_stats` permanently deletes the stats file, so it is intentionally left to prompt — add it to `permissions.allow` only if you prefer it to run without confirmation.

5. (optional) Copy the `skill\x1` folder to `%USERPROFILE%\.claude\skills\x1` (Claude Code) and/or `%USERPROFILE%\.copilot\skills\x1` (GitHub Copilot) to enable the `/x1` skill.
6. Restart the client. (In Claude Desktop and in Copilot, the MCP server list and tool schemas are read at startup.)

---

## 4. Configuration

### 4.1 x1mcp.config.json

Place this file in the same directory as `X1McpBridge.exe`. It controls which X1 sources (tables) are searched by default and which fields are returned.

```json
{
  "defaultTables": ["Files", "MSMail", "Gmail", "Dropbox"],
  "autoPreviewTimeoutMs": 10000,
  "prefetchPreviewCount": 3,
  "tempMaxAgeHours": 168,
  "tempMaxTotalMB": 500,
  "sources": {
    "Files":   ["name", "path", "size", "modified", "created", "type", "extension"],
    "MSMail":  ["subject", "from", "to", "date", "date_received", "cc", "att"],
    "Gmail":   ["subject", "from", "to", "date", "labels"],
    "Dropbox": ["name", "path", "size", "modified", "created_by"]
  }
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `defaultTables` | string array | `["Files"]` | Tables searched when no `tables` argument is passed to `x1_search`. |
| `autoPreviewTimeoutMs` | integer | `10000` | How long `x1_get_content` mode `auto` waits for a preview before falling back to internal fields (email/Dropbox have no preview provider). |
| `prefetchPreviewCount` | integer | `3` | Number of top OneDrive/GDrive results for which the bridge fires a background preview after a search, so the file is cached before you preview/open it. `0` disables prefetch. |
| `tempMaxAgeHours` | integer | `168` (7 days) | At bridge startup, files under `%TEMP%\x1mcp_previews\` older than this are deleted. Orphaned `x1mcp_extract_*.txt` / `x1mcp_content_*.txt` at `%TEMP%` (left over if a request was killed mid-flight) are always removed regardless of age. `0` disables the age sweep. See [§4.5](#45-temp-file-cleanup). |
| `tempMaxTotalMB` | integer | `500` | After the age sweep runs, if the total size of `%TEMP%\x1mcp_previews\` still exceeds this cap, oldest files are deleted first until the total is under the cap. `0` disables the size sweep. |
| `sources` | object | — | Maps each table name to the list of field names returned in search results. If a table has no entry here, search results include only the bare `uri`, `table`, and `keywords` fields. |

**Available table names** depend on which connectors are configured in X1 Desktop. Common values:

| Table | Data source |
|---|---|
| `Files` | Local and network files |
| `MSMail` | Microsoft 365 mail (Outlook/Exchange) |
| `MSCalendar` | Microsoft 365 calendar |
| `Gmail` | Google Mail |
| `Dropbox` | Dropbox |
| `OneDrive` | Microsoft OneDrive |
| `GDrive` | Google Drive |
| `Box` | Box |
| `SP365` | SharePoint Online |
| `Exchange` | On-premises Exchange |
| `Outlook` | Outlook PST/local |
| `Slack` | Slack |
| `Teams` | Microsoft Teams |
| `JIRA` | Jira |
| `Contact` | Contacts |
| `Calendar` | Calendar |
| `Task` | Tasks |

Use `x1_list_sources` to discover which tables are actually available and what fields they expose.

### 4.2 Default Tables Environment Variable

As an alternative to `x1mcp.config.json`, you can set the `X1_MCP_DEFAULT_TABLES` environment variable. It accepts a comma- or semicolon-separated list:

```
X1_MCP_DEFAULT_TABLES=Files,MSMail,Gmail
```

The environment variable takes precedence over the `defaultTables` value in the config file. The `sources` field in the config file is still used for column definitions regardless.

**Priority order for default tables:**

1. `X1_MCP_DEFAULT_TABLES` environment variable
2. `defaultTables` in `x1mcp.config.json`
3. Built-in fallback: `["Files"]`

### 4.3 Client Registration

The bridge is registered under `mcpServers` in the client's config file. Every client uses that
same top-level key:

| Client | Config file |
|---|---|
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` |
| Claude Code | `%USERPROFILE%\.claude\settings.json` |
| GitHub Copilot (app **and** CLI) | `%USERPROFILE%\.copilot\mcp-config.json` (or `$COPILOT_HOME`) |

The minimal entry for the two Claude clients:

```json
{
  "mcpServers": {
    "x1-search": {
      "command": "C:\\Users\\YourName\\AppData\\Local\\X1 Discovery\\McpBridge\\X1McpBridge.exe",
      "args": ["--proxy"]
    }
  }
}
```

Copilot's differs only by `"type": "local"` and a `"tools"` filter — see
[Section 3 → Manual installation](#3-installation).

`args` is `["--proxy"]` in every client, and that is load-bearing rather than stylistic: the shim
forwards to the one shared relay, which is what keeps a single process owning the WCF connection to
X1ServiceHost.

Use forward-slash or escaped back-slash paths. The client must be restarted after any change to this file — the MCP server list and tool schemas are read at startup.

> **Permissions (Claude Code).** Claude Code prompts before each MCP tool call unless the tool is pre-approved. The installer adds the read-only/preview tools to `permissions.allow` for you; to do it by hand, see [Section 3 → Manual installation, step 4](#3-installation). Claude Desktop has its own per-connector approval UI — choose **"Allow always"** there.
>
> **Permissions (GitHub Copilot).** Copilot also prompts per tool, but its saved approvals live in `%USERPROFILE%\.copilot\permissions-config.json`, which is auto-managed and keyed by absolute **project directory** — there is no machine-wide allowlist, so the installer has nothing to pre-approve. Choose Copilot's "always allow" at the first prompt, or start it with `--allow-tool 'x1-search(*)'`.

### 4.5 Temp file cleanup

Two bridge tools write files under `%TEMP%`:

- **`x1_generate_preview output="file"`** and **`x1_export_html`** — HTML fragments and (for `x1_export_html`) sibling image assets land in `%TEMP%\x1mcp_previews\`. Filenames are hash-based per item, so repeat calls for the same document overwrite in place. Distinct items accumulate over time.
- **`x1_get_content` / `x1_extract_file`** — Guid-named temp files at `%TEMP%\x1mcp_extract_*.txt` / `x1mcp_content_*.txt`. The request path deletes them in a `finally` block on success or failure; leftovers only exist if the bridge process was killed mid-request.

Windows does **not** automatically clean `%TEMP%`. To prevent unbounded growth, the bridge runs a cleanup sweep at startup, controlled by two settings in `x1mcp.config.json`:

| Setting | Default | What it does |
|---|---|---|
| `tempMaxAgeHours` | `168` (7 days) | Deletes files in `%TEMP%\x1mcp_previews\` older than this. Orphaned `x1mcp_extract_*.txt` / `x1mcp_content_*.txt` at `%TEMP%` are always removed regardless of age. Set to `0` to disable. |
| `tempMaxTotalMB` | `500` | After the age sweep runs, if `%TEMP%\x1mcp_previews\` is still over this cap, deletes oldest files first until the total is under the cap. Set to `0` to disable. |

Both defaults are sensible for a solo developer machine; tune them if you preview a lot of large PDFs or if disk space is tight:

```jsonc
{
  // Clean previews older than 24 hours; keep the folder under 100 MB total.
  "tempMaxAgeHours": 24,
  "tempMaxTotalMB": 100
}
```

```jsonc
{
  // Retention-heavy: keep 30 days and 5 GB.
  "tempMaxAgeHours": 720,
  "tempMaxTotalMB": 5000
}
```

```jsonc
{
  // Disable both — you'll manage %TEMP% yourself.
  "tempMaxAgeHours": 0,
  "tempMaxTotalMB": 0
}
```

The sweep runs asynchronously at bridge startup so it never blocks a tool call. It logs a single line to `%LOCALAPPDATA%\X1 Discovery\McpBridge\logs\` reporting the number of files and bytes reclaimed. Restart the bridge (or Claude Desktop) after changing these settings — they're read once at startup.

**Warning: never point the bridge at a directory you use for anything else.** Both sweeps run against `%TEMP%\x1mcp_previews\` only; the orphan sweep only touches files matching the `x1mcp_extract_*.txt` / `x1mcp_content_*.txt` glob at the temp root. Unrelated files at `%TEMP%` are untouched.

---

### 4.6 Logging and lifecycle settings

The bridge logs to a rolling file via log4net (5 MB per file, 5 backups). By default:

```
%LOCALAPPDATA%\X1 Discovery\McpBridge\logs\X1McpBridge.log
```

Both the log **location** and **level** are configurable through the registry, using the same
key (`Software\X1 Search`, checked under `HKEY_CURRENT_USER` first, then `HKEY_LOCAL_MACHINE`)
and `Verbosity` convention other X1 products already use:

| Value | Type | Default | What it does |
|---|---|---|---|
| `X1McpConnectorLog` | `REG_SZ` | *(unset — falls back to the directory above)* | **Directory** to write the log file into (consistent with other X1 products' log-location settings) — the file itself is always named `X1McpBridge.log` within that directory. Set this to redirect logging anywhere on disk. |
| `Verbosity` | `REG_SZ` | `DEBUG` if unset/unrecognized | Log level threshold. Valid values: `ALL`, `DEBUG`, `INFO`, `WARN`, `ERROR`, `FATAL`, `OFF` — same vocabulary as other X1 products' `Verbosity` setting. **Note:** this is the *same* registry value other X1 Search components read, so setting it here also affects their logging if you set it at the shared `HKLM` level; setting it at `HKCU` only affects processes running as the current user (which includes the bridge). |
| `X1McpConnectorIdleShutdown` | `REG_DWORD` or `REG_SZ` | `3600` (1 hour) if unset | Seconds of no dispatched request before the Lean shared relay (`X1McpBridge.exe --host`) shuts itself down. `0` disables it, restoring an unconditional keep-alive. See the note below on why this exists. |
| `X1McpQueryLog` | `REG_DWORD` | `0` (absent) — **off** | **Off (`0`/absent) means query content is never logged — this is the default.** Set to `1` to also log the actual search text (and a few other tool arguments — see [§5.10 x1_set_query_log](#510-x1_set_query_log)) on the same `PERF` line already written for every tool call, for diagnosing a slow or timed-out search. Deliberately a separate value from `Verbosity` — `Verbosity=DEBUG` never logs query content, regardless of this setting, so turning on debug logging for an unrelated support request never exposes your search terms. |

Example — quiet the bridge down to warnings/errors only, redirect logs to a custom directory, and shut the relay down after 15 minutes idle instead of the 1-hour default:

```powershell
New-Item -Path "HKCU:\Software\X1 Search" -Force | Out-Null
New-ItemProperty -Path "HKCU:\Software\X1 Search" -Name "Verbosity" -Value "WARN" -PropertyType String -Force
New-ItemProperty -Path "HKCU:\Software\X1 Search" -Name "X1McpConnectorLog" -Value "D:\Logs" -PropertyType String -Force
New-ItemProperty -Path "HKCU:\Software\X1 Search" -Name "X1McpConnectorIdleShutdown" -Value 900 -PropertyType DWord -Force
```

`X1McpConnectorLog` and `Verbosity` are read once at bridge startup (`BridgeLogger.Configure()`) —
restart the bridge (or Claude Desktop/Code, which relaunches it) after changing either for it to
take effect. `X1McpConnectorIdleShutdown` and `X1McpQueryLog` are both re-read on every call instead,
so a change takes effect immediately with no restart. Remove any of these registry values
(`Remove-ItemProperty`) to revert to the defaults above.

**`X1McpQueryLog` is meant to be set through Claude, not by hand.** Use the
[`x1_set_query_log`](#510-x1_set_query_log) tool ("turn on query logging for me") instead of editing
the registry value directly. Editing it yourself with `New-ItemProperty` works too, but a value set
outside the exact process Claude launches can land in a registry view that process never reads —
the same class of propagation gap XS-1692 found for other registry-configured settings — whereas the
tool writes the value from inside the running bridge itself, which reads it back the same way.

**Why the relay shuts itself down at all.** The shared relay is deliberately kept running after
every Claude session ends — that's what lets a fresh session reuse a warm connection instead of
paying startup cost every time, and what keeps exactly one process talking to the X1 service layer
(two clients racing that connection is a known crash). The cost is that the resident relay holds
its own executable and DLLs locked for as long as it runs, which blocks an upgrade from replacing
them. The standalone installer (`install.ps1`) stops the relay itself before copying new binaries,
so this mostly matters when that step itself fails partway — but the Cowork/marketplace plugin's
update mechanism has no equivalent step at all, so a resident relay can block a plugin update the
same way it would block a manual copy. The idle timeout doesn't close
that gap outright — it doesn't coordinate with the moment an update actually runs — but it bounds
how long the relay can sit there holding the lock after you've stopped using it, rather than
holding it indefinitely.

---

## 5. Tools Reference

### 5.1 x1_search

Search the local X1 index using keyword or structured query syntax.

**Parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `query` | string | `""` | Main search text — same syntax as the X1 quick search box. |
| `tables` | string[] | config default | One table, or several in one call (e.g. `["Files","MSMail"]`) — the bridge searches each sequentially and merges the results (summed `totalResults`/`returned`, concatenated `results`, plus a `byTable` breakdown with per-table `error` on failure). `limit`/`timeoutMs` apply per table, so an N-table call can take up to N × `timeoutMs`. Omit to use the configured default. |
| `limit` | integer | `20` | Maximum number of results to return. Range: 1–500. |
| `include_snippets` | boolean | `true` | Include keyword context snippets in results. |
| `includeActions` | boolean | `true` | Include an `actions` array on each result listing the Post Search Actions available for it. Pass `false` to omit it and shrink the payload. |
| `progenitorSearch` | boolean | `false` | Enable progenitor (family/thread) search mode. |
| `filters` | array or object | — | Additional column-level filters (see below). |
| `displayFields` | string[] | config columns | Result fields to include. Omit to use columns from `x1mcp.config.json`. |
| `sort` | array | — | Sort specification (see below). |
| `timeoutMs` | integer | `60000` | Search timeout in milliseconds. |

**`filters` format** — either an array of filter objects:

```json
"filters": [
  { "column": "from", "term": "alice@example.com" },
  { "table": "MSMail", "column": "subject", "term": "invoice" }
]
```

Or a legacy object map (column → term, no table qualifier):

```json
"filters": { "from": "alice@example.com", "subject": "invoice" }
```

**`sort` format:**

```json
"sort": [
  { "column": "date", "direction": "desc" }
]
```

Direction values: `"desc"` / `"descending"` / `"backwards"` (newest/highest first) or `"asc"` / `"ascending"` / `"forwards"` (oldest/lowest first, the default). The `column` must be a sortable indexed field for the table (e.g. `date` for email). The bridge **enforces the order itself** — it auto-fetches the sort column, over-fetches a small buffer, and re-sorts — so results are reliably ordered even when X1 would otherwise pin a "current" item to the top of the window.

**Response:**

```json
{
  "totalResults": 142,
  "returned": 20,
  "results": [
    {
      "uri": "file://C:/Users/.../report.pdf",
      "table": "Files",
      "keywords": "...matching context snippet...",
      "actions": [
        { "action": "get_path",       "description": "Return the full local file path" },
        { "action": "open",           "description": "Open with the associated application" },
        { "action": "show_in_folder", "description": "Open the parent folder in Explorer with the item selected" }
      ],
      "fields": {
        "name": "report.pdf",
        "path": "C:\\Users\\...\\report.pdf",
        "size": "204800",
        "modified": "46037.33875"
      },
      "snippet": "...matching context snippet..."
    }
  ],
  "highlightTerms": [
    { "term": "report", "column": "", "findType": 0 }
  ]
}
```

- **`actions`** (present unless `includeActions: false`) lists the Post Search Actions available for the result — feed the result's `table` + `uri` and one of these `action` values to [`x1_execute_action`](#56-x1_execute_action). This means you usually don't need a separate [`x1_list_actions`](#55-x1_list_actions) call.
- **`prefetchInitiated: true`** appears on OneDrive/GDrive results for which the bridge kicked off a background preview download. Calling [`x1_generate_preview`](#57-x1_generate_preview) on such a result promptly (within ~30 s) is likely to be fast.

> **Date fields:** Raw date values in `fields` (e.g. `modified`, `date`) are returned as OLE Automation floating-point numbers (days since 30 December 1899). To convert: `DateTime.FromOADate(double.Parse(value))`. (Preview cards from [`x1_generate_preview`](#57-x1_generate_preview) format these into readable timestamps automatically.)

### 5.2 x1_get_metadata

Fetch the indexed field values for a single item identified by its table and URI.

**Parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `table` | string | required | The table the item belongs to (e.g. `"Files"`, `"MSMail"`). |
| `uri` | string | required | The item's URI as returned in search results. |
| `fields` | string[] | config columns | Fields to return. Omit to use columns from `x1mcp.config.json`; omit both to return all indexed fields. |
| `timeoutMs` | integer | `30000` | Timeout in milliseconds. |

**Response:**

```json
{
  "table": "MSMail",
  "uri": "msmail://...",
  "fields": {
    "subject": "Q4 Budget Review",
    "from": "alice@example.com",
    "to": "bob@example.com",
    "date": "46037.33875",
    "att": "budget.xlsx"
  }
}
```

### 5.3 x1_get_content

Retrieve the content of an indexed item. Four modes are available:

| Mode | Description | Works for |
|---|---|---|
| `auto` | Tries preview with a 10-second timeout; falls back to `internal` if no preview provider is registered. **Recommended for most use cases.** | All connectors |
| `preview` | HTML or text preview using the connector's registered preview provider. Waits the full `timeoutMs`. | Files, and connectors with a registered preview provider |
| `extract` | Full text extraction to a temporary file. Extracts the raw text content. | Local Files only |
| `internal` | Raw indexed field values. Always available; useful when content is not needed. | All connectors |

> **Connector compatibility:** Email connectors (MSMail, Gmail, Exchange) and Dropbox have no registered preview provider in X1 Desktop. Using `mode=preview` on these will time out. Use `mode=auto` (default) or `mode=internal` instead.

**Parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `table` | string | required | The table the item belongs to. |
| `uri` | string | required | The item's URI as returned in search results. |
| `mode` | string | `"auto"` | Content retrieval mode: `auto`, `preview`, `extract`, or `internal`. |
| `timeoutMs` | integer | `120000` | Timeout in milliseconds. |

**Response (auto/preview — preview succeeded):**

```json
{
  "mode": "preview",
  "preview": "<html>...document content...</html>",
  "additionalData": null
}
```

**Response (auto — fell back to internal):**

```json
{
  "mode": "internal",
  "note": "Preview not available for this connector; returning raw index fields.",
  "rows": ["subject", "Q4 Budget", "from", "alice@example.com", "..."]
}
```

**Response (extract):**

```json
{
  "mode": "extract",
  "text": "Full extracted text content...",
  "path": "C:\\Users\\...\\AppData\\Local\\Temp\\x1mcp_extract_abc123.txt"
}
```

> **Extract mode note:** The temporary file is deleted after the text is read. The `path` field is informational only.

**Response (internal):**

```json
{
  "mode": "internal",
  "rows": ["fieldName1", "value1", "fieldName2", "value2", "..."]
}
```

### 5.4 x1_list_sources

Lists all configured X1 sources, their available display columns, and a `capabilities` object. Call this first to discover which tables are indexed, what fields can be requested in `displayFields`, and what each source supports.

**Parameters:** None.

**Response:**

```json
{
  "sources": [
    { "name": "Files",    "columns": ["name", "path", "size", "modified", "created"],
      "capabilities": { "actions": ["get_path", "open", "show_in_folder"], "preview": true } },
    { "name": "MSMail",   "columns": ["subject", "from", "to", "date", "att"],
      "capabilities": { "actions": ["open"], "preview": true } },
    { "name": "OneDrive", "columns": ["name", "path", "size", "modified", "modified_by"],
      "capabilities": { "actions": ["open", "open_url"], "preview": true } },
    { "name": "Teams",    "columns": ["message_body", "sender", "channel_display_name"],
      "capabilities": { "actions": ["open_url"], "preview": false } }
  ]
}
```

Each `capabilities` object reports the Post Search Actions available for that source and whether it has a known preview provider (`preview: true` means `x1_generate_preview` is expected to produce rich output rather than only a metadata-card fallback).

---

### 5.5 x1_list_actions

Returns the Post Search Actions available for a specific result (`table` + `uri`). Prefer the `actions` array that [`x1_search`](#51-x1_search) already includes on every result (`includeActions` defaults to `true`); use `x1_list_actions` only when you need to confirm actions for a result fetched with `includeActions: false`.

**Parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `table` | string | required | Table name from the search result. |
| `uri` | string | required | URI from the search result. |

**Response:**

```json
{
  "table": "OneDrive",
  "uri": "onedrive://...",
  "actions": [
    { "action": "open",     "description": "Open the locally cached copy directly (instant if already cached)" },
    { "action": "open_url", "description": "Open the OneDrive item in the default browser (via X1 preview URL)" }
  ]
}
```

### 5.6 x1_execute_action

Executes a Post Search Action on an indexed item. Use the `actions` array on a search result (or `x1_list_actions`) to discover which actions are valid for a given `table`/`uri`.

**Parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `table` | string | required | Table name from the search result. |
| `uri` | string | required | URI from the search result. |
| `action` | string | required | One of: `get_path`, `open`, `show_in_folder`, `get_url`, `open_url`. |
| `timeoutMs` | integer | `30000` | Timeout in milliseconds. |

**Actions:**

| Action | Effect | Typical sources |
|---|---|---|
| `get_path` | Returns the local file path (data only; no side effect). | Files |
| `open` | Opens the file, email, or **cloud cached copy** in its associated app. | Files, MSMail, OneDrive, GDrive, Dropbox, SharePoint, SP365 |
| `show_in_folder` | Reveals the item in Explorer with it selected. | Files |
| `get_url` | Returns the web URL (data only). | Gmail, GDrive |
| `open_url` | Opens the item's web URL in the default browser. | Gmail, GDrive, OneDrive, Dropbox, SharePoint, SP365, Teams, Slack |

> The `open` action works for local files **and** cloud files — it opens the local cached copy if available, falling back to a preview callback. Actions that launch something (`open`, `show_in_folder`, `open_url`) return a status; data actions (`get_path`, `get_url`) return the value.

**Response (data action):**

```json
{ "action": "get_path", "status": "ok", "path": "C:\\Users\\...\\report.docx" }
```

**Response (launch action):**

```json
{ "action": "open", "status": "ok", "message": "Opened report.docx" }
```

### 5.7 x1_generate_preview

Generates a **self-contained HTML preview** of an indexed item, suitable for display inside Claude. When a user asks to preview, view, or open an item, the returned `html` should be **rendered** — as an HTML **artifact** in Claude Desktop / claude.ai, or as an inline widget in Claude Code — rather than shown as raw markup. Works for all source types: local files (embedded text/HTML; `.docx` is extracted to readable HTML), email (headers + body), cloud files (metadata card with a link), and chat messages (formatted card).

**Parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `table` | string | required | Table name from the search result. |
| `uri` | string | required | URI from the search result. |
| `maxChars` | integer | `0` | Limit `.docx` preview output to roughly this many characters (e.g. 4000–8000 for a 2–3 page summary). `0` = full document. |
| `output` | string | `"inline"` | Output mode: `"inline"` returns the HTML in the response; `"file"` writes a self-contained HTML fragment to disk and returns only the path (see below). |
| `timeoutMs` | integer | `120000` | Timeout in milliseconds. |

#### Output modes — hybrid by intent

Choose the output mode based on what you need to do with the result:

| You need to… | `output=` | Response shape | Context cost |
|---|---|---|---|
| Read / reason over the content (summarise, quote, compare) | `"inline"` (default) | `{ html, contentType, previewType, title }` | Full HTML enters context |
| Display to the user without reading it | `"file"` | `{ mode:"file", path, title, previewType, contentType, bytes }` | ~80 tokens (path only) |

**`output="inline"` response:**

```json
{
  "html": "<!DOCTYPE html> ... </html>",
  "contentType": "text/html",
  "previewType": "docx",
  "title": "report.docx"
}
```

Render `html` as an HTML **artifact** in Claude Desktop / claude.ai, or as an inline visual widget in Claude Code. The HTML is self-contained (own `<style>`, no external scripts) and safe to render directly.

**`output="file"` response:**

```json
{
  "mode": "file",
  "path": "C:\\Users\\...\\AppData\\Local\\Temp\\x1mcp_previews\\report.docx_a1b2c3d4.html",
  "title": "report.docx",
  "previewType": "docx",
  "contentType": "text/html",
  "bytes": 48320
}
```

The file at `path` is an artifact-ready HTML fragment (body content only, no outer `<html>`/`<head>`). Pass `path` directly to the Artifact tool — the preview bytes never enter the conversation context. Use `output="file"` when the user asks to "show", "view", or "open" an item and you do not need to read its content yourself.

`previewType` is one of:

| Value | Meaning |
|---|---|
| `docx` | Word document text extracted to HTML. |
| `html` | Email body or an embedded HTML preview. |
| `text` | Plain-text content in a `<pre>` block. |
| `image` | Local image (png/jpg/gif/webp/bmp/svg) embedded as a `data:` URI `<img>` — self-contained, no external fetch. |
| `pdf` | Local PDF embedded as a `data:` URI `<embed>` — self-contained, no external fetch. |
| `metadata_card` | **Fallback** — a metadata card with a link, used when the item is a cloud file not yet cached locally, or a binary format that can't be extracted. Not the document body. |

**Binary object embedding.** When the source is a local image (png/jpg/gif/webp/bmp/svg) or a PDF, the bridge embeds the file's bytes as a `data:` URI directly in the HTML — so the preview is fully self-contained with no external network references. Objects over **1 MB** fall back to a `metadata_card`.

Preview cards format raw values for readability: OA-format dates become timestamps, byte sizes become KB/MB, and internal drive paths (`/drives/…/root:/…`) are shortened.

> **Spreadsheets and slide decks.** `.xlsx`/`.pptx` are not extracted by `x1_generate_preview` (they return a `metadata_card`). To read their content, call [`x1_get_content`](#53-x1_get_content) with `mode: "preview"` — X1 caches the real file and returns a **local path** — then extract the text locally with the bundled `extract-office-text.ps1` (see [Section 11](#11-the-x1-skill)). This keeps large attachments out of the model context.

### 5.8 x1_cost_savings
Returns the accumulated cost report for this installation. Every tool call is silently recorded and compared against what the **curated Microsoft Graph / Gmail / OneDrive connector already installed on this machine** would have cost for the same retrieval — not a hypothetical raw base64 API. Stats persist to `x1mcp_stats.json` in the bridge install directory, so they survive restarts.

The response has three top-level sections, deliberately kept apart: **`observed`** (facts, no modelling), **`comparison`** (a bounded estimate with its evidence), and **`notSavings`** (figures that are not token savings and never enter the estimate). Plus **`evidence`** (per-coefficient provenance and sample count) and **`knownLimitations`**. See [§1.1](#11-token-economy) for the methodology.

**Parameters:** None.

**Response** (abridged — long explanatory strings trimmed):

```json
{
  "observed": {
    "recordingSince": "2026-07-30T23:21:50Z",
    "lastUpdated": "2026-07-30T23:44:02Z",
    "totalCalls": 9,
    "x1TokensInContext": 13497,
    "itemsReturned": 11,
    "avgDurationMs": 212,
    "approxBytesReturned": 53988,
    "note": "Directly observed, no modelling involved. approxBytesReturned is derived from the token estimate (tokens x 4) rather than measured..."
  },
  "comparison": {
    "comparedAgainst": "the curated Microsoft Graph / Gmail / OneDrive connector ... for the same retrievals",
    "assumedAlternativeEnvironment": "has local filesystem and file-parsing tooling (e.g. Claude Code) - so it could download and parse binaries locally without those bytes entering context. This is the assumption that claims LESS.",
    "baselineTokensLow": 19864,
    "baselineTokensHigh": 27891,
    "tokensSavedLow": 6367,
    "tokensSavedHigh": 14394,
    "savingsFractionLow": 32.1,
    "savingsFractionHigh": 51.6,
    "costSavingUsd": "$0.00 to $0.04 at Claude Sonnet input pricing ($3/M); the low end assumes cached input, the high end uncached - so the upper figure is a ceiling, not an expectation",
    "coefficientsVersion": "2026-07-31r6",
    "howToReadThis": "The interval is not a confidence interval; it is the span between the most and least this evidence can support. It WIDENS when a coefficient rests on few paired samples...",
    "byCategory": [
      {
        "category": "x1_get_content (email/docx/text)",
        "calls": 1,
        "tokensSavedLow": 820,
        "tokensSavedHigh": 2003,
        "formattedSaved": "820-2.0K",
        "avgDurationMs": 932,
        "itemsReturned": 1,
        "counterfactual": "vs. the connector's rich text/HTML body (measured 2.17x, n=1)"
      },
      {
        "category": "x1_generate_preview  pdf / output=file",
        "calls": 1,
        "tokensSavedLow": 0,
        "tokensSavedHigh": 0,
        "formattedSaved": "0",
        "avgDurationMs": 508,
        "itemsReturned": 1,
        "counterfactual": "no claim - x1 returned a path, and the assumed alternative could also have parsed the file locally without spending context"
      }
    ]
  },
  "notSavings": {
    "tokensDeferred": 10032,
    "tokensDeferredMeaning": "Tokens that did not enter context because x1 returned a path instead of content. NOT a saving...",
    "capabilityGainCount": 0,
    "capabilityGainSamples": [],
    "capabilityGainMeaning": "Items the curated connector could not have reached at all: local files not mirrored to any cloud it can call..."
  },
  "evidence": [
    { "category": "Metadata / search",    "multiple": 2.13,  "pairedSamples": 2, "claims": "low bound at 1.62x, high at 2.13x", "basis": "paired x1_search rows (570-615 chars) vs outlook_email_search rows (1220-1310 chars), 2026-07-30" },
    { "category": "Text-native content",  "multiple": 2.17,  "pairedSamples": 1, "claims": "low bound at 1.47x, high at 2.17x", "basis": "paired x1_get_content extracted text (6,989 chars) vs read_resource HTML body (15,306 chars), 2026-07-30" },
    { "category": "Structured / tabular", "multiple": "n/a", "pairedSamples": 0, "claims": "nothing - unmeasured",              "basis": "NOT MEASURED - no paired xlsx/csv sample collected; claims nothing until one exists" },
    { "category": "Image / OCR-required", "multiple": "per-page, not a multiple (~1500 tokens/page)", "pairedSamples": 0, "claims": "upper bound only", "basis": "cited industry figure ~1500 vision tokens per rendered page; not independently measured" }
  ],
  "knownLimitations": [ "Coefficients rest on 1-2 paired samples per category and are vendor-derived...", "..." ]
}
```

**Reading it:**

| Field | What it means |
|---|---|
| `observed.*` | Directly measured. `approxBytesReturned` is the one derived value (tokens x 4) and says so. |
| `comparison.tokensSavedLow` / `High` | The interval the evidence supports. **Quote the low end.** A wide gap means thin calibration, not a big win. |
| `comparison.assumedAlternativeEnvironment` | Which fallback environment the comparison assumes — this single assumption changes binary-file results by an order of magnitude, so it is always stated. |
| `comparison.byCategory[].counterfactual` | Exactly what that row was compared against, including the measured multiple and its `n`, or a plain statement that it claims nothing. |
| `notSavings.tokensDeferred` | Tokens kept out of context because x1 returned a path. Not a saving — a later read pays them. |
| `notSavings.capabilityGainCount` | Items no connector could reach: local files not mirrored to any cloud. Excludes synced OneDrive/Dropbox/Drive folders. |
| `evidence[].pairedSamples` | How many paired measurements back that coefficient. `0` means the category claims nothing, or contributes to the ceiling only. |
| `knownLimitations` | The report carries its own caveats rather than assuming the reader knows them. |

**Diagnosing where time is spent (XS-1594):** every tool call also writes a single structured
line to `X1McpBridge.log`:

```
2026-07-09 10:46:20.857 INFO  [1] McpServer - PERF tool=x1_search elapsedMs=73 bytes=1625 estTokens=406
```

Grep for `PERF` to see per-call timing without waiting for the aggregated report. The log
line's own timestamp is the "hand-off" moment — when the bridge finished and returned control to
Claude. If a user reports slowness, compare `elapsedMs` here against how long the *overall* tool
call felt from Claude's side: a small `elapsedMs` with a much longer perceived delay points at
Claude-side processing, not X1.

**What each call type claims** (see [§1.1](#11-token-economy) for the derivation):

| Call type | x1 cost | Claimed against the connector |
|---|---|---|
| `x1_search` / `x1_get_metadata` | response\_json\_chars ÷ 4 | 1.62×–2.13× (measured, n=2) |
| `x1_get_content` mode=content/extract — text-native | extracted\_text\_chars ÷ 4 | 1.47×–2.17× (measured, n=1) |
| `x1_get_content` / `x1_extract_file` — xlsx/csv | extracted\_text\_chars ÷ 4 | **nothing** — never measured |
| `x1_get_content` / `x1_extract_file` — pdf/image **with** dense extracted text | extracted\_text\_chars ÷ 4 | 1.47×–2.17× — a text layer existed, so a local parse would have reached the same text |
| `x1_get_content` / `x1_extract_file` — pdf/image **with** sparse text (no text layer) | extracted\_text\_chars ÷ 4 | ceiling only: ~1,500 tokens × estimated pages. No floor — the figure is cited, not measured |
| `x1_generate_preview` `output="file"` / `"save"`, any type | 80 tokens (path only) | **nothing** on the floor; ceiling = the extracted text the connector would have inlined (sized from the fragment). Same figure recorded as `tokensDeferred` |
| `x1_get_content` mode=preview | ~50 (path only) | **nothing** at either end — a bare path gives nothing to size a ceiling from |
| `x1_generate_preview` — pdf/image, `output="inline"` | html\_chars ÷ 4 | **nothing** — the base64 data URI is equally unread on both sides |
| `x1_generate_preview` — docx/html/text, `output="inline"` | html\_chars ÷ 4 | 1.47×–2.17× (measured, n=1) |
| Tagging, actions, `list_sources`, `cost_savings` itself | response\_json\_chars ÷ 4 | **nothing** — bookkeeping, no retrieval counterfactual |

**Known limitations** are also returned in the report's own `knownLimitations` array, so a reader
never has to come here to find them. The main ones: coefficients rest on 1–2 paired samples and
are vendor-derived rather than measured on your data; the multiple is applied proportionally even
though part of the connector's overhead is fixed per item (so short items are under-credited and
long ones over-credited); the connector's real two-call chain (`search` → `read_resource`) is not
modelled, which under-counts its cost; and cloud tables (OneDrive/GDrive/SP365) expose opaque item
ids, so tabular and scanned cloud content can't be classified by extension and is priced as text —
again an under-count. Every one of these leans toward claiming less.

**Usage:** Invoke at any time to see cumulative savings. In Claude Code with the `/x1` skill installed, say *"what kind of cost savings have I achieved?"* — the skill will call this tool and render the result as a formatted summary.

> **No impact on tool calls.** Recording is done inside a `try/catch` after each successful tool response. A failure in the tracker never propagates to the caller — it is completely transparent.

---

### 5.9 x1_reset_stats

Clears all accumulated token-cost statistics by deleting the `x1mcp_stats.json` file. Use this to start a fresh tracking period — for example, at the beginning of a new project, after a major usage milestone, or to discard test data.

**Parameters:** None.

**Response:**

```json
{ "status": "ok", "message": "Token cost statistics have been reset." }
```

After a reset, the next tool call creates a new `x1mcp_stats.json` with fresh counters. The `recordingSince` timestamp in the next `x1_cost_savings` report will reflect the first call made after the reset.

**Usage in Claude Code:** Say *"reset my x1 token savings stats"* — the `/x1` skill will call this tool and confirm the reset.

> **`x1_reset_stats` is not in `permissions.allow` by default.** Because it permanently deletes the stats file, the installer leaves it to prompt so you can confirm the intent. Add it to `permissions.allow` manually if you prefer it to run without prompting.

---

### 5.10 x1_set_query_log

Enables or disables diagnostic logging of the actual search query text (and a few related tool
arguments) sent to X1, for troubleshooting a slow or timed-out search. This is the supported way to
set the [`X1McpQueryLog`](#46-logging-and-lifecycle-settings) registry value — through Claude,
rather than by hand-editing the registry.

**Off (`false`/unset) by default: no query content is ever logged unless this is turned on
explicitly.** Regardless of this setting, `Verbosity=DEBUG` alone never logs query content either —
the two are intentionally independent, so turning on debug logging for an unrelated support request
can never expose your search terms.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `enabled` | boolean | No | `true` to start logging query content, `false` to stop. Omit to report the current state without changing it. |

**What gets logged when on:** the query/filter text is appended to the same `PERF tool=... elapsedMs=...` line already written for every tool call (see [§4.6](#46-logging-and-lifecycle-settings)) — never a separate log line. Currently covers `x1_search` (the `query` argument) and `x1_get_metadata` (the `fields` argument); other tools' arguments are not yet included.

**Response:**

```json
{ "status": "ok", "queryLogEnabled": true }
```

Takes effect immediately — no restart of the bridge, X1, or Claude needed, in either direction.

**Usage in Claude Code:** Say *"turn on X1 query logging"* or *"is query logging on?"* — the `/x1` skill will call this tool and report the result.

> **`x1_set_query_log` is not in `permissions.allow` by default.** Turning it on causes subsequent search text to be written to a log file, which is a meaningfully different consequence from a stats reset — the installer leaves it to prompt so you can confirm the intent each time. Add it to `permissions.allow` manually only if you're comfortable with that.

---

## 6. Resources Reference

### x1://index/stats

Returns the X1 service host status string — the same status the X1 Desktop UI displays.

**URI:** `x1://index/stats`

**Response:**

```json
{
  "getX1ServiceHostStatus": "X1 Service Host is running. Indexed: 245,123 items.",
  "user": "Stewart Robinson",
  "machine": "DESKTOP-ABC123",
  "note": "Rich per-scanner stats require future service API exposure; this is the same status string the desktop UI uses."
}
```

---

## 7. Prompts Reference

### x1_search_best_practices

Returns a built-in prompt with tips for querying X1 effectively. Ask Claude to use this prompt for guidance on constructing efficient queries.

---

## 8. Query Syntax

The `query` parameter in `x1_search` uses X1's native search syntax — the same as typing in the X1 Desktop search box.

### Basic rules

- **Case insensitive:** `Invoice`, `invoice`, and `INVOICE` are equivalent.
- **Prefix / starts-with matching:** The term `rep` matches tokens that begin with "rep" — `report`, `reply`, `repository`, etc.
- **Phrase search:** Wrap multi-word phrases in double quotes: `"budget review"`.

### Boolean operators

Operators must be **capitalised**:

| Operator | Example | Meaning |
|---|---|---|
| `AND` | `budget AND 2024` | Both terms must appear |
| `OR` | `invoice OR receipt` | Either term must appear |
| `NOT` | `budget NOT draft` | First term present, second absent |

Parentheses can group expressions: `(invoice OR receipt) AND 2024`

### Column filters

Column-level filters target specific fields within a table. These are passed via the `filters` parameter rather than the `query` string:

```json
"filters": [
  { "column": "from",    "term": "alice@example.com" },
  { "column": "subject", "term": "invoice" }
]
```

### Combining query and filters

A `query` value and `filters` array can be used together — the bridge sends all as separate `SearchTerm` rows, which X1 evaluates as an implicit AND:

```json
{
  "query": "budget",
  "tables": ["MSMail"],
  "filters": [{ "column": "from", "term": "alice" }]
}
```

---

## 9. Usage Examples

### Find recent files matching a term

```json
{
  "name": "x1_search",
  "arguments": {
    "query": "quarterly report",
    "tables": ["Files"],
    "limit": 10,
    "displayFields": ["name", "path", "modified", "size"]
  }
}
```

### Search email from a specific sender

```json
{
  "name": "x1_search",
  "arguments": {
    "query": "invoice",
    "tables": ["MSMail", "Gmail"],
    "filters": [{ "column": "from", "term": "alice@example.com" }],
    "displayFields": ["subject", "from", "date", "att"]
  }
}
```

### Use OR to search multiple terms across sources

```json
{
  "name": "x1_search",
  "arguments": {
    "query": "invoice OR receipt OR expense",
    "tables": ["Files", "MSMail"],
    "limit": 50
  }
}
```

### Get full metadata for an item from search results

```json
{
  "name": "x1_get_metadata",
  "arguments": {
    "table": "MSMail",
    "uri": "msmail://...",
    "fields": ["subject", "from", "to", "date", "cc", "bcc", "att"]
  }
}
```

### Get content of a file

```json
{
  "name": "x1_get_content",
  "arguments": {
    "table": "Files",
    "uri": "files://C:/Users/.../report.pdf",
    "mode": "extract"
  }
}
```

### Get content of an email (auto mode — recommended)

```json
{
  "name": "x1_get_content",
  "arguments": {
    "table": "MSMail",
    "uri": "msmail://...",
    "mode": "auto"
  }
}
```

### Discover available sources before searching

```json
{
  "name": "x1_list_sources",
  "arguments": {}
}
```

### Preview a document inline (read/summarise the content)

```json
{
  "name": "x1_generate_preview",
  "arguments": {
    "table": "Files",
    "uri": "file://C:/Users/.../report.docx",
    "maxChars": 6000,
    "output": "inline"
  }
}
```

Render the returned `html` as an HTML artifact. Use `output="inline"` (the default) when you need to read or reason over the content — summarise it, quote from it, compare it.

### Display a preview as a zero-context artifact (file mode)

```json
{
  "name": "x1_generate_preview",
  "arguments": {
    "table": "Files",
    "uri": "file://C:/Users/.../analysis-cox-mac-error-logs.pdf",
    "output": "file"
  }
}
```

Returns `{ mode:"file", path:"C:\\...\\x1mcp_previews\\analysis-cox-mac-error-logs.pdf_1702e0ad.html", ... }`. Pass `path` directly to the Artifact tool — the PDF's bytes (758 KB in this case) never enter the conversation context. Use `output="file"` when the user wants to *see* the document without you needing to read it.

### Open a search result in its application

```json
{
  "name": "x1_execute_action",
  "arguments": {
    "table": "Files",
    "uri": "file://C:/Users/.../report.docx",
    "action": "open"
  }
}
```

### Find the newest spreadsheet someone emailed you

```json
{
  "name": "x1_search",
  "arguments": {
    "query": "att:xlsx",
    "tables": ["MSMail"],
    "displayFields": ["subject", "from", "date", "att"],
    "sort": [{ "column": "date", "direction": "desc" }],
    "limit": 25
  }
}
```

To preview the spreadsheet itself, find the attachment as its own item (`query: "<name> type:xlsx"`, which returns a nested `msmail://<email>/<attachment>` URI), call `x1_get_content` with `mode: "preview"` to get a local path, then extract the text with `extract-office-text.ps1`.

---

## 10. User Stories

These examples show how the tools combine to satisfy common natural-language requests. Each story includes a **theoretical token cost analysis** comparing the x1 approach against a direct-API approach (Microsoft Graph, Gmail API, or OneDrive API) returning raw base64 binary.

> **These worked examples predate the [§1.1](#11-token-economy) methodology fix and are kept as an upper-bound illustration only, not the reported baseline.** `x1_cost_savings` and §1.1 now compare against the *curated connector* (which returns readable HTML/text or a search result, not base64), classified per object type. The base64-derived numbers below are real for what they describe — "what would it cost to base64-encode this file" — but they are not what `x1_cost_savings` reports as the counterfactual, and should not be quoted as x1's savings percentage. See §1.1 for the coefficients that are actually used.

---

### "What's in the Cox error log analysis PDF I have saved?"

1. `x1_search` → `Files`, query `analysis cox error logs type:pdf`. Take the top hit.
2. `x1_generate_preview` with `output="file"` on the returned URI.
3. Render the returned `path` as a Claude artifact. The PDF's bytes never enter context.

**Token cost analysis** (actual file: 758,646 bytes)

| Step | With x1 | Without x1 (Graph API) |
|---|---|---|
| Search / list | ~400 (1 result) | ~500 (file search) |
| Fetch content | ~80 (`output="file"` path) | ~252,900 (758 KB PDF as base64) |
| **Total** | **~480 tokens** | **~253,400 tokens** |
| **Reduction** | | **528×** |

> The PDF alone would overflow a 200 K context window via direct API. With `output="file"`, its entire 758 KB passes through zero conversation tokens.

---

### "Show me the last five emails from David about the Henderson project"

1. `x1_search` → `MSMail`, query `Henderson from:David`, `sort: [{column:"date", direction:"desc"}]`, `limit: 5`.
2. Display subject/date/sender as a list.
3. When the user picks one: `x1_generate_preview` with `output="inline"` to summarise it, or `output="file"` if they just want to read it.

**Token cost analysis** (email body ~20 KB HTML, preview of 1 chosen email)

| Step | With x1 | Without x1 (Graph API) |
|---|---|---|
| Search / list 5 emails | ~2,000 | ~2,500 |
| Preview one email body | ~3,000 (inline HTML preview) | ~5,000 (20 KB HTML via `$value`) |
| **Total** | **~5,000 tokens** | **~7,500 tokens** |
| **Reduction** | | **~1.5×** |

> For plain-text email bodies x1's token advantage is modest (~1.5×). The benefit scales sharply when any of those emails carry attachments — see the PowerPoint story below for the attachment case.

---

### "Did anyone send me a PowerPoint about Q3 planning?"

1. `x1_search` → `MSMail`, query `Q3 planning att:pptx`, `displayFields: ["subject","from","date","att"]`.
2. Identify the attachment item's nested URI (form `msmail://<account>/<emailId>/<attachmentId>`).
3. `x1_get_content` with `mode:"preview"` → get a local `.pptx` path.
4. `powershell -File extract-office-text.ps1 -Path "<path>" -MaxChars 6000` → per-slide text → render as an artifact.

**Token cost analysis** (typical PowerPoint deck: 3 MB)

| Step | With x1 | Without x1 (Graph API) |
|---|---|---|
| Find email with attachment | ~1,500 | ~2,000 |
| Find attachment item | ~500 | ~3,000 (fetch email body) |
| Fetch attachment content | ~50 (path) + ~1,800 (extracted text) | ~1,000,000 (3 MB as base64) |
| **Total** | **~3,850 tokens** | **~1,006,000 tokens** |
| **Reduction** | | **261×** |

> **The direct-API approach is impossible at this file size.** A 3 MB PowerPoint base64-encoded to ~1,000,000 tokens exceeds the entire context window of any current Claude model. x1's path-based approach makes the task feasible.

---

### "Find the budget spreadsheet I updated this week"

1. `x1_search` → `Files`, query `budget type:xlsx`, `sort: [{column:"modified", direction:"desc"}]`.
2. First result is the most-recently-modified match.
3. `x1_execute_action` → `open` to launch it in Excel, or `x1_get_content` + `extract-office-text.ps1` to read its contents inline.

**Token cost analysis** (typical Excel file: 300 KB; "read contents" path shown)

| Step | With x1 | Without x1 (Graph API) |
|---|---|---|
| Search / list | ~500 | ~1,000 |
| Fetch file content | ~50 (path) + ~1,800 (extracted text) | ~100,000 (300 KB XLSX as base64) |
| **Total** | **~2,350 tokens** | **~101,000 tokens** |
| **Reduction** | | **43×** |

> If the user only wants to *open* the file in Excel, the x1 cost drops further to ~700 tokens (`execute_action open` returns a launch confirmation); no file bytes enter context at all.

---

### "What did the team decide in the Teams channel about the new auth design?"

1. `x1_search` → `Teams`, query `auth design decision`.
2. `x1_generate_preview` with `output="inline"` on the top hits — read and synthesise the decision thread.

**Token cost analysis** (5 Teams messages, ~500 chars each)

| Step | With x1 | Without x1 (Teams API) |
|---|---|---|
| Search / discover | ~2,500 (5 results + snippets) | ~2,000 (list channels) + ~3,000 (list messages across channels) |
| Fetch message bodies | ~2,400 (3 × inline previews, ~800T each) | ~625 (5 × 500-char message bodies) |
| **Total** | **~4,900 tokens** | **~5,625 tokens** |
| **Reduction** | | **~1.1×** |

> For text-only chat messages, token cost is comparable. x1's key advantage here is **discovery without knowing the channel**: the Teams API requires you to enumerate channels before you can search messages; x1 searches across all channels in one call.

---

### "Pull up the proposal document I was working on in OneDrive last week"

1. `x1_search` → `OneDrive`, query `proposal`, `sort: [{column:"modified", direction:"desc"}]`, `limit: 10`.
2. Identify the right item from title/date.
3. If `previewType` comes back as `metadata_card` (cloud-only, not locally cached), use `x1_execute_action` → `open_url` to open it in the browser instead.

**Token cost analysis** (DOCX proposal, 300 KB)

| Step | With x1 `output="inline"` | With x1 `output="file"` | Without x1 (Graph API) |
|---|---|---|---|
| Search | ~500 | ~500 | ~1,000 |
| Fetch content | ~7,500 (HTML preview) | ~80 (path only) | ~100,000 (300 KB as base64) |
| **Total** | **~8,000 tokens** | **~580 tokens** | **~101,000 tokens** |
| **Reduction** | **13×** | **174×** | — |

> Use `output="file"` when the user wants to *read* the document themselves; use `output="inline"` when you need to summarise or quote from it. Either mode is dramatically cheaper than a direct API fetch.

---

### "Search my files and email for anything about the Acme contract"

One call, both tables: `x1_search` → `tables: ["Files", "MSMail"]`, query `Acme contract`. The
bridge searches each table sequentially and merges the results — `totalResults`/`returned` summed
across both, `results` concatenated (each hit already tagged with its own `table`), plus a
`byTable` array showing each table's own count (and an `error` field instead if one table's search
failed, without losing the other's results).

Sort the merged `results` by date and present a unified list before generating any preview.

**Token cost analysis** (10 results across both sources)

| Step | With x1 (1 merged call) | Without x1 (2 separate APIs) |
|---|---|---|
| Files + email search | ~5,000 | ~5,000 (Graph Files search + Graph Mail search) |
| **Total (search only)** | **~5,000 tokens** | **~5,000 tokens** |
| **Reduction** | | **~1×** |

> For metadata-only multi-source search, token cost is similar. x1's advantage is **operational**: a single credential, one bridge process, one tool call, and a unified result set rather than two authenticated API sessions with different SDK calls. Any subsequent content or attachment fetch will show the familiar large reduction.

---

### "Show me the logo image Stewart attached in that Gmail thread"

1. `x1_search` → `Gmail`, query `logo redesign att:png OR att:jpg`, `displayFields: ["subject","from","date","att"]`.
2. Locate the attachment item's URI.
3. `x1_generate_preview` with `output="file"` — the image embeds as a `data:` URI in the fragment.
4. Render the returned `path` as an artifact. Zero bytes enter context.

**Token cost analysis** (PNG image attachment: 200 KB)

| Step | With x1 | Without x1 (Gmail API) |
|---|---|---|
| Search email | ~800 | ~1,000 |
| Fetch image | ~80 (`output="file"` path) | ~66,700 (200 KB PNG as base64) |
| **Total** | **~880 tokens** | **~67,700 tokens** |
| **Reduction** | | **77×** |

> Images are always base64-encoded when fetched through any mail API. x1's `output="file"` mode embeds the image as a `data:` URI in a local HTML file and returns only the 80-token path — the image never enters the conversation.

---

### "Summarise the three most recent invoices from the accounting team"

1. `x1_search` → `MSMail`, query `invoice from:accounting`, `sort: [{column:"date", direction:"desc"}]`, `limit: 3`.
2. For each hit, `x1_generate_preview` with `output="inline"` to read the email body.
3. Synthesise a summary across all three.

**Token cost analysis** (3 invoice emails, ~15 KB HTML body each)

| Step | With x1 | Without x1 (Graph API) |
|---|---|---|
| Search | ~2,500 (3 results) | ~2,500 (list emails) |
| Fetch 3 email bodies | ~11,250 (3 × ~3,750T HTML preview) | ~11,250 (3 × ~3,750T `$value` response) |
| **Total** | **~13,750 tokens** | **~13,750 tokens** |
| **Reduction** | | **~1×** |

> Summarising plain-text email bodies is the one scenario where x1 and a direct API are cost-equivalent — both must return the text of the email, and the cost is proportional to the email size. x1's advantage over direct API is unified discovery (no separate search API call), but there is no compression benefit when the content is already text.

**The pattern across all stories:** x1 achieves its largest token reductions on **binary attachments** (images, PDFs, Office files) where base64 encoding would otherwise inflate costs by 100–1,000×. For pure text-in-email scenarios, x1 is convenient but not dramatically cheaper. For large binary files, it is often the only approach that fits in context at all.

---

## 11. The /x1 Skill

When installed for Claude Code, the package deploys a **`/x1` skill** to `%USERPROFILE%\.claude\skills\x1\`; when installed for GitHub Copilot, the same skill goes to `%USERPROFILE%\.copilot\skills\x1\`. A skill is a set of instructions the client loads on demand; this one teaches the agent how to drive the connector efficiently and is the source for many of the conventions in this manual.

**What it provides**

- **Workflow guidance** — discover sources, search **one table, or several merged in one call**, with the right table(s) and operators (`type:ext`, `att:ext`, `from:`, `subject:`, `path:`, `name:`), sort reliably, act on the inline `actions`, and preview by **rendering** the returned HTML.
- **Attachment handling** — email attachments are first-class indexed items (find them with `type:pptx`/`type:xlsx`); preview the attachment URI, not the email.
- **A token-efficiency rule** — for attachments and large files, `x1_get_content` returns a **local file path** rather than streaming the file's bytes through the conversation, so it is far cheaper than fetching the file via a generic email/file API.

**Bundled helper — `scripts\extract-office-text.ps1`**

Extracts readable text from a cached Office file. Use it after `x1_get_content` (mode `preview`) returns a path for a `.pptx`/`.xlsx`/`.docx` that `x1_generate_preview` can't extract directly:

```powershell
powershell -File "%USERPROFILE%\.claude\skills\x1\scripts\extract-office-text.ps1" -Path "C:\...\Deck.pptx" -MaxChars 6000
```

It handles `.docx` (paragraphs), `.pptx` (per-slide text), and `.xlsx` (shared-string cell text).

**Invoking it**

The skill triggers automatically when you ask the agent to find or preview your own content ("find the latest deck so-and-so sent me", "preview that contract"), or you can invoke it explicitly with `/x1`. Skills are a Claude Code and GitHub Copilot feature; Claude Desktop uses neither `~/.claude/skills` nor `~/.copilot/skills`.

---

## 12. Diagnostic Tool

`X1McpBridge.exe` includes a built-in smoke test that verifies WCF connectivity to X1ServiceHost without going through Claude Desktop:

```
X1McpBridge.exe --smoke-wcf
```

**Success output:**
```json
{"sessionId":1,"totalResults":1523}
```

**Failure output:**
```json
{"error":"There was no endpoint listening at net.pipe://localhost/X1SearchManager ..."}
```

**Timeout output:**
```json
{"error":"timeout"}
```

Run this from a Command Prompt or PowerShell window to confirm the bridge can reach X1ServiceHost before troubleshooting Claude Desktop integration.

---

## 13. Troubleshooting

### The x1-search server does not appear in the client

**Symptoms:** the agent has no knowledge of x1_search tools; no MCP server listed.

**Checks:**
1. Verify the client's own config contains the `x1-search` entry with the correct path to `X1McpBridge.exe` — `%APPDATA%\Claude\claude_desktop_config.json`, `%USERPROFILE%\.claude\settings.json`, or `%USERPROFILE%\.copilot\mcp-config.json` (see [Section 4.3](#43-client-registration)).
2. Confirm `X1McpBridge.exe` exists at the path specified — use `dir` in Command Prompt.
3. Restart Claude Desktop after any config change — the MCP server list is read at startup.
4. Check that the path uses either forward slashes or properly escaped back-slashes (`\\`).

---

### Tool call returns "error connecting to X1ServiceHost" or similar WCF error

**Symptoms:** `x1_search` or other tools return an error about named pipes or WCF endpoints.

**Checks:**
1. Open Task Manager and confirm `X1ServiceHost.exe` is running.
2. If it is not running, start X1 Desktop — it will launch X1ServiceHost.
3. Ensure the bridge is running under the same Windows user account as X1ServiceHost. Running as a different user (e.g. Administrator vs a standard account) will fail.
4. Run the smoke test to isolate whether the issue is WCF or Claude Desktop:
   ```
   "C:\Users\YourName\AppData\Local\X1 Discovery\McpBridge\X1McpBridge.exe" --smoke-wcf
   ```

**A *clean* shutdown of X1 Search / X1ServiceHost no longer needs any of the above.** The
connector shuts itself down as soon as X1 announces it's closing gracefully, instead of leaving a
dead WCF channel around for the next tool call to fail on. What happens next depends on how the
connector is running:
- **Shared Lean relay** (most installs): the relay shuts down too, and the next tool call
  transparently launches a fresh one — no restart needed, no error surfaced.
- **Plain stdio connection** (or a Full-flavor daemon's spawned child): that session's connector
  process ends along with it, the same as if it had crashed — restart the Claude session to get a
  working connector again.

The checks above are for the remaining cases this doesn't cover: a crashed (not cleanly shut down)
X1ServiceHost, a user-account mismatch, or an endpoint that was never reachable in the first place.

---

### x1_search returns 0 results unexpectedly

**Symptoms:** Search returns `{"totalResults":0,"returned":0,"results":[]}`.

**Checks:**
1. Verify the `tables` argument matches a table that X1 has indexed. Use `x1_list_sources` to see configured tables.
2. Confirm the table name exactly matches X1's schema name — e.g. `"MSMail"` not `"Outlook"` for Microsoft 365 mail. Table names are case-insensitive in the bridge but must be recognisable to X1.
3. Search for the same term in the X1 Desktop UI to confirm data is indexed.
4. If using Boolean operators, ensure they are capitalised: `AND`, `OR`, `NOT` — lowercase versions are treated as literal search terms.
5. Increase `timeoutMs` if the index is large and searches are slow.

---

### x1_search returns results but no field values

**Symptoms:** Results have `uri` and `table` but no `fields` object.

**Cause:** No `displayFields` were specified and no column configuration exists for the table in `x1mcp.config.json`.

**Fix:** Either:
- Pass `displayFields` explicitly: `"displayFields": ["name", "path", "modified"]`
- Add the table and its columns to `x1mcp.config.json`

---

### x1_get_content times out on email or Dropbox items

**Symptoms:** `x1_get_content` with `mode=preview` on MSMail, Gmail, or Dropbox items waits for the full timeout and then errors.

**Cause:** These connectors do not have a registered preview provider in X1 Desktop. The X1 service silently drops the preview request without responding.

**Fix:** Use `mode=auto` (the default) or `mode=internal` for email and Dropbox items. The `auto` mode tries preview for 10 seconds and automatically falls back to returning raw index fields:

```json
{ "table": "MSMail", "uri": "...", "mode": "auto" }
```

---

### x1_get_content with mode=extract fails on non-file items

**Symptoms:** `x1_get_content` with `mode=extract` returns `{"error":"Text extraction failed or timed out."}`.

**Cause:** Text extraction (`extract` mode) only works for local files indexed by the Files connector. It is not supported for email, cloud storage, or any other connector type.

**Fix:** Use `mode=auto` or `mode=internal` for non-file items.

---

### Date fields show as numbers (e.g. `"46037.33875"`)

**Cause:** X1 stores dates as OLE Automation doubles (days since 30 December 1899).

**Conversion:** In .NET: `DateTime.FromOADate(46037.33875)` → `2026-01-01 08:07:48`.
In Python: `datetime(1899, 12, 30) + timedelta(days=46037.33875)`.

When asking Claude to interpret a date, provide the raw value and ask it to convert: *"The date field value is 46037.33875 — what date is that?"*

---

### A preview shows raw HTML instead of a rendered view

**Cause:** `x1_generate_preview` returns self-contained `html`; it has to be *rendered* by the client.

**Fix:** On Claude Desktop / claude.ai, ask Claude to render the result as an HTML **artifact**. In Claude Code, ask it to render the preview inline. The installed connector's tool description already instructs this, so it usually happens automatically — if it doesn't, just say *"render that as an artifact."*

---

### Previewing a `.pptx` or `.xlsx` only shows a metadata card

**Cause:** `x1_generate_preview` extracts `.docx` but not slide decks or spreadsheets, so those return `previewType: "metadata_card"`.

**Fix:** Call `x1_get_content` with `mode: "preview"` to get a local cached path, then run the bundled `extract-office-text.ps1` on it (see [Section 11](#11-the-x1-skill)).

---

### Claude Code keeps asking permission for every x1-search call

**Cause:** MCP tools prompt unless pre-approved in `permissions.allow`.

**Fix:** Re-run the installer with `-Target Code` (it adds the read-only tools), or add them by hand (see [Section 3 → Manual installation, step 4](#3-installation)), then restart Claude Code. `x1_execute_action` is intentionally left to prompt because it opens files/launches the browser. On Claude Desktop, choose **"Allow always"** in its approval dialog.

---

### GitHub Copilot fails before any x1-search tool runs

**Symptoms:** the session dies during start-up with one of these, and `x1-search` appears **nowhere** in `%USERPROFILE%\.copilot\logs` for that session:

- `You are not authorized to use this Copilot feature, it requires an enterprise or organization policy to be enabled.`
- `Execution failed: Error: 421 "Misdirected Request"`

**Cause:** both are Copilot entitlement failures, not connector failures. In the app log (`github-app.*.log`) they show up as `403 "unauthorized: not authorized to use this Copilot feature"` or `421 "Misdirected Request"` on `models.list` / `session.model.list` — GitHub's model catalog, which the app must read before it can start a session. The connector is never reached, which is why the logs mention it not at all. GitHub's own `github-mcp-server` fails identically in the same session; if it is failing too, the cause is not local.

**Fixes:**

1. **A paid Copilot license is required.** Copilot Free is not entitled to the CLI/app agent surface. Assign a paid seat.
2. **If the seat comes from an organization, enable that org's Copilot CLI policy.** A policy that was never configured behaves as disabled:
   ```powershell
   gh api orgs/YOUR-ORG/copilot/billing --jq .cli    # must be "enabled", not "unconfigured"
   ```
   Set it under **Organization settings → Copilot → Policies**, and check the **Models** tab too.
3. **After any seat or policy change, re-authenticate.** A `421` specifically means valid credentials sent to a server that is not authoritative for them — i.e. Copilot is still using the token it cached under the *previous* entitlement. Run `/logout` then `/login` in the CLI, or sign out and back in in the app.

Confirm the seat is actually being used afterwards — an empty `last_activity` means it never has been:

```powershell
gh api orgs/YOUR-ORG/copilot/billing/seats --jq '.seats[] | {login: .assignee.login, last_activity: .last_activity_at}'
```

---

### GitHub Copilot keeps asking permission for every x1-search call

**Cause:** the same prompting, but Copilot has no equivalent of `permissions.allow`, so `-Target Copilot` cannot fix it for you. Copilot writes approvals to `%USERPROFILE%\.copilot\permissions-config.json` under the absolute path of the **project directory** you were in when you approved — so an approval granted in one repo does not carry to the next.

**Fix:** choose Copilot's "always allow" option at the prompt (once per project), or start it with `--allow-tool 'x1-search(*)'`.

---

### x1-search appears twice in GitHub Copilot, or the /x1 skill is listed twice

**Cause:** the two Copilot routes were both used. `install.ps1 -Target Copilot` (or `All`) registers `x1-search` in `~/.copilot/mcp-config.json` and installs the skill to `~/.copilot/skills/x1`; the Copilot **plugin** carries its own copy of both. A leftover `skillDirectories` entry from a manual `/skill add` does the same thing to the skill on its own.

**Why it is not harmful, only confusing:** both registrations run `X1McpBridge.exe --proxy`, so they share the one relay and the WCF invariant still holds. Nothing crashes; you just see duplicates.

**Fix:** pick one route. Either `copilot plugin uninstall x1-search` and keep the installer's registration, or run `install.ps1 -Uninstall -Target Copilot` and keep the plugin. For a duplicated skill, remove the stale `skillDirectories` entry from `%USERPROFILE%\.copilot\settings.json` — the installer warns about that entry but deliberately does not edit your settings array. `install.ps1` reports both conditions when it runs.

---

### The Copilot plugin's tools never appear, though its /x1 skill works

**Cause:** a plugin's MCP servers are only started when the plugin is **enabled**. Mounting a directory with `copilot --plugin-dir` loads its skills but does not enable it, so the skill shows up and the tools silently do not.

**Fix:** add the plugin to `enabledPlugins` in `%USERPROFILE%\.copilot\settings.json`:

```json
{
  "enabledPlugins": {
    "x1-search": true
  }
}
```

Plugins installed with `copilot plugin install` are enabled for you, so this only affects locally-mounted ones. If the tools are still missing, check `%USERPROFILE%\.copilot\logs` for `failed to spawn MCP server process` — Copilot expands `${VAR}` in a server's `command` but **not** `%VAR%` or `~`, and an unset `${VAR}` fails the same way a missing binary does.

---

### Claude Desktop shows "MCP server disconnected" or similar

**Symptoms:** Claude reports the x1-search server is unavailable or disconnected mid-session.

**Checks:**
1. Check if X1ServiceHost has stopped — restart X1 Desktop to bring it back up.
2. If the bridge crashed, check Windows Event Viewer under Application logs for errors from `X1McpBridge`.
3. Restart Claude Desktop to re-launch the bridge subprocess.
4. Run the smoke test to verify connectivity:
   ```
   X1McpBridge.exe --smoke-wcf
   ```

---

### The bridge was previously installed to a different path

**Symptoms:** Claude Desktop connects to an old copy of the bridge from `%USERPROFILE%\Release` or another location.

**Fix:** Update `claude_desktop_config.json` to point to the new installation path, or re-run `install.ps1` — it will update the path automatically and warn if it is replacing an existing entry:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

Then restart Claude Desktop.
