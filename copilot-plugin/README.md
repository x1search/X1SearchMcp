# X1 Search (GitHub Copilot plugin)

Search and preview your own content from the **GitHub Copilot** app or the Copilot CLI — local
files, Outlook/Exchange/Gmail email and **attachments**, cloud files (OneDrive, GDrive, Dropbox,
SharePoint), and chat (Teams, Slack) — powered by your local X1 Search index.

Same two halves as the Cowork plugin, packaged for Copilot instead:

- **The x1-search connector** — a local MCP server (`connector/X1McpBridge.exe`) that talks to X1
  Search over WCF. Registered via `.mcp.json` using `${CLAUDE_PLUGIN_ROOT}`, so it runs from
  wherever the plugin ends up on disk.
- **The `/x1` skill** — guidance that teaches the agent to drive the connector efficiently
  (search one table or several merged in one call, use the inline `actions`, render previews,
  handle attachments, prefer path-not-bytes retrieval for large files). Includes the bundled
  `extract-office-text.ps1` helper for `.pptx`/`.xlsx` content.

## Requirements

- **Windows 10/11** with .NET Framework 4.8 (the connector is a native Windows executable).
- **X1 Desktop** installed with at least one completed index scan.
- **X1ServiceHost** running under the same Windows user account (it starts with X1 Desktop).
- **GitHub Copilot on a PAID license** — the desktop app, or the Copilot CLI
  (`npm i -g @github/copilot`). Both read the same `~/.copilot` configuration. Copilot Free is not
  sufficient: the connector plugs into the CLI/app agent surface, which a free account is not
  entitled to, and requests fail before any tool runs.
- If your Copilot seat comes from an **organization**, that org must also have the **Copilot CLI
  policy enabled** — an unset policy behaves as disabled. Check it with:
  `gh api orgs/YOUR-ORG/copilot/billing --jq .cli` (it must read `enabled`, not `unconfigured`).

The plugin bundles the connector itself, but it cannot bundle X1 Desktop / X1ServiceHost — those
must be installed and running on the machine.

## Installing

**From a local build** (what `build-installer.bat` produces), point the installer at the plugin
**directory**, by absolute path:

```powershell
copilot plugin install C:\X1SearchMcp\copilot-plugin
```

That copies the tree into `~/.copilot/installed-plugins/_direct/` and records `"enabled": true`,
so the MCP server starts and the skill loads with no further configuration.

That argument is fussier than it looks, and each wrong form fails differently:

| Argument | Result |
|---|---|
| absolute path to the plugin directory | works |
| relative path (`.\copilot-plugin`) | `Invalid plugin spec` — resolve it to an absolute path first |
| the `x1-search-copilot.plugin` **zip** | `No plugin.json found in repository` — Copilot never unpacks an archive. Unzip it, then install the directory |
| a `file:///` git URL | `Invalid plugin spec` |

Copilot also prints **"Direct plugin installs (repos, URLs, local paths) are deprecated. Only
`plugin@marketplace` installs will be supported in a future release."** The local-directory route
therefore works today but is on notice; publishing a marketplace entry is the durable answer.

**From the repository**, once this directory is published:

```powershell
copilot plugin install x1search/X1SearchMcp:copilot-plugin
```

**Without installing at all**, mount the directory per invocation:

```powershell
copilot --plugin-dir "C:\X1SearchMcp\copilot-plugin" -p "find the Q4 budget spreadsheet"
```

`--plugin-dir` differs from installing in one way that is easy to lose an hour to: it does **not**
enable the plugin, and a plugin's MCP servers only start when it is enabled. The skill loads, the
tools silently do not. Enable it by name in `~/.copilot/settings.json`:

```json
{
  "enabledPlugins": {
    "x1-search": true
  }
}
```

### Or skip the plugin entirely

`install.ps1 -Target Copilot` registers the connector in `~/.copilot/mcp-config.json` and installs
the `/x1` skill to `~/.copilot/skills/x1` directly. That is the simpler route on a machine where
you are already running the connector's own installer, and it needs no plugin enablement step.
**Use one or the other, not both** — each registers a server named `x1-search`, and the installer
warns when it sees the plugin also installed.

## What Copilot does and does not expand

Verified against the Copilot CLI (1.0.80) and the desktop app (1.1.11) by launching real sessions
and reading `~/.copilot/logs`, because none of this is documented and the failure mode is a silent
`failed to spawn MCP server process`:

| In `command` | Expanded? |
|---|---|
| `${CLAUDE_PLUGIN_ROOT}` | **yes** — resolves to the plugin root. `${COPILOT_PLUGIN_ROOT}` and `${PLUGIN_ROOT}` are accepted as aliases. |
| `${ANY_ENV_VAR}` | **yes** — ordinary environment-variable expansion, e.g. `${LOCALAPPDATA}`. |
| `%WINDOWS_STYLE%` | no — passed through literally, so the spawn fails. |
| `~/...` | no — same. |

