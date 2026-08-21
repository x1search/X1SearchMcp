# X1 Search MCP Bridge

A Model Context Protocol (MCP) server that connects **Claude Desktop**, **Claude Code**,
**claude.ai**, and the **GitHub Copilot** app (and Copilot CLI) to your local X1 Search index —
search and preview your files, email and attachments, cloud documents (OneDrive, GDrive, Dropbox,
SharePoint), and chat (Teams, Slack).

Full documentation: [docs/UserManual.md](docs/UserManual.md).

## Build the package

```bat
build-installer.bat
```

Restores packages, builds in Release, and stages the `installer\` folder (binaries,
`install.ps1`, and the `/x1` Claude Code skill).

### Flavors

There are two, and **Lean is the default** — a bare `build-installer.bat` produces the
customer package.

| | Lean (default) | Full (`--full`) |
|---|---|---|
| GraphQL API + Nitro IDE | no | yes, at `http://localhost:5250/graphql` |
| .NET 10 dependency | **none** | yes (self-contained `X1McpGraphQL.exe`) |
| Shared relay | `X1McpBridge.exe --host` (net4.8, in-process) | `X1McpGraphQL.exe` daemon |
| Plugin zip | ~7 MB | ~70 MB |
| Install directory | ~21 MB | ~182 MB |
| Build needs | MSBuild + PowerShell | also the .NET 10 SDK |

Both flavors serve the identical wire contract on the identical port, so `--proxy` and every
registered MCP entry keep working across an upgrade in either direction, and the packages are
drop-in interchangeable. Installing Lean over an existing Full install migrates automatically:
it removes the daemon's logon task, stops the daemon, deletes it, and reports the reclaimed
space — see `install.ps1`'s migration step for the ordering constraints and the
non-elevated case.

`X1McpGraphQL/docs/division-of-labor.md` has the full picture of what each process does and why
a shared relay exists at all.

## Install

```powershell
# Claude Desktop, Claude Code and GitHub Copilot (default)
powershell -ExecutionPolicy Bypass -File installer\install.ps1

# Claude Desktop only (also enables claude.ai web via the Desktop relay)
powershell -ExecutionPolicy Bypass -File installer\install.ps1 -Target Desktop

# Claude Code only — also installs the /x1 skill and pre-approves the read-only tools
powershell -ExecutionPolicy Bypass -File installer\install.ps1 -Target Code

# GitHub Copilot app / Copilot CLI only — also installs the /x1 skill
powershell -ExecutionPolicy Bypass -File installer\install.ps1 -Target Copilot

# Uninstall (add -Target Desktop|Code|Copilot to scope it)
powershell -ExecutionPolicy Bypass -File installer\install.ps1 -Uninstall
```

> Quit Claude Desktop (it minimises to the tray) and close Claude Code before re-installing —
> the running bridge locks its own binaries. Copilot shares that relay, so quit it too.

### GitHub Copilot as a plugin instead

`build-installer.bat` also produces `installer\copilot-plugin\`, a self-contained Copilot plugin
carrying both the connector and the `/x1` skill. Use it *instead of* `-Target Copilot`, not as
well — each registers a server named `x1-search`.

```powershell
copilot plugin install <package>\copilot-plugin
```

The argument must be an **absolute path to the directory**: a relative path is rejected as an
invalid spec, and the `x1-search-copilot.plugin` zip is rejected too (Copilot does not unpack
archives — unzip it first). See
[copilot-plugin/README.md](copilot-plugin/README.md) for the full matrix, the `--plugin-dir`
alternative and its enablement gotcha, and which path placeholders Copilot does and does not
expand.

## Requirements

- Windows 10/11 with .NET Framework 4.8 (in-box on Win10 1903+/Win11).
- X1 Desktop installed with at least one completed index scan.
- **X1ServiceHost** running under the same Windows user account.
- At least one supported client: Claude Desktop, Claude Code, or GitHub Copilot (the desktop app
  or the CLI — both read the same `~/.copilot` configuration, so one install target covers both).
- For GitHub Copilot specifically, a **paid Copilot license**. Copilot Free is not entitled to the
  CLI/app agent surface the connector plugs into, and an org-provided seat additionally needs the
  org's Copilot CLI policy enabled. See
  [copilot-plugin/README.md](copilot-plugin/README.md#troubleshooting-authorization).

The Lean package needs **no .NET 5+/10 runtime** on the target machine — it is net4.8 only. The
Full package's daemon is self-contained, so it needs no installed .NET 10 runtime either, but it
carries one inside the exe.

## Tools

| Tool | Purpose |
|------|---------|
| `x1_search` | Keyword / X1-syntax search (one table, or several merged in one call); results include inline `actions`. |
| `x1_list_sources` | Discover indexed tables, their columns, and `capabilities`. |
| `x1_list_actions` | Post Search Actions for a result (usually unnecessary — see `x1_search`). |
| `x1_execute_action` | Act on a result: `get_path`, `open`, `show_in_folder`, `get_url`, `open_url`. |
| `x1_get_metadata` | Field values for one item. |
| `x1_get_content` | Item content (`auto` / `content` / `preview` / `internal`). `content` is the extracted text and works for every table. |
| `x1_generate_preview` | Self-contained HTML preview to render as an artifact/widget. |
| `x1_version` | Versions and paths of both halves of the connector (daemon + bridge), plus every X1McpBridge running on the machine. Answers "which build is actually serving this session?". |

See [docs/UserManual.md](docs/UserManual.md) for parameters, configuration, the `/x1` skill,
query syntax, and troubleshooting.

## Privacy Policy

This connector runs entirely on your machine: it reads from your local X1 Search index over a
local WCF connection to `X1ServiceHost` and returns results directly to whichever Claude client
called it. It does not send your indexed content — files, email, chat, or any other data — to
X1 Discovery, Inc. or to Anthropic.

The full X1 Discovery, Inc. privacy policy, covering data collected by X1 Search itself (the
desktop application and index this connector reads from), is available at
[x1.com/privacy-and-terms](https://www.x1.com/privacy-and-terms/).