An **unset** `${VAR}` is not silently dropped either; the spawn just fails. So a typo'd variable
name looks exactly like a missing binary.

Two more behaviours worth recording:

- `.mcp.json` at the plugin root is **auto-discovered**. Declaring `"mcpServers": ".mcp.json"` in
  the manifest is also supported by Copilot, but this plugin relies on auto-discovery — that is
  what was actually tested here, and what GitHub's own shipped plugins do.
- `copilot plugin install <directory>` auto-enables; `copilot --plugin-dir <directory>` does not.
  See the install table above.
- The manifest is read from `.claude-plugin/plugin.json` natively, which is why this plugin and
  the Cowork one share a layout instead of needing a Copilot-specific manifest location.

## Tools

| Tool | Purpose |
|------|---------|
| `x1_search` | Keyword / X1-syntax search (one table, or several merged in one call); results include inline `actions`. |
| `x1_list_sources` | Discover indexed tables, columns, and `capabilities`. |
| `x1_list_actions` | Post Search Actions for a result (usually unnecessary — see `x1_search`). |
| `x1_execute_action` | Act on a result: `get_path`, `open`, `show_in_folder`, `get_url`, `open_url`. |
| `x1_get_metadata` | Field values for one item. |
| `x1_get_content` | Item content (`auto` / `content` / `preview` / `internal`). `content` is the extracted text and works for every table. |
| `x1_generate_preview` | Self-contained HTML preview. |
| `x1_version` | Versions and paths of the connector's daemon and bridge, plus every X1McpBridge running on the machine — confirms which build is serving. |

For the full reference (parameters, query syntax, configuration, troubleshooting) see the
connector's [User Manual](../docs/UserManual.md).

## Permissions

Copilot prompts the first time each tool runs. Choose its "always allow" option, or start it with
`--allow-tool 'x1-search(*)'`.

There is deliberately nothing for the installer to pre-approve here, unlike Claude Code's
`permissions.allow`: Copilot saves approvals in `~/.copilot/permissions-config.json`, which is
auto-managed and keyed by absolute **project directory**, so there is no machine-wide slot to seed.

## Troubleshooting authorization

Both of these fail **before** the plugin is loaded, so `x1-search` will not appear anywhere in
`~/.copilot/logs` when they happen. That absence is the tell: it is a Copilot entitlement problem,
not a connector problem.

**`You are not authorized to use this Copilot feature, it requires an enterprise or organization
policy to be enabled`** — with `403 "unauthorized: not authorized to use this Copilot feature"` on
`models.list` / `session.model.list` in the log. No paid seat, or the org's Copilot CLI policy is
not enabled. Assign a paid seat and enable that policy (see Requirements above).

**`421 "Misdirected Request"`** on the same endpoints, right after a seat or policy change. Copilot
is still presenting the token it cached under the previous entitlement, and 421 is precisely
"valid credentials, wrong authority". Sign out and back in — `/logout` then `/login` in the CLI, or
sign out in the app — so a fresh token is minted. GitHub's own `github-mcp-server` fails the same
way in the same session, which is a quick way to confirm the cause is not local.

Confirm a seat is actually being used (an empty `last_activity` means it never has been):

```powershell
gh api orgs/YOUR-ORG/copilot/billing/seats --jq '.seats[] | {login: .assignee.login, last_activity: .last_activity_at}'
```

## Upgrading

The connector runs a shared relay (`connector/X1McpBridge.exe`) that is deliberately kept running
after the client closes, so a later session reuses it instead of starting cold. That means it can
hold its own executable locked when the plugin updates. If an update seems not to take effect
(`x1_version` still reports the old build), quit Copilot — and Claude Desktop / Claude Code, which
share the same relay — stop the process (`Get-Process X1McpBridge | Stop-Process`), then update.

The relay also shuts itself down after an hour of no use (configurable via the
`X1McpConnectorIdleShutdown` registry value — see the
[User Manual](../docs/UserManual.md#46-logging-and-lifecycle-settings)), so an update run well
after your last search generally finds it already unloaded.

## Notes

- The connector reads `connector/x1mcp.config.json` for default tables, display columns, and
  preview settings — edit it to change which sources are searched by default.
- `connector/` and `skills/` are assembled from the build output and the canonical skill source
  (`../skill/x1`) by `build-copilot-plugin.ps1`; they are not checked into source control.
- The payload folder is deliberately not named `bin/`, matching the Cowork plugin: a top-level
  `bin/` is auto-added to PATH by Claude's CLI and gets hosted plugins rejected at upload. Keeping
  both packages on the same name means one fewer difference between them.
- `args` is `["--proxy"]`, exactly as in every other registration. That is not cosmetic — see the
  one-relay invariant in [docs/build-flavors.md](../docs/build-flavors.md). A bare
  `X1McpBridge.exe` here would own its own WCF connection instead of proxying to the shared relay,
  which is the race that crashes X1ServiceHost.
